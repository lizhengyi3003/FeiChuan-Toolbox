using Microsoft.Win32;

namespace TunHotspot.Core;

public sealed record StartupRegistrationState(bool Registered,bool Enabled,bool WindowsDisabled,string Command,string Fingerprint)
{
    public string Description=>!Registered?"未设置登录自启":WindowsDisabled?"Windows 启动管理已禁用该入口":"已启用当前账户登录自启";
}

public static class StartupRegistration
{
    public const string ValueName="TunHotspot.Toolbox.Agent";
    private const string Run=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved=@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public static StartupRegistrationState Read()=>ReadAt(Run,Approved);
    internal static StartupRegistrationState ReadAt(string runPath,string approvedPath)
    {
        using var run=Registry.CurrentUser.OpenSubKey(runPath);using var approved=Registry.CurrentUser.OpenSubKey(approvedPath);
        string command=run?.GetValue(ValueName) as string??"";byte[]? status=approved?.GetValue(ValueName) as byte[];
        bool disabled=status is {Length:>=4}&&(status[0]&1)!=0;
        return new(command.Length>0,command.Length>0&&!disabled,disabled,command,Contract.Hash(command+":"+(status is null?"missing":Convert.ToHexString(status))));
    }
    public static StartupRegistrationState SetEnabled(bool enabled,string executable,string expected,string root)=>SetAt(enabled,executable,expected,root,Run,Approved);
    internal static StartupRegistrationState SetAt(bool enabled,string executable,string expected,string root,string runPath,string approvedPath)
    {
        var before=ReadAt(runPath,approvedPath);if(before.Fingerprint!=expected)throw new InvalidOperationException("Windows 启动状态已被其他程序修改，请刷新后重新预览。");
        if(before.Registered&&!before.Command.Equals('"'+executable+"\" --background",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("同名入口指向另一位置，已保留原值；请核实安装位置。");
        using var run=Registry.CurrentUser.CreateSubKey(runPath);using var approved=Registry.CurrentUser.CreateSubKey(approvedPath);
        byte[]? old=approved.GetValue(ValueName) as byte[];
        string folder=Path.Combine(root,"autostart-changes");KumiryoPrivateFiles.Directory(folder);
        string backup=Path.Combine(folder,DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json");
        KumiryoPrivateFiles.Atomic(backup,Contract.Encode(new{before.Command,Approved=old,Requested=enabled,Time=DateTimeOffset.UtcNow,Executable=executable}));
        try
        {
            if(enabled){run.SetValue(ValueName,'"'+executable+"\" --background",RegistryValueKind.String);approved.SetValue(ValueName,new byte[]{2,0,0,0,0,0,0,0,0,0,0,0},RegistryValueKind.Binary);}
            else{run.DeleteValue(ValueName,false);approved.DeleteValue(ValueName,false);}
            var result=ReadAt(runPath,approvedPath);if(result.Enabled!=enabled)throw new IOException("登录自启回读不一致。");
            new DiagnosticLog(root).Event("设置","信息",enabled?"已开启登录自启":"已关闭登录自启","只修改本软件当前用户入口；原状态已备份。" );return result;
        }
        catch
        {
            if(before.Registered)run.SetValue(ValueName,before.Command,RegistryValueKind.String);else run.DeleteValue(ValueName,false);
            if(old is not null)approved.SetValue(ValueName,old,RegistryValueKind.Binary);else approved.DeleteValue(ValueName,false);throw;
        }
    }
}
