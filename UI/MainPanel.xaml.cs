using System.Windows;
using System.Windows.Controls;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services;
using TokenConsumptionMonitoring.UI.Diagnostics;
using MessageBox = System.Windows.MessageBox;

namespace TokenConsumptionMonitoring.UI;

public partial class MainPanel : Window
{
    private static readonly string[] CompatibilityMethodIds =
    {
        "opencode.rolling-window.api-key",
        "opencode.allowance.oauth",
        "commandcode.allowance-window.compat",
        "deepseek.console-usage.compat",
    };

    private readonly PageConfigStore _pageStore;
    private readonly List<PageConfigRecord> _pages;
    private readonly List<string> _modelDraft = new();
    private KeyFormat.Protocol _protocol = KeyFormat.Protocol.ChatCompletions;
    private PageConfigRecord? _editing;
    private bool _syncingActivePage;

    public event Action? RefreshRequested;

    /// <summary>登录入口请求：由表单当前协议判定（DeepSeekConsole → 会话登录窗；None = API Key 无需登录）。</summary>
    public event Action<LoginKind>? LoginRequested;
    public event Action? PagesChanged;
    public event Action<string>? PageSwitchRequested;
    public event Action<string>? RescanRequested;
    public event Action<string, string?>? OverrideRequested;

    /// <summary>退出应用时放行真实关闭（平时 ✕ = 隐藏，保证实例可反复 Show）。</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;   // 已 Close 的窗口无法再 Show：改为隐藏，避免下次唤出抛异常
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public MainPanel(PageConfigStore pageStore, List<PageConfigRecord> pages, MonitorState state)
    {
        InitializeComponent();
        _pageStore = pageStore;
        _pages = pages;
        DataContext = state;

        PProtocolCombo.ItemsSource = Enum.GetValues<KeyFormat.Protocol>();
        PProtocolCombo.SelectedItem = KeyFormat.Protocol.ChatCompletions;
        PCompatibilityBox.IsChecked = false;

        RefreshPageCombo();
    }

    public PageConfigRecord? ActivePage => PageCombo.SelectedItem as PageConfigRecord;

    public void RefreshPageCombo(string? selectId = null)
    {
        PageCombo.ItemsSource = null;
        PageCombo.ItemsSource = _pages;
        var idx = selectId is null ? -1 : _pages.FindIndex(p => p.Id == selectId);
        if (idx < 0) idx = _pages.Count > 0 ? 0 : -1;
        PageCombo.SelectedIndex = idx;
        PageHintText.Text = _pages.Count == 0
            ? "当前没有页面——点击「新建」创建第一个 API 配置页面"
            : $"共 {_pages.Count} 个页面 · 小组件名称 = 页面名称";
    }

    public void SetActivePageId(string? id)
    {
        if (id is null) { PageCombo.SelectedIndex = -1; return; }
        var idx = _pages.FindIndex(p => p.Id == id);
        if (PageCombo.SelectedIndex != idx)
        {
            // 激活页 → 下拉框的回写：抑制 SelectionChanged，避免再当成用户切换请求引发重扫
            _syncingActivePage = true;
            try { PageCombo.SelectedIndex = idx; }
            finally { _syncingActivePage = false; }
        }
    }

    /// <summary>编辑/新建表单是否打开（打开时外部激活页变化不抢占下拉框，避免覆盖未保存的表单内容）。</summary>
    public bool IsEditingFormOpen => ProviderForm.Visibility == Visibility.Visible;

    // ---- 页面切换 ----

