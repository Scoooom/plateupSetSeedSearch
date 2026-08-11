namespace PlateUpSeedTools.Web.Services;

/// <summary>
/// Periodically reaps search jobs whose browser tab stopped polling (closed, crashed, lost
/// network, navigated away) instead of leaving them to run to completion / the match cap forever.
/// See SearchJob.Touch's doc comment: the browser's own status-poll (GET /api/search/{jobId},
/// every ~1s per search.js) already doubles as a heartbeat, so no new client-server transport
/// (WebSocket/SSE/SignalR) is needed here - this just watches for that heartbeat going silent.
///
/// DOM lifecycle events (pagehide/beforeunload/visibilitychange) are deliberately NOT used, even
/// as a "fast path": visibilitychange in particular fires when a user merely switches to another
/// tab while leaving this one open in the background, which is not abandonment and must not
/// cancel the job. This sweep is the only signal, so there's no false-positive risk from that.
/// </summary>
public sealed class SearchJobHeartbeatMonitor(SearchJobManager jobs, IConfiguration configuration)
    : BackgroundService
{
    private readonly TimeSpan _heartbeatTimeout = TimeSpan.FromSeconds(
        configuration.GetValue<int?>("Search:HeartbeatTimeoutSeconds") ?? 90);

    private readonly TimeSpan _sweepInterval = TimeSpan.FromSeconds(
        configuration.GetValue<int?>("Search:HeartbeatSweepIntervalSeconds") ?? 10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_sweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            jobs.SweepAbandonedJobs(_heartbeatTimeout);
        }
    }
}
