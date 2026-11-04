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
    private readonly ToolboxStore _store;
    private readonly PreferencesStore _preferences;
    private readonly DiagnosticLog _diagnostics;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly LiveMonitor _monitor;
    private readonly HotspotPage _hotspot;
    private readonly ScrollViewer _hotspotHost;
    private ConfigurationPage? _configuration;
    private readonly bool _qa;
    private bool _startupUnavailable;
    private string _startupError = "";
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
    private static readonly DataTemplate Rows = (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <StackPanel Padding="4,8" Spacing="4">
            <TextBlock Text="{Binding Display}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
            <TextBlock Text="{Binding Detail}" Opacity="0.65" FontSize="12" TextTrimming="CharacterEllipsis"/>
          </StackPanel>
        </DataTemplate>
        """);

    public ToolboxWindow(bool quiet = false, bool qa = false)
    {
        string qaRoot = Path.Combine(Path.GetTempPath(), "TunHotspot-QA", Environment.ProcessId.ToString());
        _preferences = new(qa ? qaRoot : DataPaths.UserRoot);
        _diagnostics = new(qa ? qaRoot : DataPaths.UserRoot);
        var savedPreferences = _preferences.Load();
        bool recoveryQa = qa && Environment.GetCommandLineArgs().Contains("recovery-interactive");
        if (recoveryQa && !File.Exists(Path.Combine(qaRoot,"toolbox.db"))) { Directory.CreateDirectory(qaRoot); File.WriteAllText(Path.Combine(qaRoot, "toolbox.db"), "TunHotspot isolated corruption fixture"); }
        try
        {
            if(!qa){UserDataMigration.Ensure();savedPreferences=_preferences.Load();}
            _store = new(qa ? qaRoot : DataPaths.UserRoot, verifyIntegrity: true);
            _settings = savedPreferences ?? _store.Settings; _snapshot = _store.Latest;
            _store.ReconcilePending(_snapshot);
            _lastControlTime=_store.Get<DateTimeOffset>("lastControlTime");
            AttachDecisions();
            if(!qa)_=Task.Run(()=>{try{string folder=Path.Combine(_store.Root,"database-backups");if(!Directory.Exists(folder)||!Directory.EnumerateFiles(folder,"toolbox-*.db").Any(x=>File.GetLastWriteTimeUtc(x)>DateTime.UtcNow.AddHours(-6)))_store.CreateVerifiedBackup();}catch(Exception ex){_diagnostics.Record("启动后数据库备份",ex);}});
        }
        catch (Exception ex) when ((!qa || recoveryQa) && (ex is Microsoft.Data.Sqlite.SqliteException or InvalidDataException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException))
        {
            _startupUnavailable = true; _startupError = KumiryoConfiguration.SafeError(ex);
            var incident = _diagnostics.Record("加载启动数据库",ex,true);
            _startupError += $"\n错误编号：{incident.Id}；日志：{incident.LogPath}";
            // This isolated scratch store is never presented as a fresh startup baseline.
            // The original database is neither replaced nor reset, and no monitor is started.
            _store = new(Path.Combine(Path.GetTempPath(), "TunHotspot-Unavailable", Guid.NewGuid().ToString("N")));
            _settings = savedPreferences ?? new(); _snapshot = null;
            AppLog.Write("startup-data-unavailable", new { qa, type = ex.GetType().FullName, ex.HResult, Details = ex.ToString(), originalPreserved = true });
        }
        InitializeComponent(); _hotspot = new();
        _hotspotHost=new ScrollViewer{Content=_hotspot,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Stretch,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};_qa=qa;
        VersionLabel.Text=Contract.ProductVersion+" · 启动管理";
        if(qa){_settings.Theme="浅色";_settings.Backdrop="None";}
        _monitor = new(_store, Contract.Sid) { Paused = _startupUnavailable || _settings.Paused };
        _monitor.Progress += p => DispatcherQueue.TryEnqueue(() => { if (!_adminBusy) ShowProgress(p); });
        _monitor.SnapshotReady += snapshot => DispatcherQueue.TryEnqueue(() => { if (!_startupUnavailable && !_adminBusy && !_serviceConnected) ApplySnapshot(snapshot); });
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleArea);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","TunHotspot.ico"));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        if(!qa&&_settings.WindowX.HasValue&&_settings.WindowY.HasValue)
            area=DisplayArea.GetFromRect(new RectInt32(_settings.WindowX.Value,_settings.WindowY.Value,Math.Max(640,_settings.WindowWidth),Math.Max(480,_settings.WindowHeight)),DisplayAreaFallback.Nearest).WorkArea;
        double scale=GetDpiForWindow(handle)/96.0;
        int width = Math.Min((int)(1200*scale), area.Width - 30), height = Math.Min((int)(820*scale), area.Height - 30);
        if(!qa&&_settings.WindowWidth>=640&&_settings.WindowHeight>=480){width=Math.Min(_settings.WindowWidth,area.Width-30);height=Math.Min(_settings.WindowHeight,area.Height-30);}
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width-width)/2,area.Y+(area.Height-height)/2,width,height));
        if(!qa&&_settings.WindowX.HasValue&&_settings.WindowY.HasValue)AppWindow.Move(new PointInt32(Math.Clamp(_settings.WindowX.Value,area.X,area.X+area.Width-width),Math.Clamp(_settings.WindowY.Value,area.Y,area.Y+area.Height-height)));
        AppWindow.Changed+=(_,change)=>{if(!qa&&AppWindow.IsVisible&&(change.DidPositionChange||change.DidSizeChange)){_settings.WindowX=AppWindow.Position.X;_settings.WindowY=AppWindow.Position.Y;_settings.WindowWidth=AppWindow.Size.Width;_settings.WindowHeight=AppWindow.Size.Height;}};
        AppWindow.Closing += (_, e) =>
        {
            if (!_exiting && _settings.CloseToTray && _tray is not null) { e.Cancel = true; AppWindow.Hide(); }
        };
        Closed += (_, _) => Cleanup();
        try
        {
            if (qa) throw new OperationCanceledException("离屏 UI 验证不注册托盘。");
            _tray = new(handle);
            _tray.Command += HandleTrayCommand;
            _tray.MenuRequested += (x,y)=>DispatcherQueue.TryEnqueue(()=>OpenTrayMenu(x,y));
            _tray.SessionEnding += () => {_exiting = true;AppLog.Write("windows-session-ending",new{databaseMode="DELETE/FULL"});};
            _tray.FontsChanged += () => _=LoadFonts();
            _tray.Resumed += () => { if (!_startupUnavailable) { _monitor.Reconcile("登录界面或电源恢复");_=Poll(); } };
        }
        catch(Exception ex){if(!qa)Tell("托盘不可用",ex.Message,InfoBarSeverity.Warning);}
        try { _uiSettings.AdvancedEffectsEnabledChanged += (_,_)=>DispatcherQueue.TryEnqueue(ApplyAppearance); } catch(Exception ex) { AppLog.Write("optional-theme-event",new{ex.Message}); }
        try { _accessibility.HighContrastChanged += (_,_)=>DispatcherQueue.TryEnqueue(ApplyAppearance); } catch(Exception ex) { AppLog.Write("optional-accessibility-event",new{ex.Message}); }
        ApplyAppearance(); _initialized = true; Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First();
        if (_preferences.Warning.Length > 0) Tell("应用设置恢复提示", _preferences.Warning, InfoBarSeverity.Warning);
        Navigation.ItemInvoked+=(_,args)=>{if(args.InvokedItemContainer is NavigationViewItem item&&item.Tag?.ToString() is string page){if(page=="entries"){_entryFilter.Clear();RenderPage();}else if(page=="pending"){_pendingFilter.Clear();RenderPage();}}};
        _poll.Tick += async (_,_)=>await Poll(); if(!qa&&!_startupUnavailable){_poll.Start();_monitor.Start();_=Poll();}
        _=LoadFonts();
        if(quiet)DispatcherQueue.TryEnqueue(()=>AppWindow.Hide());
        AppLog.Write("toolbox-opened",new {networkChanged=false,quiet,startupAvailable=!_startupUnavailable});
    }
    internal void Show(){AppWindow.Show();Activate();}
    internal void ReportError(string message)=>Tell("操作未完成",message,InfoBarSeverity.Error);
    internal void ReportException(Exception error,string operation)
    {
        var record=_diagnostics.Record(operation,error);
        Tell(operation+"未完成",$"{record.Guidance}\n错误编号：{record.Id}；{record.Code}\n日志：{record.LogPath}\n{record.StorageWarning}",InfoBarSeverity.Error);
        Message.Visibility=Visibility.Visible;
    }
    internal void CloseForRetry(){_exiting=true;Close();}
}
