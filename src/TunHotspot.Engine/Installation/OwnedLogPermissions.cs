using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using TunHotspot.Core;

namespace TunHotspot.Engine;

// Only the fixed diagnostics directory inside the already-secured machine root
// is repairable. Unknown owners and reparse points remain hard failures.
internal static class OwnedLogPermissions
{
    internal static async Task Prepare()
    {
        string path=Path.Combine(DataPaths.MachineRoot,"diagnostics");if(!Directory.Exists(path))return;
        string original;
        try
        {
            var before=new DirectoryInfo(path).GetAccessControl();
            string? owner=before.GetOwner(typeof(SecurityIdentifier))?.Value;
            if(owner is not ("S-1-5-18" or "S-1-5-32-544"))throw new InvalidOperationException("自身日志目录属于未知账户，未接管。");
            var rules=before.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            bool Full(string sid)=>rules.Any(x=>x.AccessControlType==AccessControlType.Allow&&x.IdentityReference.Value==sid&&(x.FileSystemRights&FileSystemRights.FullControl)==FileSystemRights.FullControl);
            if(Full("S-1-5-18")&&Full("S-1-5-32-544")&&!rules.Any(x=>x.AccessControlType==AccessControlType.Deny))return;
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("自身日志目录是重解析点，未接管。");
            original=before.GetSecurityDescriptorSddlForm(AccessControlSections.Owner|AccessControlSections.Access);
        }
        catch(UnauthorizedAccessException){original=ReadTrustedDescriptor(path);}
        string archive=Path.Combine(DataPaths.MachineRoot,"diagnostics-acl-before-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json");
        File.WriteAllText(archive,Contract.Encode(new{Path=path,OriginalSddl=original,Time=DateTimeOffset.UtcNow}));
        if(new RawSecurityDescriptor(original).Owner?.Value!="S-1-5-32-544")
        {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"takeown.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)};
        foreach(string argument in new[]{"/f",path,"/a"})start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("自身日志目录维护工具未启动。");
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));await process.WaitForExitAsync(timeout.Token);
        if(process.ExitCode!=0)throw new IOException("自身日志目录维护未完成："+await error+await output);
        }
        var access=new DirectorySecurity();access.SetAccessRuleProtection(true,false);
        foreach(string sid in new[]{"S-1-5-18","S-1-5-32-544"})access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(access);
        // ComponentInstaller immediately reapplies SYSTEM + Administrators only,
        // with inheritance. No regular-user access is granted.
    }
    private static string ReadTrustedDescriptor(string path)
    {
        if(!OpenProcessToken(GetCurrentProcess(),0x28,out var token))throw new Win32Exception(Marshal.GetLastWin32Error());
        Privileges previous=default;bool changed=false;
        try
        {
            if(!LookupPrivilegeValue(null,"SeBackupPrivilege",out var luid))throw new Win32Exception(Marshal.GetLastWin32Error());
            var requested=new Privileges{Count=1,Value=luid,Attributes=2};
            if(!AdjustTokenPrivileges(token,false,ref requested,Marshal.SizeOf<Privileges>(),out previous,out _)||Marshal.GetLastWin32Error()!=0)throw new Win32Exception(Marshal.GetLastWin32Error());changed=true;
            using var file=CreateFile(path,0x20080,7,0,3,0x02200000,0);if(file.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            if(!GetFileInformationByHandleEx(file,9,out var attributes,8))throw new Win32Exception(Marshal.GetLastWin32Error());
            if((attributes.Attributes&0x400)!=0)throw new UnauthorizedAccessException("自身日志目录是重解析点，未接管。");
            uint code=GetSecurityInfo(file,1,5,out var owner,out _,out _,out _,out var descriptor);if(code!=0)throw new Win32Exception((int)code);
            try
            {
                if(new SecurityIdentifier(owner).Value is not ("S-1-5-18" or "S-1-5-32-544"))throw new UnauthorizedAccessException("自身日志目录属于未知账户，未接管。");
                int size=(int)GetSecurityDescriptorLength(descriptor);var bytes=new byte[size];Marshal.Copy(descriptor,bytes,0,size);
                return new RawSecurityDescriptor(bytes,0).GetSddlForm(AccessControlSections.Owner|AccessControlSections.Access);
            }
            finally{LocalFree(descriptor);}
        }
        finally
        {
            if(changed)RestoreTokenPrivileges(token,false,ref previous,0,0,0);
            CloseHandle(token);
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]private struct Privileges{public uint Count;public Luid Value;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct AttributeTag{public uint Attributes,Tag;}
    [DllImport("kernel32.dll")]private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(nint value);
    [DllImport("kernel32.dll")]private static extern nint LocalFree(nint value);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool LookupPrivilegeValue(string? system,string name,out Luid value);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool AdjustTokenPrivileges(nint token,bool disable,ref Privileges value,int length,out Privileges previous,out int returned);
    [DllImport("advapi32.dll",EntryPoint="AdjustTokenPrivileges",SetLastError=true)]private static extern bool RestoreTokenPrivileges(nint token,bool disable,ref Privileges value,int length,nint previous,nint returned);
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file,int kind,out AttributeTag value,int size);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(Microsoft.Win32.SafeHandles.SafeFileHandle file,int kind,uint flags,out nint owner,out nint group,out nint dacl,out nint sacl,out nint descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(nint value);
}
