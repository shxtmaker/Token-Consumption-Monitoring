namespace TokenConsumptionMonitoring.Models.Usage;

/// <summary>查询能力：查询方法能够提供的一类数据。能力可以缺失，缺失不代表查询失败。</summary>
public enum CapabilityKind
{
    /// <summary>报告用量（历史 token/请求统计，可带模型明细）。</summary>
    ReportedUsage,

    /// <summary>服务方报告的实际费用。</summary>
    ReportedCost,

    /// <summary>定价快照推导的估算成本（标记 estimated；默认不展示、不告警）。</summary>
    EstimatedCost,

    /// <summary>余额/credits/剩余额度/额度上限（账务状态）。</summary>
    BalanceOrQuota,

    /// <summary>滚动/周期窗口（used/limit/remaining/percent/reset）。</summary>
    RollingWindow,

    /// <summary>请求响应或本地遥测的单次用量（不能冒充历史统计）。</summary>
    ResponseUsage,

    /// <summary>连接/鉴权/模型目录证据（不是用量能力）。</summary>
    ProbeDiagnostic,
}

/// <summary>来源类型：用量事实按来源性质区分，不默认合并成一个数字。</summary>
public enum SourceKind
{
    /// <summary>远程官方组织/账户历史统计。</summary>
    RemoteOfficialStats,

    /// <summary>余额、credits、额度状态。</summary>
    AllowanceOrBalance,

    /// <summary>服务端返回的滚动窗口快照。</summary>
    RollingWindowSnapshot,

    /// <summary>本机客户端记录（session/SQLite/rollout/OTel）。</summary>
    LocalRecord,

    /// <summary>请求链路遥测（单次响应 usage / 本地请求）。</summary>
    ResponseUsage,

    /// <summary>控制台或私有前端来源（高风险，需显式标记）。</summary>
    ConsoleOrPrivateUI,

    /// <summary>连接/鉴权/模型探测。</summary>
    Probe,
}

/// <summary>来源稳定性等级：决定自动选择资格。</summary>
public enum SourceStability
{
    /// <summary>官方稳定，凭据匹配后自动优先。</summary>
    OfficialStable,

    /// <summary>官方但需管理权限（Admin/Management/Enterprise），权限明确匹配才参与。</summary>
    OfficialConditional,

    /// <summary>本地备选记录，官方不可用时按能力回退。</summary>
    LocalFallback,

    /// <summary>私有兼容来源，仅页面显式启用。</summary>
    PrivateCompat,

    /// <summary>仅连接诊断，不参与用量选择。</summary>
    ProbeOnly,
}

/// <summary>方法启用条件。</summary>
public enum MethodEnablement
{
    /// <summary>凭据匹配后始终参与扫描。</summary>
    Always,

    /// <summary>官方条件方法：仅凭据/权限明确匹配时参与。</summary>
    Conditional,

    /// <summary>私有兼容方法：仅页面显式启用。</summary>
    PrivateCompatOnly,
}

/// <summary>凭据类别：普通 key、管理密钥、服务账号、OAuth/控制台会话、本地记录不能互相替代。</summary>
public enum CredentialClass
{
    /// <summary>无需远程凭据（本地记录 / 无 key 页面）。</summary>
    None,

    /// <summary>普通 API key。</summary>
    ApiKey,

    /// <summary>组织管理密钥（OpenAI/Anthropic Admin）。</summary>
    AdminKey,

    /// <summary>团队/账户管理密钥（OpenRouter / xAI / Fireworks）。</summary>
    ManagementKey,

    /// <summary>服务账号密钥（OpenCode Console）。</summary>
    ServiceAccountKey,

    /// <summary>OAuth 会话（设备码 / refresh token）。</summary>
    OAuthSession,

    /// <summary>控制台 WebView2 会话（cookie）。</summary>
    ConsoleSession,

    /// <summary>本机记录（文件/SQLite/CLI 登录态），只读不上传。</summary>
    LocalRecord,
}

/// <summary>候选状态：扫描阶段对某个方法的识别结果。</summary>
public enum CandidateStatus
{
    /// <summary>可查询：已识别来源且具备条件。</summary>
    Available,

    /// <summary>缺少凭据或类型不匹配，需要配置/登录。</summary>
    AuthRequired,

    /// <summary>403 或业务权限不足。</summary>
    Forbidden,

    /// <summary>429 限流，进入冷却。</summary>
    RateLimited,

    /// <summary>超时/连接中断等临时网络错误。</summary>
    NetworkFailure,

    /// <summary>schema 缺失/版本不兼容，等待重扫或方法更新。</summary>
    SchemaMismatch,

    /// <summary>连接可用但无可靠用量来源。</summary>
    NoReliableUsage,

    /// <summary>该方法/端点不被支持。</summary>
    Unsupported,

    /// <summary>最高候选并列，需要人工选择。</summary>
    RequiresSelection,

    /// <summary>候选/快照来自过期数据。</summary>
    Stale,
}

/// <summary>快照状态：界面必须区分无数据、过期、鉴权失败、部分成功。</summary>
public enum SnapshotStatus
{
    /// <summary>报告能力全部成功。</summary>
    Success,

    /// <summary>部分能力成功，其余状态单独表达。</summary>
    SuccessPartial,

    /// <summary>支持某项能力但暂无数据。</summary>
    NoData,

    /// <summary>保留旧快照并标记过期；过期数据不触发新告警。</summary>
    Stale,

    /// <summary>鉴权失败/需要登录。</summary>
    AuthRequired,

    /// <summary>服务端拒绝访问（403），与缺少/失效凭据区分。</summary>
    Forbidden,

    /// <summary>服务端限流（429），等待冷却后重试。</summary>
    RateLimited,

    /// <summary>临时失败（网络/限流），可重试。</summary>
    TemporaryFailure,

    /// <summary>响应结构缺失或版本不兼容。</summary>
    SchemaMismatch,

    /// <summary>永久失败（schema 不支持/无用量来源）。</summary>
    PermanentFailure,

    /// <summary>仅连接/模型探测，无用量能力。</summary>
    ProbeOnly,
}

/// <summary>统计时间粒度。</summary>
public enum Granularity
{
    Unknown,
    PerRequest,
    PerModel,
    PerDay,
    PerHour,
    PerWindow,
}

/// <summary>凭据引用种类：指向受保护凭据的稳定引用，不含秘密。</summary>
public enum CredentialRefKind
{
    /// <summary>无需远程凭据（本地记录 / 无 key 页面）。</summary>
    None,

    /// <summary>按页面身份保存的 API key。</summary>
    PageApiKey,

    /// <summary>命名 API key target（如按供应商设置的凭据）。</summary>
    ApiKeyTarget,

    /// <summary>全局 OAuth 会话（opencode OAuthTokens）。</summary>
    GlobalOAuth,

    /// <summary>全局控制台会话（DeepSeek cookie）。</summary>
    GlobalConsoleSession,

    /// <summary>本地记录（无需 Windows 凭据，只读本机文件）。</summary>
    LocalRecord,
}
