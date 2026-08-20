using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>
/// 运行时方法结果缓存（内存）：键 = PageId + 配置指纹 + 方法 id + 方法版本 + 能力。
/// 名称/顺序/显示设置变化不失效；端点/协议/凭据引用/扫描输入/本地来源状态/方法版本变化使相关缓存失效。
/// </summary>
public sealed class MethodResultCache
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CachedEntry> _entries = new(StringComparer.Ordinal);

    public sealed record CachedEntry(string CacheKey, CapabilitySnapshot Snapshot, DateTimeOffset CachedAt)
    {
        /// <summary>过期仅标记显示，不触发新告警。</summary>
        public bool IsStale(TimeSpan ttl) => DateTimeOffset.UtcNow - CachedAt > ttl;
    }

    public static string KeyFor(string pageId, string fingerprint, string methodId, string methodVersion, CapabilityKind kind)
        => $"{pageId}|{fingerprint}|{methodId}|{methodVersion}|{kind}";

    /// <summary>方法级缓存键（page + 指纹 + 方法 id + 版本；能力维度保留在快照内部）。</summary>
    public static string MethodKey(string pageId, string fingerprint, string methodId, string methodVersion)
        => $"{pageId}|{fingerprint}|{methodId}|{methodVersion}";

    public bool TryGet(string key, out CachedEntry entry)
    {
        lock (_lock) return _entries.TryGetValue(key, out entry!);
    }

    public void Put(string key, CapabilitySnapshot snapshot)
    {
        lock (_lock) _entries[key] = new CachedEntry(key, snapshot, DateTimeOffset.UtcNow);
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
