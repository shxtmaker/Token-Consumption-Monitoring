using TokenConsumptionMonitoring.Services;

namespace TokenConsumptionMonitoring.Models.Usage;

/// <summary>
/// 查询方法描述：声明稳定的方法标识、凭据类别、可提供能力与来源等级。
/// 方法按“来源、能力、凭据范围”拆到可独立回退的最小单元；套餐名称不参与。
/// </summary>
public sealed record QueryMethodDescriptor(
    string MethodId,
    SourceKind SourceKind,
    CredentialClass CredentialClass,
    IReadOnlyList<CapabilityKind> Capabilities,
    SourceStability Stability,
    MethodEnablement Enablement,
    int DefaultPriority,
    string ImplementationVersion)
{
    /// <summary>构造去重后的能力集合（语义为集合；IReadOnlyList 以确保可序列化）。</summary>
    public static IReadOnlyList<CapabilityKind> CapabilitiesOf(params CapabilityKind[] kinds)
        => kinds.Distinct().ToList();
}

/// <summary>
/// 来源身份：判断两个数据结果是否来自同一统计来源的稳定标识。
/// 模型名、套餐名、页面名不能单独构成来源身份。
/// </summary>
public sealed record SourceIdentity(
    string Provider,
    string Scope,
    string MethodId,
    string Endpoint,
    string? Account = null)
{
    /// <summary>稳定去重/告警去重键（不包含秘密）。</summary>
    public string StableKey => $"{Provider}|{Scope}|{MethodId}|{Endpoint}".ToLowerInvariant();
}

/// <summary>凭据范围：凭据可访问的身份与资源边界。</summary>
public sealed record CredentialScope(
    CredentialClass Class,
    string? Provider = null,
    string? Account = null,
    string? Organization = null,
    string? Team = null)
{
    public static readonly CredentialScope None = new(CredentialClass.None);

    /// <summary>脱敏描述（不含凭据原文）。</summary>
    public string Describe() => (Class, Provider, Account, Organization, Team) switch
    {
        (CredentialClass.None, _, _, _, _) => "本地/无凭据",
        (var c, var p, { } a, _, _) => $"{EnumLabel(c)} · {p} · {a}",
        (var c, var p, _, { } o, _) => $"{EnumLabel(c)} · {p} · org {o}",
        (var c, var p, _, _, { } t) => $"{EnumLabel(c)} · {p} · team {t}",
        (var c, var p, _, _, _) => $"{EnumLabel(c)} · {p}",
    };

    private static string EnumLabel(CredentialClass c) => c switch
    {
        CredentialClass.ApiKey => "API key",
        CredentialClass.AdminKey => "Admin key",
        CredentialClass.ManagementKey => "Management key",
        CredentialClass.ServiceAccountKey => "Service-account key",
        CredentialClass.OAuthSession => "OAuth 会话",
        CredentialClass.ConsoleSession => "控制台会话",
        CredentialClass.LocalRecord => "本地记录",
        _ => "无凭据",
    };
}

/// <summary>统计覆盖：数据事实覆盖的账号范围、时间区间、时间粒度。</summary>
public sealed record Coverage(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    Granularity Granularity = Granularity.Unknown,
    string? Scope = null)
{
    public static readonly Coverage Unknown = new(null, null);
}

/// <summary>检测证据：扫描阶段支撑候选判定的可解释信息（必须脱敏）。</summary>
public sealed record DetectionEvidence(
    string Kind,
    string Detail,
    DateTimeOffset At,
    int? HttpStatus = null)
{
    public static DetectionEvidence Http(int status, string detail = "") =>
        new("http_status", string.IsNullOrEmpty(detail) ? $"HTTP {status}" : $"HTTP {status} · {detail}", DateTimeOffset.UtcNow, status);

    public static DetectionEvidence Field(string field) =>
        new("response_field", $"响应字段 {field}", DateTimeOffset.UtcNow);

    public static DetectionEvidence Auth(string detail) =>
        new("auth", detail, DateTimeOffset.UtcNow);

    public static DetectionEvidence LocalSchema(string detail) =>
        new("local_schema", detail, DateTimeOffset.UtcNow);

    public static DetectionEvidence UrlHint(string detail) =>
        new("url_hint", detail, DateTimeOffset.UtcNow);
}

/// <summary>失败信息（脱敏；允许方法 ID、状态、错误类别与耗时，禁止秘密与原始响应体）。</summary>
public sealed record FailureInfo(
    CandidateStatus Status,
    string Reason,
    DateTimeOffset At,
    string? Detail = null);

/// <summary>候选：扫描阶段对某个查询方法的识别结果，尚未成为当前方法。</summary>
public sealed record MethodCandidate(
    QueryMethodDescriptor Method,
    CandidateStatus Status,
    int Confidence,
    SourceIdentity? Source,
    CredentialScope? CredentialScope,
    Coverage Coverage,
    IReadOnlyList<DetectionEvidence> Evidence,
    FailureInfo? Failure)
{
    public bool IsAvailable => Status == CandidateStatus.Available;
}

/// <summary>页面配置中指向受保护凭据的稳定引用（不含密钥或会话原文）。</summary>
public sealed record CredentialReference(CredentialRefKind Kind, string? Target = null)
{
    public static CredentialReference None { get; } = new(CredentialRefKind.None);

    /// <summary>按页面身份创建 API key 引用。</summary>
    public static CredentialReference PageApiKey(string pageId) =>
        new(CredentialRefKind.PageApiKey, AppIdentity.ApiKeyTarget(pageId));

    public static CredentialReference ApiKeyTarget(string target) => new(CredentialRefKind.ApiKeyTarget, target);

    /// <summary>全局 OAuth 会话（opencode OAuthTokens）。</summary>
    public static CredentialReference GlobalOAuth { get; } = new(CredentialRefKind.GlobalOAuth, CredentialStore.OAuthTarget);

    public static CredentialReference GlobalConsoleSession(string target) =>
        new(CredentialRefKind.GlobalConsoleSession, target);

    public static CredentialReference LocalRecord { get; } = new(CredentialRefKind.LocalRecord);

    /// <summary>解析出的凭据类别（供方法与候选过滤使用；不读取秘密原文）。</summary>
    public CredentialClass ResolveClass() => Kind switch
    {
        CredentialRefKind.None => CredentialClass.None,
        CredentialRefKind.PageApiKey => CredentialClass.ApiKey,
        CredentialRefKind.ApiKeyTarget => CredentialClass.ApiKey,
        CredentialRefKind.GlobalOAuth => CredentialClass.OAuthSession,
        CredentialRefKind.GlobalConsoleSession => CredentialClass.ConsoleSession,
        CredentialRefKind.LocalRecord => CredentialClass.LocalRecord,
        _ => CredentialClass.None,
    };
}
