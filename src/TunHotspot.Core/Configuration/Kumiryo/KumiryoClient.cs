using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace TunHotspot.Core;

public interface IKumiryoClient
{
    bool Running(KumiryoTarget target);
    bool Connected(KumiryoTarget target);
    Task Stop(KumiryoTarget target);
    Task Start(KumiryoTarget target);
    List<KumiryoCheck> Runtime(KumiryoTarget target, KumiryoTemplate template);
}

public sealed class KumiryoClient : IKumiryoClient, IDisposable
{
    private readonly KumiryoOrdinaryLauncher? _launcher;
    private readonly KumiryoOperation? _operation;
    private readonly List<KumiryoEvent> _exitEvents = [];
    public IReadOnlyList<KumiryoEvent> ExitEvents => _exitEvents;
    public bool LastStopUsedFallback { get; private set; }
    public string LastExitMethod { get; private set; } = "尚未执行";
    public KumiryoClient() { }
    internal KumiryoClient(KumiryoOperation operation)
    { _operation = operation; if (Contract.Elevated) _launcher = new(operation.CallerPid, operation.CallerStarted); }
    internal static void CheckClientIdentity(KumiryoTarget target)
    {
        foreach (var record in ProcessSecurity.Processes().Where(x => x.Name.Equals("kumiryo.exe", StringComparison.OrdinalIgnoreCase)))
        {
            if (record.Path.Length == 0 || record.Created is null) throw new UnauthorizedAccessException("有 Kumiryo 客户端进程的身份受权限限制，需要管理员核验。");
            if (!record.Path.Equals(target.Executable, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("另一安装目录的 Kumiryo 客户端正在运行，请先退出多余实例。");
        }
    }
    private static List<ProcessRecord> Clients(KumiryoTarget target) => ProcessSecurity.Processes().Where(x =>
        x.Path.Equals(target.Executable, StringComparison.OrdinalIgnoreCase) && x.Created is not null).ToList();
    public bool Running(KumiryoTarget target) => Clients(target).Count > 0;
    public bool Connected(KumiryoTarget target) => CoreIds(target).Count > 0 && NetworkInterface.GetAllNetworkInterfaces().Any(x => x.Name.Equals("Kumiryo", StringComparison.OrdinalIgnoreCase) && x.OperationalStatus == OperationalStatus.Up);
    internal static HashSet<int> CoreIds(KumiryoTarget target)
    {
        string core = Path.Combine(Path.GetDirectoryName(target.Executable)!, "KumiryoCore.exe");
        var result = ProcessSecurity.Processes().Where(x => x.Path.Equals(core, StringComparison.OrdinalIgnoreCase)).Select(x => x.Pid).ToHashSet();
        if (result.Count > 0) return result;
        // A SYSTEM core can deny even limited image queries. Cross-check the protected service
        // configuration, live SCM PID, child identity and creation order instead of guessing by name.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\KumiryoHelperService");
            string image = key?.GetValue("ImagePath") as string ?? "";
            string helper = Path.Combine(Path.GetDirectoryName(target.Executable)!, "KumiryoHelperService.exe");
            if (!CommandInspector.ResolveExecutable(image).Equals(helper, StringComparison.OrdinalIgnoreCase)) return result;
            uint servicePid = ProcessSecurity.ServicePid("KumiryoHelperService"); if (servicePid == 0) return result;
            using var query = new System.Management.ManagementObjectSearcher($"SELECT ProcessId,ParentProcessId,CreationDate,Name FROM Win32_Process WHERE ProcessId={servicePid} OR Name='KumiryoCore.exe'");
            using var rows = query.Get();
            var records = rows.Cast<System.Management.ManagementBaseObject>().Select(x => new
            {
                Pid = Convert.ToInt32(x["ProcessId"]), Parent = Convert.ToUInt32(x["ParentProcessId"]), Name = x["Name"] as string ?? "",
                Created = x["CreationDate"] is string date ? System.Management.ManagementDateTimeConverter.ToDateTime(date).ToUniversalTime() : DateTime.MaxValue
            }).ToList();
            var service = records.FirstOrDefault(x => x.Pid == servicePid && x.Name.Equals("KumiryoHelperService.exe", StringComparison.OrdinalIgnoreCase));
            if (service is not null)
                foreach (var child in records.Where(x => x.Parent == servicePid && x.Name.Equals("KumiryoCore.exe", StringComparison.OrdinalIgnoreCase) && x.Created != DateTime.MaxValue && x.Created >= service.Created)) result.Add(child.Pid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Management.ManagementException or System.ComponentModel.Win32Exception) { }
        return result;
    }
    private void ExitEvent(string detail) => _exitEvents.Add(new(DateTimeOffset.UtcNow, "客户端退出核验", detail));
    public async Task Stop(KumiryoTarget target)
    {
        _exitEvents.Clear(); LastStopUsedFallback = false; LastExitMethod = "未运行，无需退出";
        CheckClientIdentity(target);
        var clients = Clients(target);
        if (clients.Count > 1) throw new InvalidOperationException("检测到多个 Kumiryo 客户端，请先退出多余实例。");
        foreach (var record in clients)
        {
            using var process = Process.GetProcessById(record.Pid);
            if (process.SessionId != Process.GetCurrentProcess().SessionId || ProcessSecurity.UserSid(record.Pid) != Contract.Sid || process.StartTime.ToUniversalTime() != record.Created!.Value.UtcDateTime)
                throw new UnauthorizedAccessException("客户端账户、会话或创建时间未通过核验，未处理进程。");
            var settings = KumiryoConfigPlan.Settings(KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Read(target.Preferences)));
            bool toTray = settings["appSettingProps"]?["minimizeOnExit"]?.GetValue<bool>() == true;
            bool allowed = _operation?.AllowUiStopFallback == true;
            ExitEvent($"PID {record.Pid}；路径 {record.Path}；会话 {process.SessionId}；关闭收起托盘={toTray}；界面结束兜底授权={allowed}。");
            LastExitMethod = "等待客户端正常退出";
            if (!toTray)
            {
                nint window = 0;
                EnumWindows((candidate, _) =>
                {
                    GetWindowThreadProcessId(candidate, out uint pid);
                    var name = new StringBuilder(128); GetClassName(candidate, name, name.Capacity);
                    if (pid != record.Pid || !name.ToString().Contains("FLUTTER", StringComparison.OrdinalIgnoreCase)) return true;
                    window = candidate; return false;
                }, 0);
                if (window != 0)
                {
                    bool posted = PostMessage(window, 0x0010, 0, 0);
                    int error = posted ? 0 : Marshal.GetLastWin32Error();
                    ExitEvent($"窗口关闭请求：句柄 {window}，成功={posted}，Windows 错误 {error}。");
                    if (!posted && error == 5) throw new UnauthorizedAccessException("Windows 拒绝客户端关闭请求，需要同账户管理员授权；未写入配置。");
                    if (posted && await WaitForExit(process, TimeSpan.FromSeconds(8)))
                    {
                        LastExitMethod = "窗口正常关闭";
                        ExitEvent($"客户端已结束，退出码 0x{unchecked((uint)process.ExitCode):X8}。");
                        if (process.ExitCode != 0) throw new InvalidOperationException($"客户端关闭时异常退出（0x{unchecked((uint)process.ExitCode):X8}）；原配置及初始备份保留，未注入设置。");
                        continue;
                    }
                }
            }
            else ExitEvent("客户端设置为关闭后收起托盘，不发送无效关闭请求，也不发送 WM_QUIT。");
            if (!allowed)
                throw new InvalidOperationException("客户端仍在运行或收起在托盘，本次未写入配置。请从 Kumiryo 托盘退出后重试，或在变更预览中允许“仅结束客户端界面”的兜底；此情况不等同于权限不足。");
            var operation = _operation!;
            var report = KumiryoConfiguration.ReadReport(operation.Id);
            if (report is not { InitialBackupReady: true } && report is not { BackupReady: true })
                throw new InvalidOperationException("尚无已校验的原件备份，拒绝结束客户端界面。");
            string initial = KumiryoPrivateFiles.Unprotect(Path.Combine(KumiryoConfiguration.JobFolder(operation.Id), "initial.dpapi"));
            if (Contract.Hash(initial) != operation.ExpectedHash) throw new IOException("初始备份与本次预览不一致，拒绝结束客户端界面。");
            string stable = KumiryoPrivateFiles.Read(target.Preferences); _ = KumiryoConfigPlan.ReadPreferences(stable);
            await Task.Delay(500);
            if (Contract.Hash(KumiryoPrivateFiles.Read(target.Preferences)) != Contract.Hash(stable))
                throw new InvalidOperationException("客户端仍在保存设置，未结束界面；请稍后重新预览。");
            // Hold and validate the exact UI process handle before terminating it.
            // Never use a name-wide kill, a process-tree kill or service control.
            if (process.HasExited) continue;
            _ = process.Handle;
            if (!process.MainModule!.FileName.Equals(target.Executable, StringComparison.OrdinalIgnoreCase) || process.StartTime.ToUniversalTime() != record.Created!.Value.UtcDateTime || ProcessSecurity.UserSid(process.Id) != Contract.Sid)
                throw new InvalidOperationException("客户端身份发生变化，已停止界面结束兜底。");
            LastExitMethod = "仅结束已核实的客户端界面";
            ExitEvent($"原件备份及设置稳定性通过，仅结束 PID {process.Id}；不结束子进程，不调用内核或服务停止接口。");
            process.Kill();
            if (!await WaitForExit(process, TimeSpan.FromSeconds(5))) throw new IOException("已核实的客户端界面结束超时，未写入配置。");
            LastStopUsedFallback = true;
            ExitEvent("界面进程结束已确认，可以重新读取配置并建立写入恢复备份。");
        }
    }
    private static async Task<bool> WaitForExit(Process process, TimeSpan timeout)
    {
        using var stop = new CancellationTokenSource(timeout);
        try { await process.WaitForExitAsync(stop.Token); return true; }
        catch (OperationCanceledException) { return process.HasExited; }
    }
    public async Task Start(KumiryoTarget target)
    {
        if (Running(target)) return;
        if (Contract.Elevated)
        {
            if (_launcher is null) throw new InvalidOperationException("缺少原账户普通权限启动令牌，请从普通界面发起操作。");
            _launcher.Start(target.Executable);
        }
        else
        {
            using var process = Process.Start(new ProcessStartInfo(target.Executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target.Executable)! }) ?? throw new IOException("无法重新打开 Kumiryo。");
        }
        await Task.Delay(1800);
        if (!Running(target)) throw new IOException("Kumiryo 重新打开后立即退出。");
        if (Clients(target).Any(x => KumiryoOrdinaryLauncher.IsElevated(x.Pid))) throw new InvalidOperationException("客户端重新打开后权限异常，未标记成功。");
    }
    public List<KumiryoCheck> Runtime(KumiryoTarget target, KumiryoTemplate template)
    {
        var checks = new List<KumiryoCheck>(); var cores = CoreIds(target);
        checks.Add(new("客户端", Running(target) ? "通过" : "待核对", Running(target) ? "Kumiryo 已运行。" : "Kumiryo 尚未运行。"));
        checks.Add(new("代理内核", cores.Count > 0 ? "通过" : "待核对", cores.Count > 0 ? "内核映像路径或服务路径、子进程及端口归属交叉核对通过。" : "未连接时不会自动将“已写入”标记为“已生效”。"));
        bool wantTun = template.Settings["patchClashConfig"]?["tun"]?["enable"]?.GetValue<bool>() == true;
        uint helperPid = 0; try { helperPid = ProcessSecurity.ServicePid("KumiryoHelperService"); } catch (System.ComponentModel.Win32Exception) { }
        checks.Add(new("Kumiryo 辅助服务", !wantTun ? "不适用" : helperPid > 0 ? "通过" : "待核对", !wantTun ? "模板未要求 TUN。" : helperPid > 0 ? "官方辅助服务正在运行。" : "请在 Kumiryo 设置中检查辅助服务的安装与运行状态。"));
        bool tun = NetworkInterface.GetAllNetworkInterfaces().Any(x => x.Name.Equals("Kumiryo", StringComparison.OrdinalIgnoreCase) && x.OperationalStatus == OperationalStatus.Up);
        checks.Add(new("TUN 网卡", !wantTun ? "不适用" : tun ? "通过" : "待核对", !wantTun ? "模板未要求启用 TUN。" : tun ? "Kumiryo 网卡已启用。" : "尚未确认 TUN 网卡；连接客户端后可再次核验。"));
        foreach (var port in DesiredPorts(template))
        {
            var owners = ListenerOwners(port.Protocol, port.Port);
            bool own = owners.Any(cores.Contains);
            checks.Add(new($"{port.Protocol} {port.Port}", own ? "通过" : owners.Count > 0 ? "未通过" : "待核对", own ? "监听进程属于 Kumiryo 内核。" : owners.Count > 0 ? "监听被其他进程占用。" : "尚未发现内核监听。"));
        }
        checks.Add(new("内核逐字段读回", "待核对", "当前客户端未提供已验证的完整设置读取接口；不能仅凭文件一致声明全部内核设置已经生效。"));
        checks.Add(new("系统集成", "待核对", "已迁移应用设置；开机启动登记、系统代理及 DNS 的系统副作用需结合客户端连接状态核对。"));
        return checks;
    }
    internal static List<(string Protocol, int Port)> DesiredPorts(KumiryoTemplate template)
    {
        var result = new List<(string, int)>();
        foreach (var key in new[] { "mixed-port", "socks-port", "port" })
            if (template.Settings["patchClashConfig"]?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out int port) && port > 0 && port <= 65535) result.Add(("TCP", port));
        if (template.Settings["patchClashConfig"]?["dns"]?["listen"] is System.Text.Json.Nodes.JsonValue listen && listen.TryGetValue<string>(out var endpoint) && int.TryParse(endpoint.Split(':').Last(), out int dns) && dns > 0 && dns <= 65535) result.Add(("UDP", dns));
        return result.Distinct().ToList();
    }
    internal static HashSet<int> ListenerOwners(string protocol, int port)
    {
        var result = new HashSet<int>();
        foreach (int family in new[] { 2, 23 })
        {
            int size = 0; uint error = protocol == "TCP" ? GetExtendedTcpTable(0, ref size, false, family, 3, 0) : GetExtendedUdpTable(0, ref size, false, family, 1, 0);
            if (error is not (0 or 122)) throw new IOException("无法读取端口监听信息。");
            nint memory = Marshal.AllocHGlobal(size);
            try
            {
                error = protocol == "TCP" ? GetExtendedTcpTable(memory, ref size, false, family, 3, 0) : GetExtendedUdpTable(memory, ref size, false, family, 1, 0);
                if (error != 0) throw new IOException("端口监听表读取失败。");
                int count = Marshal.ReadInt32(memory), stride = protocol == "TCP" ? family == 2 ? 24 : 56 : family == 2 ? 12 : 28;
                int portOffset = protocol == "TCP" ? family == 2 ? 8 : 20 : family == 2 ? 4 : 20;
                int ownerOffset = protocol == "TCP" ? family == 2 ? 20 : 52 : family == 2 ? 8 : 24;
                if (count < 0 || 4L + (long)count * stride > size) throw new IOException("端口监听表长度无效。");
                for (int i = 0; i < count; i++)
                {
                    nint row = memory + 4 + i * stride; int raw = Marshal.ReadInt32(row + portOffset);
                    int value = ((raw & 255) << 8) | ((raw >> 8) & 255);
                    if (value == port) result.Add(Marshal.ReadInt32(row + ownerOffset));
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        return result;
    }
    private delegate bool EnumProc(nint window, nint value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder value, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(nint window, uint message, nint w, nint l);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool order, int family, int tableClass, int reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(nint table, ref int size, bool order, int family, int tableClass, int reserved);
    public void Dispose() => _launcher?.Dispose();
}
