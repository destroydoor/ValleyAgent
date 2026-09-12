using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace ValleyAgent.Performance;

/// <summary>
///     非泛型缓存条目接口，消除反射调用。
/// </summary>
internal interface ICacheEntry
{
    public bool IsExpired { get; }
    public long LastAccessTick { get; }
}

internal sealed class CacheEntry<T> : ICacheEntry
{
    public long AccessCount;

    public CacheEntry(T value, TimeSpan ttl)
    {
        Value = value;
        CreatedAt = DateTime.UtcNow;
        Ttl = ttl;
        LastAccessTick = DateTime.UtcNow.Ticks;
        AccessCount = 0;
    }

    public T Value { get; }
    public DateTime CreatedAt { get; }
    public TimeSpan Ttl { get; }
    public long LastAccessTick { get; set; }

    public bool IsExpired
    {
        get => DateTime.UtcNow - CreatedAt > Ttl;
    }

    bool ICacheEntry.IsExpired
    {
        get => DateTime.UtcNow - CreatedAt > Ttl;
    }

    long ICacheEntry.LastAccessTick
    {
        get => LastAccessTick;
    }
}

public class CacheStats
{
    public int EntryCount { get; set; }
    public long Hits { get; set; }
    public long Misses { get; set; }
    public long Evictions { get; set; }
    public double HitRatio { get; set; }
    public int ExpiredEntries { get; set; }
}

public class CacheManager
{
    private const int DefaultMaxEntries = 1000;
    private const int CleanupInterval = 100;

    private readonly ConcurrentDictionary<string, object> _cache;
    private readonly object _factoryLock = new();
    private readonly int _maxEntries;
    private long _evictions;
    private long _hits;
    private long _misses;
    private int _writeCount;

    public CacheManager(int maxEntries = DefaultMaxEntries)
    {
        _maxEntries = maxEntries > 0 ? maxEntries : DefaultMaxEntries;
        _cache = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    public T GetOrCreate<T>(string key, Func<T> factory, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be empty.", nameof(key));
        }

        // 使用 ThrowIfNull 替代显式 if-null-throw 模式
        ArgumentNullException.ThrowIfNull(factory);

        if (_cache.TryGetValue(key, out var existingEntry))
        {
            if (existingEntry is CacheEntry<T> typedEntry && !typedEntry.IsExpired)
            {
                _ = Interlocked.Increment(ref _hits);
                typedEntry.LastAccessTick = DateTime.UtcNow.Ticks;
                _ = Interlocked.Increment(ref typedEntry.AccessCount);
                return typedEntry.Value;
            }

            _ = _cache.TryRemove(key, out _);
            _ = Interlocked.Increment(ref _evictions);
        }

        _ = Interlocked.Increment(ref _misses);
        MaybeEvictLru();

        // 工厂调用加锁，防止多线程重复执行（LLM 调用等昂贵操作）
        lock (_factoryLock)
        {
            // Double-check after acquiring lock
            if (_cache.TryGetValue(key, out existingEntry) && existingEntry is CacheEntry<T> dcheck &&
                !dcheck.IsExpired)
            {
                _ = Interlocked.Increment(ref _hits);
                return dcheck.Value;
            }

            var newValue = factory();
            var newEntry = new CacheEntry<T>(newValue, ttl);
            _ = _cache.AddOrUpdate(key, newEntry, (_, __) => newEntry);

            if (Interlocked.Increment(ref _writeCount) % CleanupInterval == 0)
            {
                CleanupExpired();
            }

            return newValue;
        }
    }

    public T? Get<T>(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return default;
        }

        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry is CacheEntry<T> typedEntry && !typedEntry.IsExpired)
            {
                _ = Interlocked.Increment(ref _hits);
                typedEntry.LastAccessTick = DateTime.UtcNow.Ticks;
                _ = Interlocked.Increment(ref typedEntry.AccessCount);
                return typedEntry.Value;
            }

            _ = _cache.TryRemove(key, out _);
            _ = Interlocked.Increment(ref _evictions);
        }

        _ = Interlocked.Increment(ref _misses);
        return default;
    }

    public void Set<T>(string key, T value, TimeSpan ttl)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Cache key cannot be empty.", nameof(key));
        }

        MaybeEvictLru();
        var entry = new CacheEntry<T>(value, ttl);
        _ = _cache.AddOrUpdate(key, entry, (_, __) => entry);

        if (Interlocked.Increment(ref _writeCount) % CleanupInterval == 0)
        {
            CleanupExpired();
        }
    }

    public bool Invalidate(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var removed = _cache.TryRemove(key, out _);
        if (removed)
        {
            _ = Interlocked.Increment(ref _evictions);
        }

        return removed;
    }

    public int InvalidatePattern(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return 0;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            return 0;
        }

        var keysToRemove = _cache.Keys.Where(k => regex.IsMatch(k)).ToList();
        var removed = 0;
        foreach (var key in keysToRemove)
        {
            if (_cache.TryRemove(key, out _))
            {
                removed++;
                _ = Interlocked.Increment(ref _evictions);
            }
        }

        return removed;
    }

    public CacheStats GetStats()
    {
        var hits = Interlocked.Read(ref _hits);
        var misses = Interlocked.Read(ref _misses);
        var total = hits + misses;

        // 使用 ICacheEntry 接口检查过期，替代反射
        var expiredCount = _cache.Values
            .Count(v => v is ICacheEntry ice && ice.IsExpired);

        return new CacheStats
        {
            EntryCount = _cache.Count,
            Hits = hits,
            Misses = misses,
            Evictions = Interlocked.Read(ref _evictions),
            HitRatio = total > 0 ? (double)hits / total : 0.0,
            ExpiredEntries = expiredCount
        };
    }

    public void Clear()
    {
        var count = _cache.Count;
        _cache.Clear();
        _ = Interlocked.Add(ref _evictions, count);
    }

    public void CleanupExpired()
    {
        var expiredKeys = _cache
            .Where(kvp => kvp.Value is ICacheEntry ice && ice.IsExpired)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            if (_cache.TryRemove(key, out _))
            {
                _ = Interlocked.Increment(ref _evictions);
            }
        }
    }

    private void MaybeEvictLru()
    {
        if (_cache.Count < _maxEntries)
        {
            return;
        }

        string? lruKey = null;
        var oldestTicks = long.MaxValue;

        foreach (var kvp in _cache)
        {
            if (kvp.Value is ICacheEntry ice)
            {
                if (ice.LastAccessTick < oldestTicks)
                {
                    oldestTicks = ice.LastAccessTick;
                    lruKey = kvp.Key;
                }
            }
        }

        if (lruKey != null && _cache.TryRemove(lruKey, out _))
        {
            _ = Interlocked.Increment(ref _evictions);
        }
    }
}