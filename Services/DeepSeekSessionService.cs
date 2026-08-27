using Microsoft.Web.WebView2.Core;

namespace TokenConsumptionMonitoring.Services;

/// <summary>
/// DeepSeek 控制台会话（WebView2 内嵌登录 + 页面上下文 fetch）。
/// 关键设计：请求由 WebView2 页面上下文发出——cookie、TLS 指纹与 WAF 行为等同浏览器，
/// 无需读取/逆向会话 cookie。会话持久化于私有 user-data-folder。
/// WebView2 控件由 DeepSeekLoginWindow 持有，初始化完成后 Attach 到此服务。
/// </summary>
public sealed class DeepSeekSessionService : IDisposable
{
    public const string ConsoleUrl = "https://platform.deepseek.com";

    // CoreWebView2 只能从 UI 线程访问——所有公开异步方法封送到 UI Dispatcher 执行
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private readonly ManualResetEventSlim _ready = new(false);
    private CoreWebView2? _webView;
    private string? _userDataFolder;
    // 页面捕获串行锁：同一响应可能被触发多次读取，并发读同一流会互相撕裂字节
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    public DeepSeekSessionService(System.Windows.Threading.Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool IsLoggedIn { get; internal set; }

    /// <summary>登录窗在 WebView2 初始化完成后调用（一次性放行所有等待者）。</summary>
    public void Attach(CoreWebView2 webView)
    {
        _webView = webView;
        _ready.Set();
    }

    public void SetUserDataFolder(string folder) => _userDataFolder = folder;

    private CoreWebView2 GetWebView(CancellationToken ct = default)
    {
        _ready.Wait(ct);
        return _webView!;
    }

    /// <summary>在页面上下文中 fetch 相对路径（封送 UI 线程）。</summary>
    public Task<(bool Ok, string Body, bool SessionInvalid, int? HttpStatus)> FetchAsync(string relativePath, CancellationToken ct = default)
        => _dispatcher.InvokeAsync(() => FetchAsyncCore(relativePath, cancellationToken: ct),
            System.Windows.Threading.DispatcherPriority.Normal, ct).Task.Unwrap();

    /// <summary>保存 cookie（封送 UI 线程）。</summary>
    public Task SaveCookiesAsync() => _dispatcher.InvokeAsync(() => SaveCookiesCoreAsync()).Task.Unwrap();

    /// <summary>恢复 cookie（封送 UI 线程）。</summary>
    public Task<bool> RestoreCookiesAsync() => _dispatcher.InvokeAsync(() => RestoreCookiesCoreAsync()).Task.Unwrap();

    private async Task<(bool Ok, string Body, bool SessionInvalid, int? HttpStatus)> FetchAsyncCore(
        string relativePath, bool post = false, CancellationToken cancellationToken = default)
    {
        var wv2 = GetWebView(cancellationToken);

        // 确保页面停留在控制台域（登录窗关闭/跳转后跨域 fetch 会失败）
        if (wv2.Source is null || !wv2.Source.StartsWith(ConsoleUrl, StringComparison.OrdinalIgnoreCase))
        {
            var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => navTcs.TrySetResult(e.IsSuccess);
            wv2.NavigationCompleted += handler;
            try
            {
                wv2.Navigate(ConsoleUrl);
                var done = await Task.WhenAny(navTcs.Task, Task.Delay(20000, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (done != navTcs.Task) Logger.Log("deepseek fetch: navigation timeout");
            }
            finally { wv2.NavigationCompleted -= handler; }
        }

        string text;
        try
        {
            // 页面 bundle 使用 XMLHttpRequest——用 XHR 而非 fetch（避免被风控区别对待）
            var script = post
                ? $"new Promise(r => {{ const x = new XMLHttpRequest(); x.open('POST','{relativePath}'); x.withCredentials = true; x.onload = () => r(x.status + '::' + x.responseText); x.send(); }})"
                : $"new Promise(r => {{ const x = new XMLHttpRequest(); x.open('GET','{relativePath}'); x.withCredentials = true; x.onload = () => r(x.status + '::' + x.responseText); x.send(); }})";
            var execTask = wv2.ExecuteScriptAsync(script);
            var completed = await Task.WhenAny(execTask, Task.Delay(20000, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (completed != execTask)
            {
                Logger.Log("deepseek fetch: execute timeout");
                return (false, "", false, null);
            }
            text = await execTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogException("deepseek fetch", ex);
            return (false, "", false, null);
        }
        if (string.IsNullOrEmpty(text) || text == "null" || text == "\"null\"") return (false, "", false, null);

        // ExecuteScriptAsync 返回值是 JSON 编码字符串：去外层引号后还需反转义（\" \\ \n 等），
        // 否则响应体内的转义引号会导致 JSON 解析失败
        string body;
        if (text.Length >= 2 && text[0] == '"')
        {
            try { body = System.Text.Json.JsonSerializer.Deserialize<string>(text) ?? ""; }
            catch (System.Text.Json.JsonException) { body = text[1..^1]; }
        }
        else body = text;
        // 拆分状态码前缀
        var sep = body.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0 && int.TryParse(body[..sep], out var status))
        {
            if (status is < 200 or >= 300)
                return (false, "", status == 401, status);
            body = body[(sep + 2)..];
        }
        if (body.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || body.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return (false, "", true, null);
        if (body.Contains("Request Blocked", StringComparison.OrdinalIgnoreCase) || body.Contains("\"code\":429", StringComparison.OrdinalIgnoreCase))
            return (false, "", false, 429);
        return (true, body, false, 200);
    }

    /// <summary>页面自身请求捕获（封送 UI 线程）。</summary>
    public Task<(bool Ok, string Body, int? HttpStatus)> FetchUsageViaPageAsync(
        string navigateTo, string pathFilter, CancellationToken ct = default)
        => _dispatcher.InvokeAsync(() => FetchUsageViaPageCoreAsync(navigateTo, pathFilter, ct),
            System.Windows.Threading.DispatcherPriority.Normal, ct).Task.Unwrap();

    /// <summary>
    /// 页面自身请求捕获：导航到目标页，拦截其发出的 API 请求响应（页面请求带完整风控上下文，
    /// 注入的 fetch/XHR 会被静默返回空对象）。返回匹配路径的响应体。
    /// </summary>
    private async Task<(bool Ok, string Body, int? HttpStatus)> FetchUsageViaPageCoreAsync(
        string navigateTo, string pathFilter, CancellationToken ct = default)
    {
        var wv2 = GetWebView(ct);
        var tcs = new TaskCompletionSource<(int StatusCode, string Body)>(TaskCreationOptions.RunContinuationsAsynchronously);

        // WebResourceResponseReceived：响应到达后触发（WebResourceRequested 是请求前事件，Response 为 null）
        void handler(object? s, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            try
            {
                if (!e.Request.Uri.StartsWith("https://platform.deepseek.com" + pathFilter, StringComparison.OrdinalIgnoreCase)) return;
                Logger.Log($"page capture hit: {e.Request.Uri}");
                _ = ReadResponseAsync(e.Response);
            }
            catch (Exception ex)
            {
                Logger.LogException("page capture", ex);
            }
        }

        async Task ReadResponseAsync(CoreWebView2WebResourceResponseView resp)
        {
            if (tcs.Task.IsCompleted) return;
            // 串行化读取：避免同一响应被并发读取时流被撕裂；后到者若 tcs 已完成直接跳过
            var entered = false;
            try
            {
                await _captureLock.WaitAsync(ct);
                entered = true;
                if (tcs.Task.IsCompleted) return;
                using var stream = await resp.GetContentAsync();
                if (stream is null)
                {
                    Logger.Log("page capture: content null");
                    tcs.TrySetResult((resp.StatusCode, ""));
                    return;
                }
                using var reader = new System.IO.StreamReader(stream);
                var body = await reader.ReadToEndAsync(ct);
                Logger.Log($"page capture body: {(body.Length > 400 ? body[..400] : body)}");
                tcs.TrySetResult((resp.StatusCode, body));
            }
            catch (Exception ex)
            {
                Logger.LogException("page capture read", ex);
            }
            finally
            {
                if (entered) _captureLock.Release();
            }
        }

        try
        {
            wv2.WebResourceResponseReceived += handler;
            // 清缓存强制页面重新发请求（首次加载已被缓存）
            await wv2.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
            wv2.Navigate(navigateTo);
            var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(40), ct);
            return (response.Body.Length > 0 && response.StatusCode is >= 200 and < 300,
                response.Body, response.StatusCode);
        }
        catch (TimeoutException)
        {
            Logger.Log("deepseek page capture: timeout (page did not request the API)");
            return (false, "", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogException("deepseek page capture", ex);
            return (false, "", null);
        }
        finally
        {
            wv2.WebResourceResponseReceived -= handler;
        }
    }

    /// <summary>会话自检：请求用户汇总接口，成功即会话有效。</summary>
    public async Task<bool> CheckSessionAsync(CancellationToken ct = default)
    {
        var (ok, body, _, _) = await FetchAsync("/api/v0/users/get_user_summary", ct);
        if (ok && body.Length > 0)
            Logger.Log($"deepseek summary: {(body.Length > 500 ? body[..500] : body)}");
        if (ok && body == "{}")
        {
            var pageUrl = await _dispatcher.InvokeAsync(() => GetWebView(ct).Source,
                System.Windows.Threading.DispatcherPriority.Normal, ct);
            Logger.Log($"deepseek page url: {pageUrl}");
            var (okK, bodyK, _, _) = await FetchAsync("/api/v0/users/get_api_keys", ct);
            Logger.Log($"deepseek api_keys: ok={okK} body={(bodyK.Length > 500 ? bodyK[..500] : bodyK)}");
        }
        IsLoggedIn = ok;
        return ok;
    }

    public void Dispose()
    {
        _ready.Dispose();
        _captureLock.Dispose();
    }

    // ---- Cookie 持久化（WebView2 session cookie 进程退出即失效，登录后保存、重启恢复） ----

    private static readonly string CookieStoreTarget = AppIdentity.DeepSeekCookiesTarget;

    /// <summary>把 platform.deepseek.com 域的 cookie 保存到凭据管理器（登录成功后调用）。</summary>
    private async Task SaveCookiesCoreAsync()
    {
        try
        {
            var wv2 = GetWebView();
            var cookies = await wv2.CookieManager.GetCookiesAsync("https://platform.deepseek.com");
            var list = new List<Dictionary<string, object?>>();
            foreach (var c in cookies)
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["name"] = c.Name, ["value"] = c.Value, ["domain"] = c.Domain,
                    ["path"] = c.Path, ["expires"] = c.Expires.ToString("o"),
                    ["isHttpOnly"] = c.IsHttpOnly, ["isSecure"] = c.IsSecure,
                });
            }
            var json = System.Text.Json.JsonSerializer.Serialize(list);
            CredentialStore.SaveSecret(CookieStoreTarget, json);
            Logger.Log($"deepseek cookies saved: {list.Count} items");
        }
        catch (Exception ex)
        {
            Logger.LogException("save deepseek cookies", ex);
        }
    }

    /// <summary>从凭据管理器恢复 cookie 到 WebView2（重启后调用，成功后会话自检）。</summary>
    private async Task<bool> RestoreCookiesCoreAsync(CancellationToken ct = default)
    {
        try
        {
            if (!CredentialStore.TryReadSecret(CookieStoreTarget, out var json) || string.IsNullOrEmpty(json))
                return false;
            var wv2 = GetWebView();
            var list = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(json) ?? new();
            foreach (var item in list)
            {
                var cookie = wv2.CookieManager.CreateCookie(
                    item["name"] as string ?? "",
                    item["value"] as string ?? "",
                    item["domain"] as string ?? "platform.deepseek.com",
                    item["path"] as string ?? "/");
                if (DateTimeOffset.TryParse(item["expires"] as string, out var exp) && exp > DateTimeOffset.UtcNow)
                    cookie.Expires = exp.UtcDateTime;
                wv2.CookieManager.AddOrUpdateCookie(cookie);
            }
            Logger.Log($"deepseek cookies restored: {list.Count} items");
            var ok = await CheckSessionAsync(ct);
            Logger.Log($"deepseek session after restore: {ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Logger.LogException("restore deepseek cookies", ex);
            return false;
        }
    }
}
