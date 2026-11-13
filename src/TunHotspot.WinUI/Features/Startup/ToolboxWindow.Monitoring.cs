using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using TunHotspot.Core;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.ViewManagement;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace TunHotspot;

public sealed partial class ToolboxWindow : Window
{
    private bool _pollDatabaseBusy;

    private async void GlobalScan_Click(object sender,RoutedEventArgs e)=>await GlobalScan();
    private async void CancelScan_Click(object sender,RoutedEventArgs e)
    {
        _adminStop?.Cancel();
        if(_serviceScanRequested){try{await AdminClient.Service(new(){Kind="cancelScan"});}catch(Exception ex){ReportError(ex.Message);}}
    }
    private void ShowProgress(ScanProgress p)
    {
        ScanTitle.Text=p.Stage;ScanDescription.Text=$"{p.Message} · 已发现 {p.Discovered:N0} 项";
        ScanProgressBar.IsIndeterminate=p.CompletedStages==0;ScanProgressBar.Value=p.TotalStages>0?100.0*p.CompletedStages/p.TotalStages:0;
        _tray?.Update($"飞船的小工具箱 · {p.Stage} · {p.Discovered} 项");
    }
    private void ApplySnapshot(ScanSnapshot snapshot)
    {
        _snapshot=snapshot;AttachDecisions();ScanProgressBar.IsIndeterminate=false;ScanProgressBar.Value=100;
        ScanTitle.Text=snapshot.Cancelled?"扫描已取消":snapshot.Complete?"采集流程完成":"采集流程完成 · 存在覆盖缺口";
        ScanDescription.Text=$"{snapshot.Privilege} · {snapshot.Entries.Count:N0} 个入口 · {snapshot.Finished.ToLocalTime():HH:mm:ss} · 只读";
        _tray?.Update($"飞船的小工具箱 · {snapshot.Entries.Count} 个入口 · 未读告警 {_store.Alerts.Count(x=>!x.IsRead)}");
        if(_page is "entries" or "pending" or "codex-origin" or "dsh-origin")_refreshEntries?.Invoke();else if(_page is "overview" or "apps" or "groups")RenderPage();
        AppLog.Write("snapshot-received",new{snapshot.Id,count=snapshot.Entries.Count,snapshot.Privilege,complete=snapshot.Complete});
    }
    private void AttachDecisions()
    {
        if (_snapshot is null) return;
        _store.AttachOrigins(_snapshot);
        AttachDecisions(_store, _snapshot);
    }

