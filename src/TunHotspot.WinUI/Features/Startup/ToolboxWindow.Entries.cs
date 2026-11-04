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
}
