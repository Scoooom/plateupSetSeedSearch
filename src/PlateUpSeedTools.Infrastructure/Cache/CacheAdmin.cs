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

    /// <summary>
    /// Deletes rows older than maxAge from exactly one named cache_* table - unlike the old,
    /// generic every-table DeleteExpiredAsync(TimeSpan) removed in commit 38990c2 (specifically
    /// because the permanent share-link cache shouldn't expire), this is parameterized by table
    /// name so a caller can scope expiry to just one cache type. tableName must match the
    /// `cache_{typeof(TValue).Name}` convention SqliteCacheStore&lt;T&gt; uses internally - the two
    /// aren't wired to a shared constant since there's currently only one caller
    /// (SavedSearchExpiryMonitor).
    /// </summary>
    public async Task<int> DeleteExpiredFromTableAsync(string tableName, TimeSpan maxAge, CancellationToken ct = default)
    {
        var cutoff = (DateTime.UtcNow - maxAge).ToString("O");
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var delete = connection.CreateCommand();
        delete.CommandText = $"DELETE FROM {tableName} WHERE created_utc < $cutoff";
        delete.Parameters.AddWithValue("$cutoff", cutoff);
        return await delete.ExecuteNonQueryAsync(ct);
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
