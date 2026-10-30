using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using TunHotspot.Core;

namespace TunHotspot.Engine;

internal static class ReadOnlyAudit
{
    public static async Task<int> Run()
    {
        var store=new ToolboxStore(DataPaths.UserRoot);var previous=store.Latest;
        string directory=Path.Combine(DataPaths.UserRoot,"audits",DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8]);Directory.CreateDirectory(directory);
        if(previous is not null)await File.WriteAllTextAsync(Path.Combine(directory,"before.json"),Contract.Encode(previous));
        string name="TunHotspot.Scan."+Guid.NewGuid().ToString("N");var acl=new PipeSecurity();
        foreach(var sid in new[]{new SecurityIdentifier(Contract.Sid),new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})acl.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
        using var pipe=NamedPipeServerStreamAcl.Create(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,4096,4096,acl);
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=AppContext.BaseDirectory};
        foreach(string arg in new[]{"--audit-worker","--pipe",name,"--owner",Contract.Sid})start.ArgumentList.Add(arg);
        Console.WriteLine("正在申请管理员权限；仅连续执行两次只读扫描，不启停或修改 Windows 入口。");
        using var worker=await Task.Run(()=>Process.Start(start))??throw new IOException("管理员扫描进程未启动。");
        using var connect=new CancellationTokenSource(TimeSpan.FromSeconds(45));await pipe.WaitForConnectionAsync(connect.Token);
        if(ProcessSecurity.PipeClient(pipe)!=worker.Id)throw new UnauthorizedAccessException("扫描连接与本次授权的进程不符。");
        var snapshots=new List<ScanSnapshot>();using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(15));
        while(true)
        {
            var message=await Wire.Receive(pipe,deadline.Token);
            if(message.Kind=="progress"&&message.Progress is not null){Console.WriteLine(message.Text+" "+message.Progress.Message);continue;}
            if(message.Kind=="error")throw new IOException(message.Text);
            if(message.Kind=="complete")break;
            if(message.Kind!="snapshot"||message.Snapshot is not ScanSnapshot snapshot)throw new InvalidDataException("扫描返回了非预期内容。");
            if(snapshot.OwnerSid!=Contract.Sid||snapshot.DeviceId!=Contract.DeviceId||snapshot.Privilege is not ("管理员" or "LocalSystem")||snapshot.Cancelled)throw new InvalidDataException("管理员身份或扫描归属未通过验证。");
            store.SaveSnapshot(snapshot);snapshots.Add(snapshot);await File.WriteAllTextAsync(Path.Combine(directory,$"pass{snapshots.Count}.json"),Contract.Encode(snapshot));
        }
        if(snapshots.Count!=2)throw new IOException("未完成两次管理员复核，未报告稳定结果。");
        var comparison=AuditComparison.Between(snapshots[0],snapshots[1]);store.Set("lastAdminAudit",comparison);
        await File.WriteAllTextAsync(Path.Combine(directory,"comparison.json"),Contract.Encode(comparison));
        if(previous is not null)await File.WriteAllTextAsync(Path.Combine(directory,"against-before.json"),Contract.Encode(AuditComparison.Between(previous,snapshots[0])));
        Console.WriteLine(Contract.Encode(new{directory,comparison.Privilege,comparison.Entries,added=comparison.Added.Count,removed=comparison.Removed.Count,changed=comparison.Changed.Count,gaps=comparison.Gaps.Count,first=snapshots[0].Id,second=snapshots[1].Id}));return 0;
    }
    public static async Task Worker(NamedPipeClientStream pipe,string owner)
    {
        for(int pass=1;pass<=2;pass++)
        {
            int current=pass;var progress=new DirectProgress<ScanProgress>(p=>Wire.Send(pipe,new(){Kind="progress",Text=$"管理员第 {current} 轮",Progress=p}).GetAwaiter().GetResult());
            var snapshot=await Task.Run(()=>new WindowsScanner(owner).Scan(progress));
            await Wire.Send(pipe,new(){Kind="snapshot",Text=$"pass{pass}",Snapshot=snapshot});
            if(pass==1)await Task.Delay(2000);
        }
        await Wire.Send(pipe,new(){Kind="complete"});
    }
}
