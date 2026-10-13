using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;

namespace TokenConsumptionMonitoring.UI;

public partial class OpenDesignConsoleWindow : Window
{
    private readonly string _profilePath;
    private readonly string _workspace;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private Task? _initializing;
    private bool _completing;
    public bool AllowClose { get; set; }
    public Func<Task<string?>>? ValidateLoginAsync { get; set; }

    public OpenDesignConsoleWindow(string profilePath, string workspace)
    {
        InitializeComponent();
        _profilePath = profilePath;
        _workspace = workspace;
    }

    private async Task InitializeAsync()
    {
        try { await (_initializing ??= InitializeCoreAsync()); }
        catch { _initializing = null; throw; }
    }

    private async Task InitializeCoreAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, _profilePath);
        await BrowserView.EnsureCoreWebView2Async(environment);
        BrowserView.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                BrowserView.CoreWebView2.Navigate(e.Uri);
        };
    }

    public async Task ShowLoginAsync()
    {
        try
        {
            Hint.Text = "登录后点击“完成并读取额度”。";
            await InitializeAsync();
            BrowserView.CoreWebView2.Navigate(OpenDesignConsoleMethod.DashboardUrl(_workspace));
        }
        catch (Exception) { Hint.Text = "无法初始化登录页面，请确认已安装 Microsoft Edge WebView2 Runtime 后重试。"; }
    }

    public async Task<JsonElement> ReadAsync(bool windows, CancellationToken ct)
    {
        if (IsVisible && !_completing)
            throw new QueryTransportException(CandidateStatus.AuthRequired, "请先在 OpenDesign 登录窗口点击“完成并读取额度”");
        await _readLock.WaitAsync(ct);
        try
        {
            await InitializeAsync().WaitAsync(TimeSpan.FromSeconds(25), ct);
            var webView = BrowserView.CoreWebView2;
            if (!OpenDesignConsoleMethod.IsConsoleOrigin(webView.Source))
            {
                var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigation.TrySetResult(e.IsSuccess);
                webView.NavigationCompleted += Navigated;
                try
                {
                    webView.Navigate(OpenDesignConsoleMethod.DashboardUrl(_workspace));
                    if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(25), ct))
                        throw new QueryTransportException(CandidateStatus.NetworkFailure, "OpenDesign 控制台加载失败");
                }
                finally { webView.NavigationCompleted -= Navigated; }
            }
            if (!OpenDesignConsoleMethod.IsConsoleOrigin(webView.Source))
                throw new QueryTransportException(CandidateStatus.AuthRequired, "OpenDesign 会话尚未登录或已失效");
            var nonce = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (!OpenDesignConsoleMethod.IsConsoleOrigin(e.Source)) return;
                try
                {
                    using var data = JsonDocument.Parse(e.WebMessageAsJson);
                    if (!data.RootElement.TryGetProperty("nonce", out var n) || n.GetString() != nonce) return;
                    completion.TrySetResult(data.RootElement.Clone());
                }
                catch (JsonException) { completion.TrySetException(new QueryTransportException(CandidateStatus.SchemaMismatch, "OpenDesign 响应结构不匹配")); }
            }
            webView.WebMessageReceived += Received;
            try
            {
                await webView.ExecuteScriptAsync(BuildReadScript(nonce, _workspace, windows)).WaitAsync(TimeSpan.FromSeconds(5), ct);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(24), ct);
            }
            finally { webView.WebMessageReceived -= Received; }
        }
        finally { _readLock.Release(); }
    }

    internal static string BuildReadScript(string nonce, string workspace, bool windows) => $$"""
        (() => {
          const nonce = {{JsonSerializer.Serialize(nonce)}};
          const workspace = {{JsonSerializer.Serialize(workspace)}};
          const origin = {{JsonSerializer.Serialize(OpenDesignConsoleMethod.ApiOrigin)}};
          const path = {{JsonSerializer.Serialize(OpenDesignConsoleMethod.ResourcePath(workspace, windows))}};
          const send = value => window.chrome.webview.postMessage({nonce, ...value});
          (async () => {
            const controller = new AbortController();
            const timer = setTimeout(() => controller.abort(), 20000);
            try {
              if (location.origin !== 'https://open-design.ai') return send({status:'auth'});
              const read = async path => {
                const response = await fetch(origin + path, {method:'GET', credentials:'include', cache:'no-store', redirect:'error',
                  headers:{Accept:'application/json', 'x-vela-workspace-id':workspace}, signal:controller.signal});
                if (!response.ok) throw {status:response.status === 401 ? 'auth' : response.status === 403 ? 'forbidden'
                  : response.status === 429 ? 'rate' : response.status >= 500 ? 'network' : 'schema'};
                try { return await response.json(); } catch { throw {status:'schema'}; }
              };
              const context = await read('/api/v1/workspaces/current');
              if (context.workspaceId !== workspace) return send({status:'forbidden'});
              const body = await read(path);
              send({status:'ready', context:{workspaceId:context.workspaceId, workspaceMemberId:context.workspaceMemberId}, body});
            } catch (error) { send({status:['auth','forbidden','rate','schema'].includes(error?.status) ? error.status : 'network'}); }
            finally { clearTimeout(timer); }
          })();
        })();
        """;

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateLoginAsync is null) return;
        DoneButton.IsEnabled = false;
        BrowserView.IsEnabled = false;
        _completing = true;
        Hint.Text = "正在核对工作空间并读取额度…";
        try
        {
            var error = await ValidateLoginAsync();
            if (error is null) Hide(); else Hint.Text = error;
        }
        catch (Exception) { Hint.Text = "额度读取失败，请检查登录状态后重试。"; }
        finally { _completing = false; DoneButton.IsEnabled = true; BrowserView.IsEnabled = true; }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); return; }
        BrowserView.Dispose();
        base.OnClosing(e);
    }
}
