using System.Diagnostics;
using System.Net.NetworkInformation;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace TunHotspot.Core;

public sealed record HotspotSnapshot(bool TunConnected,string State,uint Clients,string? Error=null,bool? IdleTimeoutEnabled=null)
{
    public bool IsOn=>State=="On";
    public bool IsTransitioning=>State=="InTransition";
}
public sealed record HotspotReply(bool Success,HotspotSnapshot? Snapshot=null,string? Error=null);

// A driver/COM fail-fast stays in this owned worker, outside the WinUI process.
public static class HotspotWorker
{
    public static async Task<int> Run(int parentPid,long parentStarted)
    {
        using var parent=Process.GetProcessById(parentPid);
        if(parent.StartTime.ToUniversalTime().Ticks!=parentStarted||parent.SessionId!=Process.GetCurrentProcess().SessionId||ProcessSecurity.UserSid(parentPid)!=Contract.Sid)throw new UnauthorizedAccessException("热点工作进程父进程核验失败。");
        _=Task.Run(async()=>{await parent.WaitForExitAsync();Environment.Exit(0);});
        var controller=new HotspotController();
        while(await Console.In.ReadLineAsync() is string command)
        {
            HotspotReply reply;
            try
            {
                switch(command){case "read":break;case "start":await controller.Start();break;case "stop":await controller.Stop();break;case "keepalive":await HotspotController.KeepAlive();break;default:throw new InvalidDataException("未知热点请求。");}
                reply=new(true,controller.Read());
            }
            catch(Exception ex){new DiagnosticLog(DataPaths.UserRoot).Record("热点工作进程："+command,ex,command=="read");reply=new(false,Error:DiagnosticLog.Clean(ex.Message));}
            await Console.Out.WriteLineAsync(Contract.Encode(reply));await Console.Out.FlushAsync();
        }
        return 0;
    }
}

internal sealed class HotspotController
{
    private NetworkOperatorTetheringManager? _manager;
    private string? _adapter;
    private static NetworkInterface? Tun()=>NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x=>x.Name.Equals("Kumiryo",StringComparison.OrdinalIgnoreCase)&&x.OperationalStatus==OperationalStatus.Up);
    private NetworkOperatorTetheringManager Manager(NetworkInterface tun)
    {
        if(_manager is not null&&_adapter==tun.Id)return _manager;
        if(!Guid.TryParse(tun.Id,out var id))throw new InvalidOperationException("无法识别 TUN 网卡。");
        var profiles=NetworkInformation.GetConnectionProfiles().Where(x=>x.NetworkAdapter?.NetworkAdapterId==id).ToArray();
        var profile=profiles.FirstOrDefault(x=>x.GetNetworkConnectivityLevel()==NetworkConnectivityLevel.InternetAccess)??profiles.FirstOrDefault()??throw new InvalidOperationException("TUN 网络尚未准备好，请稍后重试。");
        _manager=NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);_adapter=tun.Id;return _manager;
    }
    public HotspotSnapshot Read()
    {
        var tun=Tun();
        if(tun is null){_manager=null;_adapter=null;return new(false,"Unavailable",0,"Kumiryo TUN 已断开；不会自动切换到其他共享网络。");}
        try{var manager=Manager(tun);return new(true,manager.TetheringOperationalState.ToString(),manager.ClientCount,IdleTimeoutEnabled:NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled());}
        catch{_manager=null;_adapter=null;throw;}
    }
    internal static async Task KeepAlive(){if(NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled())await NetworkOperatorTetheringManager.DisableNoConnectionsTimeoutAsync();}
    public async Task Start()
    {
        var tun=Tun()??throw new InvalidOperationException("请先在 Kumiryo 中开启 TUN。");var manager=Manager(tun);
        if(manager.TetheringOperationalState==TetheringOperationalState.On)return;
        if(manager.TetheringOperationalState==TetheringOperationalState.InTransition)throw new InvalidOperationException("热点仍在切换，请稍后重新核对状态。");
        await KeepAlive();Ensure(await manager.StartTetheringAsync());await KeepAlive();int stable=0;
        for(int i=0;i<8;i++)
        {
            await Task.Delay(1000);
            if(Tun() is null)throw new InvalidOperationException("开启期间 Kumiryo TUN 已断开；热点未确认稳定。");
            var state=manager.TetheringOperationalState;
            if(state==TetheringOperationalState.On){if(++stable>=5)return;}
            else{stable=0;if(state==TetheringOperationalState.Off)throw new InvalidOperationException("Windows 在开启后关闭了热点。请查看热点日志；没有自动改动网卡、路由或共享服务。");}
        }
        throw new TimeoutException("热点尚未稳定，暂不标记开启成功。请重新读取状态。");
    }
    public async Task Stop()
    {
        var tun=Tun();var manager=_manager??(tun is not null?Manager(tun):null);
        if(manager is null)throw new InvalidOperationException("TUN 已断开，无法确认原共享会话。请使用 Windows 移动热点设置关闭。");
        Ensure(await manager.StopTetheringAsync());
    }
    private static void Ensure(NetworkOperatorTetheringOperationResult result){if(result.Status!=TetheringOperationStatus.Success)throw new InvalidOperationException($"Windows 热点操作失败：{result.Status}。{result.AdditionalErrorMessage}");}
}

