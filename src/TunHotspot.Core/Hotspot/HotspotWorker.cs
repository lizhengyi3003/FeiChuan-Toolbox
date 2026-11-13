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
        var log = new DiagnosticLog(DataPaths.UserRoot);
        var controller = new HotspotController(log);
        while(await Console.In.ReadLineAsync() is string command)
        {
            HotspotReply reply;
            try
            {
                switch(command){case "read":break;case "start":await controller.Start();break;case "stop":await controller.Stop();break;case "keepalive":await controller.KeepAlive();break;default:throw new InvalidDataException("未知热点请求。");}
                reply=new(true,controller.Read());
            }
            catch(Exception ex){log.Record("热点工作进程："+command,ex,command=="read");reply=new(false,Error:DiagnosticLog.Clean(ex.Message));}
            await Console.Out.WriteLineAsync(Contract.Encode(reply));await Console.Out.FlushAsync();
        }
        return 0;
    }
}

internal sealed class HotspotController(DiagnosticLog log)
{
    private readonly HotspotLossTracker _loss = new();
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
        if(tun is null)
        {
            _manager=null;_adapter=null;
            return Observe(new(false,"Unavailable",0,"Kumiryo TUN 已断开；不会自动切换到其他共享网络。"));
        }
        try{return Observe(Snapshot(Manager(tun)));}
        catch{_manager=null;_adapter=null;throw;}
    }

    private static HotspotSnapshot Snapshot(NetworkOperatorTetheringManager manager) =>
        new(true, manager.TetheringOperationalState.ToString(), manager.ClientCount,
            IdleTimeoutEnabled: NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled());

    private HotspotSnapshot Observe(HotspotSnapshot current, bool duringStart = false)
    {
        var lastOn = _loss.Observe(current);
        if (lastOn is not null || (duringStart && current.State is "Off" or "Unavailable"))
        {
            var incident = log.Event("热点", "警告", "检测到热点掉线", Contract.Encode(new
            {
                duringStart, current.State, current.TunConnected, current.Clients, current.IdleTimeoutEnabled,
                LastOnClients = lastOn?.Clients, LastOnIdleTimeoutEnabled = lastOn?.IdleTimeoutEnabled
            }));
            _ = HotspotWindowsEvents.RecordNearLoss(log, incident);
        }
        return current;
    }

    internal async Task KeepAlive()
    {
        if (!NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled()) return;
        await NetworkOperatorTetheringManager.DisableNoConnectionsTimeoutAsync();
        if (NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled())
            throw new InvalidOperationException("Windows 仍报告热点空闲超时已开启。");
    }

    public async Task Start()
    {
        var tun = Tun() ?? throw new InvalidOperationException("请先在 Kumiryo 中开启 TUN。");
        // A TUN profile can be recreated while retaining the same adapter ID.
        _manager = null;
        var manager = Manager(tun);
        var initial = Observe(Snapshot(manager));
        if (initial.IsOn) return;
        if (initial.IsTransitioning) throw new InvalidOperationException("热点仍在切换，请稍后重新核对状态。");
        await KeepAlive();
        var result = await manager.StartTetheringAsync();
        log.Event("热点", result.Status == TetheringOperationStatus.Success ? "信息" : "警告",
            "Windows 热点开启结果", result.Status.ToString());
        Ensure(result);
        await KeepAlive();
        int stable=0;
        for(int i=0;i<8;i++)
        {
            await Task.Delay(1000);
            if (Tun()?.Id != tun.Id)
            {
                Observe(new(false, "Unavailable", 0), duringStart: true);
                throw new InvalidOperationException("开启期间 Kumiryo TUN 已断开或切换；热点未确认稳定。");
            }
            var state = Observe(Snapshot(manager), duringStart: true);
            if (state.IsOn) { if (++stable >= 5) return; }
            else
            {
                stable = 0;
                if (state.State == "Off") throw new InvalidOperationException("Windows 在开启后关闭了热点。请查看热点日志。");
            }
        }
        throw new TimeoutException("热点尚未稳定，暂不标记开启成功。请重新读取状态。");
    }
    public async Task Stop()
    {
        var tun=Tun();var manager=_manager??(tun is not null?Manager(tun):null);
        if(manager is null)throw new InvalidOperationException("TUN 已断开，无法确认原共享会话。请使用 Windows 移动热点设置关闭。");
        Ensure(await manager.StopTetheringAsync());
        _loss.StopCompleted();
    }
    private static void Ensure(NetworkOperatorTetheringOperationResult result){if(result.Status!=TetheringOperationStatus.Success)throw new InvalidOperationException($"Windows 热点操作失败：{result.Status}。{result.AdditionalErrorMessage}");}
}

