namespace TokenConsumptionMonitoring.Services;

/// <summary>
/// 美元 ↔ microCents 统一换算（1 美元 = 10⁷ microCents）。
/// 供窗口限额（opencode / Command Code）与展示层（WindowState.FormatUsd）共用，避免魔法数字散落。
/// </summary>
public static class Money
{
    /// <summary>每美元对应的 microCents 数。</summary>
    public const long MicroCentsPerUsd = 10_000_000;

    /// <summary>美元 → microCents（负值收敛为 0，四舍五入）。</summary>
    public static long ToMicroCents(double usd) => (long)Math.Round(Math.Max(0, usd) * MicroCentsPerUsd);

    /// <summary>microCents → 美元（null 视为 0）。</summary>
    public static decimal ToUsd(long? microCents) => (microCents ?? 0) / (decimal)MicroCentsPerUsd;
}
