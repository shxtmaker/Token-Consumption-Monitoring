using TokenConsumptionMonitoring.Models.Usage;

namespace TokenConsumptionMonitoring.UI.Diagnostics;

/// <summary>余额或额度行：名称、数值和补充说明分别布局。</summary>
public sealed record BalanceRowViewModel(string Name, string Value, string? Detail = null)
{
    public string Label => $"{Name} {Value}" + (Detail is null ? "" : $" · {Detail}");

    public static BalanceRowViewModel? FromBalance(BalanceQuotaValue balance) =>
        balance.Balance is { } b ? new("余额", $"{b:0.####} {balance.Currency}")
        : balance.Remaining is { } r && balance.Limit is { } l
            ? new("剩余", $"{r:0.####} / {l:0.####} {balance.Currency}", balance.Coverage.Scope ?? "")
        : balance.Remaining is { } remaining ? new("剩余", $"{remaining:0.####} {balance.Currency}")
        : balance.Limit is { } limit ? new("额度上限", $"{limit:0.####} {balance.Currency}", "剩余未知")
        : null;
}
