using System.ComponentModel;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QuotaReferences;

namespace TokenConsumptionMonitoring.UI.Diagnostics;

/// <summary>独立的官网参考投影，不写入 CapabilitySnapshot 或连接、余额和告警状态。</summary>
public sealed class QuotaReferenceViewModel : INotifyPropertyChanged
{
    private string? _selection;
    private PageConfigRecord? _page;
    private ModelQuotaCatalog? _catalog;
    private CapabilitySnapshot? _snapshot;
    public bool IsVisible { get; private set; }
    public string ModelLabel { get; private set; } = "";
    public string AmountLabel { get; private set; } = "";
    public string StatusLabel { get; private set; } = "";
    public string SourceLabel { get; private set; } = "";
    public string SourceUrl { get; private set; } = "";
    public string RemainingLabel { get; private set; } = "剩余额度：暂不可得";
    public string Notice => "按所选模型估算美元用量价值；模型共享额度，不能相加。";

    public void Configure(PageConfigRecord? page)
    {
        var key = page is null ? null : string.Join("|", page.Id, page.Revision, page.BaseUrl, page.OpenDesignWorkspaceId,
            page.CredentialRef, page.QuotaReferenceModelId, page.QuotaReferencePlanId);
        if (_selection == key) return;
        _selection = key;
        _page = page;
        _catalog = null;
        _snapshot = null;
        RemainingLabel = "剩余额度：暂不可得";
        IsVisible = page is not null && OfficialQuotaCatalogService.ProviderOf(page.BaseUrl) is not null
            && !string.IsNullOrEmpty(page.QuotaReferenceModelId);
        ModelLabel = ""; AmountLabel = ""; SourceLabel = ""; SourceUrl = "";
        StatusLabel = IsVisible ? "正在同步官网参考额度…" : "";
        Changed();
    }

    public void Update(PageConfigRecord page, ModelQuotaCatalog catalog)
    {
        Configure(page);
        if (!IsVisible) return;
        _catalog = catalog;
        var model = catalog.Entries.FirstOrDefault(e => e.ModelId == page.QuotaReferenceModelId);
        ModelLabel = model?.ModelName ?? "所选模型暂未在官网参考表中找到";
        var entry = catalog.Entries.SingleOrDefault(e => e.ModelId == page.QuotaReferenceModelId && e.PlanId == page.QuotaReferencePlanId);
        AmountLabel = entry is null ? ""
            : entry.IsFree ? "当前标价免费，仍受平台限制"
            : $"约 ${entry.MonthlyUsd:0.####} / 月";
        if (entry is not null) ModelLabel += $" · {entry.PlanName}";
        StatusLabel = entry is null ? "官网未列出该模型与参考套餐的组合"
            : string.Join(" · ", new[] { catalog.Warning, entry.Note }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (catalog.IsStale && string.IsNullOrEmpty(StatusLabel)) StatusLabel = "参考数据已过期，请同步官网";
        SourceUrl = entry?.SourceUrl ?? model?.SourceUrl ?? "";
        SourceLabel = $"{(catalog.Provider == "opendesign" ? "OpenDesign" : "Command Code")} 官网 · {catalog.FetchedAt.ToLocalTime():MM-dd HH:mm}"
            + (catalog.IsStale ? " · 上次缓存" : "");
        Recompute();
    }

    public void Failed(PageConfigRecord page)
    {
        Configure(page);
        if (!IsVisible) return;
        AmountLabel = "";
        _catalog = null;
        StatusLabel = "官网参考额度暂不可用，请稍后同步";
        Recompute();
    }

    public void ApplySnapshot(CapabilitySnapshot snapshot) { _snapshot = snapshot; Recompute(); }
    public void ClearActual() { _snapshot = null; Recompute(); }
    public void Recompute()
    {
        RemainingLabel = ModelRemainingEstimator.Estimate(_page, _catalog, _snapshot, DateTimeOffset.UtcNow).Label;
        Changed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
