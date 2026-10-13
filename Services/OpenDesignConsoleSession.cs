using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using TokenConsumptionMonitoring.Models;
using TokenConsumptionMonitoring.Models.Usage;
using TokenConsumptionMonitoring.Services.QueryMethods;
using TokenConsumptionMonitoring.UI;

namespace TokenConsumptionMonitoring.Services;

/// <summary>页面与工作空间共同隔离 WebView2 配置；会话秘密不离开浏览器配置目录。</summary>
public sealed class OpenDesignConsoleSession(Dispatcher dispatcher, string? profileRoot = null) : IOpenDesignConsoleReader, IDisposable
{
    private readonly Dictionary<string, OpenDesignConsoleWindow> _windows = new();
    private bool _disposed;

    public Task<JsonElement> ReadAsync(PageConfigRecord page, bool windows, CancellationToken ct)
        => dispatcher.InvokeAsync(() => ReadCoreAsync(page, windows, ct), DispatcherPriority.Normal, ct).Task.Unwrap();

    private Task<JsonElement> ReadCoreAsync(PageConfigRecord page, bool windows, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = SessionKey(page);
        if (!_windows.ContainsKey(key) && !Directory.Exists(ProfilePath(key)))
            throw new QueryTransportException(CandidateStatus.AuthRequired, "请在编辑页面中点击登录，连接 OpenDesign 控制台");
        return GetWindow(page).ReadAsync(windows, ct);
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

    private OpenDesignConsoleWindow GetWindow(PageConfigRecord page)
    {
        var key = SessionKey(page);
        if (_windows.TryGetValue(key, out var existing)) return existing;
        OpenDesignConsoleMethod.TryGetWorkspace(page, out var workspace);
        var window = new OpenDesignConsoleWindow(ProfilePath(key), workspace) { Title = $"OpenDesign 登录 — {page.Name}" };
        _windows.Add(key, window);
        window.ShowActivated = false;
        window.Show();
        window.Hide();
        return window;
    }

    internal static string SessionKey(PageConfigRecord page)
    {
        if (!OpenDesignConsoleMethod.TryGetWorkspace(page, out var workspace))
            throw new QueryTransportException(CandidateStatus.Unsupported, "请在私有兼容配置中填写 OpenDesign 工作空间");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(page.Id + "|" + workspace)));
    }
    private string ProfilePath(string key) => Path.Combine(profileRoot ?? Path.Combine(SettingsStore.DataDirectory, "OpenDesignProfiles"), key);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _windows.Values) { window.AllowClose = true; window.Close(); }
        _windows.Clear();
    }
}
