using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using TunHotspot.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using Windows.UI.ViewManagement;

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
    private readonly bool _qa;
    private bool _shown, _closed, _menuShown;
    private int _x, _y;

    internal TrayMenuWindow(TrayMenuState state, Action<string> command, bool qa = false)
    {
        _state = state; _command = command; _qa = qa;
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
        ToolTipService.SetToolTip(exit, state.MachineConnected ? "退出后，机器采集组件继续运行" : "退出后，当前会话监测停止");
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
        int round = 2;
        _ = DwmSetWindowAttribute(WinRT.Interop.WindowNative.GetWindowHandle(this), 33, ref round, sizeof(int));
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
        _x = x; _y = y;
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

    private void KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape) { Dismiss(); e.Handled = true; return; }
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)) return;
        var enabled = _items.Where(x => x.IsEnabled).ToList(); if (enabled.Count == 0) return;
        object focused = FocusManager.GetFocusedElement(_root.XamlRoot);
        int index = enabled.FindIndex(x => ReferenceEquals(x, focused));
        index = e.Key switch { VirtualKey.Home => 0, VirtualKey.End => enabled.Count - 1, VirtualKey.Up => (index - 1 + enabled.Count) % enabled.Count, _ => (index + 1) % enabled.Count };
        enabled[index].Focus(FocusState.Keyboard); e.Handled = true;
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

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, int size);
}
