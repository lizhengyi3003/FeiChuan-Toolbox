using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using TunHotspot.Core;

namespace TunHotspot.Engine;

internal sealed class MonitorService : ServiceBase
{
    private CancellationTokenSource? _stop;
    private LiveMonitor? _monitor;
    public MonitorService(){ServiceName=DataPaths.ServiceName;CanStop=true;CanHandlePowerEvent=true;CanHandleSessionChangeEvent=true;AutoLog=true;}
    protected override void OnStart(string[] args)
    {
        var policy=ComponentInstaller.Policy;if(policy.Protocol!=Contract.Version)throw new InvalidDataException("监测策略版本不兼容。");
        _stop=new(); var store=new ToolboxStore(DataPaths.MachineRoot);_monitor=new(store,policy.OwnerSid){Paused=store.Settings.Paused};_monitor.Start();
        _=Task.Run(()=>Serve(store,policy.OwnerSid,_stop.Token));
    }
    private async Task Serve(ToolboxStore store,string owner,CancellationToken stop)
    {
        while(!stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe=null;
            try
            {
                var acl=new PipeSecurity();
                foreach(var sid in new[]{new SecurityIdentifier(owner),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
                    acl.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
                pipe=NamedPipeServerStreamAcl.Create(DataPaths.ServicePipe,PipeDirection.InOut,4,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,acl);
                await pipe.WaitForConnectionAsync(stop);
                var connected=Wire.ConnectedUser(pipe);if(connected!=owner)throw new UnauthorizedAccessException("连接不属于已授权监测用户。");
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop);timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var request=await Wire.Receive(pipe,timeout.Token);WireMessage response;
                switch(request.Kind)
                {
                    case "status":response=new(){Kind="status",Text="1.1.0",MonitorState=_monitor!.State,ScanCancelled=_monitor.LastScanCancelled,Progress=_monitor.Scanning?_monitor.CurrentProgress:null,Snapshot=store.Latest,Alerts=store.Alerts,Events=store.Events(500)};break;
                    case "scan": _=Task.Run(()=>_monitor!.ScanNow(stop),stop);response=new(){Kind="complete",Text="已提交机器全局扫描"};break;
                    case "cancelScan":_monitor!.CancelScan();response=new(){Kind="complete",Text="已请求取消本软件当前采集"};break;
                    case "watch":
                        var rule=request.Rule??throw new InvalidDataException("关注规则为空。");
                        var entry=store.Latest?.Entries.FirstOrDefault(x=>x.Id==rule.EntryId)??throw new InvalidDataException("机器快照无此条目，请先扫描。");
                        if(rule.Expected!=entry.ControlValue)throw new InvalidDataException("关注状态已变化，请刷新后重试。");
                        store.SaveWatch(ToolboxStore.Watch(entry));response=new(){Kind="complete",Text="已保存机器关注状态"};break;
                    case "unwatch":store.RemoveWatch(request.Text);response=new(){Kind="complete",Text="已取消关注"};break;
                    case "pause":_monitor!.Paused=request.Text=="true";var settings=store.Settings;settings.Paused=_monitor.Paused;store.Settings=settings;response=new(){Kind="complete",Text=_monitor.Paused?"机器监测已暂停":"机器监测已恢复"};break;
                    default:throw new InvalidDataException("未授权的机器组件操作。");
                }
                await Wire.Send(pipe,response,timeout.Token);
            }
            catch(OperationCanceledException){ }
            catch(Exception ex){try{if(pipe?.IsConnected==true)await Wire.Send(pipe,new(){Kind="error",Text=ex.Message},stop);}catch{} }
            finally{pipe?.Dispose();}
        }
    }
    protected override void OnStop(){_stop?.Cancel();_monitor?.Dispose();}
    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus){_monitor?.Reconcile("电源状态："+powerStatus);return true;}
    protected override void OnSessionChange(SessionChangeDescription change){_monitor?.Reconcile($"会话 {change.SessionId}：{change.Reason}");}
}
