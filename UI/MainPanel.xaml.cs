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
    private readonly PageConfigurationCommands _commands;
    private readonly PageCatalog _catalog;
    private readonly string? _configurationDiagnostic;
    private List<PageConfigRecord> _pages => _catalog.Snapshot();
    private readonly PageEditorViewModel _editor = new();
    private List<string> _modelDraft => _editor.Models;
    private KeyFormat.Protocol _protocol = KeyFormat.Protocol.ChatCompletions;
    private PageConfigRecord? _editing { get => _editor.Editing; set => _editor.Editing = value; }
    private string _loadedSecret { get => _editor.LoadedSecret; set => _editor.LoadedSecret = value; }
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

    public MainPanel(PageConfigurationCommands commands, PageCatalog catalog, MonitorState state,
        string? configurationDiagnostic = null)
    {
        InitializeComponent();
        _commands = commands;
        _catalog = catalog;
        _configurationDiagnostic = configurationDiagnostic;
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
        PageHintText.Text = !string.IsNullOrWhiteSpace(_configurationDiagnostic)
            ? $"页面配置处于只读恢复态：{_configurationDiagnostic}。原文件保持不变，请恢复有效配置后重新启动。"
            : _pages.Count == 0
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
        _loadedSecret = "";
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
        PKeyBox.Password = _commands.ReadApiKeyForEditing(page);
        _loadedSecret = PKeyBox.Password;
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
        var editing = _editing;
        var previousSelectedId = (PageCombo.SelectedItem as PageConfigRecord)?.Id;
        _editor.Name = PNameBox.Text;
        _editor.BaseUrl = PBaseUrlBox.Text;
        _editor.Protocol = _protocol;
        _editor.EnableCompatibility = PCompatibilityBox.IsChecked == true;
        var saveResult = _editor.Save(_commands, PKeyBox.Password);
        if (!saveResult.Succeeded)
        {
            RefreshPageCombo(editing?.Id ?? previousSelectedId);
            MessageBox.Show(saveResult.Diagnostic ?? "页面配置保存失败", "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ProviderForm.Visibility = Visibility.Collapsed;
        _editing = null;
        RefreshPageCombo(_editor.SavedPageId);
        // 保存后触发自动扫描
        RescanRequested?.Invoke(_editor.SavedPageId!);
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
        var saveResult = _commands.Delete(page);
        if (!saveResult.Succeeded)
        {
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

}
