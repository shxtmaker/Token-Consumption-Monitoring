using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.Services.Runtime;

/// <summary>
/// 重试策略：401/403/凭据类型不匹配、schema 错误不自动重试；
/// 429 读取冷却（本轮不重试，标记 stale）；超时/连接中断最多两次短退避重试。
/// 不得用 0 值代替失败，不得因回退成功而隐藏主来源失败原因。
/// </summary>
public static class RetryPolicy
{
    /// <summary>瞬态网络错误的最大重试次数。</summary>
    public const int MaxTransientRetries = 2;

    /// <summary>连续失败超过该次数后触发重新扫描。</summary>
    public const int RescanAfterConsecutiveFailures = 3;

    /// <summary>是否应重试（瞬态网络错误且未耗尽重试次数）。</summary>
    public static bool ShouldRetry(CandidateStatus status, int attempt)
        => status == CandidateStatus.NetworkFailure && attempt < MaxTransientRetries;

    /// <summary>终态：不自动重试，等待配置/登录变化或重新扫描。</summary>
    public static bool IsTerminal(CandidateStatus status)
        => status is CandidateStatus.AuthRequired
            or CandidateStatus.Forbidden
            or CandidateStatus.SchemaMismatch
            or CandidateStatus.Unsupported;

    /// <summary>429 限流：进入冷却而不是立即重试。</summary>
    public static bool IsRateLimited(CandidateStatus status) => status == CandidateStatus.RateLimited;

    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(2) * (attempt + 1);
}
