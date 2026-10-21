using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using UiColor = Windows.UI.Color;
using XamlButton = Microsoft.UI.Xaml.Controls.Button;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;

namespace FrpDesk;

/// <summary>
/// 托盘右键菜单。用 WinUI 而不是 WinForms 自绘，才能和界面里的 MenuFlyout 共用同一套字体、行高和悬停反馈。
/// </summary>
internal sealed class TrayMenuWindow : Window
{
    private const double MenuWidth = 180;
    // 卡片内边距 5×2 + 边框 1×2 + 三个 32 的行 + 分隔线连同上下留白 9
    private const double MenuHeight = 117;
    private const double CursorGap = 8;
    private const double CornerRadiusValue = 8;

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmcpRound = 2;
    private const int MonitorDefaultToNearest = 2;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Grid _root;
    private readonly XamlButton _openButton;
    private readonly XamlButton _stopButton;
    private readonly XamlButton _exitButton;

    private readonly SolidColorBrush _surfaceBrush = new(Colors.White);
    private readonly SolidColorBrush _hoverBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _pressedBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _inkBrush = new(Colors.Black);
    private readonly SolidColorBrush _inkHoverBrush = new(Colors.Black);
    private readonly SolidColorBrush _mutedBrush = new(Colors.Gray);
    private readonly SolidColorBrush _borderBrush = new(Colors.Gray);
    private readonly SolidColorBrush _transparentBrush = new(Colors.Transparent);

    private UiColor _borderColor;
    private bool _dark;
    private bool _isOpen;

    public event EventHandler? OpenRequested;
    public event EventHandler? StopAllRequested;
    public event EventHandler? ExitRequested;

