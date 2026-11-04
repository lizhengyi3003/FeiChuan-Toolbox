using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TunHotspot.Core;

public static class KumiryoConfiguration
{
    internal static readonly AsyncLocal<string?> FixtureRoot = new();
    public static string Root => FixtureRoot.Value ?? Path.Combine(DataPaths.UserRoot, "KumiryoConfiguration");
    public static string DefaultPreferences => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.kumiryo", "Kumiryo", "shared_preferences.json");
    public static KumiryoTarget Discover()
    {
        string? executable = ProcessSecurity.Processes().FirstOrDefault(x => x.Name.Equals("kumiryo.exe", StringComparison.OrdinalIgnoreCase) && x.Path.Length > 0)?.Path;
        if (executable is null)
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var root = RegistryKey.OpenBaseKey(hive, view); using var apps = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (var name in apps?.GetSubKeyNames() ?? [])
                {
                    using var app = apps!.OpenSubKey(name); string label = app?.GetValue("DisplayName") as string ?? "";
                    if (!label.StartsWith("Kumiryo", StringComparison.OrdinalIgnoreCase)) continue;
                    string install = app?.GetValue("InstallLocation") as string ?? "";
                    string candidate = Path.Combine(install, "kumiryo.exe"); if (File.Exists(candidate)) executable ??= candidate;
                }
            }
        executable ??= new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }.Select(x => Path.Combine(Environment.GetFolderPath(x), x == Environment.SpecialFolder.LocalApplicationData ? "Programs\\Kumiryo\\kumiryo.exe" : "Kumiryo\\kumiryo.exe")).FirstOrDefault(File.Exists) ?? "";
        return new() { Preferences = DefaultPreferences, Executable = executable, Version = File.Exists(executable) ? FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "" : "未安装" };
    }
    public static void ValidateTarget(KumiryoTarget target, bool executable = true)
    {
        if (target.Preferences.StartsWith(@"\\", StringComparison.Ordinal) || target.Executable.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("自动配置仅支持本机目录，不对网络共享路径执行读写或启动。");
        if (!Path.IsPathFullyQualified(target.Preferences) || !Path.GetFileName(target.Preferences).Equals("shared_preferences.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 Kumiryo 的 shared_preferences.json。");
        KumiryoPrivateFiles.NoLinks(target.Preferences);
        if (!File.Exists(target.Preferences)) throw new FileNotFoundException("尚无客户端设置。请先安装 Kumiryo，运行并登录一次，再回来检查。");
        _ = KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Read(target.Preferences));
        if (!executable) return;
        if (!Path.IsPathFullyQualified(target.Executable) || !Path.GetFileName(target.Executable).Equals("kumiryo.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target.Executable)) throw new FileNotFoundException("未找到 Kumiryo 程序，可手动选择已有安装目录中的 kumiryo.exe。");
        KumiryoPrivateFiles.NoLinks(target.Executable);
        var version = FileVersionInfo.GetVersionInfo(target.Executable);
        if (version.CompanyName != "com.kumiryo" || !File.Exists(Path.Combine(Path.GetDirectoryName(target.Executable)!, "KumiryoCore.exe"))) throw new InvalidDataException("客户端身份或内核文件不完整，已停止自动执行。");
        target.Version = version.ProductVersion ?? "未知";
    }
    public static KumiryoPreview Preview(KumiryoTarget target, KumiryoTemplate template)
    {
        var result = new KumiryoPreview { Target = target, Rules = (template.Settings["globalAddedRules"] as JsonArray)?.Count ?? 0 };
        try
        {
            KumiryoConfigPlan.Validate(template); ValidateTarget(target);
            KumiryoClient.CheckClientIdentity(target);
            string text = KumiryoPrivateFiles.Read(target.Preferences); var preferences = KumiryoConfigPlan.ReadPreferences(text); var config = KumiryoConfigPlan.Settings(preferences);
            result.FileHash = Contract.Hash(text); result.TemplateHash = KumiryoConfigPlan.TemplateHash(template);
            foreach (var module in KumiryoConfigPlan.Modules)
            {
                if (!config.ContainsKey(module.Key)) result.Blocks.Add("客户端缺少设置模块：" + module.Name + "，请核对客户端版本。");
            }
            result.Differences = KumiryoConfigPlan.Differences(config, template);
            result.Checks = VerifyModules(config, template);
            result.Checks.Add(new("账号、订阅与登录信息", "通过", "保留目标电脑自身的内容，不纳入迁移模板。"));
            result.Checks.Add(new("版本", target.Version == template.SourceVersion ? "通过" : "待核对", $"目标 {target.Version}；模板 {template.SourceVersion}。"));
            if (target.Version != template.SourceVersion) result.Blocks.Add("客户端版本与已验证的模板不同。本版先显示差异和导出，不自动写入未知版本。");
            var cores = KumiryoClient.CoreIds(target);
            foreach (var port in KumiryoClient.DesiredPorts(template))
            {
                var owners = KumiryoClient.ListenerOwners(port.Protocol, port.Port);
                if (owners.Any(x => !cores.Contains(x))) result.Blocks.Add($"{port.Protocol} 端口 {port.Port} 被其他进程占用，请先处理冲突。");
            }
            string? tunDevice = template.Settings["patchClashConfig"]?["tun"]?["device"]?.GetValue<string>();
            if (tunDevice != "Kumiryo") result.Blocks.Add("热点兼容要求 TUN 设备名为 Kumiryo，请核对模板。");
            if ((File.GetAttributes(target.Preferences) & FileAttributes.ReadOnly) != 0) result.Blocks.Add("配置文件具有只读属性，请先核对并解除只读属性。");
            if (!Contract.Elevated)
            {
                using var probe = new FileStream(target.Preferences, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            }
        }
        catch (UnauthorizedAccessException) { result.NeedsAdministrator = true; result.Checks.Add(new("权限", "待授权", "需要管理员核验才能确认客户端身份或配置访问权限。")); }
        catch (Exception ex) { result.Blocks.Add(SafeError(ex)); }
        return result;
    }
    public static List<KumiryoCheck> VerifyModules(JsonObject config, KumiryoTemplate template) => KumiryoConfigPlan.Modules.Select(module =>
    {
        var subset = new KumiryoTemplate { Settings = (JsonObject)template.Settings.DeepClone() };
        var changes = KumiryoConfigPlan.Differences(config, subset).Where(x => x.Path == "/" + module.Key || x.Path.StartsWith("/" + module.Key + "/", StringComparison.Ordinal)).ToList();
        return new KumiryoCheck(module.Name, changes.Count == 0 ? "通过" : "待配置", changes.Count == 0 ? "持久设置与模板一致。" : $"有 {changes.Count} 处差异。");
    }).ToList();
    public static KumiryoTemplate Capture(KumiryoTarget target)
    {
        ValidateTarget(target); return KumiryoConfigPlan.Capture(KumiryoPrivateFiles.Read(target.Preferences), target.Version);
    }
    public static string JobFolder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("操作编号无效。");
        string folder = Path.Combine(Root, "operations", id); KumiryoPrivateFiles.NoLinks(folder); return folder;
    }
    public static KumiryoReport? ReadReport(string id)
    {
        string path = Path.Combine(JobFolder(id), "report.json");
        try { return File.Exists(path) ? Contract.Decode<KumiryoReport>(KumiryoPrivateFiles.Read(path)) : null; }
        catch (IOException) { return null; }
    }
    public static List<KumiryoReport> History()
    {
        string folder = Path.Combine(Root, "operations"); if (!Directory.Exists(folder)) return [];
        var result = new List<KumiryoReport>();
        foreach (string path in Directory.EnumerateDirectories(folder))
        {
            try { if (ReadReport(Path.GetFileName(path)) is KumiryoReport report) result.Add(report); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        }
        return result.OrderByDescending(x => x.Started).Take(30).ToList();
    }
    public static string Create(KumiryoPreview preview, KumiryoTemplate template, string kind = "apply", string restoreId = "", bool allowUiStopFallback = false)
    {
        if (kind is not ("apply" or "restore")) throw new InvalidDataException("操作类型无效。");
        if (!preview.CanApply) throw new InvalidOperationException("预检尚未通过。");
        if (preview.TemplateHash != KumiryoConfigPlan.TemplateHash(template)) throw new InvalidOperationException("模板在预览后发生变化，请重新检查。");
        string? unfinished = History().FirstOrDefault(x => !x.Finished || x.BackupReady && x.WriteStarted && !x.Success && !x.Restored)?.Id;
        if (unfinished is not null) throw new InvalidOperationException("存在未完成操作，请先检查并恢复该操作。");
        return SaveOperation(new() { Kind = kind, RestoreId = restoreId, Target = preview.Target, ExpectedHash = preview.FileHash, Template = template, AllowUiStopFallback = allowUiStopFallback });
    }
    public static string CreateRestore(string id, bool allowUiStopFallback = false)
    {
        var source = ReadOperation(id); var report = ReadReport(id);
        if (report is not { BackupReady: true, Written: true, Restored: false }) throw new InvalidOperationException("此操作没有可恢复的已写入备份。");
        ValidateTarget(source.Target);
        return SaveOperation(new() { Kind = "restore", RestoreId = id, Target = source.Target, Template = source.Template, ExpectedHash = Contract.Hash(KumiryoPrivateFiles.Read(source.Target.Preferences)), AllowUiStopFallback = allowUiStopFallback });
    }
    public static bool WorkerAlive(KumiryoReport report)
    {
        if (report.WorkerPid == 0) return false;
        try { using var process = Process.GetProcessById(report.WorkerPid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == report.WorkerStarted; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try
            {
                using var query = new System.Management.ManagementObjectSearcher($"SELECT Name,CreationDate FROM Win32_Process WHERE ProcessId={report.WorkerPid}");
                query.Options.Timeout = TimeSpan.FromSeconds(2);
                using var rows = query.Get();
                return rows.Cast<System.Management.ManagementBaseObject>().Any(x =>
                    string.Equals(x["Name"] as string, "TunHotspot.Engine.exe", StringComparison.OrdinalIgnoreCase) &&
                    x["CreationDate"] is string created && System.Management.ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime().Ticks / 10 == report.WorkerStarted / 10);
            }
            catch (Exception failure) when (failure is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { return false; }
        }
    }
    public static KumiryoTemplate CurrentTemplate()
    {
        string path = Path.Combine(Root, "template.json");
        var template = File.Exists(path) ? Contract.Decode<KumiryoTemplate>(KumiryoPrivateFiles.Read(path)) : KumiryoConfigPlan.Embedded();
        KumiryoConfigPlan.Validate(template); return template;
    }
    public static void SaveTemplate(KumiryoTemplate template)
    {
        KumiryoConfigPlan.Validate(template); KumiryoPrivateFiles.Directory(Root);
        KumiryoPrivateFiles.Atomic(Path.Combine(Root, "template.json"), Contract.Encode(template));
    }
    public static void CancelUnstarted(string id, bool cancelled = false, bool wrongAccount = false)
    {
        var report = ReadReport(id);
        if (report is null || report.WriteStarted || WorkerAlive(report)) return;
        report.Finished = true; report.Success = false; report.Phase = wrongAccount ? "管理员账户不匹配" : cancelled ? "管理员授权已取消" : "工作组件未启动";
        report.Summary = wrongAccount ? "提权账户与发起账户不同，未读取或修改其他账户数据。请使用同一账户授权，或取消管理员选项后普通执行。" : cancelled ? "Windows 管理员授权已取消，本次未修改客户端配置。" : "工作组件未能启动，本次未修改客户端配置。可修复程序文件后重新预览。";
        report.Events.Add(new(DateTimeOffset.UtcNow, report.Phase, report.Summary)); Save(report);
    }
    internal static void ValidateExecutionTarget(KumiryoTarget target)
    {
        if (Contract.Elevated && !Path.GetFullPath(target.Preferences).Equals(Path.GetFullPath(DefaultPreferences), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("管理员模式只处理当前账户标准 Kumiryo 设置目录；自定义目录请用普通权限执行。");
        ValidateTarget(target);
    }
    public static void SetCaller(string id)
    {
        var op = ReadOperation(id); op.CallerPid = Environment.ProcessId; op.CallerStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        KumiryoPrivateFiles.Atomic(Path.Combine(JobFolder(id), "request.json"), Contract.Encode(op));
    }
    public static string CreateAdministratorPreview(KumiryoTarget target, KumiryoTemplate template)
    {
        if (!Path.GetFullPath(target.Preferences).Equals(Path.GetFullPath(DefaultPreferences), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("管理员核验仅支持当前账户标准设置目录，自定义目录请使用普通核验。");
        KumiryoConfigPlan.Validate(template); var op = new KumiryoOperation { Kind = "preview", Target = target, Template = template };
        KumiryoPrivateFiles.Directory(Root); string parent = Path.Combine(Root, "previews"); KumiryoPrivateFiles.Directory(parent);
        string folder = Path.Combine(parent, op.Id); KumiryoPrivateFiles.Directory(folder);
        KumiryoPrivateFiles.Atomic(Path.Combine(folder, "request.json"), Contract.Encode(op)); return op.Id;
    }
    public static int RunAdministratorPreview(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("核验编号无效。");
        string folder = Path.Combine(Root, "previews", id); KumiryoPrivateFiles.NoLinks(folder);
        var op = Contract.Decode<KumiryoOperation>(KumiryoPrivateFiles.Read(Path.Combine(folder, "request.json")));
        if (op.OwnerSid != Contract.Sid || op.DeviceId != Contract.DeviceId || op.Id != id || op.Kind != "preview") throw new InvalidDataException("核验请求不属于当前账户或本机。");
        KumiryoPreview preview;
        try { ValidateExecutionTarget(op.Target); preview = Preview(op.Target, op.Template); }
        catch (Exception ex) { preview = new() { Target = op.Target, Blocks = [SafeError(ex)] }; }
        if (preview.NeedsAdministrator) { preview.NeedsAdministrator = false; preview.Blocks.Add("管理员核验仍无法取得所需访问权限，未执行注入。"); }
        KumiryoPrivateFiles.Atomic(Path.Combine(folder, "response.json"), Contract.Encode(preview)); return 0;
    }
    public static KumiryoPreview ReadAdministratorPreview(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("核验编号无效。");
        return Contract.Decode<KumiryoPreview>(KumiryoPrivateFiles.Read(Path.Combine(Root, "previews", id, "response.json")));
    }
    internal static string SaveOperation(KumiryoOperation operation)
    {
        string folder = JobFolder(operation.Id); KumiryoPrivateFiles.Directory(Root); KumiryoPrivateFiles.Directory(Path.GetDirectoryName(folder)!); KumiryoPrivateFiles.Directory(folder);
        KumiryoPrivateFiles.Atomic(Path.Combine(folder, "request.json"), Contract.Encode(operation));
        Save(new() { Id = operation.Id, Phase = "等待执行", Summary = "即将核对当前文件并建立加密备份。" }); return operation.Id;
    }
    internal static KumiryoOperation ReadOperation(string id)
    {
        var operation = Contract.Decode<KumiryoOperation>(KumiryoPrivateFiles.Read(Path.Combine(JobFolder(id), "request.json")));
        if (operation.Id != id || operation.OwnerSid != Contract.Sid || operation.DeviceId != Contract.DeviceId || operation.Kind is not ("apply" or "restore")) throw new InvalidDataException("操作不属于当前账户或本机。");
        KumiryoConfigPlan.Validate(operation.Template); return operation;
    }
    internal static void Save(KumiryoReport report)
    {
        report.Updated = DateTimeOffset.UtcNow;
        KumiryoPrivateFiles.Atomic(Path.Combine(JobFolder(report.Id), "report.json"), Contract.Encode(report));
    }
    public static string SafeError(Exception error) => error switch
    {
        InvalidDataException or InvalidOperationException or FileNotFoundException => error.Message,
        UnauthorizedAccessException => "权限不足，已停止处理。请核对目录权限及 Kumiryo 是否以管理员运行。",
        System.ComponentModel.Win32Exception native => $"Windows 操作未完成（错误 {native.NativeErrorCode}，0x{native.NativeErrorCode:X8}），请查看本次步骤和恢复记录。",
        System.Text.Json.JsonException => "JSON 格式错误，未覆盖原配置。",
        _ => $"操作未完成（{error.GetType().Name}，0x{error.HResult:X8}），请查看本次步骤和恢复记录。"
    };
}
