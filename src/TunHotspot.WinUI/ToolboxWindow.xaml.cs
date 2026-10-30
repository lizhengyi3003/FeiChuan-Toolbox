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
    private readonly ToolboxStore _store = new(DataPaths.UserRoot);
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly LiveMonitor _monitor;
    private readonly HotspotPage _hotspot = new();
    private readonly ScrollViewer _hotspotHost;
    private readonly bool _qa;
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
        InitializeComponent(); _hotspotHost=new ScrollViewer{Content=_hotspot};_qa=qa;_settings = _store.Settings; _snapshot = _store.Latest;
        VersionLabel.Text=Contract.ProductVersion+" · 启动管理";
        _store.ReconcilePending(_snapshot);
        _lastControlTime=_store.Get<DateTimeOffset>("lastControlTime");
        AttachDecisions();
        if(qa){_settings.Theme="浅色";_settings.Backdrop="None";}
        _monitor = new(_store, Contract.Sid) { Paused = _settings.Paused };
        _monitor.Progress += p => DispatcherQueue.TryEnqueue(() => { if (!_adminBusy) ShowProgress(p); });
        _monitor.SnapshotReady += snapshot => DispatcherQueue.TryEnqueue(() => { if (!_adminBusy && !_serviceConnected) ApplySnapshot(snapshot); });
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
            _tray.SessionEnding += () => _exiting = true;
            _tray.FontsChanged += () => _=LoadFonts();
            _tray.Resumed += () => { _monitor.Reconcile("登录界面或电源恢复");_=Poll(); };
        }
        catch(Exception ex){if(!qa)Tell("托盘不可用",ex.Message,InfoBarSeverity.Warning);}
        try { _uiSettings.AdvancedEffectsEnabledChanged += (_,_)=>DispatcherQueue.TryEnqueue(ApplyAppearance); } catch(Exception ex) { AppLog.Write("optional-theme-event",new{ex.Message}); }
        try { _accessibility.HighContrastChanged += (_,_)=>DispatcherQueue.TryEnqueue(ApplyAppearance); } catch(Exception ex) { AppLog.Write("optional-accessibility-event",new{ex.Message}); }
        ApplyAppearance(); _initialized = true; Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First();
        Navigation.ItemInvoked+=(_,args)=>{if(args.InvokedItemContainer is NavigationViewItem item&&item.Tag?.ToString() is string page){if(page=="entries"){_entryFilter.Clear();RenderPage();}else if(page=="pending"){_pendingFilter.Clear();RenderPage();}}};
        _poll.Tick += async (_,_)=>await Poll(); if(!qa){_poll.Start();_monitor.Start();_=Poll();}
        _=LoadFonts();
        if(quiet)DispatcherQueue.TryEnqueue(()=>AppWindow.Hide());
        AppLog.Write("toolbox-opened",new {networkChanged=false,quiet});
    }
    internal void Show(){AppWindow.Show();Activate();}
    internal void ReportError(string message)=>Tell("操作未完成",message,InfoBarSeverity.Error);
    private TrayMenuState TrayState()=>new(_settings,_serviceConnected,_adminBusy||_serviceScanRequested,_store.Alerts.Count(x=>!x.IsRead));
    private void OpenTrayMenu(int x,int y)
    {
        try
        {
            if(_trayMenu is not null){_trayMenu.Activate();return;}
            var menu=new TrayMenuWindow(TrayState(),HandleTrayCommand);_trayMenu=menu;
            menu.Closed+=(_,_)=>{if(ReferenceEquals(_trayMenu,menu))_trayMenu=null;};menu.ShowAt(x,y);
        }
        catch(Exception ex){_trayMenu?.Dismiss();_trayMenu=null;ReportError(ex.Message);AppLog.Write("tray-menu-error",new{ex.Message});}
    }
    private async void HandleTrayCommand(string command)
    {
        try
        {
            if(command=="open")Show();else if(command is "alerts" or "settings"){Show();Navigate(command);}
            else if(command=="scan"){Show();await GlobalScan();}else if(command=="pause")await Pause();else if(command=="exit")ExitToolbox();
        }
        catch(Exception ex){ReportError(ex.Message);}
    }
    internal async Task CaptureForQa(string page)
    {
        try
        {
            bool retainSelection=page=="controls-interactive";
            if(page=="controls-interactive")
            {
                await Task.Delay(1000);
                var target=(_snapshot?.Entries??[]).First(x=>x.Provider=="services"&&ControlPolicy.Reason(x,Contract.Sid).Length==0&&!ControlPolicy.RemoteOrProxy(x));
                _entryFilter.Search=target.Name;Navigate("entries",true);await Task.Delay(300);
                ListView? FindList(DependencyObject root)
                {
                    if(root is ListView list&&Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(list)=="Entries.List")return list;
                    for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)if(FindList(VisualTreeHelper.GetChild(root,i)) is ListView found)return found;
                    return null;
                }
                var list=FindList(PageHost)??throw new InvalidOperationException("条目列表尚未加载。");
                list.SelectedItem=((IEnumerable<StartupEntry>)list.ItemsSource).First(x=>x.Id==target.Id);
                await Task.Delay(20000);page="entries";
            }
            if(page=="filter-regression")
            {
                await Task.Delay(500);var single=(_snapshot?.Entries??[]).GroupBy(x=>x.App).OrderBy(x=>x.Count()).First();
                _entryFilter.App=single.Key;Navigate("entries",true);await Task.Delay(100);
                var filtered=_entryFilter.Apply(_snapshot!.Entries).Count;
                Navigate("overview");Navigate("entries");await Task.Delay(100);
                var all=_entryFilter.Apply(_snapshot.Entries).Count;
                if(all!=_snapshot.Entries.Count||filtered>all)throw new InvalidOperationException("应用筛选返回全部失败。");
                _entryFilter.App=single.Key;Navigate("pending");if(_pendingFilter.App.Length>0)throw new InvalidOperationException("待分析继承了应用筛选。");
                for(int repeat=0;repeat<4;repeat++){Navigate("hotspot");await Task.Delay(60);Navigate("overview");await Task.Delay(60);}
                var report=new{filtered,all,total=_snapshot.Entries.Count,pendingIndependent=true,hotspotNavigationCycles=4,success=true};
                Directory.CreateDirectory(Path.Combine(DataPaths.UserRoot,"qa"));await File.WriteAllTextAsync(Path.Combine(DataPaths.UserRoot,"qa","filter-regression.json"),Contract.Encode(report));
                AppLog.Write("qa-filter-regression",report);page="entries";_entryFilter.Clear();
            }
            if(page is "tray" or "tray-dark" or "tray-interactive")
            {
                _settings.Theme=page=="tray-dark"?"深色":"浅色";
                var invoked=new TaskCompletionSource<string>();
                var menu=new TrayMenuWindow(TrayState(),command=>{AppLog.Write("qa-tray-invoked",new{command});invoked.TrySetResult(command);},true);
                try
                {
                    var area=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Nearest).WorkArea;menu.ShowAt(area.X+area.Width-30,area.Y+area.Height-30);
                    await menu.CaptureForQa(page);
                    if(page=="tray-interactive")await Task.WhenAny(invoked.Task,Task.Delay(15000));
                }
                finally{menu.Dismiss();}return;
            }
            if (!new[] { "overview", "entries", "apps", "runtime", "pending", "alerts", "changes", "controls", "groups", "hotspot", "settings","codex-origin","dsh-origin" }.Contains(page)) throw new InvalidDataException("未知验证页面。");
            await Task.Delay(1200); if(!retainSelection)Navigate(page); await Task.Delay(700);
            // RenderTargetBitmap validates this application's actual XAML tree even on a locked desktop.
            // Native system backdrop visuals are outside this capture and are not claimed as visually verified.
            ShellRoot.Background=new SolidColorBrush(Microsoft.UI.Colors.White);
            var bitmap=new RenderTargetBitmap(); await bitmap.RenderAsync(ShellRoot);
            var pixels=await bitmap.GetPixelsAsync();
            string directory=Path.Combine(DataPaths.UserRoot,"qa");Directory.CreateDirectory(directory);
            var folder=await StorageFolder.GetFolderFromPathAsync(directory);
            var file=await folder.CreateFileAsync(page+".png",CreationCollisionOption.ReplaceExisting);
            using var stream=await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());
            await encoder.FlushAsync();
            AppLog.Write("qa-capture-complete",new{page,bitmap.PixelWidth,bitmap.PixelHeight});
        }
        catch(Exception ex){AppLog.Write("qa-capture-failed",new{page,ex.Message});}
        finally{ExitToolbox();}
    }
    internal void OpenEvent(string id)
    {
        var item=_store.Alerts.FirstOrDefault(x=>x.Id==id);if(item is not null){item.IsRead=true;_store.SaveAlert(item);}Show();Navigate("alerts");
    }
    private void Cleanup()
    {
        if(!_qa)_store.Settings=_settings;
        _poll.Stop();_adminStop?.Cancel();_trayMenu?.Dismiss();_tray?.Dispose();NotificationBridge.Close();_monitor.Dispose();
        AppLog.Write("toolbox-closed",new {networkChanged=false,machineMonitorMayContinue=_serviceConnected});
    }
    private void ExitToolbox(){_exiting=true;Close();}
    private void Tell(string title,string body,InfoBarSeverity severity=InfoBarSeverity.Informational)
    {Message.Title=title;Message.Message=body;Message.Severity=severity;Message.IsOpen=true;}
    private void Navigation_SelectionChanged(NavigationView sender,NavigationViewSelectionChangedEventArgs args)
    {if(_initialized && args.SelectedItem is NavigationViewItem item){_page=item.Tag?.ToString()??"overview";if(_page=="entries"&&!_routeWithFilter)_entryFilter.Clear();if(_page=="pending"&&!_routeWithFilter)_pendingFilter.Clear();RenderPage();}}
    private void Navigate(string page,bool preserveFilter=false)
    {
        var item=Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(x=>x.Tag?.ToString()==page);
        if(!preserveFilter&&page=="entries")_entryFilter.Clear();
        _routeWithFilter=preserveFilter;
        try{if(item is not null){if(ReferenceEquals(Navigation.SelectedItem,item)){_page=page;RenderPage();}else Navigation.SelectedItem=item;}else {_page=page;RenderPage();}}
        finally{_routeWithFilter=false;}
    }
    private void RenderPage()
    {
        if(!_initialized)return;
        _refreshEntries=null;
        PageHost.Content=null;
        PageHost.Content=_page switch {"entries"=>Entries(),"codex-origin"=>Entries(agent:"Codex"),"dsh-origin"=>Entries(agent:"DSH"),"apps"=>Apps(),"groups"=>Groups(),"runtime"=>Runtime(),"pending"=>Pending(),"alerts"=>Alerts(),"changes"=>Changes(),"controls"=>Controls(),"hotspot"=>_hotspotHost,"settings"=>SettingsPage(),_=>Overview()};
    }
    private TextBlock Text(string text,double size=0,bool strong=false)
    {
        var block=new TextBlock{Text=text,FontWeight=strong?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal,TextWrapping=TextWrapping.Wrap};
        if(size>0)block.FontSize=size;else block.SetBinding(TextBlock.FontSizeProperty,new Binding{Source=PageHost,Path=new PropertyPath("FontSize")});
        return block;
    }
    private static StackPanel Column(params UIElement[] elements){var panel=new StackPanel{Spacing=12};foreach(var e in elements)panel.Children.Add(e);return panel;}
    private static Panel Row(params UIElement[] elements){var panel=new FlowPanel();foreach(var e in elements)panel.Children.Add(e);return panel;}
    private static Button Button(string label,Action clicked){var b=new Button{Content=label};b.Click+=(_,_)=>clicked();return b;}
    private static Button AsyncButton(string label,Func<Task> clicked){var b=new Button{Content=label};b.Click+=async(_,_)=>{try{await clicked();}catch(Exception ex){if(Application.Current is App app)app.ReportError(ex.Message);}};return b;}
    private static Border Card(UIElement content)
    {
        var border=(Border)XamlReader.Load("""<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Padding="18" CornerRadius="10" BorderThickness="1" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"/>""");
        border.Child=content;return border;
    }
    private static UIElement Scroll(UIElement content)=>new ScrollViewer{Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
    private UIElement Overview()
    {
        var snapshot=_snapshot;var body=Column(Text("启动与后台，一处看清",28,true),Text("扫描系统配置、核对实际运行，并把不确定的项目交给 Codex 分析。"));
        if(snapshot is null){body.Children.Add(Card(Text("正在建立首次基线。首次扫描已有的项目不会全部被报成新增。")));return Scroll(body);}
        var counts=snapshot.Entries.GroupBy(x=>x.Category).OrderByDescending(x=>x.Count());
        body.Children.Add(Card(Column(Text($"{snapshot.Entries.Count:N0} 个配置入口 · {snapshot.Processes.Count:N0} 个进程快照",22,true),
            Text($"{snapshot.Privilege} · {snapshot.Finished.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {(snapshot.Cancelled?"已取消":snapshot.Complete?"所列采集范围完成":"流程完成，存在采集缺口")}"),
            Text($"本次读取 {snapshot.Entries.Count(x=>x.ObservationStatus=="本次读取"):N0} 项；另保留未能再次确认的历史入口 {snapshot.Entries.Count(x=>x.ObservationStatus!="本次读取"):N0} 项。",12),
            Text(string.Join("   /   ",counts.Select(x=>$"{x.Key} {x.Count()}"))))));
        body.Children.Add(Card(Column(Text("监测与告警",18,true),Text($"采集：{(_serviceConnected?"已连接机器组件":_monitor.State)}\n关注 {_store.Watches.Count} 项，未读告警 {_store.Alerts.Count(x=>!x.IsRead)} 项，待分析 {_store.Pending.Count} 项。"),
            Row(Button("查看全部入口",()=>Navigate("entries")),Button("查看通知与关注",()=>Navigate("alerts"))))));
        if(_store.Get<AuditComparison>("lastAdminAudit") is AuditComparison audit)
            body.Children.Add(Card(Column(Text("最近管理员复核",18,true),Text($"{audit.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {audit.Privilege} · {audit.Entries} 项\n连续两轮：新增 {audit.Added.Count}、消失 {audit.Removed.Count}、配置/目标指纹变化 {audit.Changed.Count}；覆盖说明 {audit.Gaps.Count} 条。"),Text("仅代表该次复核窗口内的观察；覆盖缺口和之后的变化仍需查看扫描记录。",12))));
        body.Children.Add(Text("采集覆盖",20,true));
        foreach(var c in snapshot.Coverage)
        {
            var card=Column(Text(c.Display,14,true));
            if(c.Note.Length>0||c.Issues.Count>0)card.Children.Add(new Expander{Header=c.Issues.Count>0?$"查看 {c.Issues.Count} 条覆盖说明":"查看覆盖说明",HorizontalAlignment=HorizontalAlignment.Stretch,Content=Text(c.Note+"\n"+string.Join("\n",c.Issues),12)});
            body.Children.Add(Card(card));
        }
        body.Children.Add(Text(snapshot.BootObservation,12));return Scroll(body);
    }
    private sealed record FilterChoice(string Key,string Label);
    private UIElement Entries(bool pendingOnly=false,string? agent=null)
    {
        if(pendingOnly)_store.ReconcilePending(_snapshot);
        var state=agent=="Codex"?_codexFilter:agent=="DSH"?_dshFilter:pendingOnly?_pendingFilter:_entryFilter;
        var grid=new Grid{RowSpacing=10};
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var list=new ListView{ItemTemplate=Rows,SelectionMode=ListViewSelectionMode.Single};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(list,"Entries.List");
        StartupEntry? selected=null;
        var source=_snapshot?.Entries??[];
        var search=new TextBox{PlaceholderText="搜索名称、应用、命令、来源或用途",Text=state.Search,MinWidth=260};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(search,"Entries.Search");
        var choices=new List<ComboBox>();
        ComboBox Choice(string header,string all,IEnumerable<string> values,string value,Action<string> save)
        {
            var items=new[]{new FilterChoice("",all)}.Concat(values.Distinct().Order().Select(x=>new FilterChoice(x,x))).ToList();
            var box=new ComboBox{Header=header,ItemsSource=items,DisplayMemberPath="Label",SelectedValuePath="Key",MinWidth=135};
            box.SelectedItem=items.FirstOrDefault(x=>x.Key==value)??items[0];
            box.SelectionChanged+=(_,_)=>{save((box.SelectedItem as FilterChoice)?.Key??"");Filter();};
            choices.Add(box);return box;
        }
        var count=Text("",12);Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(count,"Entries.Count");
        var context=Text("",12);Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(context,"Entries.Context");
        var picked=Text("选中条目后查看证据或执行单项操作。",12);
        var explanation=Text("",12);
        var enable=AsyncButton("启用",async()=>{if(selected is not null)await ChangeEntry(selected,"setEnabled","true");});
        var disable=AsyncButton("禁用",async()=>{if(selected is not null)await ChangeEntry(selected,"setEnabled","false");});
        var startService=AsyncButton("启动服务",async()=>{if(selected is not null)await ChangeEntry(selected,"serviceStart","");});
        var stopService=AsyncButton("停止服务",async()=>{if(selected is not null)await ChangeEntry(selected,"serviceStop","");});
        var modes=new[]{new FilterChoice("auto","自动"),new FilterChoice("delayed-auto","自动（延迟）"),new FilterChoice("demand","手动"),new FilterChoice("disabled","禁用")};
        var serviceMode=new ComboBox{ItemsSource=modes,DisplayMemberPath="Label",MinWidth=125,SelectedIndex=0};
        var applyMode=AsyncButton("应用启动类型",async()=>{if(selected is not null&&serviceMode.SelectedItem is FilterChoice c)await ChangeEntry(selected,"serviceMode",c.Key);});
        var details=AsyncButton("详情与用途",async()=>{if(selected is not null)await ShowEntryDetails(selected);});
        var originButton=AsyncButton("标记创建来源",async()=>{if(selected is not null)await MarkOrigin(selected,agent);});
        var watch=AsyncButton("监视当前状态",async()=>{if(selected is not null)await Watch(selected);});
        var system=Button("打开系统配置",()=>{if(selected is not null)OpenConfiguration(selected);});
        var queue=new Button{Content=pendingOnly?"暂不分析此项":"加入待分析"};
        queue.Click+=(_,_)=>{if(selected is null)return;if(pendingOnly)_store.RemovePending(selected.Id);else _store.Enqueue(selected.Id);Filter();};
        var reset=Button("重置全部筛选",()=>{state.Clear();if(agent is not null)state.Creator=agent;search.Text="";foreach(var box in choices)box.SelectedIndex=0;Filter();});
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(reset,"Entries.Reset");
        var category=Choice("入口类别","全部类别",source.Select(x=>x.Category),state.Category,x=>state.Category=x);
        var advice=Choice("必要性建议","全部建议",new[]{"待分析","建议保留","按需保留","可考虑取消","需修复"},state.Advice,x=>state.Advice=x);
        var control=Choice("启用/启动类型","全部状态",source.Select(x=>x.ControlValue),state.Control,x=>state.Control=x);
        var runtime=Choice("实际运行","全部运行状态",source.Select(EntryQueries.Runtime),state.Runtime,x=>state.Runtime=x);
        var owner=Choice("所属账户","全部账户",source.Select(x=>x.OwnerSid),state.Owner,x=>state.Owner=x);
        var creator=Choice("创建来源","全部来源线索",CreationOrigins.Agents,state.Creator,x=>state.Creator=x);
        if(agent is not null)creator.Visibility=Visibility.Collapsed;
        var originState=Choice("来源证据","全部确认程度",new[]{"用户确认","线索待确认"},state.OriginState,x=>state.OriginState=x);
        void RefreshActions()
        {
            foreach(var button in new[]{enable,disable,startService,stopService,applyMode,details,watch,system,queue,originButton})button.IsEnabled=selected is not null;
            bool service=selected?.Provider=="services";
            startService.Visibility=stopService.Visibility=serviceMode.Visibility=applyMode.Visibility=service?Visibility.Visible:Visibility.Collapsed;
            enable.Visibility=disable.Visibility=service?Visibility.Collapsed:Visibility.Visible;
            if(selected is null){explanation.Text="";picked.Text="选中条目后查看证据或执行单项操作。";return;}
            picked.Text=$"{selected.Name} · {selected.StateText} · {selected.EffectiveAdvice}"+(state.Creator.Length>0?" · "+CreationOrigins.Get(selected,state.Creator).State:"");
            string reason=ControlPolicy.Reason(selected,Contract.Sid);
            explanation.Text=reason.Length>0?reason:service?"更改启动类型不会自动启停服务。所有修改先预览，再申请管理员授权。":"更改此入口的启用状态；其他入口与当前进程不受此按钮连带处理。";
            bool Allowed(string operation,string value){try{ControlPolicy.Validate(selected,Contract.Sid,operation,value);return true;}catch{return false;}}
            enable.IsEnabled=Allowed("setEnabled","true")&&selected.Enabled!=true;
            disable.IsEnabled=Allowed("setEnabled","false")&&selected.Enabled!=false;
            startService.IsEnabled=Allowed("serviceStart","")&&selected.State=="Stopped"&&selected.Mode!="禁用";
            stopService.IsEnabled=Allowed("serviceStop","")&&selected.State=="Running";
            applyMode.IsEnabled=reason.Length==0;serviceMode.IsEnabled=reason.Length==0;
            string current=selected.Mode switch{"自动"=>selected.Evidence.GetValueOrDefault("延迟启动")=="1"?"delayed-auto":"auto","禁用"=>"disabled",_=>"demand"};
            serviceMode.SelectedItem=modes.First(x=>x.Key==current);
            applyMode.IsEnabled=Allowed("serviceMode",current);
            watch.IsEnabled=selected.ObservationStatus=="本次读取"&&!selected.ControlValue.Contains("未确认");
        }
        void Filter()
        {
            if(agent is not null)state.Creator=agent;
            string? old=list.SelectedItem is StartupEntry e?e.Id:null;
            var pending=pendingOnly?_store.Pending.ToHashSet():null;
            var rows=state.Apply(_snapshot?.Entries??[],pending);
            list.ItemsSource=rows;
            list.SelectedItem=rows.FirstOrDefault(x=>x.Id==old);
            selected=list.SelectedItem as StartupEntry;
            int total=agent is not null?(_snapshot?.Entries??[]).Count(x=>CreationOrigins.Get(x,agent).Related):pendingOnly?(_snapshot?.Entries??[]).Count(x=>pending!.Contains(x.Id)):(_snapshot?.Entries.Count??0);
            count.Text=$"显示 {rows.Count:N0} / {total:N0} 项；本机快照共 {_snapshot?.Entries.Count??0:N0} 项。";
            context.Text=state.Active?"当前筛选："+string.Join(" · ",new[]{state.App.Length>0?"应用："+state.App:"",state.Creator,state.OriginState,state.Category,state.Advice,state.Control,state.Runtime,state.Owner,state.Search}.Where(x=>x.Length>0)):"当前未设置筛选。";
            RefreshActions();
        }
        list.SelectionChanged+=(_,_)=>{selected=list.SelectedItem as StartupEntry;RefreshActions();};
        serviceMode.SelectionChanged+=(_,_)=>{if(selected is not null&&serviceMode.SelectedItem is FilterChoice c){try{ControlPolicy.Validate(selected,Contract.Sid,"serviceMode",c.Key);applyMode.IsEnabled=true;}catch{applyMode.IsEnabled=false;}}};
        search.TextChanged+=(_,_)=>{state.Search=search.Text;Filter();};
        var header=Column(Text(agent is not null?agent+" 创建":pendingOnly?"待分析":"全部启动入口",24,true),Row(search,category,advice),
            new Expander{Header="状态、账户与来源筛选",HorizontalAlignment=HorizontalAlignment.Stretch,Content=Row(control,runtime,owner,creator,originState)},
            Row(reset,details,originButton,watch,queue,system),context,count,picked,Row(enable,disable,startService,stopService,serviceMode,applyMode),explanation);
        if(agent is not null)header.Children.Insert(1,Text($"用户确认 {source.Count(e=>CreationOrigins.Get(e,agent).Confirmed)} 项；线索待确认 {source.Count(e=>CreationOrigins.Get(e,agent).State=="线索待确认")} 项。名称和路径关联不能证明创建者；可在全部入口中确认、排除或清除来源标记。",12));
        if(pendingOnly)
        {
            var pendingIds=_store.Pending.ToHashSet();var reviewed=_store.Decisions.ToDictionary(x=>x.EntryId);
            int historical=source.Count(e=>pendingIds.Contains(e.Id)&&e.ObservationStatus!="本次读取");
            int needsEvidence=source.Count(e=>e.ObservationStatus=="本次读取"&&pendingIds.Contains(e.Id)&&reviewed.TryGetValue(e.Id,out var d)&&e.MatchesFingerprint(d.Fingerprint));
            header.Children.Insert(1,Text($"未分析条目自动进入；每批最多 100 项。其中历史保留待重新采集 {historical} 项，当前已有分析但需补证据 {needsEvidence} 项。",12));
            header.Children.Insert(2,Row(AsyncButton("导出分析包",ExportBatch),AsyncButton("导入分析结果",ImportAnalysis),AsyncButton("连接本机 Codex",ConnectCodex)));
        }
        grid.Children.Add(header);Grid.SetRow(list,1);grid.Children.Add(list);_refreshEntries=Filter;Filter();return grid;
    }
    private async Task ShowEntryDetails(StartupEntry entry)
    {
        var note=new TextBox{Header="个人用途",Text=entry.UserNote,AcceptsReturn=true,MaxLength=2000,TextWrapping=TextWrapping.Wrap};
        var dialog=new ContentDialog{XamlRoot=ShellRoot.XamlRoot,Title=entry.Name,PrimaryButtonText="保存用途",CloseButtonText="关闭",DefaultButton=ContentDialogButton.Close,
            Content=new ScrollViewer{Content=Column(new TextBox{Text=EntryDetail(entry),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=300},note),MaxHeight=480}};
        if(await dialog.ShowAsync()==ContentDialogResult.Primary){_store.SaveNote(entry.Id,note.Text);entry.UserNote=note.Text;entry.Analysis=null;_refreshEntries?.Invoke();}
    }
    private async Task MarkOrigin(StartupEntry entry,string? preferredAgent)
    {
        var agent=new ComboBox{Header="来源",ItemsSource=CreationOrigins.Agents,SelectedItem=preferredAgent??"Codex"};
        var state=new ComboBox{Header="人工标记",ItemsSource=new[]{"用户确认","已排除","清除标记"},SelectedIndex=0};
        var evidence=new TextBox{Header="确认或排除的依据",AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=2000,PlaceholderText="例如当时的操作记录、脚本位置或你的明确确认"};
        var message=Text(string.Join("\n",CreationOrigins.Agents.Select(x=>CreationOrigins.Get(entry,x).Display)),12);
        var dialog=new ContentDialog{XamlRoot=ShellRoot.XamlRoot,Title="标记创建来源",PrimaryButtonText="保存标记",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,Content=Column(Text(entry.Name,16,true),message,agent,state,evidence,Text("只保存软件内的来源标记，不改变 Windows 启动配置；目标命令变化后标记需要复核。",12))};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        _store.SaveOrigin(entry,agent.SelectedItem!.ToString()!,state.SelectedItem!.ToString()!,evidence.Text);
        if(_snapshot is not null)_store.AttachOrigins(_snapshot);_refreshEntries?.Invoke();
    }
    private static void OpenConfiguration(StartupEntry entry)
    {
        string target=entry.Provider switch{"services"=>"services.msc","tasks"=>"taskschd.msc","run" or "appx"=>"ms-settings:startupapps",_=>"regedit.exe"};
        Process.Start(new ProcessStartInfo(target){UseShellExecute=true});
    }
    private static string OperationName(string operation,string value)=>operation switch
    {"setEnabled"=>value=="true"?"启用启动入口":"禁用启动入口","serviceStart"=>"启动服务","serviceStop"=>"停止服务","serviceMode"=>"启动类型改为 "+(value switch{"auto"=>"自动","delayed-auto"=>"自动（延迟）","demand"=>"手动",_=>"禁用"}),"restore"=>"恢复备份状态",_=>"同步恢复记录"};
    private async Task ChangeEntry(StartupEntry entry,string operation,string value)
    {
        if(_adminBusy){Tell("已有操作正在进行","请等待当前扫描或修改结束。");return;}
        ControlPolicy.Validate(entry,Contract.Sid,operation,value);
        var watch=new CheckBox{Content="修改后将新启动状态设为关注值",IsChecked=true,Visibility=operation is "setEnabled" or "serviceMode"?Visibility.Visible:Visibility.Collapsed};
        var dialog=new ContentDialog{XamlRoot=ShellRoot.XamlRoot,Title=OperationName(operation,value),PrimaryButtonText="确认并申请管理员授权",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,
            Content=Column(Text(entry.Display,16,true),Text($"当前：{entry.StateText}\n目标：{OperationName(operation,value)}\n来源：{entry.Source}"),Text("执行前重新核对配置并保存受控备份，执行后读取系统实际结果。恢复记录保存在“操作与恢复”。",12),
                Text(entry.Provider=="services"?$"依赖登记：{entry.Evidence.GetValueOrDefault("依赖","未提供")}\n启动类型和当前运行状态分别处理；停止服务不会连带停止其他服务。":"登录入口的启用状态与已经运行的进程分别处理。",12),watch)};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        if(_qa)throw new InvalidOperationException("界面验证只允许查看和取消修改预览。");
        await RunControl(new(){EntryId=entry.Id,Provider=entry.Provider,Fingerprint=entry.Fingerprint,UserNote=entry.UserNote,Operation=operation,Value=value},watch.Visibility==Visibility.Visible&&watch.IsChecked==true);
    }
    private async Task RunControl(StartupControlRequest request,bool watchAfter=false)
    {
        if(_adminBusy)return;_adminBusy=true;bool previousPause=_monitor.Paused;_monitor.Paused=true;_monitor.CancelScan();GlobalScanButton.IsEnabled=false;
        try
        {
            ScanTitle.Text="等待所选操作的管理员授权";ScanDescription.Text="授权后仅执行预览中的单项操作。";ScanProgressBar.IsIndeterminate=true;
            var response=await AdminClient.Run("--control",p=>DispatcherQueue.TryEnqueue(()=>ShowProgress(p)),CancellationToken.None,request);
            var records=response.Controls??throw new InvalidDataException("未返回操作记录。");foreach(var record in records)_store.SaveControl(record);
            if(request.Operation=="history"){Tell("恢复记录已同步",$"读取本账户 {records.Count} 份受控记录。");RenderPage();return;}
            var current=records.First();bool success=current.Status is "已应用" or "已恢复";
            if(current.After is StartupEntry after)
            {
                _lastControlTime=after.ObservedAt;_store.Set("lastControlTime",_lastControlTime);
                if(success&&watchAfter&&!after.ControlValue.Contains("未确认"))
                {
                    var rule=ToolboxStore.Watch(after);_store.SaveWatch(rule);
                    if(_serviceConnected){try{await AdminClient.Service(new(){Kind="watch",Rule=rule});}catch(Exception ex){_store.AddEvents(new ObservedEvent[]{new(){Kind="监测缺口",Name="机器关注同步",Detail=ex.Message+"；请更新机器组件后重新关注。"}});}}
                }
                else if(success&&watchAfter&&request.Operation=="restore")
                {
                    _store.RemoveWatch(after.Id);
                    if(_serviceConnected){try{await AdminClient.Service(new(){Kind="unwatch",Text=after.Id});}catch(Exception ex){_store.AddEvents(new ObservedEvent[]{new(){Kind="监测缺口",Name="机器关注移除",Detail=ex.Message}});}}
                }
                var snapshot=_store.MergeControlEntry(after);if(snapshot is not null)ApplySnapshot(snapshot);
            }
            if(_serviceConnected){try{await AdminClient.Service(new(){Kind="scan"});}catch{}}
            Tell(success?"操作已由系统重新核对":"操作没有完整完成",$"{current.Before.Name}：{current.Status}。{current.Error}\n备份编号：{current.Id}",success?InfoBarSeverity.Success:InfoBarSeverity.Warning);RenderPage();
        }
        catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){Tell("管理员授权已取消","未执行该项修改。");}
        catch(Exception ex){Tell("操作未完成",ex.Message+" 如执行进程意外中断，可在“操作与恢复”同步受控备份。",InfoBarSeverity.Error);}
        finally{_adminBusy=false;_monitor.Paused=_serviceConnected||previousPause;GlobalScanButton.IsEnabled=true;ScanProgressBar.IsIndeterminate=false;ScanTitle.Text="单项操作结束";ScanDescription.Text="结果请查看提示和操作记录；全局扫描仍可独立执行。";}
    }
    private UIElement Controls()
    {
        var list=new ListView{ItemTemplate=Rows,ItemsSource=_store.Controls};
        var detail=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=185};
        var restore=AsyncButton("恢复所选备份",async()=>
        {
            if(list.SelectedItem is not StartupControlRecord record)return;
            if(record.Status is not ("已应用" or "失败，存在状态变化，可恢复")){Tell("此记录不需要自动恢复","只有已核实的应用结果或部分修改记录可恢复；未完成记录需先核对。 ");return;}
            var dialog=new ContentDialog{XamlRoot=ShellRoot.XamlRoot,Title="恢复所选条目",PrimaryButtonText="确认并申请管理员授权",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close,
                Content=Column(Text(record.Before.Name,16,true),Text($"备份前：{record.Before.StateText}\n执行后：{record.After?.StateText}\n恢复前将再次核对；若配置或运行状态已被其他内容改变，则拒绝覆盖。"))};
            if(await dialog.ShowAsync()==ContentDialogResult.Primary)await RunControl(new(){Operation="restore",RestoreId=record.Id},record.Operation is "setEnabled" or "serviceMode");
        });
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is StartupControlRecord r)detail.Text=$"{r.Display}\n{r.Detail}\n原状态：{r.Before.StateText}\n结果：{r.After?.StateText}\n来源：{r.Before.Source}\n备份编号：{r.Id}\n受控副本由管理员保存；本地记录不会作为恢复指令直接执行。";};
        return ListPage("操作与恢复",Column(Text("每项修改保存原状态和系统核对结果；恢复会重新申请权限并拒绝覆盖后续变化。",12),Row(restore,AsyncButton("同步管理员备份",()=>RunControl(new(){Operation="history"})))),list,detail);
    }
    private string EntryDetail(StartupEntry e)
    {
        var result=_store.Decisions.FirstOrDefault(x=>x.EntryId==e.Id&&e.MatchesFingerprint(x.Fingerprint));
        var text=new StringBuilder().AppendLine(e.Display).AppendLine(e.Detail).AppendLine(e.Reason).AppendLine($"观察：{e.ObservationStatus} · {e.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n来源：{e.Source}\n账户：{e.OwnerSid} · 视图：{e.View}\n命令：{e.Command}\n解析文件：{e.Executable}\n签名：{e.Signature}\n提供程序：{e.Provider}\n条目 ID：{e.Id}");
        if(result is not null)text.AppendLine($"\nCodex 建议：{result.Advice}（{result.Confidence}）\n{result.Reason}\n功能影响：{result.Impact}");
        if(e.UserNote.Length>0)text.AppendLine("\n个人用途（用户备注）："+e.UserNote);
        foreach(string agent in CreationOrigins.Agents)text.AppendLine("\n创建来源："+CreationOrigins.Get(e,agent).Display);
        foreach(var item in e.Evidence)text.AppendLine($"\n{item.Key}：{item.Value}");return text.ToString();
    }
    private async Task Watch(StartupEntry entry)
    {
        try
        {
            if(entry.ObservationStatus!="本次读取"||entry.ControlValue.Length==0||entry.ControlValue.Contains("未确认"))throw new InvalidOperationException("该条目实际控制状态尚未确认，不能建立确定期望。");
            var rule=ToolboxStore.Watch(entry);_store.SaveWatch(rule);
            if(_serviceConnected)await AdminClient.Service(new(){Kind="watch",Rule=rule});
            Tell("已监视当前状态",$"{entry.Name}：期望 {entry.ControlValue}。发现偏离会留档并按设置发送系统通知。只保存关注规则，未修改系统。");
        }
        catch(Exception ex){Tell("关注状态未完整保存",ex.Message,InfoBarSeverity.Warning);}
    }
    private UIElement Groups()
    {
        var kinds=new[]{"类别","应用","建议","启用状态","运行状态","账户","创建来源"};
        var choice=new ComboBox{Header="分类方式",ItemsSource=kinds,SelectedItem=_groupKind,MinWidth=220};
        var list=new ListView{ItemTemplate=Rows};
        void Refresh()=>list.ItemsSource=EntryQueries.Groups(_snapshot?.Entries??[],_groupKind).ToList();
        choice.SelectionChanged+=(_,_)=>{_groupKind=choice.SelectedItem?.ToString()??"类别";Refresh();};
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is EntryGroup group){EntryQueries.SelectGroup(_entryFilter,_groupKind,group.Name);Navigate("entries",true);}};
        Refresh();return ListPage("分类浏览",Column(choice,Text("支持按入口类别、应用、建议、启用状态、实际运行、账户或创建来源查看；来源分组包括待核实线索。",12)),list);
    }
    private UIElement Apps()
    {
        var list=new ListView{ItemTemplate=Rows,ItemsSource=EntryQueries.Groups(_snapshot?.Entries??[],"应用").ToList()};
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is EntryGroup row){EntryQueries.SelectGroup(_entryFilter,"应用",row.Name);Navigate("entries",true);}};
        return ListPage("按应用查看",Text("选择应用后会显示明确的应用筛选；点击左侧“全部启动入口”即返回全部。"),list);
    }
    private UIElement Runtime()
    {
        var choice=new ComboBox{ItemsSource=new[]{"持续观察事件","当前进程快照"},SelectedIndex=0};var list=new ListView{ItemTemplate=Rows};var detail=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=160};
        void Refresh(){list.ItemsSource=choice.SelectedIndex==0?(object)_store.Events():_snapshot?.Processes??[];}
        choice.SelectionChanged+=(_,_)=>Refresh();list.SelectionChanged+=(_,_)=>detail.Text=list.SelectedItem switch{ObservedEvent e=>$"{e.Display}\n{e.Source}\n{e.Association}\n{e.Path}\n{e.Detail}",ProcessRecord p=>$"{p.Display}\n{p.Detail}\n{p.Path}\n{p.Command}",_=>""};Refresh();
        return ListPage("运行与拉起记录",Column(choice,Text("普通权限仅提供可用范围。完整持续进程事件需要管理员/机器组件；父 PID 不等于确定的自动启动原因。",12)),list,detail);
    }
    private UIElement Pending()=>Entries(true);
    private UIElement Alerts()
    {
        var list=new ListView{ItemTemplate=Rows};var detail=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=150};
        var search=new TextBox{PlaceholderText="搜索通知标题、期望、实测或证据",MinWidth=300};
        var state=new ComboBox{ItemsSource=new[]{"全部通知","未读","待处理","已恢复"},SelectedIndex=0};
        void Filter(){list.ItemsSource=_store.Alerts.Where(x=>(state.SelectedIndex switch{1=>!x.IsRead,2=>!x.Resolved,3=>x.Resolved,_=>true})&&$"{x.Title} {x.Expected} {x.Actual} {x.Evidence}".Contains(search.Text.Trim(),StringComparison.CurrentCultureIgnoreCase)).ToList();}
        search.TextChanged+=(_,_)=>Filter();state.SelectionChanged+=(_,_)=>Filter();Filter();
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is AlertEvent e)detail.Text=$"{e.Display}\n{e.Detail}\n{e.Evidence}";};
        var buttons=Row(Button("标记已读",()=>{if(list.SelectedItem is AlertEvent a){a.IsRead=true;_store.SaveAlert(a);RenderPage();}}),
            Button("全部标记已读",()=>{foreach(var a in _store.Alerts){a.IsRead=true;_store.SaveAlert(a);}RenderPage();}),
            Button("重发所选通知",()=>{if(list.SelectedItem is AlertEvent a){a.Delivery=NotificationBridge.Send(a);_store.SaveAlert(a);Filter();Tell("通知发送结果",a.Delivery);}}),
            AsyncButton("以当前状态重新关注",async()=>{if(list.SelectedItem is AlertEvent a&&_snapshot?.Entries.FirstOrDefault(x=>x.Id==a.EntryId) is StartupEntry e){await Watch(e);_store.EvaluateWatches(_snapshot);RenderPage();}}));
        var watches=new ComboBox{ItemsSource=_store.Watches,DisplayMemberPath="Display",PlaceholderText="已关注的状态",MinWidth=240};
        var ruleButtons=Row(watches,AsyncButton("取消该关注",async()=>{if(watches.SelectedItem is WatchRule r){_store.RemoveWatch(r.EntryId);if(_serviceConnected)await AdminClient.Service(new(){Kind="unwatch",Text=r.EntryId});RenderPage();}}));
        return ListPage("通知与关注",Column(Text("通知划掉或清空后，记录仍保存在这里。已读与问题解决分别记录。"),Row(search,state),buttons,ruleButtons),list,detail);
    }
    private UIElement Changes()
    {
        var list=new ListView{ItemTemplate=Rows};var detail=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=170};
        var choice=new ComboBox{ItemsSource=new[]{"配置变化与缺口","历史快照（最近 200 份）"},SelectedIndex=0};
        void Refresh(){list.ItemsSource=choice.SelectedIndex==0?(object)_store.Events(5000).Where(x=>x.Kind is "入口出现" or "入口消失" or "配置变化" or "监测缺口" or "监测范围").ToList():_store.History;}
        choice.SelectionChanged+=(_,_)=>Refresh();Refresh();
        list.SelectionChanged+=(_,_)=>
        {
            if(list.SelectedItem is ObservedEvent e)detail.Text=$"{e.Display}\n{e.Source}\n{e.Detail}";
            else if(list.SelectedItem is SnapshotSummary summary&&_store.GetSnapshot(summary.Id) is ScanSnapshot old)
            {
                var before=old.Entries.ToDictionary(x=>x.Id);var current=(_snapshot?.Entries??[]).ToDictionary(x=>x.Id);
                var changed=current.Values.Where(x=>!before.TryGetValue(x.Id,out var previous)||previous.Fingerprint!=x.Fingerprint).ToList();
                var absent=before.Values.Where(x=>!current.ContainsKey(x.Id)).ToList();
                detail.Text=$"{summary.Display}\n采集权限：{old.Privilege}；流程完整：{old.Complete}\n相对该快照，当前新增或配置变化 {changed.Count} 项、未出现在当前视图 {absent.Count} 项。采集缺口不能证明配置已删除。\n"+string.Join("\n",changed.Select(x=>x.Name+" · "+x.StateText).Concat(absent.Select(x=>x.Name+" · 当前未出现")).Take(100));
            }
        };
        return ListPage("快照与变化",Column(choice,Row(AsyncButton("导出本机扫描报告",ExportReport),Button("打开本地数据目录",()=>Process.Start(new ProcessStartInfo("explorer.exe",DataPaths.UserRoot){UseShellExecute=true})))),list,detail);
    }
    private Grid ListPage(string title,UIElement top,UIElement list,UIElement? bottom=null)
    {
        var grid=new Grid{RowSpacing=12};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(Column(Text(title,24,true),top));Grid.SetRow((FrameworkElement)list,1);grid.Children.Add(list);if(bottom is not null){Grid.SetRow((FrameworkElement)bottom,2);grid.Children.Add(bottom);}return grid;
    }
    private UIElement SettingsPage()
    {
        var materials=new ComboBox{Header="背景材质",ItemsSource=new[]{"Acrylic","Mica","MicaAlt","None"},SelectedItem=_settings.Backdrop,MinWidth=180};
        var themes=new ComboBox{Header="主题",ItemsSource=new[]{"跟随系统","浅色","深色"},SelectedItem=_settings.Theme,MinWidth=160};
        var font=new ComboBox{Header="字体（DirectWrite 自动枚举）",ItemsSource=_fonts,SelectedItem=_settings.Font,Text=_settings.Font,IsEditable=true,IsTextSearchEnabled=true,MinWidth=280,MaxDropDownHeight=320};
        font.Text=_settings.Font;
        var size=new NumberBox{Header="正文字号",Value=_settings.FontSize,Minimum=10,Maximum=28,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact,Width=130};
        materials.SelectionChanged+=(_,_)=>{_settings.Backdrop=materials.SelectedItem?.ToString()??"Mica";SaveSettings();};themes.SelectionChanged+=(_,_)=>{_settings.Theme=themes.SelectedItem?.ToString()??"跟随系统";SaveSettings();};
        font.SelectionChanged+=(_,_)=>{if(font.SelectedItem is string name){_settings.Font=name;SaveSettings();}};size.ValueChanged+=(_,_)=>{if(double.IsFinite(size.Value)){_settings.FontSize=size.Value;SaveSettings();}};
        font.TextSubmitted+=(_,args)=>{string? match=_fonts.FirstOrDefault(x=>x.Equals(args.Text,StringComparison.CurrentCultureIgnoreCase));if(match is not null){font.SelectedItem=match;_settings.Font=match;SaveSettings();}else Tell("未找到该字体","请选择已枚举的字体，或安装字体后刷新列表。");args.Handled=true;};
        var tray=new ToggleSwitch{Header="关闭窗口时收起到托盘",IsOn=_settings.CloseToTray};tray.Toggled+=(_,_)=>{_settings.CloseToTray=tray.IsOn;SaveSettings();};
        var notify=new ToggleSwitch{Header="Windows 系统通知",IsOn=_settings.Notifications};notify.Toggled+=(_,_)=>{_settings.Notifications=notify.IsOn;SaveSettings();};
        var body=Column(Text("设置",28,true),Card(Column(Text("外观",18,true),Row(materials,themes),Text("Mica 使用主题与壁纸形成背景，本身不透视其他窗口；Acrylic 提供毛玻璃效果。",12),Text(_materialState,12),Row(font,size),
            Row(AsyncButton("刷新字体列表",async()=>{await LoadFonts();RenderPage();}),Button("恢复默认外观",()=>{_settings.Backdrop="Mica";_settings.Theme="跟随系统";_settings.Font="Segoe UI Variable";_settings.FontSize=14;SaveSettings();RenderPage();})))),
            Card(Column(Text("托盘与通知",18,true),tray,notify,Text("通知状态："+NotificationBridge.Status,12),Button("发送测试通知",()=>{var a=new AlertEvent{Title="工具箱测试通知",EntryId="notification-test",Expected="通知和记录可查看",Actual="测试事件",Evidence="本软件本地测试，不修改启动配置。"};_store.SaveAlert(a);a.Delivery=NotificationBridge.Send(a);_store.SaveAlert(a);Tell("测试结果",a.Delivery);}))),
            Card(Column(Text("包含开机的持续监测",18,true),Text(_serviceConnected?"机器组件已连接；关闭界面后仍采集，通知由登录托盘进程发送。":"尚未连接机器组件。当前提供普通权限范围，手动全局扫描可申请管理员采集。"),
                Row(AsyncButton("安装/更新开机采集组件",()=>Component(true)),AsyncButton("卸载采集组件",()=>Component(false)),AsyncButton(_settings.Paused?"恢复监测":"暂停监测",Pause)),
                Text("安装时会弹 UAC，并添加本软件的机器服务和当前用户登录托盘入口。卸载组件保留历史数据及受控版本文件供恢复。",12))),
            Card(Column(Text("分析与数据",18,true),Text("数据保存在当前用户本地目录；机器采集日志位于管理员控制的 ProgramData。普通运行事件与长期告警分别保留。"),Row(AsyncButton("连接 Codex",ConnectCodex),AsyncButton("导出扫描报告",ExportReport)),
                Text("Windows 11 x64 · 自包含发布 · AI 分析可选。常见登录入口、第三方任务及普通服务支持单项控制；驱动和特殊入口显示具体限制与系统配置入口。",12))));
        return Scroll(body);
    }
    private void SaveSettings(){_store.Settings=_settings;ApplyAppearance();}
    private void ApplyAppearance()
    {
        try
        {
            var theme=_settings.Theme switch{"浅色"=>ElementTheme.Light,"深色"=>ElementTheme.Dark,_=>ElementTheme.Default};
            ShellRoot.RequestedTheme=theme;Navigation.RequestedTheme=theme;
            Navigation.FontFamily=new FontFamily(_settings.Font);Navigation.FontSize=_settings.FontSize;
            PageHost.FontFamily=new FontFamily(_settings.Font);PageHost.FontSize=_settings.FontSize;
            bool highContrast=false,effects=true;
            try{highContrast=_accessibility.HighContrast;}catch{}
            try{effects=_uiSettings.AdvancedEffectsEnabled;}catch{}
            bool allow=!highContrast&&effects;
            SystemBackdrop=null;ShellRoot.Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            string material=allow?_settings.Backdrop:"None";
            if(material=="Acrylic"&&DesktopAcrylicController.IsSupported())SystemBackdrop=new DesktopAcrylicBackdrop();
            else if(material is "Mica" or "MicaAlt"&&MicaController.IsSupported())SystemBackdrop=new MicaBackdrop{Kind=material=="MicaAlt"?MicaKind.BaseAlt:MicaKind.Base};
            else {material="None";var systemBackground=_uiSettings.GetColorValue(UIColorType.Background);bool dark=theme==ElementTheme.Dark||(theme==ElementTheme.Default&&systemBackground.R+systemBackground.G+systemBackground.B<384);ShellRoot.Background=new SolidColorBrush(dark?Color.FromArgb(255,32,32,32):Color.FromArgb(255,247,249,252));}
            _materialState=$"选择：{_settings.Backdrop}；当前：{material}。系统高对比度或透明策略优先。";
        }
        catch(Exception ex){SystemBackdrop=null;ShellRoot.Background=new SolidColorBrush(Navigation.ActualTheme==ElementTheme.Dark?Microsoft.UI.Colors.Black:Microsoft.UI.Colors.White);_materialState="材质/字体已回退："+ex.Message;}
    }
    private async Task LoadFonts()
    {
        try{_fonts=await Task.Run(FontCatalog.Installed);AppLog.Write("font-enumeration",new{count=_fonts.Count});}
        catch(Exception ex){_fonts=["Segoe UI Variable","Microsoft YaHei UI"];AppLog.Write("font-enumeration-error",new{ex.Message});}
    }
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
        _tray?.Update($"TunHotspot · {p.Stage} · {p.Discovered} 项");
    }
    private void ApplySnapshot(ScanSnapshot snapshot)
    {
        _snapshot=snapshot;AttachDecisions();ScanProgressBar.IsIndeterminate=false;ScanProgressBar.Value=100;
        ScanTitle.Text=snapshot.Cancelled?"扫描已取消":snapshot.Complete?"采集流程完成":"采集流程完成 · 存在覆盖缺口";
        ScanDescription.Text=$"{snapshot.Privilege} · {snapshot.Entries.Count:N0} 个入口 · {snapshot.Finished.ToLocalTime():HH:mm:ss} · 只读";
        _tray?.Update($"TunHotspot · {snapshot.Entries.Count} 个入口 · 未读告警 {_store.Alerts.Count(x=>!x.IsRead)}");
        if(_page is "entries" or "pending" or "codex-origin" or "dsh-origin")_refreshEntries?.Invoke();else if(_page is "overview" or "apps" or "groups")RenderPage();
        AppLog.Write("snapshot-received",new{snapshot.Id,count=snapshot.Entries.Count,snapshot.Privilege,complete=snapshot.Complete});
    }
    private void AttachDecisions()
    {
        if(_snapshot is not null)_store.AttachOrigins(_snapshot);
        var decisions=_store.Decisions.ToDictionary(x=>x.EntryId);
        foreach(var e in _snapshot?.Entries??[])e.Analysis=decisions.TryGetValue(e.Id,out var decision)&&e.MatchesFingerprint(decision.Fingerprint)?decision:null;
    }
    private async Task GlobalScan()
    {
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
            using var run=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if(install)run.SetValue("TunHotspot.Toolbox.Agent",$"\"{Environment.ProcessPath}\" --background");else run.DeleteValue("TunHotspot.Toolbox.Agent",false);
            Tell(install?"开机采集组件已安装":"采集组件已卸载",install?"机器服务只读采集；当前用户登录时启动托盘通知代理。":"自身服务与登录入口已移除；历史数据和受控版本文件保留，热点状态未改变。");
            await Poll();RenderPage();
        }
        catch(Exception ex){Tell("组件操作未完成",ex.Message,InfoBarSeverity.Error);}
        finally{_adminBusy=false;GlobalScanButton.IsEnabled=true;ScanProgressBar.IsIndeterminate=false;}
    }
    private async Task Pause()
    {
        _settings.Paused=!_settings.Paused;_monitor.Paused=_settings.Paused;
        if(_serviceConnected)await AdminClient.Service(new(){Kind="pause",Text=_settings.Paused?"true":"false"});
        SaveSettings();Tell(_settings.Paused?"监测已暂停":"监测已恢复","手动扫描仍可使用；没有停止其他服务或改变热点。");if(_page=="settings")RenderPage();
        _tray?.Update($"TunHotspot · {(_settings.Paused?"监测已暂停":"监测运行中")} · 未读提醒 {_store.Alerts.Count(x=>!x.IsRead)}");
    }
    private async Task Poll()
    {
        if(_polling||_exiting)return;_polling=true;
        try
        {
            try
            {
                var response=await AdminClient.Service(new(){Kind="status"});_serviceConnected=true;_monitor.Paused=true;
                if(response.Snapshot is not null&&response.Snapshot.Id!=_machineSnapshotId&&response.Snapshot.Started>=_lastControlTime)
                {_machineSnapshotId=response.Snapshot.Id;_store.SaveSnapshot(response.Snapshot);ApplySnapshot(response.Snapshot);if(_serviceScanRequested&&response.Progress is null){_serviceScanRequested=false;CancelScanButton.IsEnabled=false;}}
                if(response.Progress is not null)ShowProgress(response.Progress);
                else if(_serviceScanRequested&&response.ScanCancelled){_serviceScanRequested=false;CancelScanButton.IsEnabled=false;ScanProgressBar.IsIndeterminate=false;ScanTitle.Text="机器扫描已取消，保留上次结果";}
                if(response.Alerts is not null)_store.MergeRemoteAlerts(response.Alerts);if(response.Events is not null)await Task.Run(()=>_store.AddEvents(response.Events));
            }
            catch{if(_serviceConnected){_serviceConnected=false;_machineSnapshotId=null;Tell("机器组件连接中断","已保留上次数据，将使用普通权限可用范围；开机/管理员监测覆盖需要重新核实。",InfoBarSeverity.Warning);}_monitor.Paused=_settings.Paused;}
            if(_settings.Notifications)
            {
                foreach(var alert in _store.Alerts.Where(x=>x.Delivery=="待发送").Take(3))
                {alert.Delivery=NotificationBridge.Send(alert);_store.SaveAlert(alert);}
            }
            var lastAnalysis=_store.Get<DateTimeOffset>("lastAnalysis");
            if(lastAnalysis!=_lastAnalysis){_lastAnalysis=lastAnalysis;AttachDecisions();if(_page is not ("hotspot" or "settings"))RenderPage();}
        }
        catch(Exception ex){Tell("状态/历史保存异常",ex.Message,InfoBarSeverity.Warning);}
        finally{_polling=false;}
    }
    private async Task<string?> PickSave(string suggested,string extension)
    {
        var picker=new FileSavePicker{SuggestedFileName=suggested};picker.FileTypeChoices.Add(extension==".html"?"HTML 报告":"JSON 数据",new List<string>{extension});
        WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));var file=await picker.PickSaveFileAsync();return file?.Path;
    }
    private async Task ExportBatch()
    {
        var batch=_store.CreateBatch();var preview=new ContentDialog{XamlRoot=ShellRoot.XamlRoot,Title="预览脱敏后的分析证据",PrimaryButtonText="继续导出",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary,
            Content=new TextBox{Text=Contract.Encode(batch),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=420,MinWidth=420}};
        if(await preview.ShowAsync()!=ContentDialogResult.Primary)return;
        string? file=await PickSave("TunHotspot-analysis-"+batch.Id[..8],".json");
        if(file is null)return;await File.WriteAllTextAsync(file,Contract.Encode(batch));Tell("分析包已导出","最多包含 100 个已加入待分析区的条目。导出前可在条目详情核对证据。");
    }
    private async Task ImportAnalysis()
    {
        var picker=new FileOpenPicker();picker.FileTypeFilter.Add(".json");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(this));var file=await picker.PickSingleFileAsync();if(file is null)return;
        if(new FileInfo(file.Path).Length>2_000_000)throw new InvalidDataException("结果文件超出 2 MB 上限。");
        _store.ApplyAnalysis(Contract.Decode<AnalysisResult>(await File.ReadAllTextAsync(file.Path)));Tell("分析结果已保存","只更新建议，系统启动配置未修改。");RenderPage();
    }
    private async Task ExportReport()
    {
        var snapshot=_snapshot??throw new InvalidOperationException("尚无扫描结果。");var file=await PickSave("TunHotspot-startup-"+DateTime.Now.ToString("yyyyMMdd-HHmm"),".html");if(file is null)return;
        static string E(string? text)=>HtmlEncoder.Default.Encode(text??"");
        var html=new StringBuilder("<!doctype html><html lang='zh-CN'><meta charset='utf-8'><title>启动与后台扫描报告</title><style>body{font:15px system-ui;margin:32px;color:#172535}table{border-collapse:collapse;width:100%}td,th{border:1px solid #d7dee5;padding:9px;text-align:left;overflow-wrap:anywhere}tr:nth-child(even){background:#f3f6f9}pre{white-space:pre-wrap}</style><h1>启动与后台只读扫描报告</h1>");
        html.Append($"<p>{E(snapshot.Finished.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))} · {E(snapshot.Privilege)} · {snapshot.Entries.Count} 个登记入口。登记不等于开机常驻。</p><h2>采集覆盖</h2>");
        foreach(var c in snapshot.Coverage)html.Append($"<p><b>{E(c.Display)}</b><br>{E(c.Note)}<br>{E(string.Join("；",c.Issues))}</p>");
        html.Append("<table><tr><th>类别</th><th>条目</th><th>状态</th><th>建议</th><th>理由</th><th>来源 / 命令</th></tr>");
        foreach(var e in snapshot.Entries)html.Append($"<tr><td>{E(e.Category)}</td><td>{E(e.Name)}<br>{E(e.App)}</td><td>{E(e.StateText)}<br>{E(e.Health)}</td><td>{E(e.EffectiveAdvice)}</td><td>{E(e.Analysis?.Reason??e.Reason)}</td><td>{E(e.Source)}<br>{E(e.Command)}</td></tr>");
        html.Append("</table></html>");await File.WriteAllTextAsync(file,html.ToString());Tell("报告已导出","报告包含本机路径和命令参数，请在分享前核对私人信息。");
    }
    private async Task ConnectCodex()
    {
        string? codex=null;var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenAI","Codex","bin");
        if(Directory.Exists(root))codex=Directory.EnumerateFiles(root,"codex.exe",SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if(codex is null){Tell("未检测到本机 Codex","扫描不依赖 Codex。可使用导出分析包、导入分析结果，或安装 Codex 后连接。",InfoBarSeverity.Warning);return;}
        if(!File.Exists(AdminClient.EnginePath))throw new FileNotFoundException("分析桥接引擎缺失。");
        var start=new ProcessStartInfo(codex){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"mcp","add","TunHotspotStartup","--",AdminClient.EnginePath,"--mcp"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("Codex 配置工具未启动。");var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
        if(process.ExitCode!=0)throw new IOException(await error);
        Tell("本地分析桥接已注册","按 Codex 的连接提示刷新 MCP 或开启后续对话，再请求分析待分析区。没有读取聊天数据库或登录凭据。");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint hwnd);
}
