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
    private readonly bool _qa;
    private readonly UISettings _uiSettings = new();
    private readonly AccessibilitySettings _accessibility = new();
    private TrayIcon? _tray;
    private ScanSnapshot? _snapshot;
    private UserSettings _settings;
    private CancellationTokenSource? _adminStop;
    private bool _adminBusy, _polling, _exiting, _serviceConnected, _serviceScanRequested;
    private bool _initialized;
    private string _page = "overview", _appFilter = "", _materialState = "";
    private string? _machineSnapshotId;
    private DateTimeOffset _lastAnalysis;
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
        InitializeComponent(); _qa=qa;_settings = _store.Settings; _snapshot = _store.Latest;
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
            _tray = new(handle, () => _settings.Paused);
            _tray.Command += async command =>
            {
                if(command=="open")Show();else if(command=="alerts"){Show();Navigate("alerts");}
                else if(command=="scan")await GlobalScan();else if(command=="pause")await Pause();else ExitToolbox();
            };
            _tray.SessionEnding += () => _exiting = true;
            _tray.FontsChanged += () => _=LoadFonts();
            _tray.Resumed += () => { _monitor.Reconcile("登录界面或电源恢复");_=Poll(); };
        }
        catch(Exception ex){if(!qa)Tell("托盘不可用",ex.Message,InfoBarSeverity.Warning);}
        try { _uiSettings.AdvancedEffectsEnabledChanged += (_,_)=>DispatcherQueue.TryEnqueue(ApplyAppearance); } catch(Exception ex) { AppLog.Write("optional-theme-event",new{ex.Message}); }
        try { _accessibility.HighContrastChanged += (_,_)=>DispatcherQueue.TryEnqueue(ApplyAppearance); } catch(Exception ex) { AppLog.Write("optional-accessibility-event",new{ex.Message}); }
        ApplyAppearance(); _initialized = true; Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First();
        _poll.Tick += async (_,_)=>await Poll(); if(!qa){_poll.Start();_monitor.Start();_=Poll();}
        _=LoadFonts();
        if(quiet)DispatcherQueue.TryEnqueue(()=>AppWindow.Hide());
        AppLog.Write("toolbox-opened",new {networkChanged=false,quiet});
    }
    internal void Show(){AppWindow.Show();Activate();}
    internal void ReportError(string message)=>Tell("操作未完成",message,InfoBarSeverity.Error);
    internal async Task CaptureForQa(string page)
    {
        try
        {
            if (!new[] { "overview", "entries", "apps", "runtime", "pending", "alerts", "changes", "hotspot", "settings" }.Contains(page)) throw new InvalidDataException("未知验证页面。");
            await Task.Delay(1200); Navigate(page); await Task.Delay(700);
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
        _poll.Stop();_adminStop?.Cancel();_tray?.Dispose();NotificationBridge.Close();_monitor.Dispose();
        AppLog.Write("toolbox-closed",new {networkChanged=false,machineMonitorMayContinue=_serviceConnected});
    }
    private void ExitToolbox(){_exiting=true;Close();}
    private void Tell(string title,string body,InfoBarSeverity severity=InfoBarSeverity.Informational)
    {Message.Title=title;Message.Message=body;Message.Severity=severity;Message.IsOpen=true;}
    private void Navigation_SelectionChanged(NavigationView sender,NavigationViewSelectionChangedEventArgs args)
    {if(_initialized && args.SelectedItem is NavigationViewItem item){_page=item.Tag?.ToString()??"overview";RenderPage();}}
    private void Navigate(string page)
    {
        var item=Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(x=>x.Tag?.ToString()==page);
        if(item is not null)Navigation.SelectedItem=item;else {_page=page;RenderPage();}
    }
    private void RenderPage()
    {
        if(!_initialized)return;
        PageHost.Content=null;
        PageHost.Content=_page switch {"entries"=>Entries(),"apps"=>Apps(),"runtime"=>Runtime(),"pending"=>Pending(),"alerts"=>Alerts(),"changes"=>Changes(),"hotspot"=>new ScrollViewer{Content=_hotspot},"settings"=>SettingsPage(),_=>Overview()};
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
        body.Children.Add(Text("采集覆盖",20,true));
        foreach(var c in snapshot.Coverage)
        {
            var card=Column(Text(c.Display,14,true));
            if(c.Note.Length>0||c.Issues.Count>0)card.Children.Add(new Expander{Header=c.Issues.Count>0?$"查看 {c.Issues.Count} 条覆盖说明":"查看覆盖说明",HorizontalAlignment=HorizontalAlignment.Stretch,Content=Text(c.Note+"\n"+string.Join("\n",c.Issues),12)});
            body.Children.Add(Card(card));
        }
        body.Children.Add(Text(snapshot.BootObservation,12));return Scroll(body);
    }
    private UIElement Entries(bool pendingOnly=false)
    {
        var grid=new Grid{RowSpacing=10};grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=new GridLength(pendingOnly?150:210)});
        var search=new TextBox{PlaceholderText="搜索名称、所属应用、命令或来源",MinWidth=240};
        var category=new ComboBox{MinWidth=150,ItemsSource=new[]{"全部类别"}.Concat((_snapshot?.Entries??[]).Select(x=>x.Category).Distinct().Order()).ToList(),SelectedIndex=0};
        var advice=new ComboBox{MinWidth=130,ItemsSource=new[]{"全部建议","待分析","建议保留","按需保留","可考虑取消","需修复"},SelectedIndex=0};
        var list=new ListView{ItemTemplate=Rows,SelectionMode=ListViewSelectionMode.Single};
        var detail=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,PlaceholderText="选中条目查看证据。登录入口、服务、任务及进程分别核对。"};ScrollViewer.SetVerticalScrollBarVisibility(detail,ScrollBarVisibility.Auto);
        var note=new TextBox{PlaceholderText="记录个人用途，例如用于远控、代理、打印或开发",MaxLength=2000,MinWidth=280};
        var saveNote=Button("保存用途",()=>{if(list.SelectedItem is StartupEntry e){_store.SaveNote(e.Id,note.Text);e.UserNote=note.Text;e.Analysis=null;detail.Text=EntryDetail(e);Tell("用途已保存并加入待分析","备注参与分析指纹；旧建议不会覆盖新用途下的判断。");}});
        var bottom=new Grid{RowSpacing=8};bottom.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});bottom.RowDefinitions.Add(new(){Height=GridLength.Auto});bottom.Children.Add(detail);var noteRow=Row(note,saveNote);Grid.SetRow(noteRow,1);bottom.Children.Add(noteRow);
        var count=Text("",12);var controls=Row(search,category,advice);
        var header=Column(Text(pendingOnly?"Codex 待分析条目":"全部启动入口",24,true),controls,Row(
            Button("监视当前状态",()=>{if(list.SelectedItem is StartupEntry e)_=Watch(e);}),
            Button(pendingOnly?"移出待分析":"加入待分析",()=>{if(list.SelectedItem is StartupEntry e){if(pendingOnly)_store.RemovePending(e.Id);else _store.Enqueue(e.Id);Filter();Tell("待分析区已更新","分析只产生建议，不修改系统配置。");}}),
            Button("清除应用筛选",()=>{_appFilter="";Filter();})),count);
        grid.Children.Add(header);Grid.SetRow(list,1);grid.Children.Add(list);Grid.SetRow(bottom,2);grid.Children.Add(bottom);
        void Filter()
        {
            var selected=_store.Pending.ToHashSet();IEnumerable<StartupEntry> entries=_snapshot?.Entries??[];
            if(pendingOnly)entries=entries.Where(x=>selected.Contains(x.Id));if(_appFilter.Length>0)entries=entries.Where(x=>x.App==_appFilter);
            if(category.SelectedIndex>0)entries=entries.Where(x=>x.Category==category.SelectedItem?.ToString());
            if(advice.SelectedIndex>0)entries=entries.Where(x=>x.EffectiveAdvice==advice.SelectedItem?.ToString());
            string q=search.Text.Trim();if(q.Length>0)entries=entries.Where(x=>$"{x.Name} {x.App} {x.Command} {x.Source} {x.Publisher}".Contains(q,StringComparison.CurrentCultureIgnoreCase));
            var rows=entries.OrderBy(x=>x.Category).ThenBy(x=>x.Name).ToList();list.ItemsSource=rows;count.Text=$"显示 {rows.Count:N0} 项{(_appFilter.Length>0?" · "+_appFilter:"")}；建议需要结合功能依赖。";
        }
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is StartupEntry e){detail.Text=EntryDetail(e);note.Text=e.UserNote;}};search.TextChanged+=(_,_)=>Filter();category.SelectionChanged+=(_,_)=>Filter();advice.SelectionChanged+=(_,_)=>Filter();Filter();return grid;
    }
    private string EntryDetail(StartupEntry e)
    {
        var result=_store.Decisions.FirstOrDefault(x=>x.EntryId==e.Id&&x.Fingerprint==e.Fingerprint);
        var text=new StringBuilder().AppendLine(e.Display).AppendLine(e.Detail).AppendLine(e.Reason).AppendLine($"观察：{e.ObservationStatus} · {e.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n来源：{e.Source}\n账户：{e.OwnerSid} · 视图：{e.View}\n命令：{e.Command}\n解析文件：{e.Executable}\n签名：{e.Signature}\n提供程序：{e.Provider}\n条目 ID：{e.Id}");
        if(result is not null)text.AppendLine($"\nCodex 建议：{result.Advice}（{result.Confidence}）\n{result.Reason}\n功能影响：{result.Impact}");
        if(e.UserNote.Length>0)text.AppendLine("\n个人用途（用户备注）："+e.UserNote);
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
    private sealed record AppRow(string App,int Count,int Running,int Unknown)
    {public string Display=>$"{App} · {Count} 个入口";public string Detail=>$"运行关联 {Running} · 待分析 {Unknown}；点击查看服务、任务和登录入口";}
    private UIElement Apps()
    {
        var list=new ListView{ItemTemplate=Rows,ItemsSource=_snapshot?.Entries.GroupBy(x=>x.App).Select(x=>new AppRow(x.Key,x.Count(),x.Count(y=>y.ProcessId>0),x.Count(y=>y.EffectiveAdvice=="待分析"))).OrderByDescending(x=>x.Count).ToList()};
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is AppRow row){_appFilter=row.App;Navigate("entries");}};
        return ListPage("按应用查看",Text("同一应用的不同启动方式合并展示；登记数量不等于常驻进程数量。"),list);
    }
    private UIElement Runtime()
    {
        var choice=new ComboBox{ItemsSource=new[]{"持续观察事件","当前进程快照"},SelectedIndex=0};var list=new ListView{ItemTemplate=Rows};var detail=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=160};
        void Refresh(){list.ItemsSource=choice.SelectedIndex==0?(object)_store.Events():_snapshot?.Processes??[];}
        choice.SelectionChanged+=(_,_)=>Refresh();list.SelectionChanged+=(_,_)=>detail.Text=list.SelectedItem switch{ObservedEvent e=>$"{e.Display}\n{e.Source}\n{e.Association}\n{e.Path}\n{e.Detail}",ProcessRecord p=>$"{p.Display}\n{p.Detail}\n{p.Path}\n{p.Command}",_=>""};Refresh();
        return ListPage("运行与拉起记录",Column(choice,Text("普通权限仅提供可用范围。完整持续进程事件需要管理员/机器组件；父 PID 不等于确定的自动启动原因。",12)),list,detail);
    }
    private UIElement Pending()
    {
        var panel=new Grid{RowSpacing=12};panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});
        var tools=Column(Text("将证据交给 Codex",22,true),Text($"待分析 {_store.Pending.Count} 项 · 单批最多 100 项 · 默认脱敏令牌等参数。"),Row(
            AsyncButton("导出分析包",ExportBatch),AsyncButton("导入分析结果",ImportAnalysis),AsyncButton("连接本机 Codex",ConnectCodex)),
            Text($"已返回建议 {_store.Decisions.Count} 项，可在全部入口中查看对应建议及证据。\n连接后在 Codex 中说：读取 TunHotspot 待分析区，分析必要性并将建议返回软件。没有安装 Codex 时仍可使用本地功能。",12));
        panel.Children.Add(tools);var entries=Entries(true);Grid.SetRow((FrameworkElement)entries,1);panel.Children.Add(entries);return panel;
    }
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
                Text("Windows 11 x64 · 自包含发布 · AI 分析可选；服务/驱动/任务的实际修改功能不包含在此只读版本。",12))));
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
        if(_page is "overview" or "apps")RenderPage();
        AppLog.Write("snapshot-received",new{snapshot.Id,count=snapshot.Entries.Count,snapshot.Privilege,complete=snapshot.Complete});
    }
    private void AttachDecisions()
    {
        var decisions=_store.Decisions.ToDictionary(x=>x.EntryId);
        foreach(var e in _snapshot?.Entries??[])e.Analysis=decisions.TryGetValue(e.Id,out var decision)&&decision.Fingerprint==e.Fingerprint?decision:null;
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
    }
    private async Task Poll()
    {
        if(_polling||_exiting)return;_polling=true;
        try
        {
            try
            {
                var response=await AdminClient.Service(new(){Kind="status"});_serviceConnected=true;_monitor.Paused=true;
                if(response.Snapshot is not null&&response.Snapshot.Id!=_machineSnapshotId)
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
