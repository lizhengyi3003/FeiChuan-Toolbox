using Microsoft.Win32;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using TunHotspot.Core;

namespace TunHotspot.Engine;

internal static class ControlExecutor
{
    private static string Root(string owner)=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"TunHotspot","Actions",owner);
    public static async Task<List<StartupControlRecord>> Run(StartupControlRequest request,string owner,IProgress<ScanProgress> progress)
    {
        if(!Contract.Elevated||request.DeviceId!=Contract.DeviceId)throw new UnauthorizedAccessException("操作需要管理员授权并且必须属于本机。");
        _=new SecurityIdentifier(owner);string root=EnsureRoot(owner);
        if(request.Operation=="history")return Directory.EnumerateFiles(root,"*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(200).Select(x=>ReadRecord(x,owner)).ToList();
        StartupControlRecord? restored=null;
        if(request.Operation=="restore")
        {
            if(!Guid.TryParseExact(request.RestoreId,"N",out _))throw new InvalidDataException("恢复记录无效。");
            restored=ReadRecord(Path.Combine(root,request.RestoreId+".json"),owner);
            if(restored.After is null||restored.Status is not ("已应用" or "失败，存在状态变化，可恢复"))throw new InvalidOperationException("此记录没有可安全恢复的已核实状态。");
            request.EntryId=restored.Before.Id;request.Provider=restored.Before.Provider;request.UserNote=restored.Before.UserNote;
            request.Fingerprint=restored.After.Fingerprint;
        }
        if(request.EntryId.Length!=64||request.UserNote.Length>2000||request.Value.Length>20)throw new InvalidDataException("操作请求无效。");
        var current=new WindowsScanner(owner).InspectTarget(request.Provider,request.EntryId,progress);current.UserNote=request.UserNote;
        if(current.Fingerprint!=request.Fingerprint)throw new InvalidOperationException("配置、文件或用途已经变化，未执行旧预览。请重新扫描并确认。");
        if(restored is not null&&(restored.Operation is "serviceStart" or "serviceStop")&&current.State!=restored.After!.State)throw new InvalidOperationException("服务运行状态已再次变化，未覆盖当前状态。");
        string operation=restored?.Operation??request.Operation;
        string target=restored is null?request.Value:RestoreValue(restored);
        string effectiveOperation=operation;
        if(restored is not null&&(operation is "serviceStart" or "serviceStop"))effectiveOperation=restored.Backup.Runtime=="Running"?"serviceStart":"serviceStop";
        ControlPolicy.Validate(current,owner,effectiveOperation,target);
        if(current.Provider=="services")CheckServiceProtection(current);
        var record=new StartupControlRecord{OwnerSid=owner,Operation=effectiveOperation,Target=target,Before=current,Backup=Capture(current),RestoreOf=restored?.Id??""};
        if(System.Text.Encoding.UTF8.GetByteCount(Contract.Encode(record))>1_000_000)throw new InvalidDataException("条目证据超过安全备份上限，未执行修改。");
        Write(root,record);
        try
        {
            progress.Report(new("修改所选条目",0,1,1,"已保存受控备份，正在执行所选操作…"));
            if(restored is not null&&current.Provider=="run")RestoreApproval(current,restored.Backup);
            else await Apply(current,effectiveOperation,target);
            var after=new WindowsScanner(owner).InspectTarget(current.Provider,current.Id);after.UserNote=current.UserNote;record.After=after;
            if(restored is not null&&current.Provider=="run")
            {var actual=Capture(after);if(actual.Exists!=restored.Backup.Exists||actual.Binary!=restored.Backup.Binary)throw new IOException("恢复后的原始状态未通过核对。");}
            else Verify(after,effectiveOperation,target);
            record.Status="已应用";
            if(restored is not null){restored.Status="已恢复";Write(root,restored);record.Status="已恢复";}
        }
        catch(Exception ex)
        {
            record.Error=ex.Message;record.Status="失败，未确认修改";
            try{record.After=new WindowsScanner(owner).InspectTarget(current.Provider,current.Id);record.After.UserNote=current.UserNote;if(record.After.Fingerprint!=record.Before.Fingerprint||record.After.State!=record.Before.State)record.Status="失败，存在状态变化，可恢复";}catch{}
        }
        Write(root,record);return restored is null?[record]:[record,restored];
    }
    private static string RestoreValue(StartupControlRecord record)=>record.Operation switch{"setEnabled"=>record.Backup.Enabled?"true":"false","serviceMode"=>record.Backup.StartMode,_=>""};
    private static void CheckServiceProtection(StartupEntry entry)
    {
        using var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64);
        using var key=root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+entry.Name);
        if(Convert.ToInt32(key?.GetValue("LaunchProtected",0))!=0||Convert.ToInt32(key?.GetValue("ErrorControl",0))==3)throw new InvalidOperationException("此服务受系统保护或属于关键启动配置，未尝试绕过保护。");
    }
    private static ControlValueBackup Capture(StartupEntry entry)
    {
        if(entry.Provider=="run")
        {
            using var root=ApprovalRoot(entry);using var key=root.OpenSubKey(ApprovalPath(entry));var value=key?.GetValue(entry.Name);
            if(value is not null&&value is not byte[])throw new InvalidDataException("启动批准记录不是预期的二进制格式。");
            return new(){Exists=value is not null,Binary=value is byte[] b?Convert.ToBase64String(b):"",Enabled=entry.Enabled??true};
        }
        if(entry.Provider=="tasks")return new(){Exists=true,Enabled=entry.Enabled??throw new InvalidDataException("任务启用状态未确认。"),Runtime=entry.State};
        return new(){Exists=true,Enabled=entry.Enabled??false,Runtime=entry.State,StartMode=entry.Mode switch{"自动"=>entry.Evidence.GetValueOrDefault("延迟启动")=="1"?"delayed-auto":"auto","手动"=>"demand","禁用"=>"disabled",_=>throw new InvalidDataException("原始启动类型未确认。")}};
    }
    private static RegistryKey ApprovalRoot(StartupEntry entry)=>entry.OwnerSid=="machine"?RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64):RegistryKey.OpenBaseKey(RegistryHive.Users,RegistryView.Registry64);
    private static string ApprovalPath(StartupEntry entry)
    {
        string prefix=entry.OwnerSid=="machine"?"":entry.OwnerSid+"\\";
        string bucket=entry.Category=="启动文件夹"?"StartupFolder":entry.View=="32"?"Run32":"Run";
        return prefix+@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\"+bucket;
    }
    private static async Task Apply(StartupEntry entry,string operation,string target)
    {
        if(operation=="setEnabled")
        {
            bool enabled=target=="true";
            if(entry.Provider=="run")
            {
                using var root=ApprovalRoot(entry);using var key=root.CreateSubKey(ApprovalPath(entry));var bytes=new byte[12];bytes[0]=enabled?(byte)2:(byte)3;
                if(!enabled)BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(bytes,4);key.SetValue(entry.Name,bytes,RegistryValueKind.Binary);
            }
            else
            {
                dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;dynamic? folder=null,task=null;
                try{service.Connect();folder=service.GetFolder("\\");task=folder.GetTask(entry.Source);task.Enabled=enabled;}
                finally{if(task is not null)Marshal.FinalReleaseComObject(task);if(folder is not null)Marshal.FinalReleaseComObject(folder);Marshal.FinalReleaseComObject(service);}
            }
            return;
        }
        if(operation=="serviceMode")
        {
            var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"config",entry.Name,"start=",target})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)??throw new IOException("服务管理器未启动。");var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync();
            if(process.ExitCode!=0)throw new IOException($"服务管理器拒绝修改（{process.ExitCode}）：{await output} {await error}");return;
        }
        using var controller=new ServiceController(entry.Name);controller.Refresh();
        if(operation=="serviceStart")
        {
            if(controller.Status==ServiceControllerStatus.Running)return;
            controller.Start();await Task.Run(()=>controller.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(20)));
        }
        else
        {
            if(controller.Status==ServiceControllerStatus.Stopped)return;
            if(!controller.CanStop)throw new InvalidOperationException("该服务目前不接受停止请求。");
            var dependencies=controller.DependentServices;try{var running=dependencies.Where(x=>x.Status!=ServiceControllerStatus.Stopped).Select(x=>x.ServiceName).ToList();if(running.Count>0)throw new InvalidOperationException("存在正在运行的依赖服务，未进行连带停止："+string.Join("、",running));}finally{foreach(var item in dependencies)item.Dispose();}
            controller.Stop(false);await Task.Run(()=>controller.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(20)));
        }
    }
    private static void RestoreApproval(StartupEntry entry,ControlValueBackup backup)
    {using var root=ApprovalRoot(entry);using var key=root.CreateSubKey(ApprovalPath(entry));if(backup.Exists)key.SetValue(entry.Name,Convert.FromBase64String(backup.Binary),RegistryValueKind.Binary);else key.DeleteValue(entry.Name,false);}
    private static void Verify(StartupEntry after,string operation,string target)
    {
        bool ok=operation switch{"setEnabled"=>after.Enabled==(target=="true"),"serviceMode"=>after.Mode==(target is "auto" or "delayed-auto"?"自动":target=="demand"?"手动":"禁用")&&(target!="delayed-auto"||after.Evidence.GetValueOrDefault("延迟启动")=="1")&&(target!="auto"||after.Evidence.GetValueOrDefault("延迟启动")!="1"),"serviceStart"=>after.State=="Running","serviceStop"=>after.State=="Stopped",_=>false};
        if(!ok)throw new IOException("系统重新读取的状态与目标不一致，未报告修改成功；可能存在保护或外部覆盖。");
    }
    private static string EnsureRoot(string owner)
    {
        string root=Root(owner);for(DirectoryInfo? p=new(root);p is not null;p=p.Parent)if(p.Exists&&(p.Attributes&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("备份路径含重解析点。");
        string product=Path.GetDirectoryName(Path.GetDirectoryName(root)!)!;
        foreach(string path in new[]{product,Path.GetDirectoryName(root)!,root})
        {
            if(Directory.Exists(path)&&new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))throw new UnauthorizedAccessException("备份目录被非系统账户预占，未采用其中数据。");
            var directory=Directory.CreateDirectory(path);var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
            foreach(string sid in new[]{"S-1-5-18","S-1-5-32-544"})acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));directory.SetAccessControl(acl);
        }
        return root;
    }
    private static StartupControlRecord ReadRecord(string path,string owner)
    {
        var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0||info.Length>4_000_000||info.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value is not ("S-1-5-18" or "S-1-5-32-544"))throw new UnauthorizedAccessException("备份文件不可信。");
        var record=Contract.Decode<StartupControlRecord>(File.ReadAllText(path));if(record.OwnerSid!=owner||record.DeviceId!=Contract.DeviceId)throw new UnauthorizedAccessException("备份不属于该账户或本机。");return record;
    }
    private static void Write(string root,StartupControlRecord record)
    {
        string target=Path.Combine(root,record.Id+".json"),temporary=Path.Combine(root,record.Id+"."+Guid.NewGuid().ToString("N")+".tmp");
        if(File.Exists(target)&&(File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0)throw new UnauthorizedAccessException("备份文件不可替换为重解析点。");
        File.WriteAllText(temporary,Contract.Encode(record));var info=new FileInfo(temporary);var acl=info.GetAccessControl();acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));info.SetAccessControl(acl);File.Move(temporary,target,true);
    }
}
