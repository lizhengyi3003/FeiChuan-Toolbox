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
}
