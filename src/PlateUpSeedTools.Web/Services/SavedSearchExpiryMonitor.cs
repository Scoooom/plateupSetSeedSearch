using PlateUpSeedTools.Infrastructure.Cache;
using PlateUpSeedTools.Web.Contracts;

namespace PlateUpSeedTools.Web.Services;

/// <summary>
/// Periodically deletes Saved-Search rows older than ~7 days from cache_SavedSearchResult only -
/// deliberately scoped to that one table (via CacheAdmin.DeleteExpiredFromTableAsync's table-name
/// parameter), not a revival of the old generic every-cache_*-table sweep removed in commit
/// 38990c2. The permanent "Copy Link" cache (cache_CachedSearchResult) and Explorer-preload cache
/// (cache_ExplorerPreload) must keep their existing no-expiry behavior; this monitor never touches
/// them. See SearchContracts.cs's SavedSearchResult doc comment for the full save/expiry design.
/// </summary>
// Depends on ICacheStore<SavedSearchResult> (never actually called - CacheAdmin's raw-SQL sweep
// below doesn't go through it) purely to force that singleton's construction, which is what
// creates cache_SavedSearchResult in the first place (SqliteCacheStore<T>'s constructor runs
// EnsureTable()). Without this, nothing guarantees the table exists before this monitor's first
// sweep - the table otherwise isn't created until something actually saves a search.
public sealed class SavedSearchExpiryMonitor(
    CacheAdmin cacheAdmin, IConfiguration configuration, ICacheStore<SavedSearchResult> _)
    : BackgroundService
{
    private const string TableName = "cache_SavedSearchResult";

    private readonly TimeSpan _maxAge = TimeSpan.FromDays(
        configuration.GetValue<double?>("SavedSearches:MaxAgeDays") ?? 7);

    private readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(
        configuration.GetValue<double?>("SavedSearches:SweepIntervalMinutes") ?? 60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_sweepInterval);
        do
        {
            await cacheAdmin.DeleteExpiredFromTableAsync(TableName, _maxAge, stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
