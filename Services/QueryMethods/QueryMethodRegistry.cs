using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

/// <summary>
/// 查询方法注册表：枚举全部已注册方法（远程 + 本地记录）。
/// 每个方法在 ScanAsync 内自行判定启用条件（私有兼容方法仅页面显式启用），
/// 注册表不按供应商或套餐名过滤。
/// </summary>
public sealed class QueryMethodRegistry
{
    private readonly List<IQueryMethod> _methods;

    public QueryMethodRegistry(IEnumerable<IQueryMethod> methods)
        => _methods = methods.OrderBy(m => m.Describe().DefaultPriority).ToList();

    /// <summary>全部方法（按固定优先级排序）。</summary>
    public IReadOnlyList<IQueryMethod> Methods => _methods;

    /// <summary>全部方法描述（诊断/目录展示用）。</summary>
    public IReadOnlyList<QueryMethodDescriptor> Descriptors => _methods.Select(m => m.Describe()).ToList();

    public IQueryMethod? Find(string methodId) => _methods.FirstOrDefault(m => m.Describe().MethodId == methodId);

    /// <summary>装配默认注册表。未实现的方法不注册为候选。</summary>
    public static QueryMethodRegistry BuildDefault(
        OpenCodeUsageClient opencode,
        OpenCodeAuthService openCodeAuth,
        DeepSeekSessionService deepSeekSession,
        DeepSeekUsageClient deepSeekUsage,
        ZCodeUsageService zcode,
        CommandCodeUsageClient commandCode)
    {
        var methods = new List<IQueryMethod>
        {
            new EndpointProbeMethod(openCodeAuth, deepSeekSession),
            new OpenCodeRollingWindowApiKeyMethod(opencode),
            new OpenCodeAllowanceOAuthMethod(opencode, openCodeAuth),
            new DeepSeekBalanceApiKeyMethod(),
            new CommandCodeAllowanceWindowMethod(commandCode),
            new DeepSeekConsoleUsageMethod(deepSeekSession, deepSeekUsage),
            new LocalZCodeUsageMethod(zcode),
        };
        return new QueryMethodRegistry(methods);
    }
}
