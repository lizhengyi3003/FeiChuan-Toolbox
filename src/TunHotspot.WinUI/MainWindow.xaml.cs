using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace TunHotspot;

public sealed partial class MainWindow : Window
{
    private readonly HotspotService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private HotspotSnapshot? _snapshot;
    private bool _refreshing;
    private bool _busy;
    private bool _closed;
    private int _operationVersion;
    private int _startRequests;
    private int _stopRequests;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleArea);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "TunHotspot.ico"));
        var scale = GetDpiForWindow(handle) / 96.0;
        var size = new SizeInt32((int)(610 * scale), (int)(570 * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2,
            area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = false;
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _closed = true; _timer.Stop(); AppLog.Write("window-closed", new { networkChanged = false }); };
        _timer.Start();
        AppLog.Write("window-opened", new { networkChanged = false });
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _busy || _closed) return;
        _refreshing = true;
        try
        {
            var state = await Task.Run(_service.ReadState);
            if (_closed || _busy) return;
            if (_snapshot != state)
            {
                _snapshot = state;
                Render(state);
            }
            AppLog.SaveState(new { time = DateTimeOffset.Now, pid = Environment.ProcessId,
                tunConnected = state.TunConnected, hotspotState = state.State, clients = state.Clients,
                userStartRequests = _startRequests, userStopRequests = _stopRequests, automaticNetworkSwitching = false });
        }
        catch (Exception ex) { if (!_closed) ShowError(ex.Message); }
        finally { _refreshing = false; }
    }

    private void Render(HotspotSnapshot state)
    {
        StatusText.Text = state.State switch { "On" => "热点已开启", "Off" => "热点已关闭", "InTransition" => "热点正在切换", _ => "等待网络准备就绪" };
        StatusDescription.Text = state.IsOn ? "手机连接原来的电脑热点即可。"
            : state.TunConnected ? "准备就绪，点击下方按钮共享连接。" : "先在 Kumiryo 中开启 TUN。";
        TunText.Text = state.TunConnected ? "Kumiryo · 已连接" : "未连接";
        ClientsText.Text = state.IsOn ? $"{state.Clients} 台" : "—";
        StartButton.IsEnabled = state.TunConnected && !state.IsOn && !state.IsTransitioning;
        StopButton.IsEnabled = state.IsOn || state.IsTransitioning || state.State == "Unknown";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _startRequests++;
        await OperateAsync(true);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        _stopRequests++;
        await OperateAsync(false);
    }

    private async Task OperateAsync(bool start)
    {
        var version = ++_operationVersion;
        _busy = true;
        _snapshot = null;
        MessageBar.IsOpen = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = start;
        StatusText.Text = start ? "正在开启热点…" : "正在关闭热点…";
        StatusDescription.Text = "请稍等，正在应用你的操作。";
        AppLog.Write(start ? "user-start-request" : "user-stop-request", new { version });
        try
        {
            if (start) await _service.StartAsync(); else await _service.StopAsync();
            AppLog.Write("user-operation-completed", new { start, version });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed && version == _operationVersion) ShowError(ex.Message);
            AppLog.Write("user-operation-error", new { start, version, ex.Message });
        }
        finally
        {
            if (!_closed && version == _operationVersion)
            {
                _busy = false;
                // Keep the recovery control available until the next successful state read.
                StopButton.IsEnabled = true;
                await RefreshAsync();
            }
        }
    }

    private void ShowError(string message)
    {
        MessageBar.Title = "操作未完成";
        MessageBar.Message = message;
        MessageBar.Severity = InfoBarSeverity.Error;
        MessageBar.IsOpen = true;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
}
