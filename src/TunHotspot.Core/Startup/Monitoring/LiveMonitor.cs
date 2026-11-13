using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;
using System.Diagnostics;
using System.Threading.Channels;

namespace TunHotspot.Core;

public sealed class LiveMonitor : IDisposable
{
    private readonly ToolboxStore _store;
    private readonly string _owner;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly Channel<ObservedEvent> _events = Channel.CreateBounded<ObservedEvent>(new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.Wait });
    private readonly List<FileSystemWatcher> _folders = [];
    private readonly List<RegistryChangeWatch> _registryWatches=[];
    private readonly object _watchGate=new();
    private readonly object _lifecycleGate = new();
    private readonly List<Task> _tasks = [];
    private Task? _shutdown;
    private volatile bool _stopping;
    private TraceEventSession? _etw;
    private CancellationTokenSource? _activeScan;
    private int _scanning;
    private long _etwLost;
    private long _registryHints;
    private long _dropped;
    private long _lastChange;
    private long _lastScan;
    private string _registryHash = "";
    public string State { get; private set; } = "未启动";
    public ScanProgress? CurrentProgress { get; private set; }
    public bool Scanning => Volatile.Read(ref _scanning) != 0;
    public bool LastScanCancelled { get; private set; }
    public void CancelScan() { try { _activeScan?.Cancel(); } catch(ObjectDisposedException) { } }
    private volatile bool _paused;
    public bool Paused
    {
        get=>_paused;
        set
        {
            if (_stopping || _paused == value) return;
            _paused = value;
            Enqueue(new() { Kind = value ? "监测暂停" : "监测恢复", Name = "本软件采集", Detail = "手动扫描仍可使用。" });
            if (!value) Changed();
        }
    }
    public event Action<ScanSnapshot>? SnapshotReady;
    public event Action<ScanProgress>? Progress;
    public LiveMonitor(ToolboxStore store, string owner) { _store = store; _owner = owner; }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_stopping || _tasks.Count != 0) return;
            State = "配置变化监测启动；进程事件准备中";
            _tasks.Add(Task.Run(RecordEvents));
            _tasks.Add(Task.Run(ObserveEtw));
            _tasks.Add(Task.Run(Loop));
            _tasks.Add(Task.Run(WatchStartupFolders));
        }
    }

    private void WatchStartupFolders()
    {
        try
        {
            using var m = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = m.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            var paths = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) };
            if (root is not null) foreach (var sid in root.GetSubKeyNames())
            { using var k = root.OpenSubKey(sid); var p = k?.GetValue("ProfileImagePath") as string; if (p is not null) paths.Add(Path.Combine(Environment.ExpandEnvironmentVariables(p), @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup")); }
            foreach (var path in paths)
            {
                if (_stopping) break;
                FileSystemWatcher? watcher = null;
                try
                {
                    if(CommandInspector.LocalPathGap(path) is not null||!Directory.Exists(path))continue;
                    watcher = new FileSystemWatcher(path) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
                    watcher.Changed += (_, _) => Changed(); watcher.Created += (_, _) => Changed(); watcher.Deleted += (_, _) => Changed(); watcher.Renamed += (_, _) => Changed();
                    watcher.Error += (_, _) => { Changed(); Publish(new() { Kind = "监测缺口", Name = "启动目录通知", Detail = "目录通知发生错误，安排补扫。" }); };
                    watcher.EnableRaisingEvents = true;
                    _folders.Add(watcher);
                    watcher = null;
                }
                catch (Exception ex) { Publish(new() { Kind = "监测缺口", Name = path, Detail = ex.Message }); }
                finally { watcher?.Dispose(); }
            }
        }
        catch (Exception ex) { Publish(new() { Kind = "监测缺口", Name = "目录监测", Detail = ex.Message }); }
    }
    private void Changed() => Interlocked.Exchange(ref _lastChange,Stopwatch.GetTimestamp());
    public void Reconcile(string reason){Changed();Publish(new(){Kind="监测恢复核对",Name=reason,Detail="将重新核对当前配置；不推定这是一次完整内核重启。"});}
    public async Task<ScanSnapshot?> ScanNow(CancellationToken token = default)
    {
        if (_stopping) return null;
        if (!await _scanGate.WaitAsync(0, token)) return null;
        try
        {
            if (_stopping) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            _activeScan=linked;LastScanCancelled=false;Volatile.Write(ref _scanning,1);
            var progress = new DirectProgress<ScanProgress>(p => { CurrentProgress=p;Progress?.Invoke(p); });
            var snapshot = await Task.Run(() => new WindowsScanner(_owner, _store.Latest).Scan(progress, linked.Token), linked.Token);
            LastScanCancelled=snapshot.Cancelled;
            _store.SaveSnapshot(snapshot);RefreshRegistryWatches(); Interlocked.Exchange(ref _lastScan,Stopwatch.GetTimestamp()); SnapshotReady?.Invoke(snapshot); return snapshot;
        }
        catch(OperationCanceledException){LastScanCancelled=true;return null;}
        finally { _activeScan=null;Volatile.Write(ref _scanning,0);_scanGate.Release(); }
    }
    private void RefreshRegistryWatches()
    {
        lock(_watchGate)
        {
            foreach(var watcher in _registryWatches)watcher.Dispose();_registryWatches.Clear();
            if(_stop.IsCancellationRequested)return;
            foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
            {
                Add(RegistryHive.LocalMachine,view,@"SOFTWARE\Microsoft\Windows\CurrentVersion",false,1);
                foreach(var path in new[]{@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved",@"SYSTEM\CurrentControlSet\Services"})Add(RegistryHive.LocalMachine,view,path);
                foreach(var path in new[]{@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options"})Add(RegistryHive.LocalMachine,view,path,true,5,false);
            }
            try
            {
                using var users=RegistryKey.OpenBaseKey(RegistryHive.Users,RegistryView.Registry64);
                foreach(string sid in users.GetSubKeyNames().Where(x=>x.StartsWith("S-1-5-")&&!x.EndsWith("_Classes")))
                {
                    string path=sid+@"\Software\Microsoft\Windows\CurrentVersion";
                    Add(RegistryHive.Users,RegistryView.Registry64,path,false,1);
                    foreach(string suffix in new[]{@"\Run",@"\RunOnce",@"\Explorer\StartupApproved"})Add(RegistryHive.Users,RegistryView.Registry64,path+suffix);
                }
            }
            catch(UnauthorizedAccessException){ }
        }
        void Add(RegistryHive hive,RegistryView view,string path,bool subtree=true,uint filter=5,bool verifySample=true)
        {
            try
            {
                using var root=RegistryKey.OpenBaseKey(hive,view);var key=root.OpenSubKey(path);if(key is null)return;
                _registryWatches.Add(new(key,error=>{Interlocked.Increment(ref _registryHints);if(!verifySample||error is not null)Changed();if(error is not null)Publish(new(){Kind="监测缺口",Name="注册表变化通知",Detail=error});},subtree,filter));
            }
            catch(System.Security.SecurityException){ }
            catch(UnauthorizedAccessException){ }
            catch(Exception ex){Publish(new(){Kind="监测缺口",Name="注册表变化通知",Detail=ex.Message});}
        }
    }
    private async Task Loop()
    {
        try
        {
            await ScanNow(_stop.Token);
            var nextMaintenance = DateTimeOffset.UtcNow.AddHours(1);
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token);
                var now = DateTimeOffset.UtcNow;
                if(_etw is { } session)
                {
                    try { long lost=session.EventsLost;if(lost>_etwLost){Publish(new(){Kind="监测缺口",Name="ETW 丢失事件",Detail=$"系统报告累计丢失 {lost} 个事件；期间运行关系不保证完整。"});_etwLost=lost;Changed();} } catch(ObjectDisposedException) { }
                }
                _store.Set("monitorHeartbeat", new { time = now, state = State, privilege = Contract.Privilege, paused = Paused, dropped = Interlocked.Read(ref _dropped), etwLost=_etwLost, registryHints=Interlocked.Read(ref _registryHints) });
                if (!Paused)
                {
                    string hash = RegistryStamp(); if (_registryHash.Length > 0 && hash != _registryHash) Changed(); _registryHash = hash;
                    var elapsed=Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastScan));
                    if (elapsed > TimeSpan.FromMinutes(5) || (Interlocked.Read(ref _lastChange) > Interlocked.Read(ref _lastScan) && elapsed > TimeSpan.FromSeconds(10))) await ScanNow(_stop.Token);
                }
                if (now > nextMaintenance) { _store.Maintain(); nextMaintenance = now.AddHours(1); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { State = "监测中断：" + ex.Message;new DiagnosticLog(_store.Root).Record("启动管理监测中断",ex); try { _store.Set("monitorFailure", State); } catch { } }
    }
    private string RegistryStamp()
    {
        var values = new System.Text.StringBuilder();
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run" }) Append(machine, path);
            foreach (string sid in users.GetSubKeyNames().Where(x => x.StartsWith("S-1-5-") && !x.EndsWith("_Classes")).Order(StringComparer.OrdinalIgnoreCase))
                foreach (var suffix in new[] { @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", @"\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", @"\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" }) Append(users, sid + suffix);
            using var services = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is not null) foreach (string name in services.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase))
            { using var s = services.OpenSubKey(name); values.Append(name).Append(':').Append(s?.GetValue("Start")).Append(':').Append(s?.GetValue("DelayedAutoStart")).Append(':').Append(s?.GetValue("ImagePath")).Append(';'); }
        }
        catch (Exception) { /* The full collector reports permission gaps; this is a bounded change hint. */ }
        return Contract.Hash(values.ToString());
        void Append(RegistryKey root, string path)
        {
            try { using var key = root.OpenSubKey(path); if (key is null) return; values.Append(path); foreach (string name in key.GetValueNames().Order()) { var v = key.GetValue(name); values.Append(name).Append('=').Append(v is byte[] b ? Convert.ToHexString(b) : v).Append(';'); } }
            catch (UnauthorizedAccessException) { }
        }
    }
    private void ObserveEtw()
    {
        if (!Contract.Elevated)
        {
            State = "普通权限配置监测；进程事件需管理员/开机采集组件";
            Publish(new() { Kind = "监测范围", Name = "普通权限", Detail = State }); return;
        }
        try
        {
            using var session = new TraceEventSession("TunHotspot.Processes." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N"),TraceEventSessionOptions.NoRestartOnCreate);
            session.StopOnDispose = true;
            // Source and EventsLost can start a session. Enable the kernel first, before exposing it to the polling loop.
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            session.Source.Kernel.ProcessStart += p => Publish(new() { Time = p.TimeStamp.ToUniversalTime(), Kind = "进程创建", Pid = p.ProcessID, ParentPid = p.ParentID,
                Name = p.ProcessName, Path = p.ImageFileName, Detail = p.CommandLine ?? "", Source = "ETW Kernel Process", Association = "父进程仅为关系证据；具体启动入口需关联核实" });
            session.Source.Kernel.ProcessStop += p => Publish(new() { Time = p.TimeStamp.ToUniversalTime(), Kind = "进程退出", Pid = p.ProcessID, ParentPid = p.ParentID, Name = p.ProcessName, Source = "ETW Kernel Process" });
            session.Source.Dynamic.All += p =>
            {
                if (p.ProviderName != "Microsoft-Windows-TaskScheduler") return;
                string task = ""; try { task = Convert.ToString(p.PayloadByName("TaskName")) ?? ""; } catch { }
                Publish(new() { Time = p.TimeStamp.ToUniversalTime(), Kind = "任务事件", Pid = p.ProcessID, Name = task.Length > 0 ? task : p.EventName, Source = p.ProviderName,
                    Detail = p.ToString(), Association = "任务调度 ETW 事件；事件 PID 可能为宿主，未直接认定为目标 PID" }); Changed();
            };
            try { session.EnableProvider("Microsoft-Windows-TaskScheduler", TraceEventLevel.Informational); } catch (Exception ex) { Publish(new() { Kind = "监测缺口", Name = "任务 ETW", Detail = ex.Message }); }
            _etw = session;State = Contract.Privilege+" ETW 进程与配置监测运行中";
            using var registration = _stop.Token.Register(() => { try { session.Source.StopProcessing(); } catch { } });
            if (!_stopping) session.Source.Process();
        }
        catch (Exception ex) { State = "配置监测运行；ETW 不可用：" + ex.Message; Publish(new() { Kind = "监测缺口", Name = "ETW", Detail = State }); }
        finally { _etw = null; }
    }
    private void Publish(ObservedEvent entry)
    {
        if (_stopping || Paused) return;
        Enqueue(entry);
    }

    private void Enqueue(ObservedEvent entry)
    {
        if (!_events.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
    }
    private async Task RecordEvents()
    {
        try
        {
            while (await _events.Reader.WaitToReadAsync())
            {
                var batch = new List<ObservedEvent>();
                while (batch.Count < 300 && _events.Reader.TryRead(out var item)) batch.Add(item);
                _store.AddEvents(batch);
                foreach(var gap in batch.Where(x=>x.Kind=="监测缺口"))
                {
                    string key=Contract.Hash("monitor|"+gap.Name);
                    var alert=_store.Alerts.FirstOrDefault(x=>x.EntryId==key&&!x.Resolved&&x.Actual==gap.Detail);
                    if(alert is null)alert=new(){EntryId=key,Title="监测覆盖需要核实："+gap.Name,Expected="采集链路可用",Actual=gap.Detail,Evidence="监测故障记录，不表示其他软件修改了启动配置。"};else{alert.LastSeen=DateTimeOffset.UtcNow;alert.Count++;}
                    _store.SaveAlert(alert);
                }
                if (!_stopping) await Task.Delay(250);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { State = "运行事件保存失败：" + ex.Message;new DiagnosticLog(_store.Root).Record("启动管理运行事件保存失败",ex); }
    }
    public Task StopAsync()
    {
        lock (_lifecycleGate)
        {
            if (_shutdown is not null) return _shutdown;
            _stopping = true;
            return _shutdown = Task.Run(StopCore);
        }
    }

    private async Task StopCore()
    {
        _stop.Cancel();
        _events.Writer.TryComplete();
        try { await Task.WhenAll(_tasks).ConfigureAwait(false); }
        catch (Exception ex) { new DiagnosticLog(_store.Root).Record("停止启动管理监测", ex); }
        // Service-initiated scans can run outside the monitor's own task list.
        await _scanGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var watcher in _folders) watcher.Dispose();
            _folders.Clear();
            lock (_watchGate)
            {
                foreach (var watcher in _registryWatches) watcher.Dispose();
                _registryWatches.Clear();
            }
            _stop.Dispose();
            State = "已停止";
        }
        finally { _scanGate.Release(); }
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}

public sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
{ public void Report(T value) => handler(value); }
