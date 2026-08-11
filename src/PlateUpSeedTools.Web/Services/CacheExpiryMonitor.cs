using PlateUpSeedTools.Infrastructure.Cache;

namespace PlateUpSeedTools.Web.Services;

/// <summary>
/// Periodically deletes cache rows older than <c>CacheStore:MaxAgeHours</c> (default 24h) across
/// every `cache_*` table via CacheAdmin.DeleteExpiredAsync - covers layout previews and the
/// shareable search-result/Explorer-preload caches uniformly, one sweep for all of them. Follows
/// SearchJobHeartbeatMonitor's shape (ctor takes the dependency + IConfiguration, PeriodicTimer
/// loop), but sweeps once immediately on startup rather than waiting for the first tick, so stale
/// rows don't linger for a full interval after a fresh deploy.
/// </summary>
public sealed class CacheExpiryMonitor(CacheAdmin cacheAdmin, IConfiguration configuration)
    : BackgroundService
{
    private readonly TimeSpan _maxAge = TimeSpan.FromHours(
        configuration.GetValue<double?>("CacheStore:MaxAgeHours") ?? 24);

    private readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(
        configuration.GetValue<double?>("CacheStore:ExpirySweepIntervalMinutes") ?? 60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_sweepInterval);
        do
        {
            await cacheAdmin.DeleteExpiredAsync(_maxAge, stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
