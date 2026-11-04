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
    private void ReadProcesses()
    {
        _result.Processes=ProcessSecurity.Processes();var native=_result.Processes.ToDictionary(x=>x.Pid);
        try
        {
            using var query = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery("SELECT Name,ProcessId,ParentProcessId,ExecutablePath,CommandLine,CreationDate FROM Win32_Process"), new EnumerationOptions { Timeout = TimeSpan.FromSeconds(25) });
            using var rows = query.Get();
            foreach (ManagementObject row in rows)
            {
                Check();using(row)
                {
                    int pid=Convert.ToInt32(row["ProcessId"]);if(!native.TryGetValue(pid,out var p))continue;
                    var created=Text(row["CreationDate"]);DateTimeOffset? observed=null;if(created.Length>0)try{observed=ManagementDateTimeConverter.ToDateTime(created);}catch{}
                    if(p.Created.HasValue&&observed.HasValue&&Math.Abs((p.Created.Value-observed.Value).TotalMilliseconds)>5)continue;
                    p.Command=Text(row["CommandLine"]);if(p.Path.Length==0)p.Path=Text(row["ExecutablePath"]);p.Source+="；WMI 补充可访问命令行";
                }
            }
        }
        catch(Exception ex){Issue("已用原生 ToolHelp 获取进程；WMI 命令行补充不可用："+ex.Message);}
        foreach(var p in _result.Processes)
        {
            var services=_result.Entries.Where(x=>x.Provider=="services"&&x.ProcessId>0&&x.ProcessId==p.Pid&&(!p.Created.HasValue||p.Created<=x.ObservedAt)).Select(x=>x.Name).ToList();
            if(services.Count>0)p.Association="当前 SCM PID 对应（非历史启动因果）："+string.Join(", ",services);
        }
        _stage.Note = $"{_result.Processes.Count(x => string.IsNullOrEmpty(x.Path))} 个进程路径未读取到；快照无法补回全部历史拉起事件。";
    }
}
