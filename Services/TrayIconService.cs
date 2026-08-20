using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TokenConsumptionMonitoring.Models;

namespace TokenConsumptionMonitoring.Services;

/// <summary>托盘图标 + 气球通知。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private IntPtr _currentHIcon;

    public event Action? LeftClick;
    public event Action? RefreshRequested;
    public event Action? OpenPanelRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _icon = new NotifyIcon
        {
            Text = "Token Usage Monitoring — 未连接",
            Visible = true,
            Icon = MakeIcon(Color.Gray),
        };

        var menu = new ContextMenuStrip();
        var refresh = new ToolStripMenuItem("立即刷新", null, (_, _) => RefreshRequested?.Invoke());
        var panel = new ToolStripMenuItem("配置页面", null, (_, _) => OpenPanelRequested?.Invoke());
        var settings = new ToolStripMenuItem("设置", null, (_, _) => SettingsRequested?.Invoke());
        var exit = new ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke());
        menu.Items.AddRange(new ToolStripItem[] { refresh, panel, new ToolStripSeparator(), settings, new ToolStripSeparator(), exit });
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => LeftClick?.Invoke();
    }

    public void SetState(ConnectionStatus status, AlertLevel level)
    {
        var color = level switch
        {
            AlertLevel.Critical => Color.Red,
            AlertLevel.Warn => Color.Goldenrod,
            _ => status switch
            {
                ConnectionStatus.Ok => Color.ForestGreen,
                ConnectionStatus.AuthError => Color.Red,
                ConnectionStatus.Offline => Color.Gray,
                _ => Color.DimGray,
            },
        };

        var newIcon = MakeIcon(color);
        _icon.Icon = newIcon;
        if (_currentHIcon != IntPtr.Zero) { DestroyIcon(_currentHIcon); }
        _currentHIcon = newIcon.Handle;

        _icon.Text = level switch
        {
            AlertLevel.Critical => "Token Usage — 临界告警",
            AlertLevel.Warn => "Token Usage — 注意",
            _ => status switch
            {
                ConnectionStatus.AuthError => "Token Usage — 会话失效",
                ConnectionStatus.Offline => "Token Usage — 连接中断",
                ConnectionStatus.Ok => "Token Usage — 正常",
                _ => "Token Usage",
            },
        };
    }

    public void Balloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(6000);
    }

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            TrayIconDesigns.FusionWaveCoin(g, color);   // 方案 A：脑波币（币形轮廓 + 脑波曲线）
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_currentHIcon != IntPtr.Zero) { DestroyIcon(_currentHIcon); _currentHIcon = IntPtr.Zero; }
    }
}
