namespace PlateUpSeedTools.Infrastructure.Cache;

public interface ICacheStore<TValue>
{
    Task<TValue?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, TValue value, CancellationToken ct = default);

    Task<bool> RemoveAsync(string key, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);

    Task<int> CountAsync(CancellationToken ct = default);
}
