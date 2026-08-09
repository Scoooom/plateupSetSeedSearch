using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace PlateUpSeedTools.Infrastructure.Cache;

/// <summary>
/// A single-table SQLite key/value cache. The table name is derived from TValue's type name so
/// multiple ICacheStore&lt;T&gt; registrations for different T can safely share one SQLite file
/// (e.g. the future search-results cache alongside this layout cache) without key collisions.
/// </summary>
public sealed class SqliteCacheStore<TValue> : ICacheStore<TValue>
{
    private readonly string _connectionString;
    private readonly string _tableName;

    public SqliteCacheStore(IOptions<CacheOptions> options)
    {
        _connectionString = $"Data Source={options.Value.SqlitePath}";
        _tableName = $"cache_{typeof(TValue).Name}";
        EnsureTable();
    }

    private void EnsureTable()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {_tableName} (
                cache_key TEXT PRIMARY KEY,
                value_json TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public async Task<TValue?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value_json FROM {_tableName} WHERE cache_key = $key";
        command.Parameters.AddWithValue("$key", key);

        var result = await command.ExecuteScalarAsync(ct);
        return result is string json ? JsonSerializer.Deserialize<TValue>(json) : default;
    }

    public async Task SetAsync(string key, TValue value, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {_tableName} (cache_key, value_json, created_utc) VALUES ($key, $json, $created)
            ON CONFLICT(cache_key) DO UPDATE SET value_json = excluded.value_json, created_utc = excluded.created_utc;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value));
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_tableName} WHERE cache_key = $key";
        command.Parameters.AddWithValue("$key", key);
        var rows = await command.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_tableName}";
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {_tableName}";
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }
}
