using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using TunHotspot.Core;
using Windows.UI.ViewManagement;

namespace TunHotspot;

public sealed partial class ToolboxWindow : Window
{
    private bool _allowClose;
    private Task? _shutdown;
    private Task _databaseWork = Task.CompletedTask;
    internal bool IsShuttingDown => _exiting;
    internal bool DatabaseWorkActive => !_databaseWork.IsCompleted;
    internal void TrackDatabaseWork(Task work) => _databaseWork = work;

    internal void OpenEvent(string id)
    {
        if (_exiting || _startupLoading) return;
        if (_startupUnavailable)
        {
            Show();
            Navigate("settings");
            return;
        }

        var item = _store.Alerts.FirstOrDefault(alert => alert.Id == id);
        if (item is not null)
        {
            item.IsRead = true;
            _store.SaveAlert(item);
        }
        Show();
        Navigate("alerts");
    }
    private void AdvancedEffectsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_exiting) ApplyAppearance(); });
    private void HighContrastChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!_exiting) ApplyAppearance(); });

    internal void StartBackgroundWork()
    {
        if (_qa || _exiting) return;
        NotificationBridge.Register();
        try
        {
            _tray = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
            _tray.Command += HandleTrayCommand;
            _tray.UpdateExitRequested += () => DispatcherQueue.TryEnqueue(() => _ = ShutdownAsync(forUpdate: true));
            _tray.MenuRequested += (x, y) => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_exiting) OpenTrayMenu(x, y);
            });
            _tray.SessionEnding += () => DispatcherQueue.TryEnqueue(() => _ = ShutdownAsync(forUpdate: true));
            _tray.FontsChanged += () => { if (!_exiting && _fontLoad is not null) _ = LoadFonts(); };
            _tray.Resumed += () =>
            {
                if (_exiting || _startupUnavailable) return;
                _monitor.Reconcile("登录界面或电源恢复");
                _ = Poll();
            };
        }
        catch (Exception ex) { Tell("托盘不可用", ex.Message, InfoBarSeverity.Warning); }
        if (_startupStore is null) return;
        _poll.Start();
        _monitor.Start();
        _ = Poll();
        var stop = _windowStop.Token;
        _startupBackup = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stop).ConfigureAwait(false);
                _store.ReconcilePending();
                stop.ThrowIfCancellationRequested();
                string folder = Path.Combine(_store.Root, "database-backups");
                if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "toolbox-*.db")
                    .Any(file => File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddHours(-6)))
                    _store.CreateVerifiedBackup(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex) { _diagnostics.Record("后台数据库维护", ex); }
        });
    }

    internal Task ShutdownAsync(bool forUpdate = false, bool forRetry = false) =>
        _shutdown ??= ShutdownCore(forUpdate, forRetry);

    private async Task ShutdownCore(bool forUpdate, bool forRetry)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _exiting = true;
        AppWindow.Hide();
        _poll.Stop();
        _trayMenu?.Dismiss();
        _tray?.Dispose();
        _tray = null;
        _windowStop.Cancel();
        _adminStop?.Cancel();
        _configuration?.Dispose();
        try { _uiSettings.AdvancedEffectsEnabledChanged -= AdvancedEffectsChanged; } catch (Exception ex) { AppLog.Write("theme-unsubscribe-error", new { ex.Message }); }
        try { _accessibility.HighContrastChanged -= HighContrastChanged; } catch (Exception ex) { AppLog.Write("accessibility-unsubscribe-error", new { ex.Message }); }
        try
        {
            Task savePreferences = Task.CompletedTask;
            if (!_qa && !forRetry && !_startupLoading)
            {
                var settings = Contract.Decode<UserSettings>(Contract.Encode(_settings));
                savePreferences = Task.Run(() =>
                {
                    try { _preferences.Save(settings); }
                    catch (Exception ex) { _diagnostics.Record("关闭时保存应用设置", ex); }
                });
            }
            await Task.WhenAll(savePreferences, _pollTask, _startupBackup, _databaseWork,
                _hotspot.StopAsync().AsTask(), _startupMonitor?.StopAsync() ?? Task.CompletedTask);
        }
        catch (Exception ex) { _diagnostics.Record("工具箱退出", ex); }
        finally
        {
            _windowStop.Dispose();
            SystemBackdrop = null;
            AppLog.Write("toolbox-closed", new { forUpdate, forRetry, elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, machineMonitorMayContinue = _serviceConnected });
            _allowClose = true;
            Close();
        }
    }

    private async void ExitToolbox() => await ShutdownAsync();
    private void Tell(string title, string body, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        Message.Title = title;
        Message.Message = body;
        Message.Severity = severity;
        Message.IsOpen = true;
        var action = severity switch
        {
            InfoBarSeverity.Error => "ui-operation-error",
            InfoBarSeverity.Warning => "ui-operation-warning",
            _ => "ui-notice"
        };
        AppLog.Write(action, new { title, body });
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
