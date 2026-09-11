using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.UI;

namespace TokenConsumptionMonitoring.Services;

/// <summary>每个页面独立的 WebView2 会话；不导出 Cookie，也不把 API Key 注入网页。</summary>
public sealed class FireworksConsoleSession(Dispatcher dispatcher) : IFireworksConsoleBalanceReader, IDisposable
{
    private readonly Dictionary<string, FireworksBalanceWindow> _windows = new();
    private bool _disposed;

    public Task<FireworksConsoleBalance> ReadAsync(PageConfigRecord page, CancellationToken ct)
        => dispatcher.InvokeAsync(() => ReadCoreAsync(page, ct), DispatcherPriority.Normal, ct).Task.Unwrap();

    private Task<FireworksConsoleBalance> ReadCoreAsync(PageConfigRecord page, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_windows.ContainsKey(page.Id) && !Directory.Exists(ProfilePath(page.Id)))
            throw new QueryTransportException(CandidateStatus.AuthRequired, "请在编辑页面中点击登录，连接 Fireworks 控制台");
        return GetWindow(page).ReadAsync(ct);
    }

    public async Task ShowLoginAsync(PageConfigRecord page, Func<Task<string?>> validate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var window = GetWindow(page);
        window.ValidateLoginAsync = validate;
        window.ShowActivated = true;
        window.Show();
        window.Activate();
        await window.ShowLoginAsync();
    }

    private FireworksBalanceWindow GetWindow(PageConfigRecord page)
    {
        if (_windows.TryGetValue(page.Id, out var existing)) return existing;
        var window = new FireworksBalanceWindow(ProfilePath(page.Id)) { Title = $"Fireworks 余额登录 — {page.Name}" };
        _windows.Add(page.Id, window);
        // WebView2 需要原生窗口宿主；后台恢复会话时不激活窗口。
        window.ShowActivated = false;
        window.Show();
        window.Hide();
        return window;
    }

    private static string ProfilePath(string pageId) => Path.Combine(SettingsStore.DataDirectory, "FireworksProfiles",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pageId))));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _windows.Values)
        {
            window.AllowClose = true;
            window.Close();
        }
        _windows.Clear();
    }
}
