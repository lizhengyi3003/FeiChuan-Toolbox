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
    private void ReadMetadata()
    {
        var previous = _previous?.Entries.GroupBy(x => x.Executable, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        var files = new Dictionary<string, FileFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _result.Entries)
        {
            Check();
            e.Executable = CommandInspector.ResolveExecutable(e.Command, _users.FirstOrDefault(x => x.Sid == e.OwnerSid).Path, e.Evidence.GetValueOrDefault("工作目录", ""));
            if(e.Category=="Explorer 扩展")e.Executable=CommandInspector.ResolveShellModule(e.Command,e.View=="Registry32");
            if (!string.IsNullOrEmpty(e.Executable))
            {
                if (!files.TryGetValue(e.Executable, out var fact))
                { fact = CommandInspector.Inspect(e.Executable, previous.GetValueOrDefault(e.Executable)); files[e.Executable] = fact; }
                e.Health = fact.Health; e.FileIdentity = fact.Identity; e.Signature = fact.Signature;
                if (fact.Company.Length > 0) e.Publisher = fact.Company;
                if (e.App == "未确认所属应用" && fact.Product.Length > 0) e.App = fact.Product;
                if (e.App == "未确认所属应用") e.App = Path.GetFileNameWithoutExtension(e.Executable);
            }
            else if (e.Health == "待检查") e.Health = "目标未解析或非文件登记";
            if(e.Category=="LSA 扩展")
            {
                foreach(string module in e.Command.Split(['|',','],StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim().Trim('"')).Where(x=>System.Text.RegularExpressions.Regex.IsMatch(x,@"^[A-Za-z0-9_.-]+$")))
                {
                    string path=Path.Combine(Environment.SystemDirectory,module.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?module:module+".dll");
                    var fact=CommandInspector.Inspect(path,null);
                    e.Evidence["认证载荷 "+module]=$"{path} · {fact.Health} · {fact.Company} · {fact.Signature}";
                    e.Configuration["module:"+module]=fact.Identity;
                }
            }
            if (e.Payload.Length > 0)
            {
                string payload = CommandInspector.ResolveExecutable(e.Payload, null, "");
                if (payload.Length > 0) { var fact = CommandInspector.Inspect(payload, null, false); e.Evidence["载荷文件"] = $"{payload} · {fact.Health} · {fact.Signature}"; }
            }
            Classifier.Apply(e);
        }
        _stage.Note = "签名离线验证；无法验证不等于恶意。文件、进程与配置采集有各自观察时刻。";
    }
}
