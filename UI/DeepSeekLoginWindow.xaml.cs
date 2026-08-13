using System.Windows;
using Microsoft.Web.WebView2.Core;
using TokenUsageMonitorV3.Services;
using MessageBox = System.Windows.MessageBox;

namespace TokenUsageMonitorV3.UI;

public partial class DeepSeekLoginWindow : Window
{
    private readonly DeepSeekSessionService _session;

    /// <summary>WebView2 初始化 + 会话自检完成（重启后 cookie 持久，可能直接有效）。</summary>
    public event Action? SessionReady;

    /// <summary>退出应用时放行真实关闭（平时 ✕/完成 = 隐藏，保证实例与 WebView2 可反复复用）。</summary>
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

    public DeepSeekLoginWindow(DeepSeekSessionService session)
    {
        InitializeComponent();
        _session = session;
        Loaded += (_, _) => _ = EnsureInitializedAsync();
    }

    /// <summary>初始化 WebView2 + 会话自检（启动时调用，无需显示窗口；cookie 持久于 profile/凭据管理器）。</summary>
    public async Task<bool> EnsureInitializedAsync()
    {
        try
        {
            Logger.Log("ds init: creating env");
            var userDataFolder = System.IO.Path.Combine(SettingsStore.DataDirectory, "WebView2Profile");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            Logger.Log("ds init: ensure core");
            await DeepSeekWebView.EnsureCoreWebView2Async(env);
            Logger.Log("ds init: attach");
            _session.Attach(DeepSeekWebView.CoreWebView2);
            _session.SetUserDataFolder(userDataFolder);
            DeepSeekWebView.CoreWebView2.Navigate(DeepSeekSessionService.ConsoleUrl);

            Logger.Log("ds init: session check");
            var ok = await _session.CheckSessionAsync();
            Logger.Log($"ds init: check={ok}");
            if (!ok)
            {
                Logger.Log("ds init: restore cookies");
                ok = await _session.RestoreCookiesAsync();
                Logger.Log($"ds init: restore={ok}");
            }
            if (ok) SessionReady?.Invoke();
            return ok;
        }
        catch (Exception ex)
        {
            Logger.LogException("webview2 init", ex);
            return false;
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _ = CheckAndNotifyAsync();
        Close();
    }

    private async Task CheckAndNotifyAsync()
    {
        var ok = await _session.CheckSessionAsync();
        if (ok)
        {
            await _session.SaveCookiesAsync();
            SessionReady?.Invoke();
        }
        else Logger.Log("deepseek session check after login failed");
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
        => DeepSeekWebView.CoreWebView2?.Navigate(DeepSeekSessionService.ConsoleUrl);
}
