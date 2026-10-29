using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Runtime.InteropServices;

namespace TunHotspot;

public partial class App : Application
{
    private MainWindow? _window;
    private AppInstance? _instance;
    // The installer only detects this mutex; it never terminates the application.
    private Mutex? _installerMutex;

    public App()
    {
        InitializeComponent();
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, e) => AppLog.Write("xaml-resource-error", new { e.Message });
        UnhandledException += (_, e) => AppLog.Write("unhandled-error", new { e.Message });
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _instance = AppInstance.FindOrRegisterForKey("TunHotspot.WinUI.Main");
            if (!_instance.IsCurrent)
            {
                await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
                Exit();
                return;
            }
            _instance.Activated += (_, _) => _window?.DispatcherQueue.TryEnqueue(ActivateMainWindow);
            _installerMutex = new Mutex(false, "TunHotspot.WinUI.Running");
            _window = new MainWindow();
            ActivateMainWindow();
        }
        catch (Exception ex)
        {
            AppLog.Write("launch-error", new { ex.Message, Type = ex.GetType().FullName, Details = ex.ToString() });
            throw;
        }
    }

    private void ActivateMainWindow()
    {
        if (_window is null) return;
        _window.Activate();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(handle, 9);
        SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint handle);
}
