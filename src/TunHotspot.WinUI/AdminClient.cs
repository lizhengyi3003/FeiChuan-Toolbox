using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using TunHotspot.Core;

namespace TunHotspot;

internal static class AdminClient
{
    internal static string EnginePath => Path.Combine(AppContext.BaseDirectory, "Engine", "TunHotspot.Engine.exe");
    internal static async Task<WireMessage> Run(string operation, Action<ScanProgress> progress, CancellationToken cancellationToken)
    {
        if (operation is not ("--scan" or "--install" or "--uninstall")) throw new InvalidOperationException("未知采集操作。");
        if (!File.Exists(EnginePath)) throw new FileNotFoundException("采集引擎未发布。请使用完整预览目录。", EnginePath);
        string name = "TunHotspot.Scan." + Guid.NewGuid().ToString("N");
        var acl=new PipeSecurity();
        foreach(var sid in new[]{new SecurityIdentifier(Contract.Sid),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
            acl.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,acl);
        var start = new ProcessStartInfo(EnginePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(EnginePath)! };
        start.ArgumentList.Add(operation); start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(name); start.ArgumentList.Add("--owner"); start.ArgumentList.Add(Contract.Sid);
        using var process = await Task.Run(() => Process.Start(start)) ?? throw new InvalidOperationException("采集引擎未启动。");
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); connect.CancelAfter(TimeSpan.FromSeconds(45));
        await pipe.WaitForConnectionAsync(connect.Token);
        if (ProcessSecurity.PipeClient(pipe) != process.Id) throw new UnauthorizedAccessException("采集连接与已授权进程不符。");
        using var cancel = cancellationToken.Register(() => { try { Wire.Send(pipe, new() { Kind = "cancel" }).GetAwaiter().GetResult(); } catch { } });
        while (true)
        {
            var message = await Wire.Receive(pipe, cancellationToken);
            if (message.Kind == "progress" && message.Progress is not null) progress(message.Progress);
            else if (message.Kind == "error") throw new InvalidOperationException(message.Text);
            else if (message.Kind is "snapshot" or "complete") return message;
        }
    }
    internal static async Task<WireMessage> Service(WireMessage request, CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", DataPaths.ServicePipe, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Impersonation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await pipe.ConnectAsync(timeout.Token);
        int pid = checked((int)ProcessSecurity.PipeServer(pipe));
        if (ProcessSecurity.ServicePid(DataPaths.ServiceName) != (uint)pid) throw new UnauthorizedAccessException("监测管道不属于 SCM 报告的本软件服务进程。");
        using var registration=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+DataPaths.ServiceName);
        string registeredPath=CommandInspector.ResolveExecutable(registration?.GetValue("ImagePath") as string??"");
        if(!registeredPath.StartsWith(Path.GetFullPath(DataPaths.ComponentRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("机器采集程序不在受控目录。");
        // The kernel-provided pipe PID must match SCM and the administrator-controlled service registration.
        await Wire.Send(pipe, request, timeout.Token); var response = await Wire.Receive(pipe, timeout.Token);
        if (response.Kind == "error") throw new InvalidOperationException(response.Text); return response;
    }
}
