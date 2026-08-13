using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace TokenUsageMonitorV3.UI;

public partial class FloatingWindow : Window
{
    public event Action? OpenPanelRequested;
    public event Action? AccountSwitchRequested;
    public event Action? RefreshRequested;
    /// <summary>锁定状态切换（持久化由 App 负责）。</summary>
    public event Action<bool>? LockToggleRequested;

    private bool _dragging;
    private Point _dragStart;
    private DateTime _lastClickTime;
    private bool _locked;

    // XAML 属性语法生成的画刷可能被冻结（修改无效），改用代码创建的可变画刷
    // 背景：纯色黑；边框：随不透明度同步淡出，0% 时不留轮廓
    private readonly SolidColorBrush _bgBrush = new(System.Windows.Media.Colors.Black);
    private readonly SolidColorBrush _borderBrush = new(System.Windows.Media.Colors.White) { Opacity = 0.2 };

    public FloatingWindow()
    {
        InitializeComponent();
        Root.Background = _bgBrush;
        Root.BorderBrush = _borderBrush;
        Loaded += (_, _) => PositionBottomRight();
    }

    private void PositionBottomRight()
    {
        var wa = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = wa.Right / dpi.DpiScaleX - Width - 16;
        Top = wa.Bottom / dpi.DpiScaleY - Height - 16;
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button || _locked) return;   // 锁定后不可拖动
        _dragging = true;
        _dragStart = e.GetPosition(this);
        Root.CaptureMouse();
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        Left += pos.X - _dragStart.X;
        Top += pos.Y - _dragStart.Y;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Root.ReleaseMouseCapture();
        var pos = e.GetPosition(this);
        if ((pos - _dragStart).Length < 4)
        {
            var now = DateTime.Now;
            if ((now - _lastClickTime).TotalMilliseconds < 300)
            {
                _lastClickTime = default;
                OpenPanelRequested?.Invoke();
            }
            else
            {
                _lastClickTime = now;
            }
        }
    }

    private void AccountBtn_Click(object sender, RoutedEventArgs e) => AccountSwitchRequested?.Invoke();

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    /// <summary>不透明度标定：100%=纯色黑背景（完全不透明）；0%=背景层与边框完全消失，仅剩内容浮于桌面；中间值纯色黑均匀变淡。</summary>
    public void SetBackgroundOpacity(int percent)
    {
        _bgBrush.Opacity = percent / 100.0;
        _borderBrush.Opacity = 0.2 * percent / 100.0;
    }

    /// <summary>锁定：不可拖动且置顶；解锁：可拖动、其他程序全屏可覆盖。</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        Topmost = locked;
        LockBtn.Foreground = new SolidColorBrush(locked
            ? System.Windows.Media.Color.FromRgb(0xEE, 0xF1, 0xF5)
            : System.Windows.Media.Color.FromRgb(0x8A, 0x8F, 0x98));
        LockBtn.ToolTip = locked ? "已锁定：不可拖动且置顶（点击解锁）" : "锁定：不可拖动且置顶";
    }

    private void Lock_Click(object sender, RoutedEventArgs e)
    {
        SetLocked(!_locked);
        LockToggleRequested?.Invoke(_locked);
    }

    private void OpenPanel_Click(object sender, RoutedEventArgs e) => OpenPanelRequested?.Invoke();
}
