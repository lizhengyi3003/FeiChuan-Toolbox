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

public sealed class WindowsScanner
{
    private readonly string _owner;
    private readonly ScanSnapshot? _previous;
    private readonly List<UserHive> _users = [];
    private ScanSnapshot _result = new();
    private Coverage _stage = new();
    private CancellationToken _cancel;
    private string? _targetProvider,_targetId;
    private readonly record struct UserHive(string Sid, string Path, RegistryKey? Key);
    public WindowsScanner(string ownerSid, ScanSnapshot? previous = null)
    { _ = new SecurityIdentifier(ownerSid); _owner = ownerSid; _previous = previous; }
    public StartupEntry InspectTarget(string provider,string id,IProgress<ScanProgress>? progress=null)
    {
        if(provider is not ("run" or "services" or "tasks")||id.Length!=64)throw new InvalidDataException("修改目标范围无效。");
        _targetProvider=provider;_targetId=id;var snapshot=Scan(progress);
        return snapshot.Entries.SingleOrDefault(x=>x.Id==id)??throw new InvalidOperationException("所选条目已不存在或无法重新确认，请刷新扫描。");
    }

    public ScanSnapshot Scan(IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        _cancel = cancellationToken; _result = new() { OwnerSid = _owner };
        var providers = new (string Id, string Name, Action Run)[] {
            ("profiles", "账户与配置覆盖", ReadProfiles), ("run", "普通登录与启动文件夹", ReadLogin),
            ("appx", "打包应用启动", ReadPackaged), ("services", "服务、驱动与补充登记", ReadServices),
            ("tasks", "全部计划任务与 COM 动作", ReadTasks), ("advanced", "系统登录及间接入口", ReadAdvanced),
            ("extensions", "Shell、打印与网络扩展", ReadExtensions), ("wmi", "WMI 永久订阅", ReadWmi),
            ("processes", "当前进程与已知关联", ReadProcesses), ("metadata", "文件、来源和建议", ReadMetadata)
        };
        if(_targetProvider is not null)providers=providers.Where(x=>x.Id=="profiles"||x.Id==_targetProvider||x.Id=="metadata").ToArray();
        try
        {
            for (int index = 0; index < providers.Length; index++)
            {
                if (_cancel.IsCancellationRequested) { _result.Cancelled = true; break; }
                var p = providers[index]; _stage = new() { Provider = p.Id, Name = p.Name, Status = "扫描中" };
                _result.Coverage.Add(_stage);
                progress?.Report(new(p.Name, index, providers.Length, _result.Entries.Count, $"正在读取{p.Name}…"));
                var before = _result.Entries.Count; var timer = Stopwatch.StartNew();
                try
                {
                    if(p.Id=="metadata"&&_targetId is not null)_result.Entries=_result.Entries.Where(x=>x.Id==_targetId).ToList();
                    p.Run();
                    if(p.Id=="profiles"&&_targetId is not null)foreach(var user in _users.Where(x=>x.Sid!=_owner).ToList()){user.Key?.Dispose();_users.Remove(user);}
                    _stage.Status = _stage.Issues.Count == 0 ? "成功" : "部分成功";
                }
                catch (OperationCanceledException) { _stage.Status = "取消"; _result.Cancelled = true; break; }
                catch (Exception ex) { Issue(ex.Message); _stage.Status = "失败"; }
                _result.Entries = _result.Entries.DistinctBy(x => x.Id).ToList();
                _stage.Count = p.Id == "profiles" ? _users.Count : p.Id == "processes" ? _result.Processes.Count : p.Id == "metadata" ? _result.Entries.Count : _result.Entries.Count - before;
                _stage.Seconds = timer.Elapsed.TotalSeconds;
                progress?.Report(new(p.Name, index + 1, providers.Length, _result.Entries.Count, $"{p.Name}：{_stage.Status}，{_stage.Count} 项"));
            }
        }
        finally
        {
            foreach (var u in _users) u.Key?.Dispose(); _users.Clear();
            _result.Finished = DateTimeOffset.UtcNow;
            _result.Entries = _result.Entries.DistinctBy(x => x.Id).ToList();
        }
        return _result;
    }
    private void Issue(string message) { if (_stage.Issues.Count < 150) _stage.Issues.Add(message); }
    private void Check() => _cancel.ThrowIfCancellationRequested();
    private static string Text(object? value) => value switch { null => "", byte[] b => Convert.ToHexString(b), string[] a => string.Join(" | ", a), _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
    private static string Value(RegistryKey? key, string name) => Text(key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    private static RegistryKey Machine(RegistryView view = RegistryView.Registry64) => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
    private static RegistryKey Users(RegistryView view = RegistryView.Registry64) => RegistryKey.OpenBaseKey(RegistryHive.Users, view);
    private StartupEntry Add(string category, string name, string source, string command = "", string owner = "machine", string view = "64")
    {
        var entry = new StartupEntry { Provider = _stage.Provider, Category = category, Name = name, Source = source,
            Command = command, OwnerSid = owner, View = view };
        entry.Configuration["command"] = command; entry.Seal(); _result.Entries.Add(entry); return entry;
    }
    private void ReadProfiles()
    {
        if (_owner == Contract.Sid)
        {
            var current = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            _users.Add(new(_owner, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), current));
        }
        using var registry = Machine(); using var profiles = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        using var hku = Users();
        var loaded = hku.GetSubKeyNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (profiles is null) { Issue("无法读取 ProfileList。"); return; }
        foreach (var sid in profiles.GetSubKeyNames())
        {
            Check(); if (_users.Any(x => x.Sid == sid)) continue;
            try
            {
                using var profile = profiles.OpenSubKey(sid); var path = Environment.ExpandEnvironmentVariables(Value(profile, "ProfileImagePath"));
                if (loaded.Contains(sid)) _users.Add(new(sid, path, hku.OpenSubKey(sid, false)));
                else if (CommandInspector.LocalPathGap(path) is null&&Directory.Exists(path)) _users.Add(new(sid, path, null));
            }
            catch (Exception ex) { Issue($"账户 {sid}：{ex.Message}"); }
        }
        foreach (var sid in new[] { ".DEFAULT", "S-1-5-18", "S-1-5-19", "S-1-5-20" })
            if (loaded.Contains(sid) && !_users.Any(x => x.Sid == sid))
            { try { _users.Add(new(sid, "", hku.OpenSubKey(sid, false))); } catch (Exception ex) { Issue($"系统账户 {sid}：{ex.Message}"); } }
        if (!_users.Any(x => x.Sid == _owner)) Issue("目标用户 hive 未加载；将读取可访问副本，打包应用状态可能不完整。");
        _stage.Note = string.Join("；", _users.Select(x => $"{x.Sid}：{(x.Key is null ? "未加载" : "已加载只读")}"));
    }
    private void ReadLogin()
    {
        const string baseKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = Machine(view); ReadRunRoot(machine, "HKLM", "machine", view, baseKey);
            foreach (var user in _users.Where(x => x.Key is not null && view == RegistryView.Registry64))
            {
                try { using var users = Users(view); using var root = users.OpenSubKey(user.Sid); if (root is not null) ReadRunRoot(root, "HKU\\" + user.Sid, user.Sid, view, baseKey); }
                catch (Exception ex) { Issue($"{user.Sid}/{view}：{ex.Message}"); }
            }
        }
        foreach (var user in _users.Where(x => x.Key is null && !string.IsNullOrEmpty(x.Path)))
        {
            Check();
            try
            {
                var file = Path.Combine(user.Path, "NTUSER.DAT");
                if (!File.Exists(file)) { Issue($"{user.Sid}：未能确认 NTUSER.DAT 存在，未将其判为空配置。"); continue; }
                var values = OfflineHive.ReadStartup(file);
                foreach (var v in values)
                {
                    var e = Add("其他账户登录启动", v.Name, $"离线副本\\{user.Sid}\\{v.Key}", v.Value, user.Sid);
                    e.OneShot = v.Key.EndsWith("RunOnce", StringComparison.OrdinalIgnoreCase); e.Mode = "配置存在，用户禁用状态未确认";
                    e.Evidence["读取方式"] = "仅解析 hive 副本；未挂载现场，未重放事务日志";
                }
                Issue($"{user.Sid}：已只读解析 NTUSER 副本常用入口；其他状态及 hive 事务一致性未完全验证。");
            }
            catch (Exception ex) { Issue($"{user.Sid} 离线配置：{ex.Message}"); }
        }
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "machine");
        foreach (var user in _users.Where(x => !string.IsNullOrEmpty(x.Path)))
            ReadStartupFolder(Path.Combine(user.Path, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"), user.Sid);
    }
    private void ReadRunRoot(RegistryKey root, string label, string owner, RegistryView view, string baseKey)
    {
        using var approvalRoot=owner=="machine"?Machine(RegistryView.Registry64):null;
        foreach (var suffix in new[] { @"Run", @"RunOnce", @"RunOnceEx", @"Policies\Explorer\Run" })
        {
            Check();
            try
            {
                using var key = root.OpenSubKey(baseKey + "\\" + suffix); if (key is null) continue;
                foreach (var name in key.GetValueNames())
                {
                    var e = Add("普通登录启动", name, $"{label}\\{baseKey}\\{suffix}", Value(key, name), owner, view == RegistryView.Registry64 ? "64" : "32");
                    e.OneShot = suffix.StartsWith("RunOnce"); e.Enabled = ReadApproval(approvalRoot??root, name, view == RegistryView.Registry64 ? "Run" : "Run32");
                    if (e.OneShot) e.Enabled = null;
                    e.Mode = e.OneShot ? "一次性登录配置" : e.Enabled.HasValue ? "登录" : "登录状态未确认";
                    e.Configuration["enabled"] = e.Enabled?.ToString() ?? "unknown";
                    e.Evidence["注册表类型"] = key.GetValueKind(name).ToString();
                }
                if (suffix == "RunOnceEx") foreach (var child in key.GetSubKeyNames())
                    ReadRegistryValues(root, baseKey + "\\" + suffix + "\\" + child, label, owner, "一次性登录配置");
            }
            catch (Exception ex) { Issue($"{label}/{suffix}：{ex.Message}"); }
        }
    }
    private static bool? ReadApproval(RegistryKey root, string name, string bucket)
    {
        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + bucket);
        if (key?.GetValue(name) is not byte[] { Length: >= 4 } data) return null;
        return data[0] switch { 2 or 6 => true, 3 or 7 => false, _ => null };
    }
    private void ReadStartupFolder(string path, string owner)
    {
        try
        {
            if(CommandInspector.LocalPathGap(path) is string gap){Issue(path+"："+gap);return;}
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path))
            {
                Check(); if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var command = file; string working = "";
                if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    Type? type = Type.GetTypeFromProgID("WScript.Shell");
                    if (type is not null)
                    {
                        dynamic shell = Activator.CreateInstance(type)!; dynamic link = shell.CreateShortcut(file);
                        try { command = $"\"{link.TargetPath}\" {link.Arguments}"; working = link.WorkingDirectory; }
                        finally { Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
                    }
                }
                var e = Add("启动文件夹", Path.GetFileName(file), file, command, owner); e.Mode = "登录";
                using var root = owner == "machine" ? Machine() : Users().OpenSubKey(owner);
                e.Enabled = root is null ? null : ReadApproval(root, Path.GetFileName(file), "StartupFolder");
                e.Configuration["enabled"] = e.Enabled?.ToString() ?? "unknown"; e.Evidence["工作目录"] = working;
            }
        }
        catch (Exception ex) { Issue($"启动文件夹 {path}：{ex.Message}"); }
    }
    private void ReadPackaged()
    {
        var manager = new PackageManager();
        foreach (var user in _users.Where(x => x.Sid.StartsWith("S-1-5-21-")))
        {
            try
            {
                foreach (var package in manager.FindPackagesForUser(user.Sid))
                {
                    Check(); if (package.IsFramework || package.IsResourcePackage) continue;
                    try
                    {
                        var xml = SafeXml(File.ReadAllText(Path.Combine(package.InstalledLocation.Path, "AppxManifest.xml")));
                        foreach (var node in xml.Descendants().Where(x => x.Name.LocalName == "StartupTask"))
                        {
                            var id = (string?)node.Attribute("TaskId") ?? "";
                            using var root = Users(); using var state = root.OpenSubKey(user.Sid + @"\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\" + package.Id.FamilyName + "\\" + id);
                            var actual = state?.GetValue("State"); var extension = node.Parent;
                            var executable = (string?)extension?.Attribute("Executable") ?? (string?)extension?.Parent?.Attribute("Executable") ?? "";
                            var e = Add("打包应用启动", id, $"AppX\\{package.Id.FamilyName}\\{id}", Path.Combine(package.InstalledLocation.Path, executable), user.Sid);
                            e.App = package.Id.Name; e.Mode = "登录";
                            e.Enabled = actual is int n ? n switch { 2 or 4 => true, 0 or 1 or 3 => false, _ => null } : null;
                            e.Configuration["enabled"] = e.Enabled?.ToString() ?? "unknown";
                            e.Evidence["实际用户状态"] = actual?.ToString() ?? "未确认";
                            e.Evidence["清单默认值"] = (string?)node.Attribute("Enabled") ?? "未指定";
                            e.Evidence["包发布者"] = package.Id.Publisher;
                            if (e.Enabled is null) Issue($"{package.Id.Name}/{user.Sid}：实际启动状态未确认。");
                        }
                    }
                    catch (Exception ex) { Issue($"包 {package.Id.Name}：{ex.Message}"); }
                }
            }
            catch (Exception ex) { Issue($"账户 {user.Sid} 打包应用：{ex.Message}"); }
        }
    }
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
                else e.State = "未包含在 CIM 运行列表；登记不等于已加载";
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
    private static string RegistryTree(RegistryKey? root, int depth)
    {
        if (root is null || depth < 0) return "";
        var result = new StringBuilder();
        foreach (var n in root.GetValueNames()) result.Append(n).Append('=').Append(Value(root, n)).Append(';');
        foreach (var n in root.GetSubKeyNames()) { using var child = root.OpenSubKey(n); result.Append(n).Append('{').Append(RegistryTree(child, depth - 1)).Append('}'); }
        return result.ToString();
    }
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
                        var command = execute is null ? "" : $"\"{execute.Element(ns + "Command")?.Value}\" {execute.Element(ns + "Arguments")?.Value}";
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
    private static string ResolveClsid(string clsid, string sid)
    {
        if (!Guid.TryParse(clsid, out _)) return "";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
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
                if (Path.IsPathRooted(executable)) return executable;
                var location = Value(package, "PackageRootFolder"); if (location.Length > 0) return Path.Combine(location, executable);
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
    private void ReadExtensions()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = Machine(view);
            const string approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";
            using var key = machine.OpenSubKey(approved);
            if (key is not null) foreach (var name in key.GetValueNames())
            {
                Check(); var e = Add("Explorer 扩展", Value(key, name), "HKLM\\" + approved + "\\" + name, ResolveClsid(name, _owner), view: view.ToString());
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
    private void ReadWmi()
    {
        foreach (var className in new[] { "__EventFilter", "CommandLineEventConsumer", "ActiveScriptEventConsumer", "__FilterToConsumerBinding" })
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
    private void ReadMetadata()
    {
        var previous = _previous?.Entries.GroupBy(x => x.Executable, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        var files = new Dictionary<string, FileFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _result.Entries)
        {
            Check();
            e.Executable = CommandInspector.ResolveExecutable(e.Command, _users.FirstOrDefault(x => x.Sid == e.OwnerSid).Path, e.Evidence.GetValueOrDefault("工作目录", ""));
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