    private static void AttachDecisions(ToolboxStore store, ScanSnapshot snapshot)
    {
        var decisions = store.Decisions.ToDictionary(x => x.EntryId);
        foreach (var entry in snapshot.Entries)
            entry.Analysis = decisions.TryGetValue(entry.Id, out var decision) && entry.MatchesFingerprint(decision.Fingerprint) ? decision : null;
    }
    private async Task GlobalScan()
    {
        if (_startupUnavailable) { Navigate("overview"); return; }
        if(_adminBusy)return;_adminBusy=true;GlobalScanButton.IsEnabled=false;CancelScanButton.IsEnabled=true;_adminStop=new();
        bool localWasPaused=_monitor.Paused;_monitor.Paused=true;_monitor.CancelScan();
        try
        {
            if(_serviceConnected){await AdminClient.Service(new(){Kind="scan"});_serviceScanRequested=true;ScanTitle.Text="机器全局扫描已提交";ScanDescription.Text="LocalSystem 组件正在采集；下次刷新显示阶段进度与结果。";ScanProgressBar.IsIndeterminate=true;}
            else
            {
                ScanTitle.Text="等待 Windows 管理员授权";ScanDescription.Text="请在 UAC 窗口确认；仅执行只读全局扫描。";ScanProgressBar.IsIndeterminate=true;
                var response=await AdminClient.Run("--scan",p=>DispatcherQueue.TryEnqueue(()=>ShowProgress(p)),_adminStop.Token);
                if(response.Snapshot is not null){_store.SaveSnapshot(response.Snapshot);ApplySnapshot(response.Snapshot);RenderPage();}
            }
        }
        catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){Tell("已取消管理员授权","保留先前结果，本次不标为管理员扫描成功。");ScanTitle.Text="管理员扫描未授权";}
        catch(OperationCanceledException){Tell("扫描已取消","保留已有结果；不会停止系统服务或热点。");ScanTitle.Text="扫描已取消";}
        catch(Exception ex){Tell("扫描未完成",ex.Message,InfoBarSeverity.Error);ScanTitle.Text="扫描失败";}
        finally{_adminBusy=false;_monitor.Paused=_serviceConnected||localWasPaused;GlobalScanButton.IsEnabled=true;CancelScanButton.IsEnabled=_serviceScanRequested;ScanProgressBar.IsIndeterminate=_serviceScanRequested;_adminStop?.Dispose();_adminStop=null;}
    }
    private async Task Component(bool install)
    {
        if(_adminBusy)return;_adminBusy=true;GlobalScanButton.IsEnabled=false;
        try
        {
            await AdminClient.Run(install?"--install":"--uninstall",p=>DispatcherQueue.TryEnqueue(()=>ShowProgress(p)),CancellationToken.None);
            if(install&&_settings.Paused)await AdminClient.Service(new(){Kind="pause",Text="true"});
            var login=StartupRegistration.Read();StartupRegistration.SetEnabled(install,Environment.ProcessPath!,login.Fingerprint,DataPaths.UserRoot);
            Tell(install?"开机采集组件已安装":"采集组件已卸载",install?"机器服务只读采集；当前用户登录时启动托盘通知代理。":"自身服务与登录入口已移除；历史数据和受控版本文件保留，热点状态未改变。");
            await Poll();RenderPage();
        }
        catch(Exception ex){Tell("组件操作未完成",ex.Message,InfoBarSeverity.Error);}
        finally{_adminBusy=false;GlobalScanButton.IsEnabled=true;ScanProgressBar.IsIndeterminate=false;}
    }
    private async Task Pause()
    {
        if (_startupUnavailable) { Navigate("overview"); return; }
        _settings.Paused=!_settings.Paused;_monitor.Paused=_settings.Paused;
        if(_serviceConnected)await AdminClient.Service(new(){Kind="pause",Text=_settings.Paused?"true":"false"});
        SaveSettings();Tell(_settings.Paused?"监测已暂停":"监测已恢复","手动扫描仍可使用；没有停止其他服务或改变热点。");if(_page=="settings")RenderPage();
        _tray?.Update($"飞船的小工具箱 · {(_settings.Paused?"监测已暂停":"监测运行中")} · 未读提醒 {_store.Alerts.Count(x=>!x.IsRead)}");
    }
    private Task _pollTask = Task.CompletedTask;
    private Task Poll()
    {
        if (!_pollTask.IsCompleted || _exiting) return _pollTask;
        return _pollTask = PollCore();
    }

    private async Task PollCore()
    {
        if (_startupUnavailable) return;
        if(_polling||_exiting)return;_polling=true;
        try
        {
            WireMessage? response = null;
            try { response = await AdminClient.Service(new(){Kind="status"}, _windowStop.Token); }
            catch (Exception ex)
            {
                if (_exiting) return;
                if (_serviceConnected)
                {
                    _serviceConnected = false;
                    _machineSnapshotId = null;
                    ReportException(ex, "连接机器采集组件");
                }
                _monitor.Paused = _settings.Paused;
            }
            if (_exiting) return;
            if (response is not null)
            {
                _serviceConnected=true;_monitor.Paused=true;
                if(response.Snapshot is not null&&response.Snapshot.Id!=_machineSnapshotId&&response.Snapshot.Started>=_lastControlTime)
                {
                    _store.SaveSnapshot(response.Snapshot);
                    ApplySnapshot(response.Snapshot);
                    _machineSnapshotId=response.Snapshot.Id;
                    if(_serviceScanRequested&&response.Progress is null){_serviceScanRequested=false;CancelScanButton.IsEnabled=false;}
                }
                if(response.Progress is not null)ShowProgress(response.Progress);
                else if(_serviceScanRequested&&response.ScanCancelled){_serviceScanRequested=false;CancelScanButton.IsEnabled=false;ScanProgressBar.IsIndeterminate=false;ScanTitle.Text="机器扫描已取消，保留上次结果";}
                await Task.Run(() =>
                {
                    if (response.Alerts is not null) _store.MergeRemoteAlerts(response.Alerts);
                    if (response.Events is not null) _store.AddEvents(response.Events);
                });
                if (_exiting) return;
            }
            bool notifications = _settings.Notifications;
            var status = await Task.Run(() => (
                Alerts: notifications ? _store.Alerts.Where(alert => alert.Delivery == "待发送").Take(3).ToArray() : [],
                LastAnalysis: _store.Get<DateTimeOffset>("lastAnalysis"),
                Overview: OverviewData.Read(_store)));
            if (_exiting) return;
            foreach (var alert in status.Alerts)
            {
                alert.Delivery = NotificationBridge.Send(alert);
                _store.SaveAlert(alert);
            }
            var lastAnalysis = status.LastAnalysis;
            if(lastAnalysis!=_lastAnalysis){_lastAnalysis=lastAnalysis;AttachDecisions();if(_page is not ("hotspot" or "settings" or "configuration"))RenderPage();}
            if (_overviewData != status.Overview)
            {
                _overviewData = status.Overview;
                if (_page == "overview") RenderPage();
            }
            _pollDatabaseBusy = false;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            if (!_pollDatabaseBusy)
                new DiagnosticLog(AppLog.DataRoot).Event("数据库", "警告", "本地状态同步等待数据库",
                    "本轮同步遇到数据库写锁，将在下一轮刷新重试。", $"SQLite {ex.SqliteErrorCode}/{ex.SqliteExtendedErrorCode}");
            _pollDatabaseBusy = true;
        }
        catch(Exception ex)
        {
            ReportException(ex,"状态与历史读取");
            if(ex is Microsoft.Data.Sqlite.SqliteException sql && sql.SqliteErrorCode is 11 or 26)
            {
                _startupUnavailable=true;_startupError="运行中发现数据库异常，已暂停当前界面的启动读写。请查看诊断日志。";
                _poll.Stop();
                await _monitor.StopAsync();
                if(_page is not ("settings" or "configuration" or "hotspot"))RenderPage();
            }
        }
        finally{_polling=false;}
    }
}
