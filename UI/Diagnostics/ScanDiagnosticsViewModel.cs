using System.Collections.ObjectModel;
using System.ComponentModel;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.Scanning;

namespace TokenConsumptionMonitoring.UI.Diagnostics;

/// <summary>
/// 扫描诊断工作台视图模型：左栏页面配置/扫描状态、中栏候选方法链、右栏能力矩阵。
/// 表达查询决策与诊断事实，不替代用量快照，不展示模型目录。
/// </summary>
public sealed class ScanDiagnosticsViewModel : INotifyPropertyChanged
{
    public ObservableCollection<MethodCandidateViewModel> Candidates { get; } = new();

    public string Fingerprint { get; private set; } = "";
    public string ScannedAtLabel { get; private set; } = "尚未扫描";
    public string SelectionLabel { get; private set; } = "尚未选择方法";
    public bool RequiresSelection { get; private set; }
    public bool IsScanning { get; private set; }
    public bool HasCandidates => Candidates.Count > 0;
    public string CandidateCountLabel => $"{Candidates.Count} 个候选";
    public string ConfigText { get; private set; } = "";
    public string[] CapabilityLegend => new[] { "窗口", "余额/额度", "用量", "费用", "探测" };

    public void SetScanning(bool scanning, PageConfigRecord? page = null)
    {
        IsScanning = scanning;
        if (page is not null) BuildConfigText(page);
        Notify(nameof(IsScanning));
    }

    /// <summary>没有活动页面时清除上一页的诊断投影。</summary>
    public void Clear()
    {
        Fingerprint = "";
        ScannedAtLabel = "尚未扫描";
        SelectionLabel = "尚未选择方法";
        RequiresSelection = false;
        IsScanning = false;
        ConfigText = "";
        Candidates.Clear();
        Notify(nameof(Fingerprint), nameof(ScannedAtLabel), nameof(SelectionLabel), nameof(RequiresSelection),
            nameof(IsScanning), nameof(HasCandidates), nameof(CandidateCountLabel), nameof(ConfigText));
    }

    /// <summary>扫描完成后更新：候选链 + 选择状态 + 指纹。effectiveMethodId 为当前选中（含临时覆盖）。</summary>
    public void Update(PageConfigRecord page, ScanReport? report, string? effectiveMethodId)
    {
        BuildConfigText(page);
        if (report is null)
        {
            Fingerprint = "";
            ScannedAtLabel = "尚未扫描";
            SelectionLabel = "尚未选择方法";
            RequiresSelection = false;
            Candidates.Clear();
            Notify(nameof(Fingerprint), nameof(ScannedAtLabel), nameof(SelectionLabel), nameof(RequiresSelection),
                nameof(HasCandidates), nameof(CandidateCountLabel), nameof(ConfigText));
            return;
        }

        Fingerprint = report.Fingerprint;
        ScannedAtLabel = $"最近扫描 {report.ScannedAt.ToLocalTime():HH:mm:ss}";
        RequiresSelection = report.RequiresSelection;
        SelectionLabel = report.RequiresSelection
            ? "能力来源存在并列 — 需要选择（可临时覆盖）"
            : report.SelectedMethodIds.Count > 0
                ? $"已选来源：{string.Join("；", report.SelectedMethodIds.Select(pair => $"{pair.Key}={pair.Value}"))}"
                : report.SelectionStatus switch
                {
                    CandidateStatus.AuthRequired => "需要凭据/权限",
                    CandidateStatus.NoReliableUsage => "无可用用量来源",
                    _ => "未发现可用查询方法",
                };

        // 重建候选链
        Candidates.Clear();
        var effective = report.SelectedMethodIds.Values.ToHashSet(StringComparer.Ordinal);
        if (effectiveMethodId is not null) effective.Add(effectiveMethodId);
        foreach (var c in report.Candidates)
            Candidates.Add(new MethodCandidateViewModel(c, effective));

        Notify(nameof(Fingerprint), nameof(ScannedAtLabel), nameof(SelectionLabel), nameof(RequiresSelection), nameof(HasCandidates), nameof(CandidateCountLabel), nameof(ConfigText));
    }

    /// <summary>仅刷新“当前方法”标记（临时覆盖后）。</summary>
    public void RefreshCurrent(string effectiveMethodId)
    {
        foreach (var c in Candidates) c.RefreshCurrent(effectiveMethodId);
    }

    private void BuildConfigText(PageConfigRecord page)
    {
        var hasKey = page.CredentialRef.ResolveClass() == CredentialClass.ApiKey
            && Services.CredentialStore.TryReadSecret(page.CredentialRef.Target!, out _);
        ConfigText = $"名称：{page.Name}\nBase URL：{page.BaseUrl}\n协议：{page.Protocol}\n" +
                     $"凭据：{(page.CredentialRef.ResolveClass() switch
                     {
                         CredentialClass.None => "无需凭据/本地记录",
                         CredentialClass.ApiKey => hasKey ? "API key 已配置" : "API key 未配置",
                         CredentialClass.ConsoleSession => "控制台会话",
                         CredentialClass.OAuthSession => "OAuth 会话",
                         CredentialClass.LocalRecord => "本地记录",
                         _ => page.CredentialRef.ResolveClass().ToString(),
                     })}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
