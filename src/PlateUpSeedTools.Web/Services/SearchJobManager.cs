using System.Collections.Concurrent;
using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Web.Contracts;

namespace PlateUpSeedTools.Web.Services;

public enum SearchJobStatus
{
    Queued,
    Running,
    Completed,
    Cancelled,
    Error,
}

public sealed class SearchJob
{
    public required string Id { get; init; }
    public volatile SearchJobStatus Status = SearchJobStatus.Queued;
    private long _checkedCount;
    public long CheckedCount => Interlocked.Read(ref _checkedCount);
    public void SetCheckedCount(long value) => Interlocked.Exchange(ref _checkedCount, value);
    public required long TotalToCheck { get; init; }
    public readonly ConcurrentQueue<SearchMatchDto> Matches = new();
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public volatile bool MatchCapReached;
    public required CancellationTokenSource Cts { get; init; }

    // Updated on every client status poll (SearchEndpoints.GetStatus) - this doubles as the
    // "browser is still watching this job" heartbeat SearchJobHeartbeatMonitor relies on to reap
    // jobs whose tab went away (closed/crashed/lost network) without ever telling the server.
    private long _lastHeartbeatTicks;
    public DateTimeOffset LastHeartbeatUtc => new(Interlocked.Read(ref _lastHeartbeatTicks), TimeSpan.Zero);
    public void Touch() => Interlocked.Exchange(ref _lastHeartbeatTicks, DateTimeOffset.UtcNow.Ticks);
}

/// <summary>
/// Runs seed-search jobs (see PlateUpSeedTools.Engine.Cards.SeedSearcher) as cancellable background
/// work the browser polls for progress, instead of blocking an HTTP request for the (potentially
/// very long) duration of a search.
///
/// This is NOT a single-global-job lock. The tool's target is a publicly accessible,
/// multi-visitor site (currently in single-user development, but that's a development-stage fact,
/// not the design target) - a handful of visitors (~5-7 at a realistic peak) could plausibly start
/// searches around the same time, and a single global job would either reject everyone but the
/// first visitor or force them to share one job. Instead this runs a bounded worker pool: up to
/// <see cref="MaxConcurrentSearchJobs"/> jobs actually execute their Parallel.For at once (gated by
/// <see cref="_slots"/>), with each running job's own degree of parallelism capped so that the
/// *combined* CPU use of every simultaneously-running job stays roughly bounded to one core's
/// worth of headroom below the machine's total core count - keeping the rest of the site (layout
/// previews, health checks) responsive under load rather than every job independently trying to
/// claim every core. Extra requests beyond the pool size queue (Status = Queued) rather than being
/// rejected.
/// </summary>
public sealed class SearchJobManager
{
    private static readonly TimeSpan JobRetention = TimeSpan.FromMinutes(30);

    private readonly GameDataRepository _repo;
    private readonly int _maxDegreeOfParallelismPerJob;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, SearchJob> _jobs = new();

    public int MaxConcurrentSearchJobs { get; }

    public SearchJobManager(GameDataRepository repo, IConfiguration configuration)
    {
        _repo = repo;
        MaxConcurrentSearchJobs = configuration.GetValue<int?>("Search:MaxConcurrentJobs")
            ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        _maxDegreeOfParallelismPerJob = Math.Max(1, Environment.ProcessorCount / MaxConcurrentSearchJobs);
        _slots = new SemaphoreSlim(MaxConcurrentSearchJobs, MaxConcurrentSearchJobs);
    }

    public SearchJob StartSearch(SeedSearchRequest request, long matchCap)
    {
        EvictOldJobs();

        var job = new SearchJob
        {
            Id = Guid.NewGuid().ToString("N"),
            TotalToCheck = request.TotalToCheck,
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource(),
        };
        // Baseline the heartbeat at creation, not just on first poll - otherwise a sweep tick that
        // lands before the browser's first GET /api/search/{jobId} would see a "stale" heartbeat
        // (default DateTimeOffset) and immediately cancel a job nobody has abandoned yet.
        job.Touch();
        _jobs[job.Id] = job;

        // Task.Run is required here, not just calling RunAsync directly: when a semaphore slot is
        // immediately available, SemaphoreSlim.WaitAsync completes synchronously, so everything
        // after it - including the CPU-bound SeedSearcher.Search call - would otherwise run inline
        // on the calling (ASP.NET request) thread with no async boundary in between, blocking the
        // HTTP response for the entire search instead of returning the job immediately. Confirmed
        // by hand: without Task.Run here, POST /api/search itself hangs until a large search
        // finishes rather than returning right away.
        _ = Task.Run(() => RunAsync(job, request, matchCap));
        return job;
    }

    public bool TryGetJob(string id, out SearchJob job) => _jobs.TryGetValue(id, out job!);

    public bool TryCancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        job.Cts.Cancel();
        return true;
    }

    /// <summary>
    /// Called periodically by <see cref="SearchJobHeartbeatMonitor"/>. Cancels any still-running/
    /// queued job whose owning browser tab has stopped polling <c>GET /api/search/{jobId}</c> (tab
    /// closed, crashed, or lost network - see SearchJob.Touch's doc comment) for longer than
    /// <paramref name="heartbeatTimeout"/>, then runs the existing finished-job eviction so it's no
    /// longer dependent on someone else starting a new search to ever run.
    /// </summary>
    public void SweepAbandonedJobs(TimeSpan heartbeatTimeout)
    {
        var cutoff = DateTimeOffset.UtcNow - heartbeatTimeout;
        foreach (var job in _jobs.Values)
        {
            if (job.Status is SearchJobStatus.Queued or SearchJobStatus.Running && job.LastHeartbeatUtc < cutoff)
            {
                job.Cts.Cancel();
            }
        }

        EvictOldJobs();
    }

    private async Task RunAsync(SearchJob job, SeedSearchRequest request, long matchCap)
    {
        try
        {
            // Waiting here (rather than before the job is created/returned) is what lets the
            // browser see Status == Queued and a "waiting for a free slot" message instead of the
            // request itself blocking or failing while other visitors' searches occupy the pool.
            await _slots.WaitAsync(job.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            job.Status = SearchJobStatus.Cancelled;
            job.FinishedAt = DateTimeOffset.UtcNow;
            return;
        }

        try
        {
            job.Status = SearchJobStatus.Running;
            var result = SeedSearcher.Search(
                _repo,
                request,
                _maxDegreeOfParallelismPerJob,
                matchCap,
                onMatch: (seed, otFriendly) => job.Matches.Enqueue(new SearchMatchDto(seed, otFriendly)),
                onProgress: job.SetCheckedCount,
                cancellationToken: job.Cts.Token);

            job.MatchCapReached = result.MatchCapReached;
            job.Status = SearchJobStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = SearchJobStatus.Cancelled;
        }
        catch (Exception ex)
        {
            job.Status = SearchJobStatus.Error;
            job.Error = ex.Message;
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            _slots.Release();
        }
    }

    private void EvictOldJobs()
    {
        var cutoff = DateTimeOffset.UtcNow - JobRetention;
        foreach (var (id, job) in _jobs)
        {
            if (job.FinishedAt is { } finishedAt && finishedAt < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }
}
