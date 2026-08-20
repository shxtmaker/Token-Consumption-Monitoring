using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// 查询方法接口：所有远程方法和本地方法通过同一接口接入。
/// 方法独立于供应商名称和套餐名称；一个方法是可独立探测、授权、失败和回退的最小来源能力单元。
/// </summary>
public interface IQueryMethod
{
    /// <summary>描述阶段：声明稳定的方法标识、凭据类别与可提供能力。</summary>
    QueryMethodDescriptor Describe();

    /// <summary>扫描阶段：只读探测来源，返回支持状态、证据、覆盖范围与明确失败状态。</summary>
    Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken cancellationToken);

    /// <summary>查询阶段：按已选候选读取能力化用量快照。</summary>
    Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken cancellationToken);
}
