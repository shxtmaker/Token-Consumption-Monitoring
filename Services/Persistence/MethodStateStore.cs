using System.Text.Json;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Persistence;

/// <summary>
/// 页面扫描状态：候选链、能力槽来源选择、检测指纹与最近扫描时间。
/// 与用户配置分离、可重建；绑定页面身份与配置指纹；失效时丢弃并重新扫描，不删除用户配置或凭据。
/// </summary>
public sealed class PageMethodState
{
    public string PageId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<MethodCandidate> Candidates { get; set; } = new();
    public Dictionary<CapabilityKind, string> SelectedMethodIdsByCapability { get; set; } = new();

    /// <summary>诊断兼容字段，仅表示能力计划中的第一个方法，不再作为唯一选择依据。</summary>
    public string? SelectedMethodId { get; set; }
    public CandidateStatus SelectionStatus { get; set; }
}

/// <summary>方法状态存储：%APPDATA%\TokenConsumptionMonitoring\runtime\{pageId}.json（原子写入）。</summary>
public sealed class MethodStateStore
{
    private readonly string _directoryPath;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly object _lock = new();

    public MethodStateStore(string? baseDirectory = null)
        => _directoryPath = System.IO.Path.Combine(
            baseDirectory ?? Services.SettingsStore.DataDirectory, "runtime");

    private string PathFor(string pageId) => System.IO.Path.Combine(_directoryPath, $"{pageId}.json");

    public PageMethodState? Load(string pageId)
    {
        lock (_lock)
        {
            try
            {
                var path = PathFor(pageId);
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<PageMethodState>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                // 方法缓存损坏/版本未知：只丢弃运行时缓存，不删除页面与凭据
                Services.Logger.Log($"method state {pageId}: 读取失败，丢弃运行时缓存");
                Services.Logger.LogException("method state load", ex);
                return null;
            }
        }
    }

    public void Save(PageMethodState state)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_directoryPath);
                var path = PathFor(state.PageId);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Services.Logger.LogException("method state save", ex);
            }
        }
    }

    public void Delete(string pageId)
    {
        lock (_lock)
        {
            try { if (File.Exists(PathFor(pageId))) File.Delete(PathFor(pageId)); } catch { }
        }
    }
}
