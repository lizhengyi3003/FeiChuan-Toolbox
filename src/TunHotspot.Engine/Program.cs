using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using TunHotspot.Core;

namespace TunHotspot.Engine;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--mcp") return await McpServer.Run();
            if(args.Length==1&&args[0]=="--audit-scan")return await ReadOnlyAudit.Run();
            if(args.Length==1&&args[0]=="--hotspot-diagnostics"){Console.WriteLine(Contract.Encode(HotspotDiagnostics.Read()));return 0;}
            if(args.Length==1&&args[0] is "--qa-control-snapshot" or "--qa-control-service")
            {
                if(!File.Exists(@"C:\TestScripts\run.ps1")||!Directory.Exists(@"C:\Users\WDAGUtilityAccount"))throw new InvalidOperationException("仅允许项目隔离沙盒测试。");
                if(args[0]=="--qa-control-service"){if(!WindowsIdentity.GetCurrent().IsSystem)throw new UnauthorizedAccessException();ServiceBase.Run(new ControlFixtureService());return 0;}
                var snapshot=new WindowsScanner(Contract.Sid).Scan();Console.WriteLine(Contract.Encode(new{DeviceId=Contract.DeviceId,Entries=snapshot.Entries.Where(x=>x.Name.StartsWith("TunHotspot.Qa.",StringComparison.Ordinal)).Select(x=>new{Entry=x,Fingerprint=x.Fingerprint}).ToList()}));return 0;
            }
            if (args.Length == 1 && args[0] == "--service")
            {
                if (!WindowsIdentity.GetCurrent().IsSystem) throw new UnauthorizedAccessException("机器采集服务需要由 SCM 以 LocalSystem 启动。");
                ServiceBase.Run(new MonitorService()); return 0;
            }
            if (args.Length == 1 && args[0] == "--remove-component")
            {if(!Contract.Elevated)throw new UnauthorizedAccessException("卸载机器采集组件需要 UAC。");await ComponentInstaller.Uninstall();return 0;}
            if (args.Length == 1 && args[0] is "--diagnostic-scan" or "--refresh-user-data")
            {
                var result = new WindowsScanner(Contract.Sid).Scan(new DirectProgress<ScanProgress>(p => Console.Error.WriteLine(p.Message)));
                if(args[0]=="--refresh-user-data")new ToolboxStore(DataPaths.UserRoot).SaveSnapshot(result);
                Console.WriteLine(Contract.Encode(result)); return 0;
            }
            if (args.Length == 5 && args[1] == "--pipe" && args[3] == "--owner" && args[0] is "--scan" or "--install" or "--uninstall" or "--control" or "--audit-worker")
            {
                if (!Contract.Elevated) throw new UnauthorizedAccessException("此操作需要 Windows UAC 授权。");
                if (!Regex.IsMatch(args[2], "^TunHotspot\\.Scan\\.[a-f0-9]{32}$")) throw new InvalidDataException("管道标识无效。");
                _ = new SecurityIdentifier(args[4]);
                using var pipe = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
                await pipe.ConnectAsync(15000);
                int server = checked((int)ProcessSecurity.PipeServer(pipe));
                if (ProcessSecurity.UserSid(server) != args[4]) throw new UnauthorizedAccessException("采集请求与实际发起账户不符。");
                if(args[0]=="--audit-worker")await ReadOnlyAudit.Worker(pipe,args[4]);
                else if(args[0]=="--control")
                {
                    var request=await Wire.Receive(pipe);if(request.Kind!="controlRequest"||request.Control is null)throw new InvalidDataException("缺少有类型的操作请求。");
                    var progress=new DirectProgress<ScanProgress>(p=>Wire.Send(pipe,new(){Kind="progress",Progress=p}).GetAwaiter().GetResult());
                    try{var records=await ControlExecutor.Run(request.Control,args[4],progress);await Wire.Send(pipe,new(){Kind="controlResult",Controls=records});}
                    catch(Exception ex){await Wire.Send(pipe,new(){Kind="error",Text=ex.Message});return 1;}
                }
                else if (args[0] == "--scan")
                {
                    using var stop = new CancellationTokenSource();
                    var listen = Task.Run(async () => { try { var command = await Wire.Receive(pipe, stop.Token); if (command.Kind == "cancel") stop.Cancel(); } catch { stop.Cancel(); } });
                    var progress = new DirectProgress<ScanProgress>(p => Wire.Send(pipe, new() { Kind = "progress", Progress = p }).GetAwaiter().GetResult());
                    var snapshot = await Task.Run(() => new WindowsScanner(args[4]).Scan(progress, stop.Token));
                    await Wire.Send(pipe, new() { Kind = "snapshot", Snapshot = snapshot }); stop.Cancel();
                }
                else
                {
                    await Wire.Send(pipe, new() { Kind = "progress", Progress = new("管理员组件", 0, 1, 0, args[0] == "--install" ? "正在安装受控机器采集组件…" : "正在卸载本软件采集组件…") });
                    try
                    {
                        if (args[0] == "--install") await ComponentInstaller.Install(args[4]); else await ComponentInstaller.Uninstall();
                        await Wire.Send(pipe, new() { Kind = "complete", Text = "操作完成" });
                    }
                    catch (Exception ex) { await Wire.Send(pipe, new() { Kind = "error", Text = ex.Message }); return 1; }
                }
                return 0;
            }
            Console.Error.WriteLine("TunHotspot 只读采集引擎：由工具箱启动，或使用 --mcp / --diagnostic-scan。"); return 2;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
