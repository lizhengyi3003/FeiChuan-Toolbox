using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;

namespace TunHotspot;

public partial class App : Application
{
    private ToolboxWindow? _window;
    private AppInstance? _instance;
    private volatile string? _pendingEvent;
    // The installer only detects this mutex; it never terminates the application.
    private Mutex? _installerMutex;

    public App()
    {
        if (Environment.GetCommandLineArgs().Contains("--qa-capture")) RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => AppLog.Write("xaml-resource-error", new { e.Message });
        UnhandledException += (_, e) => AppLog.Write("unhandled-error", new { e.Message });
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var commandLine = Environment.GetCommandLineArgs();
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
            int qaIndex = Array.IndexOf(commandLine, "--qa-capture");
            bool qa = qaIndex >= 0;
            if (!qa)
            {
                NotificationBridge.Invoked += id => { _pendingEvent=id;_window?.DispatcherQueue.TryEnqueue(() => _window.OpenEvent(id)); };
                NotificationBridge.Register();
            }
            _instance = AppInstance.FindOrRegisterForKey(qa ? "TunHotspot.WinUI.Qa" : "TunHotspot.WinUI.Main");
            if (!_instance.IsCurrent)
            {
                await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                Exit();
                return;
            }
            _instance.Activated += (_, activation) => _window?.DispatcherQueue.TryEnqueue(() => HandleActivation(activation));
            _installerMutex = new Mutex(false, "TunHotspot.WinUI.Running");
            bool quiet = Environment.GetCommandLineArgs().Contains("--background");
            _window = new ToolboxWindow(quiet, qa);
            if(_pendingEvent is string eventId)_window.OpenEvent(eventId);else if (!quiet) ActivateMainWindow();
            if (qa) _ = _window.CaptureForQa(qaIndex + 1 < commandLine.Length ? commandLine[qaIndex + 1] : "overview");
        }
        catch (Exception ex)
        {
            AppLog.Write("launch-error", new { ex.Message, Type = ex.GetType().FullName, Details = ex.ToString() });
            throw;
        }
    }
    private void HandleActivation(AppActivationArguments activation)
    {
        if(activation.Kind==ExtendedActivationKind.AppNotification&&activation.Data is Microsoft.Windows.AppNotifications.AppNotificationActivatedEventArgs notification&&notification.Arguments.TryGetValue("eventId",out string? id)&&Guid.TryParseExact(id,"N",out _))
            _window?.OpenEvent(id);
        else ActivateMainWindow();
    }

    private void ActivateMainWindow()
    {
        if (_window is null) return;
        _window.Activate();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    internal void ReportError(string message) => _window?.ReportError(message);

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
}
