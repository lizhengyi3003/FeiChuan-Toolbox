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
    private void ReadServices()
    {
        var running = new Dictionary<string, (string State, int Pid, string Description)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach(var service in ProcessSecurity.Services())running[service.Name]=(service.State,service.Pid,service.DisplayName);
            _stage.Note="运行状态通过原生 SCM 枚举；注册表补充启动配置，未枚举到的登记单列。";
        }
        catch(Exception ex){Issue("原生 SCM 枚举："+ex.Message);}
        foreach (var className in running.Count>0?Array.Empty<string>():new[] { "Win32_Service", "Win32_SystemDriver" })
        {
            try
            {
                using var query = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery($"SELECT Name,State,Description{(className == "Win32_Service" ? ",ProcessId" : "")} FROM {className}"), new EnumerationOptions { Timeout = TimeSpan.FromSeconds(25) });
                using var rows = query.Get();
                foreach (ManagementObject row in rows) { using (row) running[Text(row["Name"])] = (Text(row["State"]), className == "Win32_Service" ? Convert.ToInt32(row["ProcessId"]) : 0, Text(row["Description"])); }
            }
            catch (Exception ex) { Issue($"{className}：{ex.Message}"); }
        }
        using var machine = Machine(); using var root = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"); if (root is null) throw new IOException("无法读取服务登记。");
        foreach (var name in root.GetSubKeyNames())
        {
            Check();
            try
            {
                using var key = root.OpenSubKey(name); if (key is null) continue;
                var type = Convert.ToInt32(key.GetValue("Type", 0)); var start = key.GetValue("Start");
                if (start is null) continue;
                var driver = (type & 0xB) != 0 && (type & 0x30) == 0;
                var mode = Convert.ToInt32(start) switch { 0 => "引导", 1 => "系统", 2 => "自动", 3 => "手动", 4 => "禁用", _ => "未知" };
                var e = Add(running.ContainsKey(name) ? driver ? "驱动" : "服务" : "服务/驱动补充登记", name, @"HKLM\SYSTEM\CurrentControlSet\Services\" + name, Value(key, "ImagePath"));
                e.Mode = mode; e.Configuration["start"] = Text(start); e.Configuration["type"] = type.ToString(); e.UserInstance = (type & 0x80) != 0;
                if (running.TryGetValue(name, out var runtime)) { e.State = runtime.State; e.ProcessId = runtime.Pid; e.Evidence["描述"] = runtime.Description; }
                else e.State = "未包含在本次运行列表；登记不等于已加载";
                using var parameters = key.OpenSubKey("Parameters"); var dll = Value(parameters, "ServiceDll");
                e.Payload = dll; e.Configuration["serviceDll"] = dll;
                e.Evidence["显示名称"] = Value(key, "DisplayName"); e.Evidence["执行账户"] = Value(key, "ObjectName");
                e.Configuration["delayedAutoStart"]=Value(key,"DelayedAutoStart");
                e.Evidence["依赖"] = Value(key, "DependOnService"); e.Evidence["延迟启动"] = Value(key, "DelayedAutoStart");
                e.Configuration["dependencies"] = Value(key, "DependOnService");
                using var triggers = key.OpenSubKey("TriggerInfo"); e.Evidence["触发器登记"] = triggers is null ? "无" : string.Join(", ", triggers.GetSubKeyNames());
                e.Configuration["triggerInfo"] = RegistryTree(triggers, 3);
                e.Evidence["恢复动作"] = Value(key, "FailureActions"); e.Configuration["recovery"] = Value(key, "FailureActions");
            }
            catch (Exception ex) { Issue($"服务 {name}：{ex.Message}"); }
        }
        var instances=_result.Entries.Where(x=>x.Provider=="services"&&x.UserInstance).ToList();
        var owners=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var e in instances.Where(x=>x.ProcessId>0))
        {
            int split=e.Name.LastIndexOf('_');if(split<1)continue;
            try{string sid=ProcessSecurity.UserSid(e.ProcessId);if(_users.Any(x=>x.Sid==sid))owners[e.Name[(split+1)..]]=sid;}catch{}
        }
        foreach(var e in instances)
        {
            int split=e.Name.LastIndexOf('_');if(split<1||!owners.TryGetValue(e.Name[(split+1)..],out string? sid))continue;
            string template=e.Name[..split];using var key=root.OpenSubKey(template);
            if((Convert.ToInt32(key?.GetValue("Type",0))&0x40)==0)continue;
            e.OwnerSid=sid;e.Id=Contract.Hash($"services|user-instance|{sid}|{template.ToLowerInvariant()}");
            e.Evidence["用户服务模板"]=template;e.Evidence["已核对账户"]=sid;
        }
    }
}
