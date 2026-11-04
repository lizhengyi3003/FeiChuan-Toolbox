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
    private void ReadExtensions()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = Machine(view);
            const string approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";
            using var key = machine.OpenSubKey(approved);
            if (key is not null) foreach (var name in key.GetValueNames())
            {
                Check(); var e = Add("Explorer 扩展", Value(key, name), "HKLM\\" + approved + "\\" + name, ResolveClsid(name, _owner,view), view: view.ToString());
                e.Mode = "Explorer 宿主加载；不代表开机常驻"; e.Evidence["CLSID"] = name;
            }
        }
        using var m = Machine(); using var monitors = m.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Monitors");
        if (monitors is not null) foreach (var name in monitors.GetSubKeyNames())
        { using var key = monitors.OpenSubKey(name); var e = Add("打印监视器", name, @"HKLM\SYSTEM\CurrentControlSet\Control\Print\Monitors\" + name, Value(key, "Driver")); e.Mode = "打印服务宿主加载"; }
        using var services = m.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is not null) foreach (var name in services.GetSubKeyNames())
        {
            Check(); using var key = services.OpenSubKey(name + "\\NetworkProvider");
            if (key is not null) { var e = Add("网络提供程序", name, @"HKLM\SYSTEM\CurrentControlSet\Services\" + name + @"\NetworkProvider", Value(key, "ProviderPath")); e.Mode = "网络宿主加载"; }
        }
        ReadRegistryValues(m, @"SYSTEM\CurrentControlSet\Control\NetworkProvider\Order", "HKLM", "machine", "网络提供程序顺序");
        _stage.Note = "已列出批准的 Shell 扩展、打印和网络提供程序；未将所有 COM 类或全部 Winsock 二进制目录登记直接等同启动应用。";
    }
}
