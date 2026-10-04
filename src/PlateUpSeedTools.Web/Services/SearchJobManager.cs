using System.Collections.Concurrent;
using PlateUpSeedTools.Engine.Cards;
using PlateUpSeedTools.Engine.Data;
using PlateUpSeedTools.Engine.Seeding;
using PlateUpSeedTools.Infrastructure.Cache;
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

    // The job's own original web-layer request, retained so it can self-persist a CachedSearchResult
    // at terminal time (see SearchJobManager.FinalizeTerminalJobAsync) with no client round-trip -
    // unlike the pre-existing lazy "Copy Link" flow (ShareSearchResult), which has the client resend
    // its own payload specifically because the job never kept it before now.
    public required SearchStartRequest OriginalRequest { get; init; }

    // Non-null only for a notify-enabled job (NotifyOnCompletion == true AND push is server-
    // configured - see StartSearch) - minted eagerly at job creation so the client has a working
    // link from the moment the job starts, not just after it finishes. Indexed in
    // SearchJobManager._jobsByHash while the job is alive; the SAME hash is reused as the permanent
    // ICacheStore<CachedSearchResult> key once the job reaches a terminal state, so the link keeps
    // resolving forever afterward with zero new storage.
    public string? ShareHash { get; init; }

    // Null unless this job opted into notify-mode's heartbeat immunity + hard deadline - see
    // StartSearch/SweepAbandonedJobs/RunAsync.
    public DateTimeOffset? DeadlineUtc { get; init; }

    // True when this Completed job actually stopped because it hit DeadlineUtc rather than
    // exhausting its search space or being explicitly cancelled - see RunAsync's two
    // OperationCanceledException handlers. Surfaced as Completed (not a new status), same
    // "stopped early, still a success" convention as MatchCapReached.
    public volatile bool TimedOut;

    public required bool NotifyOnCompletion { get; init; }

    // Set post-construction once the client's separate POST /api/search/{jobId}/push-subscription
    // call lands - necessarily after StartSearch already returned a jobId, since subscribing
    // requires a service-worker-ready + Notification-permission round-trip the browser can't do
    // synchronously within the original POST /api/search request.
    public PushSubscriptionDto? PushSubscription { get; set; }

    // Populated once the search finishes: representative match seed -> the other seed strings that
    // hash to the same value (identical game). Empty during the run - GetStatus/ShareSearchResult
    // fold this into the SearchMatchDto list at read time. See SeedSearchResult.EquivalentSeeds.
    public IReadOnlyDictionary<string, SeedEquivalents> Equivalents { get; set; } =
        new Dictionary<string, SeedEquivalents>();

    // Updated on every client status poll (SearchEndpoints.GetStatus) - this doubles as the
    // "browser is still watching this job" heartbeat SearchJobHeartbeatMonitor relies on to reap
    // jobs whose tab went away (closed/crashed/lost network) without ever telling the server.
    // Notify-enabled jobs are immune to this - see SweepAbandonedJobs - so for them this is purely
    // informational.
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
    private readonly ICacheStore<CachedSearchResult> _cache;
    private readonly PushNotificationSender _pushSender;
    private readonly ILogger<SearchJobManager> _logger;
    private readonly int _maxDegreeOfParallelismPerJob;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<string, SearchJob> _jobs = new();

    // Hash -> job, for notify-enabled jobs only (see StartSearch) - lets a "live" link resolve
    // while the job is still running. Removed in lockstep with _jobs eviction (EvictOldJobs); by
    // then FinalizeTerminalJobAsync has already written the same hash into the permanent
    // ICacheStore<CachedSearchResult>, so the link keeps resolving afterward via that path instead.
    private readonly ConcurrentDictionary<string, SearchJob> _jobsByHash = new();

    public int MaxConcurrentSearchJobs { get; }

    // Hard wall-clock cap for a notify-enabled job (see StartSearch/RunAsync) - the user's own
    // framing: decoupling heartbeat-abandonment cancellation is only safe in exchange for an
    // absolute ceiling on how long a headless (nobody polling) job can run. Regular jobs are
    // unaffected - they keep today's no-cap-but-heartbeat-gated behavior.
    public TimeSpan MaxJobDuration { get; }

    public SearchJobManager(
        GameDataRepository repo,
        IConfiguration configuration,
        ICacheStore<CachedSearchResult> cache,
        PushNotificationSender pushSender,
        ILogger<SearchJobManager> logger)
    {
        _repo = repo;
        _cache = cache;
        _pushSender = pushSender;
        _logger = logger;
        MaxConcurrentSearchJobs = configuration.GetValue<int?>("Search:MaxConcurrentJobs")
            ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        _maxDegreeOfParallelismPerJob = Math.Max(1, Environment.ProcessorCount / MaxConcurrentSearchJobs);
        _slots = new SemaphoreSlim(MaxConcurrentSearchJobs, MaxConcurrentSearchJobs);
        MaxJobDuration = TimeSpan.FromSeconds(configuration.GetValue<int?>("Search:MaxJobDurationSeconds") ?? 3600);
    }

    public SearchJob StartSearch(SeedSearchRequest request, long matchCap, SearchStartRequest originalRequest)
    {
        EvictOldJobs();

        // Only takes effect if the client actually attached a push subscription afterward AND the
        // server has WebPush:* configured - defense in depth against a stale/bypassed client
        // silently getting heartbeat immunity with no way to ever be notified (see
        // PushNotificationSender.IsConfigured). Checked here (not just client-side) because this
        // is also what gates minting ShareHash/DeadlineUtc below.
        var notifyEnabled = originalRequest.NotifyOnCompletion == true && _pushSender.IsConfigured;

        var job = new SearchJob
        {
            Id = Guid.NewGuid().ToString("N"),
            TotalToCheck = request.TotalToCheck,
            StartedAt = DateTimeOffset.UtcNow,
            Cts = new CancellationTokenSource(),
            OriginalRequest = originalRequest,
            NotifyOnCompletion = notifyEnabled,
            ShareHash = notifyEnabled ? ShareHash.Generate() : null,
            DeadlineUtc = notifyEnabled ? DateTimeOffset.UtcNow + MaxJobDuration : null,
        };
        // Baseline the heartbeat at creation, not just on first poll - otherwise a sweep tick that
        // lands before the browser's first GET /api/search/{jobId} would see a "stale" heartbeat
        // (default DateTimeOffset) and immediately cancel a job nobody has abandoned yet.
        job.Touch();
        _jobs[job.Id] = job;
        if (job.ShareHash is not null)
        {
            _jobsByHash[job.ShareHash] = job;
            job.Cts.CancelAfter(MaxJobDuration);
        }

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

    public bool TryGetJobByHash(string hash, out SearchJob job) => _jobsByHash.TryGetValue(hash, out job!);

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
            // A notify-enabled job is immune to heartbeat-abandonment cancellation by design - that's
            // the whole point of the feature (keep running if the tab closes). It's bounded instead
            // by its own DeadlineUtc, enforced via job.Cts.CancelAfter at creation time (see
            // StartSearch), not by this sweep.
            if (job.Status is SearchJobStatus.Queued or SearchJobStatus.Running
                && !job.NotifyOnCompletion
                && job.LastHeartbeatUtc < cutoff)
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
            MarkCancelledOrTimedOut(job);
            job.FinishedAt = DateTimeOffset.UtcNow;
            await FinalizeTerminalJobAsync(job);
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
                onMatch: (seed, otFriendly) =>
                {
                    // Computed for every match regardless of whether the search was size-filtered,
                    // same "always tag" convention as otFriendly above - see SearchMatchDto.MapSize.
                    var mapSize = !string.IsNullOrEmpty(request.SettingSlug) &&
                                  SeedSearcher.TryResolveMapSize(_repo, new Seed(seed), request.SettingSlug, out var size)
                        ? size.ToString()
                        : null;
                    job.Matches.Enqueue(new SearchMatchDto(seed, otFriendly, MapSize: mapSize));
                },
                onProgress: job.SetCheckedCount,
                cancellationToken: job.Cts.Token);

            job.MatchCapReached = result.MatchCapReached;
            job.Equivalents = result.EquivalentSeeds;
            job.Status = SearchJobStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            MarkCancelledOrTimedOut(job);
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

        await FinalizeTerminalJobAsync(job);
    }

    // Both OperationCanceledException sites above throw via the same job.Cts.Token, so "hit the
    // notify-mode hard deadline" (job.Cts.CancelAfter, set in StartSearch) is told apart from an
    // explicit user Cancel (SearchJobManager.TryCancel) purely by whether the deadline has actually
    // passed - heartbeat-timeout cancel can't reach a notify job at all (see SweepAbandonedJobs'
    // NotifyOnCompletion guard), so there's no third cause to distinguish here. Surfaced as
    // Completed + TimedOut (not a new status), same "stopped early, still a success" convention as
    // MatchCapReached - see SearchJob.TimedOut's doc comment.
    private static void MarkCancelledOrTimedOut(SearchJob job)
    {
        if (job.DeadlineUtc is { } deadline && DateTimeOffset.UtcNow >= deadline)
        {
            job.TimedOut = true;
            job.Status = SearchJobStatus.Completed;
        }
        else
        {
            job.Status = SearchJobStatus.Cancelled;
        }
    }

    /// <summary>
    /// Runs a notify-enabled job's two terminal side effects - persisting a permanent shareable
    /// snapshot and sending the completion push - once it reaches any terminal state. A no-op for a
    /// regular (non-notify) job (ShareHash is null). Each side effect is independently best-effort:
    /// a cache or push failure is logged and swallowed, never allowed to affect the job's own
    /// already-set terminal Status.
    /// </summary>
    private async Task FinalizeTerminalJobAsync(SearchJob job)
    {
        if (job.ShareHash is null)
        {
            return;
        }

        try
        {
            // Not Error: CachedSearchResult has no field to represent a failure, and an error run
            // has nothing useful to show - it's left to expire via the live-job eviction below
            // instead (same ~30 minutes a regular job's failure already gets).
            if (job.Status is SearchJobStatus.Completed or SearchJobStatus.Cancelled)
            {
                var cached = new CachedSearchResult(
                    job.OriginalRequest,
                    SearchResultProjection.WithEquivalents(job),
                    job.TotalToCheck,
                    job.CheckedCount,
                    job.MatchCapReached,
                    job.TimedOut);
                await _cache.SetAsync(job.ShareHash, cached);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist the shareable result for search job {JobId}.", job.Id);
        }

        // Completed covers both a real finish and a TimedOut partial finish - not Cancelled (the
        // user explicitly stopped it; they're presumably still there, a push would be noise) and not
        // Error (nothing useful to report).
        if (job.Status == SearchJobStatus.Completed && job.PushSubscription is { } subscription)
        {
            await SendCompletionPushAsync(job, subscription);
        }
    }

    private async Task SendCompletionPushAsync(SearchJob job, PushSubscriptionDto subscription)
    {
        var body = job.TimedOut
            ? $"Stopped after the 1-hour time limit - {job.Matches.Count} match(es) found."
            : $"Done - {job.Matches.Count} match(es) found.";

        try
        {
            await _pushSender.SendCompletionPushAsync(
                job.Id, subscription, "Seed search finished", body, $"/search/cached#{job.ShareHash}", CancellationToken.None);
        }
        catch (Exception ex)
        {
            // PushNotificationSender already swallows WebPushException internally - this is a
            // defensive backstop against anything else going wrong, same rationale as the
            // cache-persist try/catch above.
            _logger.LogWarning(ex, "Failed to send completion push for search job {JobId}.", job.Id);
        }
    }

    /// <summary>
    /// Backs POST /api/search/{jobId}/push-subscription. Returns false if the job doesn't exist or
    /// never opted into notify-mode (a client can't retroactively attach a subscription to a
    /// regular job). If the job has ALREADY reached Completed by the time this lands (e.g. a
    /// near-instant match beating the service-worker/permission round-trip), sends the completion
    /// push immediately rather than relying on RunAsync's own FinalizeTerminalJobAsync call, which
    /// already ran with PushSubscription still null.
    /// </summary>
    public bool TryRegisterPushSubscription(string jobId, PushSubscriptionDto subscription)
    {
        if (!_jobs.TryGetValue(jobId, out var job) || !job.NotifyOnCompletion)
        {
            return false;
        }

        job.PushSubscription = subscription;

        if (job.Status == SearchJobStatus.Completed)
        {
            _ = Task.Run(() => SendCompletionPushAsync(job, subscription));
        }

        return true;
    }

    private void EvictOldJobs()
    {
        var cutoff = DateTimeOffset.UtcNow - JobRetention;
        foreach (var (id, job) in _jobs)
        {
            if (job.FinishedAt is { } finishedAt && finishedAt < cutoff)
            {
                _jobs.TryRemove(id, out _);
                if (job.ShareHash is not null)
                {
                    _jobsByHash.TryRemove(job.ShareHash, out _);
                }
            }
        }
    }
}
