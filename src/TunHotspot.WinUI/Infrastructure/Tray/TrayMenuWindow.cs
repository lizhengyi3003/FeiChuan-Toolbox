using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Runtime.InteropServices;
using TunHotspot.Core;
using Windows.Foundation;
using Windows.Graphics;

namespace TunHotspot;

internal sealed record TrayMenuState(UserSettings Settings,bool MachineConnected,bool ScanBusy,int Unread,bool StartupAvailable=true);

// WinUI supplies menu item states, spacing, theme and separators. The desktop window anchors it.
internal sealed class TrayMenuWindow : Window
{
    private readonly Grid _root = new();
    private readonly MenuFlyout _menu;
    private MenuFlyoutPresenter? _presenter;
    private readonly List<MenuFlyoutItem> _items = [];
    private readonly TrayMenuState _state;
    private readonly Action<string> _command;
    private bool _shown, _closed, _menuShown;

    internal TrayMenuWindow(TrayMenuState state, Action<string> command)
    {
        _state = state; _command = command;
        Title = "飞船的小工具箱 · 托盘菜单";
        ExtendsContentIntoTitleBar = true;
        AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        _menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        Add("打开工具箱", "\uE80F", "open", "O");
        Add("兼容热点", "\uE701", "hotspot", "H");
        Add("Kumiryo 一键配置", "\uE713", "configuration", "K");
        Add("全局扫描（管理员）", "\uE721", "scan", "G", !state.ScanBusy);
        Add(state.Unread > 0 ? $"通知与关注（{state.Unread}）" : "通知与关注", "\uE7F4", "alerts", "N");
        Add(state.Settings.Paused ? "恢复监测" : "暂停监测", state.Settings.Paused ? "\uE768" : "\uE769", "pause", "P",state.StartupAvailable);
        _menu.Items.Add(new MenuFlyoutSeparator());
        Add("设置", "\uE713", "settings", "S");
        _menu.Items.Add(new MenuFlyoutSeparator());
        var exit = Add("退出托盘", "\uE8BB", "exit", "Q");
        AutomationProperties.SetHelpText(exit, state.MachineConnected ? "退出后，机器采集组件继续运行" : "退出后，当前会话监测停止");
        Content = _root;
        ApplyAppearance();
        _root.Loaded += (_, _) => ShowMenu();
        _menu.Opened += (_, _) =>
        {
            _presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(_root.XamlRoot)
                .Select(x => x.Child).OfType<MenuFlyoutPresenter>().FirstOrDefault();
            if (_presenter is not null) AutomationProperties.SetAutomationId(_presenter, "TrayMenu.Menu");
        };
        _menu.Closed += (_, _) => Dismiss();
        Closed += (_, _) => _closed = true;
    }

    private MenuFlyoutItem Add(string text, string glyph, string command, string access, bool enabled = true)
    {
        var item = new MenuFlyoutItem
        {
            Text = text, Icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16 },
            FontFamily = new FontFamily("Segoe UI Variable"), FontSize = 14,
            MinHeight = 36, Padding = new Thickness(12, 7, 12, 7), IsEnabled = enabled, AccessKey = access
        };
        AutomationProperties.SetName(item, text);
        AutomationProperties.SetAutomationId(item, "TrayMenu." + command);
        item.Click += (_, _) => { Dismiss(); _command(command); };
        _items.Add(item); _menu.Items.Add(item);
        return item;
    }

    private void ApplyAppearance()
    {
        _root.RequestedTheme = _state.Settings.Theme switch { "浅色" => ElementTheme.Light, "深色" => ElementTheme.Dark, _ => ElementTheme.Default };
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 240d));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, _root.RequestedTheme));
        _menu.MenuFlyoutPresenterStyle = style;
        // Use the menu theme instead of the large-window Mica card. System accessibility takes precedence.
        _root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    internal void ShowAt(int x, int y)
    {
        // WinUI can enforce a minimum HWND size. Clip the anchor itself before
        // showing it; the unconstrained MenuFlyout draws in its own popup.
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) throw new Win32Exception("无法创建托盘菜单锚点区域。");
        nint anchor = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Suppress the anchor's DWM frame and shadow as well as its client area.
        int disableNonClientRendering = 1;
        _ = DwmSetWindowAttribute(anchor, 2, ref disableNonClientRendering, sizeof(int));
        if (SetWindowRgn(anchor, region, true) == 0)
        {
            _ = DeleteObject(region);
            throw new Win32Exception("无法隐藏托盘菜单锚点。");
        }
        AppWindow.MoveAndResize(new RectInt32(x, y, 1, 1));
        _shown = true; Activate();
        if (_root.IsLoaded) ShowMenu();
        AppLog.Write("tray-menu-opened", new { framework = "WinUI 3", control = "MenuFlyoutPresenter", _state.Settings.Theme, systemMenuTypography = true });
    }

    private void ShowMenu()
    {
        if (!_shown || _closed || _menuShown) return;
        _menuShown = true;
        _menu.ShowAt(_root, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = new Point(0, 0), Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight
        });
    }

    internal void Dismiss() { if (!_closed) { _closed = true; _menu.Hide(); Close(); } }

    internal async Task CaptureForQa(string name)
    {
        for (int attempt = 0; _presenter is null && attempt < 30; attempt++) await Task.Delay(50);
        if (_presenter is null) throw new InvalidOperationException("托盘菜单尚未显示。");
        await Task.Delay(300);
        // RenderTargetBitmap cannot capture a native popup's separate compositor surface.
        // Test-TrayUi captures the visible popup from its actual UI Automation bounds.
        AppLog.Write("qa-tray-ready", new { name, _presenter.ActualWidth, _presenter.ActualHeight, controls = _items.Count });
    }

    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint region);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
