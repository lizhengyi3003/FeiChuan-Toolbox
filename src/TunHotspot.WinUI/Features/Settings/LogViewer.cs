using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TunHotspot.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace TunHotspot;

internal sealed class LogViewer:UserControl
{
    private readonly DiagnosticLog _log;
    private readonly Window _owner;
    private readonly ComboBox _category=new(){Header="类别",MinWidth=150};
    private readonly ComboBox _level=new(){Header="严重等级",MinWidth=130};
    private readonly ComboBox _time=new(){Header="时间范围",MinWidth=130,ItemsSource=new[]{"全部时间","最近 1 小时","最近 24 小时","最近 7 天"},SelectedIndex=0};
    private readonly TextBox _search=new(){Header="搜索",PlaceholderText="操作、错误码、编号或详情",MinWidth=230};
    private readonly ListView _list=new(){Height=220,DisplayMemberPath="Display"};
    private readonly TextBox _details=new(){IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=160};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private List<DiagnosticRecord> _visible=[];
    private IReadOnlyList<DiagnosticRecord> _loaded=[];
    private IReadOnlyList<DiagnosticRecord> _machine=[];
    internal LogViewer(DiagnosticLog log,Window owner)
    {
        _log=log;_owner=owner;
        _category.ItemsSource=new[]{"全部类别"}.Concat(DiagnosticLog.Categories).ToArray();_category.SelectedIndex=0;
        _level.ItemsSource=new[]{"全部等级"}.Concat(DiagnosticLog.Severities).ToArray();_level.SelectedIndex=0;
        var filters=new FlowPanel();foreach(var item in new FrameworkElement[]{_category,_level,_time,_search})filters.Children.Add(item);
        var actions=new FlowPanel();var refresh=new Button{Content="刷新日志"};var copy=new Button{Content="复制所选详情"};var export=new Button{Content="导出当前筛选（脱敏）"};
        refresh.Click+=async(_,_)=>{Refresh(true);if(AppLog.DataRoot.Equals(DataPaths.UserRoot,StringComparison.OrdinalIgnoreCase))await LoadMachine();};copy.Click+=(_,_)=>{try{if(_list.SelectedItem is DiagnosticRecord){var data=new DataPackage();data.SetText(_details.Text);Clipboard.SetContent(data);}}catch(Exception ex){_status.Text="复制失败："+DiagnosticLog.Clean(ex.Message);}};
        export.Click+=async(_,_)=>{try{var picker=new FileSavePicker{SuggestedFileName="飞船日志-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")};picker.FileTypeChoices.Add("JSON 日志",new[]{".json"});WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(_owner));var file=await picker.PickSaveFileAsync();if(file is not null){await Windows.Storage.FileIO.WriteTextAsync(file,DiagnosticLog.Clean(Contract.Encode(_visible)));_status.Text=$"已导出 {_visible.Count} 条日志。";}}catch(Exception ex){var record=_log.Record("导出日志",ex);_status.Text=$"导出失败：{record.Id}；{record.Guidance}";}};
        foreach(var button in new[]{refresh,copy,export})actions.Children.Add(button);
        var body=new StackPanel{Spacing=12};foreach(var item in new UIElement[]{filters,actions,_status,_list,_details})body.Children.Add(item);Content=body;
        _list.SelectionChanged+=(_,_)=>_details.Text=_list.SelectedItem is DiagnosticRecord row?$"{row.Display}\n版本 {row.Version} · PID {row.ProcessId}\n{row.Code}\n{row.Guidance}\n{row.Details}\n日志：{row.LogPath}\n{row.StorageWarning}":"";
        foreach(var box in new[]{_category,_level,_time})box.SelectionChanged+=(_,_)=>Refresh();_search.TextChanged+=(_,_)=>Refresh();
        AutomationProperties.SetAutomationId(_category,"Logs.Category");AutomationProperties.SetAutomationId(_level,"Logs.Level");AutomationProperties.SetAutomationId(_list,"Logs.List");AutomationProperties.SetAutomationId(_search,"Logs.Search");AutomationProperties.SetAutomationId(_status,"Logs.Status");
        Refresh(true);
        if(AppLog.DataRoot.Equals(DataPaths.UserRoot,StringComparison.OrdinalIgnoreCase))_=LoadMachine();
    }
    private async Task LoadMachine()
    {
        try{var response=await AdminClient.Service(new(){Kind="diagnostics"});_machine=response.Diagnostics??[];Refresh(true);}
        catch(Exception ex)when(ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException){/* User logs remain available without a current machine component. */}
    }
    private void Refresh(bool read=false)
    {
        try
        {
            if(read)
            {
                _loaded=_log.Recent(limit:2500);
                _loaded=_loaded.Concat(_machine).OrderByDescending(x=>x.Time).Take(2500).ToList();
            }
            var oldest=_time.SelectedIndex switch{1=>DateTimeOffset.UtcNow.AddHours(-1),2=>DateTimeOffset.UtcNow.AddDays(-1),3=>DateTimeOffset.UtcNow.AddDays(-7),_=>DateTimeOffset.MinValue};
            _visible=_loaded.Where(x=>x.Time>=oldest&&(_category.SelectedIndex<=0||x.Category==_category.SelectedItem?.ToString())&&(_level.SelectedIndex<=0||x.Severity==_level.SelectedItem?.ToString())&&(string.IsNullOrWhiteSpace(_search.Text)||$"{x.Operation} {x.Id} {x.Code} {x.Details}".Contains(_search.Text,StringComparison.OrdinalIgnoreCase))).ToList();
            _list.ItemsSource=_visible;_status.Text=$"当前筛选 {_visible.Count} 条；最多载入最近 2500 条。刷新只读取日志，不重做启停或注入。";
        }
        catch(Exception ex){_status.Text="日志读取失败，可重新刷新："+DiagnosticLog.Clean(ex.Message);}
    }
}
