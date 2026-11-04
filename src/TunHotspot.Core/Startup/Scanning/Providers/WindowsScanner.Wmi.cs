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
    private void ReadWmi()
    {
        foreach (var className in new[] { "__EventFilter", "CommandLineEventConsumer", "ActiveScriptEventConsumer", "NTEventLogEventConsumer", "LogFileEventConsumer", "__FilterToConsumerBinding" })
        {
            Check();
            try
            {
                using var query = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\subscription"), new ObjectQuery("SELECT * FROM " + className), new EnumerationOptions { Timeout = TimeSpan.FromSeconds(15) });
                using var rows = query.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        var named = row.Properties.Cast<PropertyData>().FirstOrDefault(x => x.Name == "Name")?.Value;
                        var e = Add("WMI 永久订阅", className + " · " + (Text(named) is { Length: > 0 } n ? n : row.Path.RelativePath), row.Path.Path);
                        foreach (PropertyData p in row.Properties) if (p.Value is not null) e.Configuration[p.Name] = Text(p.Value);
                        e.Command = e.Configuration.GetValueOrDefault("CommandLineTemplate", e.Configuration.GetValueOrDefault("ExecutablePath", ""));
                        e.Mode = "WMI 事件触发"; e.Evidence["说明"] = "Filter、Consumer 和 Binding 分开保存；应按引用关系联合判断。";
                    }
                }
            }
            catch (Exception ex) { Issue($"WMI {className}：{ex.Message}"); }
        }
    }
}
