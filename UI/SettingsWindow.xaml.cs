using System.Windows;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Services;
using MessageBox = System.Windows.MessageBox;

namespace TokenConsumptionMonitoring.UI;

/// <summary>系统设置（v4）：轮询/会话自检/自启/桌面组件。进入面板入口在托盘菜单。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;

    /// <summary>桌面组件（悬浮窗）开关被切换，即时生效。</summary>
    public event Action<bool>? FloatingWidgetToggleRequested;
    /// <summary>token 消耗量显示开关被切换，即时生效。</summary>
    public event Action<bool>? DailyUsageToggleRequested;
    /// <summary>桌面组件透明度变化（百分比，即时生效）。</summary>
    public event Action<int>? OpacityChangeRequested;

    private bool _initialized;

    public SettingsWindow(AppSettings settings, SettingsStore settingsStore)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;

        PollBox.Text = settings.PollIntervalMinutes.ToString();
        CheckBox.Text = settings.ProbeIntervalSeconds.ToString();
        AutoStartBox.IsChecked = settings.AutoStart;
        WidgetBox.IsChecked = settings.ShowFloatingWidget;
        DailyTokensBox.IsChecked = settings.ShowDailyTokens;
        OpacitySlider.Value = settings.WidgetOpacityPercent;
        OpacityValueText.Text = $"{settings.WidgetOpacityPercent}%";
        _initialized = true;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PollBox.Text, out var poll) || poll is < 10 or > 120)
        { MessageBox.Show("轮询间隔需在 10–120 分钟之间"); return; }
        if (!int.TryParse(CheckBox.Text, out var check) || check is < 10 or > 600)
        { MessageBox.Show("会话自检间隔需在 10–600 秒之间"); return; }

        _settings.PollIntervalMinutes = poll;
        _settings.ProbeIntervalSeconds = check;
        _settings.AutoStart = AutoStartBox.IsChecked == true;
        _settings.ShowFloatingWidget = WidgetBox.IsChecked == true;
        _settings.ShowDailyTokens = DailyTokensBox.IsChecked == true;
        _settings.WidgetOpacityPercent = (int)Math.Round(OpacitySlider.Value);

        AutoStart.Set(_settings.AutoStart);
        _settingsStore.Save(_settings);
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>桌面组件开关：即时显隐悬浮窗并持久化（不受保存/取消影响）。</summary>
    private void WidgetBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var on = WidgetBox.IsChecked == true;
        _settings.ShowFloatingWidget = on;
        _settingsStore.Save(_settings);
        FloatingWidgetToggleRequested?.Invoke(on);
    }

    /// <summary>token 消耗量开关：即时显隐今日用量区并持久化（不受保存/取消影响）。</summary>
    private void DailyTokensBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        var on = DailyTokensBox.IsChecked == true;
        _settings.ShowDailyTokens = on;
        _settingsStore.Save(_settings);
        DailyUsageToggleRequested?.Invoke(on);
    }

    /// <summary>透明度滑杆：即时应用到桌面组件并持久化（不受保存/取消影响）。</summary>
    private void OpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = (int)Math.Round(OpacitySlider.Value);
        OpacityValueText.Text = $"{v}%";
        if (!_initialized) return;
        _settings.WidgetOpacityPercent = v;
        _settingsStore.Save(_settings);
        OpacityChangeRequested?.Invoke(v);
    }
}
