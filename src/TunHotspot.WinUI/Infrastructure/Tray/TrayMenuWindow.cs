using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using TunHotspot.Core;
using Windows.Graphics;
using Windows.System;

namespace TunHotspot;

internal sealed record TrayMenuState(UserSettings Settings, bool MachineConnected, bool ScanBusy, int Unread, bool StartupAvailable = true);

internal sealed class TrayMenuWindow : Window
{
    private const int MenuWidth = 248;
    private const int ItemHeight = 36;
    private const int SeparatorHeight = 7;
    private const int FrameHeight = 12;
    private const uint CornerPreferenceAttribute = 33;
    private const uint BorderColorAttribute = 34;
    private const uint RoundedCorners = 2;
    private const uint NoFrameBorder = 0xFFFFFFFE;
    private const uint CalculateClientArea = 0x0083;
    private const uint FrameChangedWithoutMoving = 0x0037;
    private const nuint FrameSubclassId = 1;
    private static readonly SubclassProc FrameProcedure = ProcessFrameMessage;
    private readonly Border _root;
    private readonly StackPanel _rows = new();
    private readonly List<Button> _items = [];
    private readonly Action<string> _command;
    private int _separatorCount;
    private bool _activated, _closed;

    internal TrayMenuWindow(TrayMenuState state, Action<string> command)
    {
        _command = command;
        Title = "飞船的小工具箱 · 托盘菜单";
        ExtendsContentIntoTitleBar = true;
        AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;

        _root = (Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Background="{ThemeResource SolidBackgroundFillColorBaseBrush}"
                    Padding="6"/>
            """);
        if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported() &&
            !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast &&
            new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled)
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            _root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        _root.RequestedTheme = state.Settings.Theme switch
        {
            "浅色" => ElementTheme.Light,
            "深色" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        AutomationProperties.SetAutomationId(_root, "TrayMenu.Menu");
        _root.Child = _rows;
        _root.PreviewKeyDown += OnKeyDown;
        Content = _root;

        Add("打开工具箱", "\uE80F", "open", "O");
        Add("兼容热点", "\uE701", "hotspot", "H");
        Add("Kumiryo 一键配置", "\uE713", "configuration", "K");
        Add("全局扫描（管理员）", "\uE721", "scan", "G", !state.ScanBusy);
        Add(state.Unread > 0 ? $"通知与关注（{state.Unread}）" : "通知与关注", "\uE7F4", "alerts", "N");
        Add(state.Settings.Paused ? "恢复监测" : "暂停监测", state.Settings.Paused ? "\uE768" : "\uE769", "pause", "P", state.StartupAvailable);
        AddSeparator();
        Add("设置", "\uE713", "settings", "S");
        AddSeparator();
        var exit = Add("退出托盘", "\uE8BB", "exit", "Q");
        AutomationProperties.SetHelpText(exit, state.MachineConnected ? "退出后，机器采集组件继续运行" : "退出后，当前会话监测停止");

        _root.Loaded += (_, _) => _items[0].Focus(FocusState.Programmatic);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) _activated = true;
            else if (_activated) Dismiss();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _ = RemoveWindowSubclass(WinRT.Interop.WindowNative.GetWindowHandle(this), FrameProcedure, FrameSubclassId);
        };
    }

    private Button Add(string text, string glyph, string command, string access, bool enabled = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new FontIcon
        {
            Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16,
            Width = 20, VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new TrayMenuButton
        {
            Content = row, FontSize = 14, Height = ItemHeight, Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(8), IsEnabled = enabled, AccessKey = access
        };
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var style) && style is Style subtle)
            button.Style = subtle;
        AutomationProperties.SetName(button, text);
        AutomationProperties.SetAutomationId(button, "TrayMenu." + command);
        button.Click += (_, _) => { Dismiss(); _command(command); };
        _items.Add(button);
        _rows.Children.Add(button);
        return button;
    }

    private void AddSeparator()
    {
        _rows.Children.Add((Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Background="{ThemeResource DividerStrokeColorDefaultBrush}" Height="1" Margin="5,3"/>
            """));
        _separatorCount++;
    }

    internal void ShowAt(int x, int y)
    {
        var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Move(new PointInt32(area.X, area.Y));
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = Math.Max(96u, GetDpiForWindow(window)) / 96d;
        int width = (int)Math.Ceiling(MenuWidth * scale);
        int height = (int)Math.Ceiling((_items.Count * ItemHeight + _separatorCount * SeparatorHeight + FrameHeight) * scale);
        int left = x - width;
        int top = y - height - (int)(6 * scale);
        // Explorer leaves its overflow surface open behind a notification icon's context menu.
        // Cover that surface when the menu was invoked from inside it, without changing Explorer state.
        nint overflow = FindWindow("TopLevelWindowForOverflowXamlIsland", null);
        if (overflow != 0 && IsWindowVisible(overflow) && GetWindowRect(overflow, out var bounds) &&
            x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom)
        {
            width = Math.Min(area.Width, Math.Max(width, bounds.Right - bounds.Left + (int)(16 * scale)));
            left = bounds.Left - (width - (bounds.Right - bounds.Left)) / 2;
            top = bounds.Bottom - height;
        }
        left = Math.Clamp(left, area.X, Math.Max(area.X, area.X + area.Width - width));
        top = Math.Clamp(top, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
        bool frameRemoved = SetWindowSubclass(window, FrameProcedure, FrameSubclassId, 0)
            && SetWindowPos(window, 0, 0, 0, 0, 0, FrameChangedWithoutMoving);
        AppWindow.MoveAndResize(new RectInt32(left, top, width, height));
        // Let DWM clip the acrylic surface itself so its edge and window corners share a single outline.
        uint corners = RoundedCorners;
        uint border = NoFrameBorder;
        int cornerResult = DwmSetWindowAttribute(window, CornerPreferenceAttribute, ref corners, sizeof(uint));
        int borderResult = DwmSetWindowAttribute(window, BorderColorAttribute, ref border, sizeof(uint));
        Activate();
        AppLog.Write("tray-menu-opened", new { framework = "WinUI 3", control = "TrayMenuWindow", frameRemoved, cornerResult, borderResult });
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            Dismiss();
            return;
        }
        if (args.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)) return;
        var active = _items.Where(item => item.IsEnabled).ToList();
        int current = active.FindIndex(item => item.FocusState != FocusState.Unfocused);
        int index = args.Key switch
        {
            VirtualKey.Home => 0,
            VirtualKey.End => active.Count - 1,
            VirtualKey.Up => current <= 0 ? active.Count - 1 : current - 1,
            _ => (current + 1) % active.Count
        };
        active[index].Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    internal void Dismiss()
    {
        if (_closed) return;
        _closed = true;
        Close();
    }

    internal async Task CaptureForQa(string name)
    {
        for (int attempt = 0; !_root.IsLoaded && attempt < 30; attempt++) await Task.Delay(50);
        if (!_root.IsLoaded) throw new InvalidOperationException("托盘菜单尚未显示。");
        AppLog.Write("qa-tray-ready", new { name, _root.ActualWidth, _root.ActualHeight, controls = _items.Count });
    }

    private sealed class TrayMenuButton : Button
    {
        protected override AutomationPeer OnCreateAutomationPeer() => new MenuButtonPeer(this);

        private sealed class MenuButtonPeer(Button owner) : ButtonAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.MenuItem;
        }
    }

    private static nint ProcessFrameMessage(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
        => message == CalculateClientArea ? 0 : DefSubclassProc(window, message, wParam, lParam);

    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref uint value, uint size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
}
