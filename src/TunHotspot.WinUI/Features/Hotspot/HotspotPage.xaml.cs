using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TunHotspot.Core;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace TunHotspot;

public sealed partial class HotspotPage : UserControl
{
    private readonly HotspotService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly HotspotRecoveryPolicy _recovery = new();
    private HotspotSnapshot? _snapshot;
    private bool _refreshing;
    private bool _busy;
    private bool _closed, _ready;
    private int _operationVersion;
    private int _startRequests;
    private int _stopRequests;

    public HotspotPage()
    {
        InitializeComponent();
        _ready = true;
        _timer.Tick += async (_, _) => await RefreshAsync();
        // Health observation continues when switching workspaces or minimizing to the tray.
        _timer.Start();
        Loaded += async (_, _) => { if (_snapshot is not null) Render(_snapshot); await RefreshAsync(); };
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || _busy || _closed) return;
        _refreshing = true;
        try
        {
            var state = await _service.ReadState();
            if (_closed || _busy) return;
            if (_snapshot != state)
            {
                AppLog.Write("hotspot-state-changed", new { previous = _snapshot?.State, state.State, state.TunConnected, state.Clients, state.IdleTimeoutEnabled, recoveryRequested = _recovery.Requested });
                if(_snapshot?.IsOn==true&&!state.IsOn&&!state.IsTransitioning)
                    ShowError(state.TunConnected?"Windows 已关闭热点。可查看热点类别日志后手动重试。":"Kumiryo TUN 已断开。工具箱保持运行，热点不会自动换用其他网络。");
                _snapshot = state;
                Render(state);
            }
            AppLog.SaveState(new { time = DateTimeOffset.Now, pid = Environment.ProcessId,
                tunConnected = state.TunConnected, hotspotState = state.State, clients = state.Clients,
                userStartRequests = _startRequests, userStopRequests = _stopRequests,
                recoveryRequested = _recovery.Requested, recoveryAttempts = _recovery.Attempts,
                noConnectionsTimeoutEnabled = state.IdleTimeoutEnabled, automaticNetworkSwitching = _recovery.Requested });
            if (_recovery.Observe(DateTimeOffset.UtcNow, state.TunConnected, state.State))
            {
                _recovery.AttemptStarted(DateTimeOffset.UtcNow);
                AppLog.Write("hotspot-recovery-request", new { attempt = _recovery.Attempts, userOptIn = true });
                await OperateAsync(true, recovery: true);
            }
            RenderRecovery();
        }
        catch (Exception ex) { if (!_closed) { Render(new(false,"Unknown",0,ex.Message));ShowError(ex.Message); } }
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
        KeepAliveToggle.IsEnabled = state.IsOn || _recovery.Requested;
        RenderRecovery();
    }

    private void RenderRecovery() => RecoveryText.Text = _recovery.Exhausted && _snapshot?.IsOn != true
        ? "连续重连未成功，请检查 Kumiryo 的 TUN 后手动重试。"
        : _recovery.Requested ? "保持连接已开启。短暂断开后会重试，手动关闭热点会同时停止重连。"
        : "开启此选项后，可在短暂断开时重连；打开软件本身不会开启热点。";

    private async void KeepAlive_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _recovery.SetRequested(KeepAliveToggle.IsOn);
        AppLog.Write("hotspot-recovery-opt-in", new { enabled = _recovery.Requested });
        RenderRecovery();
        if (_recovery.Requested)
            try { await _service.KeepConnectedAsync(); }
            catch (Exception ex) { ShowError("未能关闭热点空闲超时：" + ex.Message); }
    }

    internal void Dispose()
    {
        _closed = true;
        _timer.Stop();
        _recovery.SetRequested(false);
        _service.Dispose();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _recovery.SetRequested(KeepAliveToggle.IsOn);
        _startRequests++;
        await OperateAsync(true);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        _recovery.SetRequested(false);
        KeepAliveToggle.IsOn = false;
        _stopRequests++;
        await OperateAsync(false);
    }

    private async Task OperateAsync(bool start, bool recovery = false)
    {
        var version = ++_operationVersion;
        _busy = true;
        _snapshot = null;
        MessageBar.IsOpen = false;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = start;
        StatusText.Text = start ? "正在开启热点…" : "正在关闭热点…";
        StatusDescription.Text = "请稍等，正在应用你的操作。";
        AppLog.Write(recovery ? "hotspot-recovery-start" : start ? "user-start-request" : "user-stop-request", new { version });
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
        var incident=new DiagnosticLog(AppLog.DataRoot).Event("热点","错误","热点状态或操作未完成",message);
        MessageBar.Title = "操作未完成";
        MessageBar.Message = message+$"\n日志编号：{incident.Id}；设置 → 日志查看器 → 热点。";
        MessageBar.Severity = InfoBarSeverity.Error;
        MessageBar.IsOpen = true;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
}
