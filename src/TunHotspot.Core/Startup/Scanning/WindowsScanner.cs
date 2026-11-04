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
}
