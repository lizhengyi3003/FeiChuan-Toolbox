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
}