public sealed class HotspotWorkerClient(string enginePath,string logRoot):IDisposable,IAsyncDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifecycleGate = new();
    private Task? _shutdown;
    private Process? _worker;
    private DateTimeOffset _nextStart;
    public async Task<HotspotSnapshot> Send(string command)
    {
        if(command is not ("read" or "start" or "stop" or "keepalive"))throw new InvalidDataException("未知热点请求。");
        CancellationToken stop;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            stop = _stop.Token;
        }
        await _gate.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            stop.ThrowIfCancellationRequested();
            if(_worker is null||_worker.HasExited)
            {
                if(DateTimeOffset.UtcNow<_nextStart)throw new IOException("热点工作进程正在冷却，请稍后重试读取。");
                _worker?.Dispose();_nextStart=DateTimeOffset.UtcNow.AddSeconds(10);
                var start=new ProcessStartInfo(enginePath){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
                using var parent = Process.GetCurrentProcess();
                start.ArgumentList.Add("--hotspot-worker");start.ArgumentList.Add(Environment.ProcessId.ToString());start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString());
                _worker=Process.Start(start)??throw new IOException("热点工作进程未启动。");
                _worker.ErrorDataReceived+=(_,e)=>{if(e.Data is not null)new DiagnosticLog(logRoot).Event("热点","警告","热点工作进程诊断",e.Data);};_worker.BeginErrorReadLine();
                new DiagnosticLog(logRoot).Event("热点","信息","热点隔离进程已启动",$"PID={_worker.Id}；只读状态恢复不会开启热点。");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(command=="read"?12:50));
            await _worker.StandardInput.WriteLineAsync(command.AsMemory(), timeout.Token).ConfigureAwait(false);
            await _worker.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            string? line=await _worker.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if(line is null)throw new IOException("热点工作进程意外退出。工具箱仍在运行；可稍后重新读取热点状态。");
            if(line.Length>65536)throw new InvalidDataException("热点响应过大。");
            var reply=Contract.Decode<HotspotReply>(line);
            if(!reply.Success)throw new InvalidOperationException(reply.Error??"热点操作失败。");
            return reply.Snapshot??throw new InvalidDataException("热点响应缺少状态。");
        }
        catch (Exception ex) when (stop.IsCancellationRequested && ex is IOException or OperationCanceledException)
        {
            throw new OperationCanceledException(stop);
        }
        catch(Exception ex)when(ex is IOException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            int? code=_worker is {HasExited:true}?_worker.ExitCode:null;
            new DiagnosticLog(logRoot).Record("热点隔离进程中断",new IOException($"请求 {command} 未完成；退出码 {code?.ToString()??"未知"}。不会自动重复启停操作。",ex),command=="read");
            StopWorker();throw;
        }
        finally{_gate.Release();}
    }
    private void StopWorker()
    {
        using var worker = _worker;
        _worker = null;
        if (worker is null) return;
        try
        {
            if (!worker.HasExited) worker.Kill();
            if (!worker.WaitForExit(2000))
                new DiagnosticLog(logRoot).Event("热点", "警告", "热点工作进程退出超时");
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex) { new DiagnosticLog(logRoot).Record("退出热点工作进程", ex); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleGate) return new(_shutdown ??= Task.Run(Shutdown));
    }

    private async Task Shutdown()
    {
        _stop.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopWorker(); }
        finally { _gate.Release(); _stop.Dispose(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
