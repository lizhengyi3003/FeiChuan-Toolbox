using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Windows.Management.Deployment;
using EnumerationOptions = System.Management.EnumerationOptions;

namespace TunHotspot.Core;

public sealed partial class WindowsScanner
{
    private static string RegistryTree(RegistryKey? root, int depth)
    {
        if (root is null || depth < 0) return "";
        var result = new StringBuilder();
        foreach (var n in root.GetValueNames()) result.Append(n).Append('=').Append(Value(root, n)).Append(';');
        foreach (var n in root.GetSubKeyNames()) { using var child = root.OpenSubKey(n); result.Append(n).Append('{').Append(RegistryTree(child, depth - 1)).Append('}'); }
        return result.ToString();
    }
    private static string ResolveClsid(string clsid, string sid,RegistryView? onlyView=null)
    {
        if (!Guid.TryParse(clsid, out _)) return "";
        foreach (var view in onlyView.HasValue?new[]{onlyView.Value}:new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = Machine(view); using var users = Users(view); using var user = users.OpenSubKey(sid + @"\Software\Classes");
            foreach (var root in new[] { user, machine.OpenSubKey(@"SOFTWARE\Classes") })
            {
                if (root is null) continue;
                foreach (var sub in new[] { "LocalServer32", "InprocServer32" })
                { using var key = root.OpenSubKey($@"CLSID\{clsid}\{sub}"); var path = Value(key, ""); if (!string.IsNullOrEmpty(path)) return path; }
            }
            using var index = machine.OpenSubKey(@"SOFTWARE\Classes\PackagedCom\ClassIndex\" + clsid);
            if (index is null) continue;
            foreach (var packageName in index.GetSubKeyNames())
            {
                using var package = machine.OpenSubKey(@"SOFTWARE\Classes\PackagedCom\Package\" + packageName);
                using var @class = package?.OpenSubKey("Class\\" + clsid); var serverId = Value(@class, "ServerId");
                if (serverId.Length == 0) continue;
                using var server = package?.OpenSubKey("Server\\" + serverId); var executable = Value(server, "Executable");
                if(string.IsNullOrWhiteSpace(executable))continue;
                if (Path.IsPathRooted(executable)) return executable;
                var location = Value(package, "PackageRootFolder");
                if(location.Length==0)
                {
                    using var repository=machine.OpenSubKey(@"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PackageRepository\Packages\"+packageName);
                    location=Value(repository,"Path");
                }
                if(Path.IsPathFullyQualified(location))return Path.Combine(location,executable);
            }
        }
        return "";
    }
    private void ReadRegistryValues(RegistryKey root, string path, string label, string owner, string category, params string[] names)
    {
        Check();
        try
        {
            using var key = root.OpenSubKey(path); if (key is null) return;
            foreach (var name in key.GetValueNames().Where(x => names.Length == 0 || names.Contains(x, StringComparer.OrdinalIgnoreCase)))
            {
                var value = Value(key, name); if (value.Length == 0) continue;
                var e = Add(category, string.IsNullOrEmpty(name) ? "(默认)" : name, label + "\\" + path, value, owner, root.View == RegistryView.Registry32 ? "32" : "64");
                e.Mode = "宿主/系统事件触发；登记不代表开机常驻";
                e.Evidence["注册表类型"] = key.GetValueKind(name).ToString();
            }
        }
        catch (Exception ex) { Issue($"{path}：{ex.Message}"); }
    }
    private void ReadAdvanced()
    {
        using var machine = Machine();
        foreach (var (path, category, names) in new[] {
            (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Winlogon", new[]{"Shell","Userinit","Taskman","AppSetup"}),
            (@"SYSTEM\CurrentControlSet\Control\Session Manager", "Session Manager", new[]{"BootExecute","Execute","SetupExecute"}),
            (@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "AppInit", new[]{"AppInit_DLLs","LoadAppInit_DLLs","RequireSignedAppInit_DLLs"}),
            (@"SYSTEM\CurrentControlSet\Control\Lsa", "LSA 扩展", new[]{"Authentication Packages","Notification Packages","Security Packages"}),
            (@"SYSTEM\CurrentControlSet\Control\SecurityProviders", "安全提供程序", new[]{"SecurityProviders"}),
            (@"SYSTEM\CurrentControlSet\Control\Session Manager\AppCertDlls", "AppCert DLL", Array.Empty<string>()) })
            ReadRegistryValues(machine, path, "HKLM", "machine", category, names);
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var m = Machine(view);
            foreach (var sub in new[] { @"SOFTWARE\Microsoft\Active Setup\Installed Components", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Notify" })
            {
                try
                {
                    using var root = m.OpenSubKey(sub); if (root is null) continue;
                    foreach (var name in root.GetSubKeyNames())
                        ReadRegistryValues(m, sub + "\\" + name, "HKLM", "machine", sub.Contains("Active Setup") ? "Active Setup" : "进程/登录扩展", "StubPath", "Debugger", "MonitorProcess", "DllName", "GlobalFlag", "ReportingMode");
                }
                catch (Exception ex) { Issue(ex.Message); }
            }
        }
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Scripts" })
            ReadTree(machine, path, "HKLM", "machine", "组策略脚本", 5);
        foreach (var user in _users.Where(x => x.Key is not null))
        {
            ReadRegistryValues(user.Key!, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", "HKU\\" + user.Sid, user.Sid, "用户登录扩展", "load", "run");
            ReadRegistryValues(user.Key!, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "HKU\\" + user.Sid, user.Sid, "用户 Winlogon", "Shell");
            ReadTree(user.Key!, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts", "HKU\\" + user.Sid, user.Sid, "用户组策略脚本", 5);
        }
        _stage.Note = "按所列注册范围只读采集；浏览器私有后台配置、全部应用自有数据库及容器内部配置未深入解析。";
    }
    private void ReadTree(RegistryKey root, string path, string label, string owner, string category, int depth)
    {
        if (depth < 0) return;
        try
        {
            ReadRegistryValues(root, path, label, owner, category);
            using var key = root.OpenSubKey(path); if (key is null) return;
            foreach (var child in key.GetSubKeyNames()) ReadTree(root, path + "\\" + child, label, owner, category, depth - 1);
        }
        catch (Exception ex) { Issue(ex.Message); }
    }
}
