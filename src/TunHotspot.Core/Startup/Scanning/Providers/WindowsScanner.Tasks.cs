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
    private static XDocument SafeXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 2_000_000, XmlResolver = null });
        return XDocument.Load(reader);
    }
    private void ReadTasks()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("计划任务 COM 接口不可用。");
        dynamic service = Activator.CreateInstance(type)!;
        try { service.Connect(); dynamic folder = service.GetFolder("\\"); try { ReadTaskFolder(folder); } finally { Marshal.FinalReleaseComObject(folder); } }
        finally { Marshal.FinalReleaseComObject(service); }
    }
    private void ReadTaskFolder(dynamic folder)
    {
        Check();
        try
        {
            dynamic tasks = folder.GetTasks(1);
            try
            {
                for (int i = 1; i <= (int)tasks.Count; i++)
                {
                    Check(); dynamic task = tasks.Item(i);
                    try
                    {
                        string path = task.Path; string xmlText = task.Xml; var xml = SafeXml(xmlText);
                        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                        var root = xml.Root!; var actions = root.Element(ns + "Actions");
                        var execute = actions?.Element(ns + "Exec"); var com = actions?.Element(ns + "ComHandler");
                        var command = execute is null ? "" : CommandInspector.TaskCommand(execute.Element(ns + "Command")?.Value??"",execute.Element(ns + "Arguments")?.Value??"");
                        var e = Add("计划任务", (string)task.Name, path, command); e.Enabled = (bool)task.Enabled;
                        e.State = ((int)task.State) switch { 1 => "禁用", 2 => "排队", 3 => "就绪", 4 => "运行中", _ => "未知" };
                        e.Configuration["enabled"] = e.Enabled.ToString()!;
                        e.Configuration["actions"] = actions?.ToString(SaveOptions.DisableFormatting) ?? "";
                        e.Configuration["triggers"] = root.Element(ns + "Triggers")?.ToString(SaveOptions.DisableFormatting) ?? "";
                        var principal = root.Element(ns + "Principals")?.Element(ns + "Principal");
                        e.Configuration["principal"] = principal?.ToString(SaveOptions.DisableFormatting) ?? "";
                        e.Evidence["执行账户"] = principal?.Element(ns + "UserId")?.Value ?? principal?.Element(ns + "GroupId")?.Value ?? "";
                        e.Evidence["工作目录"] = execute?.Element(ns + "WorkingDirectory")?.Value ?? "";
                        e.Evidence["任务 XML"] = xmlText;
                        e.Evidence["作者"]=root.Element(ns+"RegistrationInfo")?.Element(ns+"Author")?.Value??"";
                        e.Evidence["描述"]=root.Element(ns+"RegistrationInfo")?.Element(ns+"Description")?.Value??"";
                        var enabledTriggers = root.Element(ns + "Triggers")?.Elements().Where(x => x.Element(ns + "Enabled")?.Value != "false").ToList() ?? [];
                        e.Mode = enabledTriggers.Count == 0 ? "无启用触发器；可能被外部调用" : string.Join("、", enabledTriggers.Select(x => x.Name.LocalName).Distinct());
                        e.Evidence["最近执行结果"] = "0x" + unchecked((uint)(int)task.LastTaskResult).ToString("X8");
                        if (com is not null)
                        {
                            string clsid = com.Element(ns + "ClassId")?.Value ?? ""; e.Evidence["COM ClassId"] = clsid;
                            e.Command = ResolveClsid(clsid, _owner); e.Configuration["resolvedCom"] = e.Command;
                            if (e.Command.Length == 0) { e.Health = "COM 目标未解析"; Issue($"任务 {path}：COM {clsid} 目标未解析。"); }
                        }
                    }
                    catch (Exception ex) { Issue($"任务：{ex.Message}"); }
                    finally { Marshal.FinalReleaseComObject(task); }
                }
            }
            finally { Marshal.FinalReleaseComObject(tasks); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Issue($"任务目录：{ex.Message}"); }
        dynamic folders = folder.GetFolders(0);
        try
        {
            for (int i = 1; i <= (int)folders.Count; i++)
            {
                dynamic child = folders.Item(i);
                try { ReadTaskFolder(child); } catch (Exception ex) { Issue(ex.Message); }
                finally { Marshal.FinalReleaseComObject(child); }
            }
        }
        finally { Marshal.FinalReleaseComObject(folders); }
    }
}
