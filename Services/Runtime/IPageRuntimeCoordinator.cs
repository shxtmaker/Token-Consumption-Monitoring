using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>一次刷新/重扫的结果：能力快照 +（若本轮扫描）候选链 + 失败信息 + 需要鉴权的凭据类别。</summary>
public sealed record PageRuntimeResult(
    string PageId,
    CapabilitySnapshot Snapshot,
    ScanReport? Scan,
    FailureInfo? Failure,
    CredentialClass? AuthCredentialClass = null);

/// <summary>
/// 页面运行时协调入口 seam：PageEngine 只管理页面列表、活动页、生命周期与 UI dispatcher，
/// 候选扫描、方法查询、按能力回退、缓存和状态生成全部委托给该模块。测试可直接替换实现。
/// </summary>
public interface IPageRuntimeCoordinator
{
    Task<PageRuntimeResult> RefreshAsync(PageConfigRecord page, RefreshReason reason, CancellationToken cancellationToken);

    Task<ScanReport> RescanAsync(PageConfigRecord page, ScanReason reason, CancellationToken cancellationToken);

    /// <summary>临时覆盖自动选择（只作用于当前运行时；配置变化/重扫后恢复自动选择）。</summary>
    void SetTemporaryOverride(string pageId, string? methodId);
}
