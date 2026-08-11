using Microsoft.Data.Sqlite;

namespace PlateUpSeedTools.Infrastructure.Cache;

/// <summary>
/// Type-agnostic cache administration (clear/count across every `cache_*` table in the SQLite file),
/// for the CLI - which shouldn't need to reference every value type (LayoutResponse etc.) each
/// SqliteCacheStore&lt;T&gt; is instantiated with just to clear the cache.
/// </summary>
public sealed class CacheAdmin(CacheOptions options)
{
    private string ConnectionString => $"Data Source={options.SqlitePath}";

    public async Task<int> ClearAllAsync(CancellationToken ct = default)
    {
        var total = 0;
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        foreach (var table in await GetCacheTablesAsync(connection, ct))
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table}";
            total += Convert.ToInt32(await count.ExecuteScalarAsync(ct));

            await using var delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM {table}";
            await delete.ExecuteNonQueryAsync(ct);
        }

        return total;
    }

    /// <summary>
    /// Deletes rows older than <paramref name="maxAge"/> from every `cache_*` table, using the
    /// `created_utc` column SqliteCacheStore already writes on every insert but (until this) never
    /// read back. One generic sweep covers every cache uniformly (layout previews, shared search
    /// results, Explorer preloads, ...) rather than each cache type needing its own expiry logic -
    /// see SearchJobHeartbeatMonitor.cs and Services/CacheExpiryMonitor.cs for the periodic caller.
    /// </summary>
    public async Task DeleteExpiredAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        var cutoff = (DateTime.UtcNow - maxAge).ToString("O");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        foreach (var table in await GetCacheTablesAsync(connection, ct))
        {
            await using var delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM {table} WHERE created_utc < $cutoff";
            delete.Parameters.AddWithValue("$cutoff", cutoff);
            await delete.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<IReadOnlyDictionary<string, int>> GetStatsAsync(CancellationToken ct = default)
    {
        var stats = new Dictionary<string, int>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);

        foreach (var table in await GetCacheTablesAsync(connection, ct))
        {
            await using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table}";
            stats[table] = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        }

        return stats;
    }

    private static async Task<List<string>> GetCacheTablesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'cache_%'";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
