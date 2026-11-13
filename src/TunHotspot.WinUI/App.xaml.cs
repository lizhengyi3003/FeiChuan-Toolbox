using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TunHotspot.Core;

namespace TunHotspot;

public partial class App : Application
{
    private ToolboxWindow? _window;
    private LaunchFailureWindow? _launchFailure;
    private bool _qaStartupFailureInjected;
    private AppInstance? _instance;
    private volatile string? _pendingEvent;
    // Also retained for older installers; current installers request a scoped exit.
    private Mutex? _installerMutex;
    private IDisposable? _installerExit;
    private bool _opening, _updateRequested, _showWhenReady;

    public App()
    {
        var startupArgs = Environment.GetCommandLineArgs();
        int captureIndex = Array.IndexOf(startupArgs, "--qa-capture");
        if(captureIndex>=0)AppLog.Configure(Path.Combine(Path.GetTempPath(),"TunHotspot-QA",Environment.ProcessId.ToString()));
        if (captureIndex >= 0) RequestedTheme = captureIndex + 1 < startupArgs.Length && startupArgs[captureIndex + 1].Contains("dark") ? ApplicationTheme.Dark : ApplicationTheme.Light;
        InitializeComponent();
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => AppLog.Write("xaml-resource-error", new { e.Message });
        UnhandledException += (_, e) =>
        {
            new TunHotspot.Core.DiagnosticLog(AppLog.DataRoot).Record("未处理的界面异常",e.Exception);
            if(_window is not null && e.Exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
            {
                try{_window.ReportException(e.Exception,"界面操作");e.Handled=true;}catch{ }
            }
        };
        TaskScheduler.UnobservedTaskException += (_,e)=>
        {
            new TunHotspot.Core.DiagnosticLog(AppLog.DataRoot).Record("后台任务异常",e.Exception);
            e.SetObserved();_window?.DispatcherQueue.TryEnqueue(()=>_window.ReportException(e.Exception,"后台任务"));
        };
        AppDomain.CurrentDomain.UnhandledException += (_,e)=>
        {if(e.ExceptionObject is Exception error)new TunHotspot.Core.DiagnosticLog(AppLog.DataRoot).Record("进程终止前异常",error);};
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        int qaIndex = Array.IndexOf(commandLine, "--qa-capture");
        try
        {
            if (TunHotspot.Core.SetupGate.IsBlocked(Environment.ProcessPath!))
            {
                // A persistent error window would itself lock the DLLs being updated.
                // The installer owns the visible status; retain a local diagnostic and exit.
                try { AppLog.Write("launch-paused-for-update", new { Message = TunHotspot.Core.SetupGate.BlockedMessage }); }
                finally { Exit(); }
                return;
            }
            AppLog.Write("launch-started", new { pid = Environment.ProcessId, TunHotspot.Core.Contract.Elevated, version = TunHotspot.Core.Contract.ProductVersion });
            if(commandLine.Contains("--qa-notification"))
            {
                NotificationBridge.Register();
                var store=new TunHotspot.Core.ToolboxStore(TunHotspot.Core.DataPaths.UserRoot);
                var alert=new TunHotspot.Core.AlertEvent{Title="工具箱系统通知验证",EntryId="notification-qa",Expected="通知及历史留档",Actual="测试事件",Evidence="通知验证，仅写入本软件的历史记录。"};
                store.SaveAlert(alert);alert.Delivery=NotificationBridge.Send(alert);store.SaveAlert(alert);
                int count=-1;string? historyError=null;
                try{count=(await Microsoft.Windows.AppNotifications.AppNotificationManager.Default.GetAllAsync()).Count;}catch(Exception ex){historyError=ex.Message;}
                string directory=Path.Combine(TunHotspot.Core.DataPaths.UserRoot,"qa");Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory,"notification.json"),TunHotspot.Core.Contract.Encode(new{time=DateTimeOffset.UtcNow,status=NotificationBridge.Status,alert.Delivery,count,historyError,persisted=store.Alerts.Any(x=>x.Id==alert.Id)}));
                NotificationBridge.Close();Exit();return;
            }
            if(commandLine.Contains("--uninstall-user-integration"))
            {
                using var run=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",true);
                string? value=run?.GetValue("TunHotspot.Toolbox.Agent") as string;
                if(value is not null&&value.StartsWith("\""+Environment.ProcessPath+"\"",StringComparison.OrdinalIgnoreCase))run!.DeleteValue("TunHotspot.Toolbox.Agent",false);
                try { Microsoft.Windows.AppNotifications.AppNotificationManager.Default.UnregisterAll(); } catch { }
                Exit();return;
            }
            bool qa = qaIndex >= 0;
            _instance = AppInstance.FindOrRegisterForKey(qa ? $"TunHotspot.WinUI.Qa.{Environment.ProcessId}" : "TunHotspot.WinUI.Main");
            if (!_instance.IsCurrent)
            {
                await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                Exit();
                return;
            }
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _instance.Activated += (_, activation) => dispatcher.TryEnqueue(() => HandleActivation(activation));
            if (!qa)
            {
                _installerMutex = new Mutex(false, "TunHotspot.WinUI.Running");
                _installerExit = SetupGate.WatchForExit(() => dispatcher.TryEnqueue(RequestUpdateExit));
                NotificationBridge.Invoked += id => dispatcher.TryEnqueue(() => OpenNotification(id));
                NotificationBridge.Register();
            }
            await OpenMainWindow(commandLine, qaIndex);
        }
        catch (Exception ex)
        {
            if (_updateRequested) RequestUpdateExit();
            else ShowLaunchFailure(ex, commandLine, qaIndex);
        }
    }

    private async Task OpenMainWindow(string[] commandLine, int qaIndex, bool retry = false)
    {
        if (_opening || _updateRequested) return;
        _opening = true;
        Window? loadingWindow = null;
        bool canCloseLoadingWindow = false;
        try
        {
            long started = Stopwatch.GetTimestamp();
            bool qa = qaIndex >= 0, quiet = !retry && commandLine.Contains("--background");
            string page = qa && qaIndex + 1 < commandLine.Length ? commandLine[qaIndex + 1] : "overview";
            // Exercise the real launch boundary without opening or changing the user's database.
            if (qa && page == "startup-error" && !_qaStartupFailureInjected)
            {
                _qaStartupFailureInjected = true;
                throw new Microsoft.Data.Sqlite.SqliteException("SQLite Error 11: QA 启动失败测试。", 11);
            }
            if (SetupGate.IsBlocked(Environment.ProcessPath!)) { RequestUpdateExit(); return; }
            var previous = _window;
            var loading = Task.Run(() => ToolboxWindow.LoadStartupData(qa));
            previous?.TrackDatabaseWork(loading);
            if (!qa && !quiet && previous is null && _launchFailure is null &&
                await Task.WhenAny(loading, Task.Delay(400)) != loading && !_updateRequested)
            {
                loadingWindow = new Window
                {
                    Title = "飞船的小工具箱",
                    Content = new Microsoft.UI.Xaml.Controls.StackPanel
                    {
                        Padding = new Thickness(28),
                        Spacing = 16,
                        Children =
                        {
                            new Microsoft.UI.Xaml.Controls.TextBlock { Text = "正在读取并核验启动数据…" },
                            new Microsoft.UI.Xaml.Controls.ProgressBar { IsIndeterminate = true }
                        }
                    }
                };
                var window = loadingWindow;
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(480, 180));
                window.AppWindow.Closing += (_, args) =>
                {
                    if (!canCloseLoadingWindow)
                    {
                        args.Cancel = true;
                        window.AppWindow.Hide();
                    }
                };
                window.Activate();
            }
            var data = await loading;
            loadingWindow?.AppWindow.Hide();
            if (_updateRequested || SetupGate.IsBlocked(Environment.ProcessPath!)) { RequestUpdateExit(); return; }
            if (previous?.IsShuttingDown == true) return;
            double dataMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            quiet &= !_showWhenReady;
            _showWhenReady = false;
            var next = new ToolboxWindow(data, quiet, qa);
            _window = next;
            next.Closed += (_, _) =>
            {
                if (!ReferenceEquals(_window, next)) return;
                _window = null;
                ReleaseApplicationResources();
            };
            if (!quiet) ActivateMainWindow();
            if (previous is not null) await previous.ShutdownAsync(forRetry: true);
            var previousFailure = _launchFailure; _launchFailure = null;
            previousFailure?.Close();
            if (_pendingEvent is string eventId)
                OpenNotification(eventId);
            AppLog.Write("toolbox-opened", new { quiet, startupAvailable = data.Store is not null, dataMs, readyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
            if (_updateRequested) { await next.ShutdownAsync(forUpdate: true); return; }
            next.DispatcherQueue.TryEnqueue(next.StartBackgroundWork);
            if (qa) _ = next.CaptureForQa(page == "startup-error" ? "overview" : page);
        }
        finally
        {
            canCloseLoadingWindow = true;
            loadingWindow?.Close();
            _opening = false;
            if (_updateRequested) RequestUpdateExit();
        }
    }

    private void RequestUpdateExit()
    {
        _updateRequested = true;
        if (_opening) return;
        if (_window is not null) { _ = _window.ShutdownAsync(forUpdate: true); return; }
        if (_launchFailure is not null) _launchFailure.Close();
        else { ReleaseApplicationResources(); Exit(); }
    }

    private void ReleaseApplicationResources()
    {
        NotificationBridge.Close();
        _installerExit?.Dispose(); _installerExit = null;
        _installerMutex?.Dispose(); _installerMutex = null;
    }

    private void OpenNotification(string id)
    {
        if (_window is null) { _pendingEvent = id; return; }
        _pendingEvent = null;
        try { _window.OpenEvent(id); }
        catch (Exception ex) { AppLog.Write("activation-error", new { Details = ex.ToString() }); _window.ReportError(ex.Message); }
    }

    private void ShowLaunchFailure(Exception error, string[] commandLine, int qaIndex)
    {
        AppLog.Write("launch-error", new { pid = Environment.ProcessId, error.Message, Type = error.GetType().FullName, Details = error.ToString() });
        if (_launchFailure is null)
        {
            _launchFailure = new LaunchFailureWindow(async () =>
            {
                try
                {
                    if (_instance is null || !_instance.IsCurrent)
                        throw new InvalidOperationException("启动注册未完成，请关闭此窗口后重新打开软件。");
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    await OpenMainWindow(commandLine, qaIndex);
                }
                catch (Exception ex) { ShowLaunchFailure(ex, commandLine, qaIndex); }
            });
            _launchFailure.Closed += (_, _) =>
            {
                _launchFailure = null;
                if (_window is null) { ReleaseApplicationResources(); Exit(); }
            };
        }
        _launchFailure.SetError(error);
        _launchFailure.Activate();
        if (qaIndex >= 0 && commandLine.ElementAtOrDefault(qaIndex + 1) == "startup-error") _ = _launchFailure.CaptureForQa();
    }
    private void HandleActivation(AppActivationArguments activation)
    {
        if(activation.Kind==ExtendedActivationKind.AppNotification&&activation.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs notification&&notification.Arguments.TryGetValue("eventId",out string? id)&&Guid.TryParseExact(id,"N",out _))
            OpenNotification(id);
        else if (_window is null && _launchFailure is null) _showWhenReady = true;
        else ActivateMainWindow();
    }

    private void ActivateMainWindow()
    {
        if (_window is null && _launchFailure is not null) { _launchFailure.Activate(); return; }
        if (_window is null) return;
        _window.Activate();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    internal void ReportError(string message) => _window?.ReportError(message);
    internal void ReportException(Exception error,string operation) => _window?.ReportException(error,operation);
    internal async void RetryMainWindow()
    {
        if (_window?.IsShuttingDown == true || _window?.DatabaseWorkActive == true) return;
        var arguments=Environment.GetCommandLineArgs();int qaIndex=Array.IndexOf(arguments,"--qa-capture");
        try
        {
            await OpenMainWindow(arguments, qaIndex, retry: true);
        }
        catch(Exception ex){if (_updateRequested) RequestUpdateExit(); else ShowLaunchFailure(ex,arguments,qaIndex);}
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
}
