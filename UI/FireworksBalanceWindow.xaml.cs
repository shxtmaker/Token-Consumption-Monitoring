using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;

namespace TokenConsumptionMonitoring.UI;

public partial class FireworksBalanceWindow : Window
{
    public const string ConsoleUrl = "https://app.fireworks.ai/account/home";
    private readonly string _profilePath;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private Task? _initializing;
    private bool _completing;
    private FireworksConsoleBalance? _lastRead;
    public bool AllowClose { get; set; }
    public Func<Task<string?>>? ValidateLoginAsync { get; set; }

    public FireworksBalanceWindow(string profilePath)
    {
        InitializeComponent();
        _profilePath = profilePath;
    }

    private Task InitializeAsync() => _initializing ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        var environment = await CoreWebView2Environment.CreateAsync(null, _profilePath);
        await BrowserView.EnsureCoreWebView2Async(environment);
        BrowserView.CoreWebView2.NavigationStarting += (_, _) => _lastRead = null;
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
            _lastRead = null;
            Hint.Text = "登录后点击“完成并读取余额”。";
            await InitializeAsync();
            BrowserView.CoreWebView2.Navigate(ConsoleUrl);
        }
        catch (Exception)
        {
            _initializing = null;
            Hint.Text = "无法初始化登录页面，请确认已安装 Microsoft Edge WebView2 Runtime 后重试。";
        }
    }

    public async Task<FireworksConsoleBalance> ReadAsync(CancellationToken ct)
    {
        if (IsVisible && !_completing)
            throw new QueryTransportException(CandidateStatus.AuthRequired, "请先在 Fireworks 登录窗口点击“完成并读取余额”");
        await _readLock.WaitAsync(ct);
        try
        {
            if (_lastRead is { } cached && DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromSeconds(10)) return cached;
            await InitializeAsync().WaitAsync(TimeSpan.FromSeconds(25), ct);
            var webView = BrowserView.CoreWebView2;
            var navigation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Navigated(object? sender, CoreWebView2NavigationCompletedEventArgs e) => navigation.TrySetResult(e.IsSuccess);
            webView.NavigationCompleted += Navigated;
            try
            {
                webView.Navigate(ConsoleUrl);
                if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(25), ct))
                    throw new QueryTransportException(CandidateStatus.NetworkFailure, "Fireworks 控制台加载失败");
            }
            finally { webView.NavigationCompleted -= Navigated; }
            if (!IsConsoleOrigin(webView.Source))
                throw new QueryTransportException(CandidateStatus.AuthRequired, "Fireworks 会话尚未登录或已失效");

            var nonce = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<FireworksConsoleBalance>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Received(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (!IsConsoleOrigin(e.Source)) return;
                try
                {
                    using var data = JsonDocument.Parse(e.WebMessageAsJson);
                    if (!data.RootElement.TryGetProperty("nonce", out var n) || n.GetString() != nonce) return;
                    completion.TrySetResult(ParseObservation(data.RootElement));
                }
                catch (Exception ex) when (ex is JsonException or QueryTransportException or InvalidOperationException)
                { completion.TrySetException(ex); }
            }
            webView.WebMessageReceived += Received;
            try
            {
                await webView.ExecuteScriptAsync(BuildReadScript(nonce)).WaitAsync(TimeSpan.FromSeconds(5), ct);
                _lastRead = await completion.Task.WaitAsync(TimeSpan.FromSeconds(22), ct);
                return _lastRead;
            }
            finally { webView.WebMessageReceived -= Received; }
        }
        finally { _readLock.Release(); }
    }

    internal static bool IsConsoleOrigin(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IdnHost.Equals("app.fireworks.ai", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort && uri.UserInfo.Length == 0;

    internal static FireworksConsoleBalance ParseObservation(JsonElement data)
    {
        var status = data.GetProperty("status").GetString();
        if (status == "auth") throw new QueryTransportException(CandidateStatus.AuthRequired, "请登录 Fireworks 控制台");
        if (status == "network") throw new QueryTransportException(CandidateStatus.NetworkFailure, "Fireworks 控制台会话检查失败");
        if (status != "ready") throw new QueryTransportException(CandidateStatus.SchemaMismatch, "控制台未显示可识别的 Credits 余额");
        var account = data.GetProperty("accountId").GetString() ?? "";
        var text = data.GetProperty("credits").GetString() ?? "";
        if (!Regex.IsMatch(account, @"\A[a-zA-Z0-9_-]+\z")
            || !Regex.IsMatch(text, @"\A(?:-\$|\$-?)(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d{1,2})?\z")
            || !decimal.TryParse(text.Replace("$", ""), NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var balance))
            throw new QueryTransportException(CandidateStatus.SchemaMismatch, "Fireworks 控制台账户或余额格式不匹配");
        return new(account, balance, DateTimeOffset.UtcNow);
    }

    internal static string BuildReadScript(string nonce) => $$"""
        (() => {
          const nonce = {{JsonSerializer.Serialize(nonce)}};
          const send = value => window.chrome.webview.postMessage({ nonce, ...value });
          (async () => {
            try {
              if (location.origin !== 'https://app.fireworks.ai') return send({status:'auth'});
              const controller = new AbortController();
              const timer = setTimeout(() => controller.abort(), 10000);
              let response;
              try { response = await fetch('/api/v2/auth/refresh', {credentials:'same-origin', cache:'no-store', redirect:'error', signal:controller.signal}); }
              finally { clearTimeout(timer); }
              if (response.status === 401 || response.status === 403) return send({status:'auth'});
              if (!response.ok) return send({status:'network'});
              const {session} = await response.json();
              if (!session?.accountID) return send({status:'auth'});
              for (let i = 0; i < 30; i++) {
                const matches = [...document.querySelectorAll('button')].filter(button =>
                  !button.disabled && button.getAttribute('aria-disabled') !== 'true' && button.getClientRects().length > 0
                  && /Total Spend:/.test(button.innerText) && /Credits:/.test(button.innerText));
                if (matches.length === 1) {
                  const amount = matches[0].innerText.match(/Credits:\s*((?:-\$|\$-?)[\d,]+(?:\.\d{1,2})?)(?=\s|$)/);
                  if (amount) return send({status:'ready',accountId:session.accountID,credits:amount[1]});
                }
                await new Promise(resolve => setTimeout(resolve, 200));
              }
              send({status:'schema'});
            } catch { send({status:'network'}); }
          })();
        })();
        """;

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (ValidateLoginAsync is null) return;
        DoneButton.IsEnabled = false;
        BrowserView.IsEnabled = false;
        _completing = true;
        _lastRead = null;
        Hint.Text = "正在核对账户并读取余额…";
        try
        {
            var error = await ValidateLoginAsync();
            if (error is null) Hide();
            else Hint.Text = error;
        }
        catch (Exception) { Hint.Text = "余额读取失败，请检查登录状态后重试。"; }
        finally { _completing = false; DoneButton.IsEnabled = true; BrowserView.IsEnabled = true; }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); return; }
        BrowserView.Dispose();
        base.OnClosing(e);
    }
}