public sealed class HotspotWorkerClient(string enginePath,string logRoot):IDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private Process? _worker;
    private bool _disposed;
    private DateTimeOffset _nextStart;
    public async Task<HotspotSnapshot> Send(string command)
    {
        if(command is not ("read" or "start" or "stop" or "keepalive"))throw new InvalidDataException("未知热点请求。");
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            if(_worker is null||_worker.HasExited)
            {
                if(DateTimeOffset.UtcNow<_nextStart)throw new IOException("热点工作进程正在冷却，请稍后重试读取。");
                _worker?.Dispose();_nextStart=DateTimeOffset.UtcNow.AddSeconds(10);
                var start=new ProcessStartInfo(enginePath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                start.ArgumentList.Add("--hotspot-worker");start.ArgumentList.Add(Environment.ProcessId.ToString());start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());
                _worker=Process.Start(start)??throw new IOException("热点工作进程未启动。");
                _worker.ErrorDataReceived+=(_,e)=>{if(e.Data is not null)new DiagnosticLog(logRoot).Event("热点","警告","热点工作进程诊断",e.Data);};_worker.BeginErrorReadLine();
                new DiagnosticLog(logRoot).Event("热点","信息","热点隔离进程已启动",$"PID={_worker.Id}；只读状态恢复不会开启热点。");
            }
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(command=="read"?12:50));
            await _worker.StandardInput.WriteLineAsync(command);await _worker.StandardInput.FlushAsync();
            string? line=await _worker.StandardOutput.ReadLineAsync(timeout.Token);
            if(line is null)throw new IOException("热点工作进程意外退出。工具箱仍在运行；可稍后重新读取热点状态。");
            if(line.Length>65536)throw new InvalidDataException("热点响应过大。");
            var reply=Contract.Decode<HotspotReply>(line);
            if(!reply.Success)throw new InvalidOperationException(reply.Error??"热点操作失败。");
            return reply.Snapshot??throw new InvalidDataException("热点响应缺少状态。");
        }
        catch(Exception ex)when(ex is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            int? code=_worker is {HasExited:true}?_worker.ExitCode:null;
            new DiagnosticLog(logRoot).Record("热点隔离进程中断",new IOException($"请求 {command} 未完成；退出码 {code?.ToString()??"未知"}。不会自动重复启停操作。",ex),command=="read");
            StopWorker();throw;
        }
        finally{_gate.Release();}
    }
    private void StopWorker(){if(_worker is not null){try{if(!_worker.HasExited)_worker.Kill();}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){} _worker.Dispose();_worker=null;}}
    public void Dispose(){_disposed=true;StopWorker();}
}
