using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>
/// 方法级运行时缓存。LastSuccessfulSnapshot 与 LastAttemptResult 分离，
/// 失败尝试只更新诊断，不覆盖最近成功的能力值。
/// </summary>
public sealed class MethodResultCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CachedEntry> _entries = new(StringComparer.Ordinal);

    public sealed record CachedEntry(
        string CacheKey,
        CapabilitySnapshot Snapshot,
        DateTimeOffset CachedAt)
    {
        /// <summary>最近一次成功的快照；Snapshot 保留为兼容的简短访问入口。</summary>
        public CapabilitySnapshot LastSuccessfulSnapshot { get; init; } = Snapshot;

        /// <summary>最近一次查询尝试，失败也会保存。</summary>
        public MethodQueryResult? LastAttemptResult { get; init; }

        public DateTimeOffset LastAttemptAt { get; init; } = CachedAt;

        /// <summary>无成功值时为 false；Snapshot 仅为占位空快照。</summary>
        public bool HasLastSuccessfulSnapshot { get; init; } = true;

        public bool IsStale(TimeSpan ttl) => DateTimeOffset.UtcNow - CachedAt > ttl;
    }

    public static string KeyFor(string pageId, string fingerprint, string methodId, string methodVersion, CapabilityKind kind)
        => $"{pageId}|{fingerprint}|{methodId}|{methodVersion}|{kind}";

    /// <summary>方法级缓存键（page + 指纹 + 方法 id + 版本；能力条目保留在快照内部）。</summary>
    public static string MethodKey(string pageId, string fingerprint, string methodId, string methodVersion)
        => $"{pageId}|{fingerprint}|{methodId}|{methodVersion}";

    public bool TryGet(string key, out CachedEntry entry)
    {
        lock (_lock) return _entries.TryGetValue(key, out entry!);
    }

    /// <summary>记录成功快照，同时更新最近尝试。</summary>
    public void Put(string key, CapabilitySnapshot snapshot, MethodQueryResult? attempt = null)
    {
        lock (_lock)
        {
            var at = attempt?.FetchedAt ?? snapshot.Metadata.FetchedAt;
            _entries[key] = new CachedEntry(key, snapshot, snapshot.Metadata.FetchedAt)
            {
                LastSuccessfulSnapshot = snapshot,
                LastAttemptResult = attempt,
                LastAttemptAt = at,
                HasLastSuccessfulSnapshot = true,
            };
        }
    }

    /// <summary>只记录失败/无成功数据的尝试，不覆盖既有成功快照。</summary>
    public void RecordAttempt(string key, MethodQueryResult attempt)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var previous))
            {
                _entries[key] = previous with
                {
                    LastAttemptResult = attempt,
                    LastAttemptAt = attempt.FetchedAt,
                };
                return;
            }

            var empty = CapabilitySnapshot.Empty("", "");
            _entries[key] = new CachedEntry(key, empty, attempt.FetchedAt)
            {
                LastSuccessfulSnapshot = empty,
                LastAttemptResult = attempt,
                LastAttemptAt = attempt.FetchedAt,
                HasLastSuccessfulSnapshot = false,
            };
        }
    }

    /// <summary>删除某页面全部缓存条目（配置/指纹变化后）。</summary>
    public void InvalidatePage(string pageId)
    {
        lock (_lock)
        {
            var prefix = pageId + "|";
            foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _entries.Remove(key);
        }
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }
}