    private void PageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingActivePage) return;
        if (PageCombo.SelectedItem is PageConfigRecord page)
        {
            PageSwitchRequested?.Invoke(page.Id);
            if (ProviderForm.Visibility == Visibility.Visible)
            {
                _editing = page; // 表单打开时切换页面 = 载入该页信息（编辑模式）
                FillForm(page);
            }
        }
    }

    // ---- 供应商表单 ----

    private void AddProvider_Click(object sender, RoutedEventArgs e)
    {
        _editing = null;
        ClearForm();
        ProviderForm.Visibility = Visibility.Visible;
    }

    private void EditPage_Click(object sender, RoutedEventArgs e)
    {
        if (PageCombo.SelectedItem is not PageConfigRecord page)
        {
            MessageBox.Show("请先创建并选中一个页面", "编辑", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _editing = page;
        FillForm(page);
        ProviderForm.Visibility = Visibility.Visible;
    }

    private void ClearForm()
    {
        PNameBox.Text = "";
        PBaseUrlBox.Text = "";
        PKeyBox.Password = "";
        _modelDraft.Clear();
        PModelsList.ItemsSource = null;
        PProtocolCombo.SelectedItem = KeyFormat.Protocol.ChatCompletions;
        PCompatibilityBox.IsChecked = false;
        RefreshFormHints();
    }

    private void FillForm(PageConfigRecord page)
    {
        PNameBox.Text = page.Name;
        PBaseUrlBox.Text = page.BaseUrl;
        PProtocolCombo.SelectedItem = page.ParseProtocol();
        PCompatibilityBox.IsChecked = page.EnabledCompatibilityMethods.Count > 0;
        PKeyBox.Password = "";
        if (page.CredentialRef.ResolveClass() == CredentialClass.ApiKey
            && CredentialStore.TryReadSecret(page.CredentialRef.Target!, out var key))
            PKeyBox.Password = key ?? "";
        _modelDraft.Clear();
        _modelDraft.AddRange(page.ConfiguredModelHints);
        PModelsList.ItemsSource = null;
        PModelsList.ItemsSource = _modelDraft;
        RefreshFormHints();
    }

    private void RefreshFormHints()
    {
        var provider = Services.Scanning.CredentialResolver.ProviderOf(PBaseUrlBox.Text);
        PProviderHint.Text = PBaseUrlBox.Text.Length == 0
            ? "输入 Base URL 后自动识别供应商提示（自动扫描会按能力选择查询方法）"
            : $"识别提示：{(provider ?? "自定义/通用")} · {KeyFormat.Describe(_protocol)}";
        PKeyHint.Text = KeyFormat.KeyHint(_protocol);
        PKeyHint.Visibility = _protocol == KeyFormat.Protocol.DeepSeekConsole ? Visibility.Collapsed : Visibility.Visible;
        PModelsHint.Text = _modelDraft.Count == 0 ? "点击「+ 添加模型」或「自动拉取」" : $"{_modelDraft.Count} 个模型";
    }

    private void PBaseUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var url = PBaseUrlBox.Text.ToLowerInvariant();
        var suggested = url.Contains("platform.deepseek.com") ? KeyFormat.Protocol.DeepSeekConsole
            : url.Contains("anthropic.com") ? KeyFormat.Protocol.Anthropic
            : _protocol;
        if (suggested != _protocol)
        {
            _protocol = suggested;
            PProtocolCombo.SelectedItem = suggested;
        }
        RefreshFormHints();
    }

    private void PProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PProtocolCombo.SelectedItem is KeyFormat.Protocol p)
        {
            _protocol = p;
            RefreshFormHints();
        }
    }

    private void PKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (PKeyBox.Password.Length == 0) { PKeyHint.Text = KeyFormat.KeyHint(_protocol); return; }
        var (valid, hint) = KeyFormat.Validate(_protocol, PKeyBox.Password);
        PKeyHint.Text = hint;
        PKeyHint.Foreground = valid
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0xC9, 0xA0))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x48));
    }

    private void DiagnosticsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= 980;
        if (wide)
        {
            DiagnosticsColumn0.Width = new GridLength(1, GridUnitType.Star);
            DiagnosticsColumn1.Width = new GridLength(1, GridUnitType.Star);
            DiagnosticsColumn2.Width = new GridLength(1, GridUnitType.Star);
            DiagnosticsRow0.Height = new GridLength(1, GridUnitType.Star);
            DiagnosticsRow1.Height = new GridLength(0);
            DiagnosticsRow2.Height = new GridLength(0);
            Grid.SetColumn(ConfigDiagnosticsPanel, 0);
            Grid.SetRow(ConfigDiagnosticsPanel, 0);
            Grid.SetColumn(CandidateDiagnosticsPanel, 1);
            Grid.SetRow(CandidateDiagnosticsPanel, 0);
            Grid.SetColumn(CapabilityDiagnosticsPanel, 2);
            Grid.SetRow(CapabilityDiagnosticsPanel, 0);
            ConfigDiagnosticsPanel.Margin = new Thickness(0, 0, 6, 0);
            CandidateDiagnosticsPanel.Margin = new Thickness(0, 0, 6, 0);
            CapabilityDiagnosticsPanel.Margin = new Thickness(0);
        }
        else
        {
            DiagnosticsColumn0.Width = new GridLength(1, GridUnitType.Star);
            DiagnosticsColumn1.Width = new GridLength(0);
            DiagnosticsColumn2.Width = new GridLength(0);
            DiagnosticsRow0.Height = GridLength.Auto;
            DiagnosticsRow1.Height = GridLength.Auto;
            DiagnosticsRow2.Height = GridLength.Auto;
            Grid.SetColumn(ConfigDiagnosticsPanel, 0);
            Grid.SetRow(ConfigDiagnosticsPanel, 0);
            Grid.SetColumn(CandidateDiagnosticsPanel, 0);
            Grid.SetRow(CandidateDiagnosticsPanel, 1);
            Grid.SetColumn(CapabilityDiagnosticsPanel, 0);
            Grid.SetRow(CapabilityDiagnosticsPanel, 2);
            ConfigDiagnosticsPanel.Margin = new Thickness(0, 0, 0, 6);
            CandidateDiagnosticsPanel.Margin = new Thickness(0, 0, 0, 6);
            CapabilityDiagnosticsPanel.Margin = new Thickness(0);
        }
    }

    private void PAddModel_Click(object sender, RoutedEventArgs e)
    {
        var model = PNewModelBox.Text.Trim();
        if (string.IsNullOrEmpty(model)) return;
        if (!_modelDraft.Contains(model))
        {
            _modelDraft.Add(model);
            PModelsList.ItemsSource = null;
            PModelsList.ItemsSource = _modelDraft;
        }
        PNewModelBox.Text = "";
        RefreshFormHints();
    }

    private async void PFetchModels_Click(object sender, RoutedEventArgs e)
    {
        PModelsHint.Text = "正在拉取模型列表…";
        try
        {
            var models = await ModelFetcher.FetchAsync(PBaseUrlBox.Text, _protocol, PKeyBox.Password);
            if (models.Count == 0) { PModelsHint.Text = "拉取失败或接口无模型（可手动添加）"; return; }
            _modelDraft.Clear();
            _modelDraft.AddRange(models);
            PModelsList.ItemsSource = null;
            PModelsList.ItemsSource = _modelDraft;
            PModelsHint.Text = $"已拉取 {models.Count} 个模型";
        }
        catch (Exception ex)
        {
            PModelsHint.Text = $"拉取失败：{ex.Message}（可手动添加）";
        }
    }

    private void SavePage()
    {
        var name = PNameBox.Text.Trim();
        var baseUrl = PBaseUrlBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) { MessageBox.Show("请输入名称（如：DeepSeek 官方）"); return; }
        if (string.IsNullOrEmpty(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out _)) { MessageBox.Show("请输入有效的 Base URL"); return; }

        var editing = _editing;
        var previousSelectedId = (PageCombo.SelectedItem as PageConfigRecord)?.Id;
        var page = editing is null ? new PageConfigRecord() : ClonePage(editing);
        page.Name = name;
        page.BaseUrl = baseUrl;
        page.Protocol = _protocol.ToString();
        page.ConfiguredModelHints = new List<string>(_modelDraft);

        // 凭据引用与查询方法解耦；私有兼容来源必须由高级开关显式启用。
        page.EnabledCompatibilityMethods = PCompatibilityBox.IsChecked == true
            ? editing is { EnabledCompatibilityMethods.Count: > 0 }
                ? new List<string>(editing.EnabledCompatibilityMethods)
                : CompatibilityMethodIds.ToList()
            : new List<string>();
        string? secretToSave = null;
        string? secretTarget = null;
        if (_protocol == KeyFormat.Protocol.DeepSeekConsole)
            page.CredentialRef = CredentialReference.GlobalConsoleSession(AppIdentity.DeepSeekCookiesTarget);
        else
        {
            page.CredentialRef = CredentialReference.PageApiKey(page.Id);
            var key = PKeyBox.Password;
            if (string.IsNullOrEmpty(key) && _editing is null) { MessageBox.Show("请输入 API Key"); return; }
            if (!string.IsNullOrEmpty(key))
            {
                var (valid, hint) = KeyFormat.Validate(_protocol, key);
                if (!valid) { MessageBox.Show($"API Key 无效：{hint}"); return; }
                secretTarget = AppIdentity.ApiKeyTarget(page.Id);
                secretToSave = key;
            }
        }

        var editingIndex = editing is null ? -1 : _pages.IndexOf(editing);
        if (editing is null) _pages.Add(page);
        else if (editingIndex >= 0) _pages[editingIndex] = page;
        var saveResult = _pageStore.Save(new PageConfigDocument
        {
            SchemaVersion = PageConfigDocument.CurrentSchemaVersion,
            Pages = _pages,
        });
        if (!saveResult.Succeeded)
        {
            if (editing is null) _pages.Remove(page);
            else if (editingIndex >= 0) _pages[editingIndex] = editing;
            RefreshPageCombo(editing?.Id ?? previousSelectedId);
            MessageBox.Show(saveResult.Diagnostic ?? "页面配置保存失败", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (secretToSave is not null && secretTarget is not null)
            CredentialStore.SaveSecret(secretTarget, secretToSave);

        ProviderForm.Visibility = Visibility.Collapsed;
        _editing = null;
        RefreshPageCombo(page.Id);
        // 保存后触发自动扫描
        RescanRequested?.Invoke(page.Id);
        PagesChanged?.Invoke();
    }

    private void PFormCancel_Click(object sender, RoutedEventArgs e)
    {
        ProviderForm.Visibility = Visibility.Collapsed;
        _editing = null;
    }

    private void PageDelete_Click(object sender, RoutedEventArgs e)
    {
        if (PageCombo.SelectedItem is not PageConfigRecord page) return;
        if (MessageBox.Show($"删除页面「{page.Name}」？（凭据管理器中的 key 保留）", "删除页面",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var index = _pages.IndexOf(page);
        _pages.Remove(page);
        var saveResult = _pageStore.Save(new PageConfigDocument
        {
            SchemaVersion = PageConfigDocument.CurrentSchemaVersion,
            Pages = _pages,
        });
        if (!saveResult.Succeeded)
        {
            if (index >= 0) _pages.Insert(index, page);
            RefreshPageCombo(page.Id);
            MessageBox.Show(saveResult.Diagnostic ?? "页面配置保存失败", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        RefreshPageCombo();
        PagesChanged?.Invoke();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        // 「登录」按钮只存在于编辑表单内：以表单当前协议判定。
        // 新建/编辑未保存时活动页的凭据类别仍是旧值，按保存态判定会让
        // 新建 DeepSeek 控制台页的登录被当成 API Key 页处理（用户视角 = 点了没反应）。
        Services.Logger.Log($"panel login click: protocol={_protocol}");
        LoginRequested?.Invoke(_protocol == KeyFormat.Protocol.DeepSeekConsole
            ? LoginKind.DeepSeekConsole
            : LoginKind.None);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        if (ActivePage is { } page) RescanRequested?.Invoke(page.Id);
    }

    private void UseCandidate_Click(object sender, RoutedEventArgs e)
    {
        if (ActivePage is not { } page) return;
        if ((sender as FrameworkElement)?.DataContext is MethodCandidateViewModel candidate)
            OverrideRequested?.Invoke(page.Id, candidate.IsCurrent ? null : candidate.MethodId);
    }

    /// <summary>表单底部「保存」：校验并持久化当前表单配置。</summary>
    private void FormSave_Click(object sender, RoutedEventArgs e)
    {
        try { SavePage(); }
        catch (System.ComponentModel.Win32Exception ex) { MessageBox.Show(ex.Message); }
    }

    private static PageConfigRecord ClonePage(PageConfigRecord page) => new()
    {
        Id = page.Id,
        Name = page.Name,
        BaseUrl = page.BaseUrl,
        Protocol = page.Protocol,
        CredentialRef = page.CredentialRef,
        ConfiguredModelHints = new List<string>(page.ConfiguredModelHints),
        EnabledCompatibilityMethods = new List<string>(page.EnabledCompatibilityMethods),
        SortOrder = page.SortOrder,
        Deprecated = page.Deprecated is { } deprecated
            ? new DeprecatedPageSettings
            {
                AmountWarnCny = deprecated.AmountWarnCny,
                AmountCriticalCny = deprecated.AmountCriticalCny,
                TokenWarn = deprecated.TokenWarn,
                TokenCritical = deprecated.TokenCritical,
            }
            : null,
    };
}
