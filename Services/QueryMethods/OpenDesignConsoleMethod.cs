using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.Services.QueryMethods;

public interface IOpenDesignConsoleReader
{
    Task<JsonElement> ReadAsync(PageConfigRecord page, bool windows, CancellationToken ct);
}

/// <summary>工作空间钱包和 Design Plan 分别查询，故障与数据范围互相独立。</summary>
public sealed class OpenDesignConsoleMethod(IOpenDesignConsoleReader? reader, bool windows = false) : IQueryMethod
{
    public const string BalanceMethodId = "opendesign.balance.console";
    public const string WindowsMethodId = "opendesign.windows.console";
    public const string ApiOrigin = "https://amr-api.open-design.ai";
    private readonly QueryMethodDescriptor _descriptor = new(windows ? WindowsMethodId : BalanceMethodId,
        SourceKind.ConsoleOrPrivateUI, CredentialClass.ConsoleSession,
        QueryMethodDescriptor.CapabilitiesOf(windows ? CapabilityKind.RollingWindow : CapabilityKind.BalanceOrQuota),
        SourceStability.PrivateCompat, MethodEnablement.PrivateCompatOnly, 30, "1.0.0");

    public static bool TryGetWorkspace(string url, out string workspace)
    {
        workspace = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsConsoleOrigin(url)
            || uri.AbsolutePath.TrimEnd('/') != "/cloud/dashboard" || uri.Fragment.Length != 0) return false;
        var parts = uri.Query.TrimStart('?').Split('&');
        // 仅接受一个明确的工作空间参数；拒绝重复参数和带操作副作用的控制台链接。
        if (parts.Length != 1 || !parts[0].StartsWith("workspaceId=", StringComparison.Ordinal)) return false;
        workspace = Uri.UnescapeDataString(parts[0][12..]);
        if (Regex.IsMatch(workspace, @"\A[a-zA-Z0-9_-]{1,128}\z")) return true;
        workspace = "";
        return false;
    }

    public static bool IsConsoleOrigin(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.IdnHost.Equals("open-design.ai", StringComparison.OrdinalIgnoreCase);

    public static string DashboardUrl(string workspace) => "https://open-design.ai/cloud/dashboard?workspaceId=" + Uri.EscapeDataString(workspace);
    public static string ResourcePath(string workspace, bool windows) => "/api/v1/workspaces/" + Uri.EscapeDataString(workspace)
        + (windows ? "/billing/coding-plan-usage" : "/wallet/balance");
    public QueryMethodDescriptor Describe() => _descriptor;

    public async Task<MethodCandidate> ScanAsync(PageConfigRecord page, ScanContext context, CancellationToken ct)
    {
        var result = await QueryAsync(page, null!, ct);
        if (result.Status != SnapshotStatus.Success)
            return MethodSupport.NotAvailable(_descriptor, result.Failure!.Status, result.Failure.Reason)
                with { Failure = result.Failure };
        var value = result.Capabilities[0];
        return MethodSupport.Available(_descriptor, value.CredentialScope, value.Coverage,
            [DetectionEvidence.Field(windows ? "windows / limitCredits / remainingCredits / resetsAt" : "balanceUsd")], value.Source, 90);
    }

    public async Task<MethodQueryResult> QueryAsync(PageConfigRecord page, MethodCandidate candidate, CancellationToken ct)
    {
        try
        {
            if (!TryGetWorkspace(page.BaseUrl, out var workspace) || !page.EnabledCompatibilityMethods.Contains(_descriptor.MethodId))
                throw new QueryTransportException(CandidateStatus.Unsupported, "OpenDesign 需要启用兼容查询，并填写含 workspaceId 的套餐与余额链接");
            if (page.CredentialRef.ResolveClass() != CredentialClass.ConsoleSession || reader is null)
                throw new QueryTransportException(CandidateStatus.AuthRequired, "请选择 OpenDesign 控制台登录并完成登录");
            var response = await reader.ReadAsync(page, windows, ct);
            return Parse(response, page.Id, workspace, windows, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is QueryTransportException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException or TimeoutException or OperationCanceledException)
        {
            var failure = ex as QueryTransportException ?? new QueryTransportException(
                ex is TimeoutException or OperationCanceledException ? CandidateStatus.NetworkFailure : CandidateStatus.SchemaMismatch,
                ex is TimeoutException or OperationCanceledException ? "OpenDesign 读取超时" : "OpenDesign 响应结构不匹配");
            return MethodQueryResult.Empty(QueryFailureClassifier.SnapshotStatusOf(failure.Status), failure.Message)
                with { Failure = new(failure.Status, failure.Message, DateTimeOffset.UtcNow, RetryAt: failure.RetryAt) };
        }
    }

    internal static MethodQueryResult Parse(JsonElement observation, string pageId, string workspace, bool windows, DateTimeOffset now)
    {
        var status = observation.GetProperty("status").GetString();
        if (status != "ready")
        {
            var code = status switch
            {
                "auth" => CandidateStatus.AuthRequired, "forbidden" => CandidateStatus.Forbidden,
                "rate" => CandidateStatus.RateLimited, "network" => CandidateStatus.NetworkFailure,
                _ => CandidateStatus.SchemaMismatch,
            };
            throw new QueryTransportException(code, status switch
            {
                "auth" => "OpenDesign 会话尚未登录或已失效", "forbidden" => "没有权限访问此 OpenDesign 工作空间",
                "rate" => "OpenDesign 请求过于频繁，请稍后重试", "network" => "OpenDesign 网络查询失败",
                _ => "OpenDesign 响应结构不匹配",
            }, retryAt: code == CandidateStatus.RateLimited ? now.AddMinutes(1) : null);
        }
        var context = observation.GetProperty("context");
        if (context.GetProperty("workspaceId").GetString() != workspace)
            throw new QueryTransportException(CandidateStatus.Forbidden, "OpenDesign 返回的工作空间与页面配置不一致");
        var member = context.GetProperty("workspaceMemberId").GetString();
        if (string.IsNullOrWhiteSpace(member)) throw Schema();
        var body = observation.GetProperty("body");
        if (body.TryGetProperty("workspaceId", out var bodyWorkspace) && bodyWorkspace.GetString() != workspace)
            throw new QueryTransportException(CandidateStatus.Forbidden, "OpenDesign 数据属于其他工作空间");
        var method = windows ? WindowsMethodId : BalanceMethodId;
        var source = new SourceIdentity("opendesign", $"console:page:{pageId}:workspace:{workspace}:member:{member}", method,
            ApiOrigin + ResourcePath(workspace, windows), workspace);
        var scope = new CredentialScope(CredentialClass.ConsoleSession, "opendesign", member, Team: workspace);
        var coverage = new Coverage(null, null, windows ? Granularity.PerWindow : Granularity.Unknown,
            windows ? "工作空间成员 Design Plan" : "工作空间钱包");
        var values = new List<CapabilityValue>();
        if (!windows)
        {
            var balance = Number(body.GetProperty("balanceUsd"));
            values.Add(new BalanceQuotaValue(CapabilityKind.BalanceOrQuota, source, scope, coverage, now, 0.9, true, false,
                balance, null, null, null, "USD", "USD", now.AddMinutes(5)));
        }
        else
        {
            if (!body.GetProperty("eligible").GetBoolean())
                throw new QueryTransportException(CandidateStatus.NoReliableUsage, "此 OpenDesign 工作空间成员暂无 Design Plan 额度");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var window in body.GetProperty("windows").EnumerateArray())
            {
                var id = window.GetProperty("policyId").GetString();
                var duration = window.GetProperty("durationSeconds").GetInt64();
                var limit = Number(window.GetProperty("limitCredits"));
                var remaining = Number(window.GetProperty("remainingCredits"));
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id) || duration <= 0 || limit <= 0) throw Schema();
                DateTimeOffset? resets = null;
                if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind != JsonValueKind.Null)
                {
                    if (!DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsed)) throw Schema();
                    resets = parsed;
                }
                var name = duration % 86400 == 0 ? $"{duration / 86400} 天额度"
                    : duration % 3600 == 0 ? $"{duration / 3600} 小时额度" : $"{duration} 秒额度";
                // 与控制台保持一致：超额使用或余额加成只夹紧百分比，不改变原始钱包金额。
                var percent = (int)Math.Round((1 - Math.Clamp(remaining, 0, limit) / limit) * 100, MidpointRounding.AwayFromZero);
                values.Add(new RollingWindowValue(CapabilityKind.RollingWindow, source, scope, coverage, now, 0.9, true, false,
                    id, name, null, null, null, null, percent, resets, "percent",
                    resets is { } end && end > now && end < now.AddMinutes(5) ? end : now.AddMinutes(5)));
            }
            if (values.Count == 0) throw new QueryTransportException(CandidateStatus.NoReliableUsage, "OpenDesign 暂无额度窗口");
        }
        return new(values, SnapshotStatus.Success, null, now);
    }

    private static decimal Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)) return number;
        throw Schema();
    }
    private static QueryTransportException Schema() => new(CandidateStatus.SchemaMismatch, "OpenDesign 金额或额度窗口字段无效");
}