    public TrayMenuWindow()
    {
        Title = "FrpDesk";
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _appWindow.IsShownInSwitchers = false;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var card = new Border
        {
            Background = _surfaceBrush,
            BorderBrush = _borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CornerRadiusValue),
            Padding = new Thickness(5)
        };
        var panel = new StackPanel();
        _openButton = CreateItem("打开主界面", (_, _) =>
        {
            HideMenu();
            OpenRequested?.Invoke(this, EventArgs.Empty);
        });
        _stopButton = CreateItem("全部停止", (_, _) =>
        {
            HideMenu();
            StopAllRequested?.Invoke(this, EventArgs.Empty);
        });
        _exitButton = CreateItem("退出", (_, _) =>
        {
            HideMenu();
            ExitRequested?.Invoke(this, EventArgs.Empty);
        });
        panel.Children.Add(_openButton);
        panel.Children.Add(_stopButton);
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(9, 4, 9, 4),
            Background = _borderBrush
        });
        panel.Children.Add(_exitButton);
        card.Child = panel;

        _root = new Grid { Background = _surfaceBrush };
        _root.Children.Add(card);
        _root.KeyDown += Root_KeyDown;
        Content = _root;

        Activated += TrayMenuWindow_Activated;
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void ApplyPalette(
        UiColor surface,
        UiColor hover,
        UiColor pressed,
        UiColor ink,
        UiColor inkHover,
        UiColor muted,
        UiColor border,
        bool dark)
    {
        _surfaceBrush.Color = surface;
        _hoverBrush.Color = hover;
        _pressedBrush.Color = pressed;
        _inkBrush.Color = ink;
        _inkHoverBrush.Color = inkHover;
        _mutedBrush.Color = muted;
        _borderBrush.Color = border;
        _borderColor = border;
        _dark = dark;
        if (_isOpen) ApplyChrome();
    }

    public void ShowAtCursor(bool canStopAll)
    {
        _stopButton.IsEnabled = canStopAll;
        ApplyChrome();
        _appWindow.MoveAndResize(CalculateBounds());
        _isOpen = true;
        Activate();
        _ = SetForegroundWindow(_hwnd);
        _openButton.Focus(FocusState.Pointer);
    }

    public void HideMenu()
    {
        if (!_isOpen) return;
        _isOpen = false;
        _appWindow.Hide();
    }

    private XamlButton CreateItem(string text, RoutedEventHandler onClick)
    {
        var button = new XamlButton
        {
            Content = text,
            Height = 32,
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = _transparentBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Foreground = _inkBrush,
            FontSize = 14
        };
        // 默认模板的背景/文字色走主题资源，这里换成应用令牌，深色与高对比度才不会跳回系统灰。
        button.Resources["ButtonBackgroundPointerOver"] = _hoverBrush;
        button.Resources["ButtonBackgroundPressed"] = _pressedBrush;
        button.Resources["ButtonBackgroundDisabled"] = _transparentBrush;
        button.Resources["ButtonBorderBrush"] = _transparentBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = _transparentBrush;
        button.Resources["ButtonBorderBrushPressed"] = _transparentBrush;
        button.Resources["ButtonBorderBrushDisabled"] = _transparentBrush;
        button.Resources["ButtonForegroundPointerOver"] = _inkHoverBrush;
        button.Resources["ButtonForegroundPressed"] = _inkHoverBrush;
        button.Resources["ButtonForegroundDisabled"] = _mutedBrush;
        button.IsEnabledChanged += (_, _) =>
            button.Foreground = button.IsEnabled ? _inkBrush : _mutedBrush;
        button.Click += onClick;
        return button;
    }

    private void TrayMenuWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) HideMenu();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                HideMenu();
                e.Handled = true;
                break;
            case VirtualKey.Down:
                MoveFocus(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveFocus(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveFocus(int delta)
    {
        var items = new[] { _openButton, _stopButton, _exitButton };
        var enabled = items.Where(item => item.IsEnabled).ToList();
        if (enabled.Count == 0) return;
        var focused = FocusManager.GetFocusedElement(_root.XamlRoot) as XamlButton;
        var current = focused is null ? -1 : enabled.IndexOf(focused);
        var next = current < 0 ? 0 : (current + delta + enabled.Count) % enabled.Count;
        enabled[next].Focus(FocusState.Keyboard);
    }

    private RectInt32 CalculateBounds()
    {
        _ = GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        _ = GetMonitorInfo(monitor, ref info);
        var scale = 1f;
        if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96f;

        var width = (int)Math.Round(MenuWidth * scale);
        var height = (int)Math.Round(MenuHeight * scale);
        var gap = (int)Math.Round(CursorGap * scale);
        var work = info.rcWork;

        int x, y;
        if (cursor.Y >= work.Bottom)
        {
            x = cursor.X - width;
            y = work.Bottom - height - gap;
        }
        else if (cursor.Y < work.Top)
        {
            x = cursor.X - width;
            y = work.Top + gap;
        }
        else if (cursor.X < work.Left)
        {
            x = work.Left + gap;
            y = cursor.Y - height;
        }
        else if (cursor.X >= work.Right)
        {
            x = work.Right - width - gap;
            y = cursor.Y - height;
        }
        else
        {
            x = cursor.X;
            y = cursor.Y + gap;
            if (y + height > work.Bottom) y = cursor.Y - height - gap;
        }

        x = Math.Max(work.Left + gap, Math.Min(x, work.Right - width - gap));
        y = Math.Max(work.Top + gap, Math.Min(y, work.Bottom - height - gap));
        return new RectInt32(x, y, width, height);
    }

    private void ApplyChrome()
    {
        var dark = _dark ? 1 : 0;
        _ = DwmSetWindowAttributeInt(_hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        var corner = DwmcpRound;
        _ = DwmSetWindowAttributeInt(_hwnd, DwmwaWindowCornerPreference, ref corner, sizeof(int));
        var colorRef = (uint)(_borderColor.R | (_borderColor.G << 8) | (_borderColor.B << 16));
        _ = DwmSetWindowAttributeUInt(_hwnd, DwmwaBorderColor, ref colorRef, sizeof(uint));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public MonitorRect rcMonitor;
        public MonitorRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out MonitorPoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(MonitorPoint point, int flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeInt(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeUInt(IntPtr hwnd, int attribute, ref uint value, int size);
}
