using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Security.Cryptography;
using TunHotspot.Core;
using Microsoft.Win32;

namespace TunHotspot.Engine;

internal sealed record ComponentPolicy(int Protocol, string OwnerSid, string Version);
internal static class ComponentInstaller
{
    private static string PolicyPath => Path.Combine(DataPaths.MachineRoot, "policy.json");
    public static ComponentPolicy Policy => Contract.Decode<ComponentPolicy>(File.ReadAllText(PolicyPath));
    public static async Task Install(string owner)
    {
        _ = new SecurityIdentifier(owner); EnsureAdmin();
        if (File.Exists(PolicyPath) && Policy.OwnerSid != owner) throw new InvalidOperationException("组件属于其他已授权用户，未替换其监测配置。");
        SecureDirectory(Path.GetDirectoryName(DataPaths.ComponentRoot)!,false);
        SecureDirectory(Path.GetDirectoryName(DataPaths.MachineRoot)!,true);
        SecureDirectory(DataPaths.ComponentRoot, false); SecureDirectory(DataPaths.MachineRoot, true);
        await OwnedLogPermissions.Prepare();
        SecureDirectory(Path.Combine(DataPaths.MachineRoot,"diagnostics"),true);
        CheckTree(DataPaths.MachineRoot); foreach(var file in Directory.EnumerateFiles(DataPaths.MachineRoot))ProtectFile(file,true);
        var sourceFiles=Directory.EnumerateFiles(AppContext.BaseDirectory,"*",SearchOption.AllDirectories)
            .Where(x=>!x.EndsWith(".pdb",StringComparison.OrdinalIgnoreCase)&&!x.EndsWith(".db",StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).Select(x=>new{Path=x,Relative=Path.GetRelativePath(AppContext.BaseDirectory,x),Hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(x)))}).ToList();
        string stamp=Contract.Hash(string.Join("\n",sourceFiles.Select(x=>x.Relative+":"+x.Hash)))[..12];
        string target = Path.Combine(DataPaths.ComponentRoot, Contract.ProductVersion + "-" + stamp);
        CheckTarget(target);if(Directory.Exists(target))CheckTree(target);
        bool valid=Directory.Exists(target)&&sourceFiles.All(x=>File.Exists(Path.Combine(target,x.Relative))&&Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target,x.Relative))))==x.Hash);
        if(valid){SecureDirectory(target,false);foreach(var dir in Directory.EnumerateDirectories(target,"*",SearchOption.AllDirectories))SecureDirectory(dir,false);foreach(var file in Directory.EnumerateFiles(target,"*",SearchOption.AllDirectories))ProtectFile(file);}
        if (!valid)
        {
            if(Directory.Exists(target))
            {
                string archive=target+".incomplete-"+Guid.NewGuid().ToString("N");CheckTarget(target);CheckTarget(archive);Directory.Move(target,archive);
            }
            string staging=target+".staging-"+Guid.NewGuid().ToString("N");SecureDirectory(staging,false);
            foreach(var source in sourceFiles)
            {
                if(source.Relative.StartsWith("..")||(File.GetAttributes(source.Path)&FileAttributes.ReparsePoint)!=0)throw new IOException("采集程序路径或文件无效。");
                string destination=Path.Combine(staging,source.Relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(source.Path,destination,false);ProtectFile(destination);
                if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)))!=source.Hash)throw new IOException("复制期间文件变化，未切换机器采集版本。");
            }
            CheckTarget(staging);CheckTarget(target);Directory.Move(staging,target);
        }
        var executable = Path.Combine(target, "TunHotspot.Engine.exe");
        if (!File.Exists(executable)) throw new IOException("机器采集程序缺失。");
        string? previous = ServiceBinary();
        string? previousPolicy=File.Exists(PolicyPath)?File.ReadAllText(PolicyPath):null;
        bool createdService=false;
        if (previous is not null) VerifyOwnService(previous);
        try
        {
            if (previous is not null) await Stop();
            if (File.Exists(Path.Combine(DataPaths.MachineRoot,"toolbox.db")))
            {
                var store = new ToolboxStore(DataPaths.MachineRoot,true);
                using var sourceDb = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=Path.Combine(store.Root,"toolbox.db"),Pooling=false}.ToString()); sourceDb.Open();
                string backupPath=Path.Combine(DataPaths.MachineRoot,"before-update-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".db");
                using var backup = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=backupPath,Pooling=false}.ToString()); backup.Open(); sourceDb.BackupDatabase(backup);
            }
            File.WriteAllText(PolicyPath, Contract.Encode(new ComponentPolicy(Contract.Version, owner, Contract.ProductVersion)));
            ProtectFile(PolicyPath,true);
            string binary = $"\"{executable}\" --service";
            if (previous is null) { await Sc("create",DataPaths.ServiceName,"binPath=",binary,"start=","auto","DisplayName=","飞船的小工具箱 · 启动与后台只读监测");createdService=true; }
            else await Sc("config",DataPaths.ServiceName,"binPath=",binary,"start=","auto","DisplayName=","飞船的小工具箱 · 启动与后台只读监测");
            await Sc("description",DataPaths.ServiceName,"只读采集启动配置与运行事件；不自动停用或恢复其他软件。");
            using var service = new ServiceController(DataPaths.ServiceName); service.Start(); await Task.Run(()=>service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(20)));
        }
        catch
        {
            if(previousPolicy is not null){try{File.WriteAllText(PolicyPath,previousPolicy);ProtectFile(PolicyPath,true);}catch{}}
            if (previous is not null) { try { await Sc("config",DataPaths.ServiceName,"binPath=",previous); using var old=new ServiceController(DataPaths.ServiceName); old.Start(); } catch { } }
            else if(createdService){try{await Stop();await Sc("delete",DataPaths.ServiceName);}catch{}}
            throw;
        }
    }
    public static async Task Uninstall()
    {
        EnsureAdmin(); string? binary=ServiceBinary(); if(binary is null) return; VerifyOwnService(binary);
        await Stop(); await Sc("delete",DataPaths.ServiceName);
        // Keep the protected database and versioned files for recovery; no recursive deletion from an elevated process.
    }
    private static async Task Stop()
    {
        using var service = new ServiceController(DataPaths.ServiceName);
        if(service.Status==ServiceControllerStatus.Stopped)return;
        service.Stop(); await Task.Run(()=>service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(20)));
    }
    private static string? ServiceBinary()
    { using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+DataPaths.ServiceName);return key?.GetValue("ImagePath") as string; }
    private static void VerifyOwnService(string binary)
    {
        string path=CommandInspector.ResolveExecutable(binary);
        if(path.Length==0 || !Path.GetFullPath(path).StartsWith(Path.GetFullPath(DataPaths.ComponentRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("同名服务不属于受控采集目录，未修改。");
    }
    private static void EnsureAdmin() { if(!Contract.Elevated)throw new UnauthorizedAccessException("需要 UAC 管理员授权。"); }
    private static void SecureDirectory(string path,bool privateData)
    {
        string full=Path.GetFullPath(path);
        for(DirectoryInfo? p=new DirectoryInfo(full);p is not null;p=p.Parent) if(p.Exists&&(p.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("受控目录路径含重解析点。");
        if(Directory.Exists(full))
        {
            var owner=new DirectoryInfo(full).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            if(owner is not ("S-1-5-18" or "S-1-5-32-544"))throw new UnauthorizedAccessException("受控目录已被非系统账户预先创建，未接管或执行其中内容。");
        }
        var info=Directory.CreateDirectory(full);
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        var flags=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
            acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,flags,PropagationFlags.None,AccessControlType.Allow));
        if(!privateData)acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,flags,PropagationFlags.None,AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
    private static void ProtectFile(string path,bool privateData=false)
    {
        var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("受控文件不能是重解析点。");
        var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
        foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
        if(!privateData)acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,AccessControlType.Allow));
        info.SetAccessControl(acl);
    }
    private static void CheckTree(string path)
    {
        foreach(var item in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if((item.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("受控目录不能包含重解析点。");
            if(item is DirectoryInfo d)CheckTree(d.FullName);
        }
    }
    private static void CheckTarget(string path)
    {
        string full=Path.GetFullPath(path);
        if(!full.StartsWith(Path.GetFullPath(DataPaths.ComponentRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("组件版本路径越界。");
        if(Directory.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("组件版本路径含重解析点。");
    }
    private static async Task Sc(params string[] args)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("SCM 工具未启动。");
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
        if(process.ExitCode!=0)throw new IOException($"SCM 返回 {process.ExitCode}：{await output} {await error}");
    }
}
