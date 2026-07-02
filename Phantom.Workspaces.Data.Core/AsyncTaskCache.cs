using System.Collections.Concurrent;

namespace Phantom.Workspaces.Data;

/// <summary>
/// A thread-safe cache that stores in-flight <see cref="Task{TValue}"/> results.
/// Concurrent callers for the same key share a single factory invocation.
/// Failed fetches are evicted so the next call can retry.
/// </summary>
public sealed class AsyncTaskCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Lazy<Task<TValue>>> _cache;

    public AsyncTaskCache(IEqualityComparer<TKey>? comparer = null)
        => _cache = new(comparer);

    /// <summary>
    /// Returns the cached task for <paramref name="key"/>, or starts a new fetch via
    /// <paramref name="factory"/> if the key is absent.  The underlying fetch always runs
    /// with <see cref="CancellationToken.None"/> so the shared result is not bound to any
    /// single caller's token.  Pass <paramref name="cancellationToken"/> to cancel only the
    /// caller's wait, not the underlying fetch.
    /// </summary>
    public Task<TValue> GetOrFetchAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> factory,
        CancellationToken cancellationToken = default)
    {
        var lazy = _cache.GetOrAdd(
            key,
            k => new Lazy<Task<TValue>>(() => FetchAndEvictOnFailureAsync(k, factory)));
        return lazy.Value.WaitAsync(cancellationToken);
    }

    private async Task<TValue> FetchAndEvictOnFailureAsync(
        TKey key,
        Func<TKey, CancellationToken, Task<TValue>> factory)
    {
        try
        {
            return await factory(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    public bool TryRemove(TKey key) => _cache.TryRemove(key, out _);

    public void Clear() => _cache.Clear();
}
