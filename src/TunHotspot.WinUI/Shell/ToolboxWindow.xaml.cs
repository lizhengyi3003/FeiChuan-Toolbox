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
    private ToolboxStore? _startupStore;
    private ToolboxStore _store => _startupStore ?? throw new InvalidOperationException("启动数据库暂不可用。");
    private PreferencesStore _preferences;
    private DiagnosticLog _diagnostics;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private LiveMonitor? _startupMonitor;
    private LiveMonitor _monitor => _startupMonitor ?? throw new InvalidOperationException("启动监测暂不可用。");
    private readonly CancellationTokenSource _windowStop = new();
    private readonly HotspotPage _hotspot;
    private readonly ScrollViewer _hotspotHost;
    private ConfigurationPage? _configuration;
    private readonly bool _qa;
    private bool _startupUnavailable;
    private bool _startupLoading;
    private string _startupStage = "读取应用设置";
    private int _startupStep = 1;
    private string _startupError = "";
    private string? _startupDiagnosticPath;
    private bool _qaPageErrorInjected;
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private TrayIcon? _tray;
    private TrayMenuWindow? _trayMenu;
    private ScanSnapshot? _snapshot;
    private UserSettings _settings;
    private CancellationTokenSource? _adminStop;
    private bool _adminBusy, _polling, _exiting, _serviceConnected, _serviceScanRequested;
    private bool _initialized;
    private bool _routeWithFilter;
    private string _page = "overview", _materialState = "",_groupKind="类别";
    private string _lastStartupPage = "overview";
    private readonly EntryFilter _entryFilter=new(),_pendingFilter=new();
    private readonly EntryFilter _codexFilter=new(){Creator="Codex"},_dshFilter=new(){Creator="DSH"};
    private Action? _refreshEntries;
    private string? _machineSnapshotId;
    private DateTimeOffset _lastAnalysis;
    private DateTimeOffset _lastControlTime;
    private List<string> _fonts = [];
    private DataTemplate? _rows;
    private DataTemplate Rows => _rows ??= (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <StackPanel Padding="4,8" Spacing="4">
            <TextBlock Text="{Binding Display}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
            <TextBlock Text="{Binding Detail}" Opacity="0.65" FontSize="12" TextTrimming="CharacterEllipsis"/>
          </StackPanel>
        </DataTemplate>
        """);

    private OverviewData? _overviewData;
    internal sealed record StartupData(PreferencesStore Preferences, DiagnosticLog Diagnostics, ToolboxStore? Store,
        UserSettings Settings, ScanSnapshot? Snapshot, OverviewData? Overview, DateTimeOffset LastControlTime, string Error, string? DiagnosticPath);

    internal static StartupData LoadStartupData(bool qa, IProgress<(int Step, string Stage)>? progress = null)
    {
        string root = qa ? AppLog.DataRoot : DataPaths.UserRoot;
        var preferences = new PreferencesStore(root);
        var diagnostics = new DiagnosticLog(root);
        var savedPreferences = preferences.Load();
        bool recoveryQa = qa && Environment.GetCommandLineArgs().Contains("recovery-interactive");
        if (recoveryQa && !File.Exists(Path.Combine(root,"toolbox.db"))) { Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "toolbox.db"), "TunHotspot isolated corruption fixture"); }
        try
        {
            progress?.Report((2, "检查启动数据库"));
            long opening = Stopwatch.GetTimestamp();
            var store = new ToolboxStore(root, verifyIntegrity: true);
            double databaseMs = Stopwatch.GetElapsedTime(opening).TotalMilliseconds;
            long reading = Stopwatch.GetTimestamp();
            progress?.Report((3, "读取启动管理概览"));
            var settings = savedPreferences ?? store.Settings;
            var snapshot = store.Latest;
            if (snapshot is not null)
            {
                AttachDecisions(store, snapshot);
            }
            var overview = snapshot is null ? null : OverviewData.Read(store);
            var lastControl = store.Get<DateTimeOffset>("lastControlTime");
            AppLog.Write("startup-data-timing", new { databaseMs, readingMs = Stopwatch.GetElapsedTime(reading).TotalMilliseconds });
            return new(preferences, diagnostics, store, settings, snapshot, overview, lastControl, "", null);
        }
        catch (Exception ex) when ((!qa || recoveryQa) && (ex is Microsoft.Data.Sqlite.SqliteException or InvalidDataException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException))
        {
            var incident = diagnostics.Record("加载启动数据库",ex,true);
            string? path = string.IsNullOrEmpty(incident.LogPath) ? null : incident.LogPath;
            string error = $"{incident.Guidance}\n{DiagnosticLog.Clean(ex.Message)}\n错误编号：{incident.Id}；{incident.Code}";
            error += path is null ? $"\n{incident.StorageWarning}" : $"\n日志：{path}";
            AppLog.Write("startup-data-unavailable", new { qa, type = ex.GetType().FullName, ex.HResult, Details = ex.ToString(), originalPreserved = true });
            return new(preferences, diagnostics, null, savedPreferences ?? new(), null, null, default, error, path);
        }
    }

    internal static StartupData PendingStartup(bool qa)
    {
        string root = qa ? AppLog.DataRoot : DataPaths.UserRoot;
        var preferences = new PreferencesStore(root);
        return new(preferences, new DiagnosticLog(root), null, preferences.Load() ?? new UserSettings(),
            null, null, default, "", null);
    }

    internal ToolboxWindow(StartupData data, bool quiet, bool qa, bool startupLoading = false)
    {
        _startupLoading = startupLoading;
        _preferences = data.Preferences;
        _diagnostics = data.Diagnostics;
        _startupStore = data.Store;
        _startupUnavailable = data.Store is null;
        _settings = data.Settings;
        _snapshot = data.Snapshot;
        _overviewData = data.Overview;
        _lastControlTime = data.LastControlTime;
        _startupError = data.Error;
        _startupDiagnosticPath = data.DiagnosticPath;
        InitializeComponent(); _hotspot = new();
        _hotspotHost=new ScrollViewer{Content=_hotspot,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};_qa=qa;
        VersionLabel.Text=Contract.ProductVersion+" · 启动管理";
        if(qa){_settings.Theme="浅色";_settings.Backdrop="None";}
        InitializeMonitor();
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleArea);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","TunHotspot.ico"));
        RestoreWindowPlacement(handle);
        SettingsModeButton.IsEnabled = !_startupLoading;
        AppWindow.Changed+=(_,change)=>{if(!qa&&!_startupLoading&&!_exiting&&AppWindow.IsVisible&&(change.DidPositionChange||change.DidSizeChange)){_settings.WindowX=AppWindow.Position.X;_settings.WindowY=AppWindow.Position.Y;_settings.WindowWidth=AppWindow.Size.Width;_settings.WindowHeight=AppWindow.Size.Height;}};
        AppWindow.Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (!_exiting && _settings.CloseToTray)
            {
                StartTray();
                if (_tray is not null)
                {
                    AppWindow.Hide();
                    return;
                }
                if (_startupLoading) return;
            }
            DispatcherQueue.TryEnqueue(ExitToolbox);
        };
        try { _uiSettings.AdvancedEffectsEnabledChanged += AdvancedEffectsChanged; } catch(Exception ex) { AppLog.Write("optional-theme-event",new{ex.Message}); }
        try { _accessibility.HighContrastChanged += HighContrastChanged; } catch(Exception ex) { AppLog.Write("optional-accessibility-event",new{ex.Message}); }
        ApplyAppearance(); _initialized = true; Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First();
        if (_preferences.Warning.Length > 0) Tell("应用设置恢复提示", _preferences.Warning, InfoBarSeverity.Warning);
        Navigation.ItemInvoked+=(_,args)=>{if(args.InvokedItemContainer is NavigationViewItem item&&item.Tag?.ToString() is string page){if(page=="entries"){_entryFilter.Clear();RenderPage();}else if(page=="pending"){_pendingFilter.Clear();RenderPage();}}};
        _poll.Tick += async (_,_)=>await Poll();
        if(quiet)DispatcherQueue.TryEnqueue(()=>AppWindow.Hide());
    }
    private void InitializeMonitor()
    {
        if (_startupStore is null) return;
        _startupMonitor = new(_startupStore, Contract.Sid) { Paused = _settings.Paused };
        _startupMonitor.Progress += p => DispatcherQueue.TryEnqueue(() => { if (!_exiting && !_adminBusy) ShowProgress(p); });
        _startupMonitor.SnapshotReady += snapshot => DispatcherQueue.TryEnqueue(() => { if (!_exiting && !_startupUnavailable && !_adminBusy && !_serviceConnected) ApplySnapshot(snapshot); });
    }
    private void RestoreWindowPlacement(nint handle)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        if (!_qa && _settings.WindowX.HasValue && _settings.WindowY.HasValue)
            area = DisplayArea.GetFromRect(new RectInt32(_settings.WindowX.Value, _settings.WindowY.Value,
                Math.Max(640, _settings.WindowWidth), Math.Max(480, _settings.WindowHeight)), DisplayAreaFallback.Nearest).WorkArea;
        double scale = GetDpiForWindow(handle) / 96.0;
        int width = Math.Min((int)(1200 * scale), area.Width - 30), height = Math.Min((int)(820 * scale), area.Height - 30);
        if (!_qa && _settings.WindowWidth >= 640 && _settings.WindowHeight >= 480)
        {
            width = Math.Min(_settings.WindowWidth, area.Width - 30);
            height = Math.Min(_settings.WindowHeight, area.Height - 30);
        }
        int x = !_qa && _settings.WindowX.HasValue ? Math.Clamp(_settings.WindowX.Value, area.X, area.X + area.Width - width) : area.X + (area.Width - width) / 2;
        int y = !_qa && _settings.WindowY.HasValue ? Math.Clamp(_settings.WindowY.Value, area.Y, area.Y + area.Height - height) : area.Y + (area.Height - height) / 2;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }
    internal bool StartupLoading => _startupLoading;
    internal void ReportStartupStage(int step, string stage)
    {
        if (!_startupLoading || _exiting) return;
        _startupStep = step;
        _startupStage = stage;
        if (_page is not ("hotspot" or "configuration")) RenderPage();
    }
    internal void CompleteStartup(StartupData data)
    {
        _preferences = data.Preferences;
        _diagnostics = data.Diagnostics;
        _startupStore = data.Store;
        _startupUnavailable = data.Store is null;
        _settings = data.Settings;
        _snapshot = data.Snapshot;
        _overviewData = data.Overview;
        _lastControlTime = data.LastControlTime;
        _startupError = data.Error;
        _startupDiagnosticPath = data.DiagnosticPath;
        if (_settings.WindowX.HasValue || _settings.WindowWidth >= 640)
            RestoreWindowPlacement(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _startupLoading = false;
        SettingsModeButton.IsEnabled = true;
        InitializeMonitor();
        ApplyAppearance();
        if (_page is not ("hotspot" or "configuration")) RenderPage();
        if (_preferences.Warning.Length > 0) Tell("应用设置恢复提示", _preferences.Warning, InfoBarSeverity.Warning);
    }
    internal void Show(){if(_exiting)return;AppWindow.Show();Activate();}
    internal void ReportError(string message)=>Tell("操作未完成",message,InfoBarSeverity.Error);
    internal void ReportException(Exception error,string operation)
    {
        var record=_diagnostics.Record(operation,error);
        Tell(operation+"未完成",$"{record.Guidance}\n错误编号：{record.Id}；{record.Code}\n日志：{record.LogPath}\n{record.StorageWarning}",InfoBarSeverity.Error);
        Message.Visibility=Visibility.Visible;
    }
}
