using System.Diagnostics;
using System.Text.Json.Nodes;

namespace TunHotspot.Core;

public static class KumiryoConfigWorker
{
    public static async Task<int> Run(string id)
    {
        var op = KumiryoConfiguration.ReadOperation(id); using var client = new KumiryoClient(op);
        return await new KumiryoJobRunner(client, KumiryoConfiguration.ValidateExecutionTarget).Run(id);
    }
    public static async Task<int> Recover(string id)
    {
        var op = KumiryoConfiguration.ReadOperation(id); using var client = new KumiryoClient(op);
        return await new KumiryoJobRunner(client, KumiryoConfiguration.ValidateExecutionTarget).Recover(id);
    }
    public static async Task<int> Watch(string id, int workerId)
    {
        var op = KumiryoConfiguration.ReadOperation(id); using var client = new KumiryoClient(op);
        using var worker = Process.GetProcessById(workerId);
        if (workerId == Environment.ProcessId || worker.SessionId != Process.GetCurrentProcess().SessionId || ProcessSecurity.UserSid(workerId) != Contract.Sid) return 1;
        string workerPath = ProcessSecurity.Processes().First(x => x.Pid == workerId).Path;
        if (!workerPath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await worker.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            // Only the exact verified, still-open handle to this operation's worker is terminated.
            // Kumiryo, its core, remote tools and services are never terminated here.
            if (!worker.HasExited) { worker.Kill(); await worker.WaitForExitAsync(); }
        }
        if (KumiryoConfiguration.ReadReport(id) is { Finished: false }) return await new KumiryoJobRunner(client, KumiryoConfiguration.ValidateExecutionTarget).Recover(id);
        return 0;
    }
}

internal sealed class KumiryoJobRunner(IKumiryoClient client, Action<KumiryoTarget> validate)
{
    private static void Step(KumiryoReport report, string phase, int progress, string detail)
    {
        report.Phase = phase; report.Progress = progress;
        report.Events.Add(new(DateTimeOffset.UtcNow, phase, detail)); KumiryoConfiguration.Save(report);
        new DiagnosticLog(DataPaths.UserRoot).Event("配置迁移","信息",phase,$"操作 {report.Id} · {progress}% · {detail}");
    }
    private static FileStream Lock(KumiryoTarget target)
    {
        KumiryoPrivateFiles.Directory(KumiryoConfiguration.Root);
        string path = Path.Combine(KumiryoConfiguration.Root, Contract.Hash(Path.GetFullPath(target.Preferences).ToLowerInvariant()) + ".lock");
        KumiryoPrivateFiles.NoLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public async Task<int> Run(string id)
    {
        var op = KumiryoConfiguration.ReadOperation(id); var report = KumiryoConfiguration.ReadReport(id) ?? new() { Id = id };
        if (report.Finished || report.WriteStarted || report.BackupReady) throw new InvalidOperationException("操作已经执行或中断，不能重复写入。请使用恢复入口。");
        FileStream? guard = null;
        try
        {
            guard = Lock(op.Target);
            validate(op.Target);
            report.WorkerPid = Environment.ProcessId; report.WorkerStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            report.ExecutionPrivilege = Contract.Privilege;
            report.UiStopFallbackAllowed = op.AllowUiStopFallback;
            Step(report, "执行前复核", 5, "核对客户端身份、当前账户、文件指纹与迁移范围。");
            string initial = KumiryoPrivateFiles.Read(op.Target.Preferences);
            if (Contract.Hash(initial) != op.ExpectedHash) throw new InvalidOperationException("配置在预览后发生变化，请重新检查；本次未写入。");
            report.ClientWasRunning = client.Running(op.Target); report.WasConnected = client.Connected(op.Target);
            string folder = KumiryoConfiguration.JobFolder(id);
            KumiryoPrivateFiles.Protect(Path.Combine(folder, "initial.dpapi"), initial);
            if (Contract.Hash(KumiryoPrivateFiles.Unprotect(Path.Combine(folder, "initial.dpapi"))) != op.ExpectedHash) throw new IOException("初始加密备份读回校验失败，未退出客户端。");
            report.InitialBackupReady = true;
            Step(report, "初始备份已校验", 15, "原设置已用当前 Windows 用户 DPAPI 加密保存并读回校验；写入恢复备份将在客户端退出后另行建立。");
            await StopClient(op.Target, report);
            Step(report, "客户端已退出", 30, "重新读取退出时保存的设置；不停止代理内核或任何系统服务。");
            string beforeText = KumiryoPrivateFiles.Read(op.Target.Preferences); var before = KumiryoConfigPlan.ReadPreferences(beforeText);
            JsonObject after;
            if (op.Kind == "restore")
            {
                var prior = KumiryoConfiguration.ReadOperation(op.RestoreId); var priorReport = KumiryoConfiguration.ReadReport(op.RestoreId);
                if (priorReport is not { BackupReady: true, Written: true, Restored: false } || !Path.GetFullPath(prior.Target.Preferences).Equals(Path.GetFullPath(op.Target.Preferences), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("原操作没有可恢复备份，或恢复目标已变化。");
                string originalFolder = KumiryoConfiguration.JobFolder(op.RestoreId);
                var original = KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Unprotect(Path.Combine(originalFolder, "before.dpapi")));
                var applied = KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Unprotect(Path.Combine(originalFolder, "after.dpapi")));
                after = KumiryoConfigPlan.RestoreBetween(before, original, applied);
            }
            else after = KumiryoConfigPlan.Merge(before, op.Template);
            if (!KumiryoConfigPlan.ProtectedContentEqual(before, after)) throw new InvalidOperationException("账号、订阅或迁移范围外内容发生变化，已停止写入。");
            string afterText = after.ToJsonString();
            KumiryoPrivateFiles.Protect(Path.Combine(folder, "before.dpapi"), beforeText);
            KumiryoPrivateFiles.Protect(Path.Combine(folder, "after.dpapi"), afterText);
            if (Contract.Hash(KumiryoPrivateFiles.Unprotect(Path.Combine(folder, "before.dpapi"))) != Contract.Hash(beforeText)) throw new IOException("备份读回校验失败。");
            if (Contract.Hash(KumiryoPrivateFiles.Unprotect(Path.Combine(folder, "after.dpapi"))) != Contract.Hash(afterText)) throw new IOException("目标副本校验失败。");
            report.BackupReady = true;
            Step(report, "加密备份已校验", 45, "退出后的原始设置及目标副本均已校验，账号、订阅保持目标电脑自身内容。");
            report.WriteStarted = true; Step(report, "正在原子写入", 55, "写入前再次检查文件指纹，以完整临时文件进行原子替换。");
            GuardedWrite(op.Target.Preferences, Contract.Hash(beforeText), afterText);
            report.Written = true; Step(report, "文件已写入", 65, "文件已保存，接下来核对全部九个模块并重新打开客户端。");
            var actual = KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Read(op.Target.Preferences));
            if (!JsonNode.DeepEquals(actual, after)) throw new IOException("原子写入后的文件读回不一致。");
            await client.Start(op.Target);
            Step(report, "客户端已重新打开", 80, "核验持久设置及运行状态；不能回读的内核设置单独标明。");
            if (report.WasConnected)
            {
                for (int i = 0; !client.Connected(op.Target) && i < 15; i++) await Task.Delay(1000);
                if (!client.Connected(op.Target)) throw new IOException("重新打开后未恢复原有连接状态。");
            }
            actual = KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Read(op.Target.Preferences));
            if (op.Kind == "apply") report.Checks = KumiryoConfiguration.VerifyModules(KumiryoConfigPlan.Settings(actual), op.Template);
            else
            {
                bool restored = ManagedEqual(actual, after);
                report.Checks = [new("恢复后的持久设置", restored ? "通过" : "未通过", restored ? "恢复范围内内容与备份一致。" : "客户端重新打开后设置发生变化。")];
            }
            report.FileComplete = report.Checks.All(x => x.State == "通过");
            if (!report.FileComplete) throw new InvalidOperationException("客户端重新打开后有设置被覆盖；已进入恢复流程。");
            var runtimeTemplate = op.Template;
            if (op.Kind == "restore")
            {
                runtimeTemplate = new() { SourceVersion = op.Target.Version };
                var settings = KumiryoConfigPlan.Settings(after);
                foreach (var module in KumiryoConfigPlan.Modules) runtimeTemplate.Settings[module.Key] = settings[module.Key]?.DeepClone();
            }
            report.Checks.AddRange(client.Runtime(op.Target, runtimeTemplate));
            if (report.Checks.Any(x => x.State == "未通过")) throw new IOException("运行核验发现冲突或失败，已进入恢复流程。");
            report.RuntimeComplete = report.Checks.All(x => x.State is "通过" or "不适用");
            report.Success = true; report.Finished = true;
            report.Summary = op.Kind == "restore" ? "备份设置已恢复并重新打开客户端。" : report.RuntimeComplete ? "九个设置模块已完整写入，运行检查通过。" : "九个设置模块已完整写入；部分运行检查仍待核对，请查看明细。";
            if (op.Kind == "restore" && KumiryoConfiguration.ReadReport(op.RestoreId) is KumiryoReport originalReport)
            {
                originalReport.Restored = true; originalReport.FileComplete = originalReport.RuntimeComplete = false;
                originalReport.Phase = "已由后续操作恢复"; originalReport.Summary += " 本次修改已由后续恢复操作撤回。";
                originalReport.Events.Add(new(DateTimeOffset.UtcNow, "后续恢复完成", "对应修改已撤回，当前不再应用这条记录的目标设置。")); KumiryoConfiguration.Save(originalReport);
            }
            Step(report, "执行完成", 100, report.Summary); return 0;
        }
        catch (Exception error)
        {
            report.Success = false; report.Summary = KumiryoConfiguration.SafeError(error);
            report.NeedsAdministrator = error is UnauthorizedAccessException || error is System.ComponentModel.Win32Exception { NativeErrorCode: 5 };
            Step(report, "执行未完成", report.Progress, report.Summary);
            if (report.BackupReady && report.WriteStarted)
            {
                try { await Rollback(op, report); }
                catch (Exception ex) { report.Events.Add(new(DateTimeOffset.UtcNow, "恢复未完成", KumiryoConfiguration.SafeError(ex))); report.Summary += " 自动恢复未完成，备份已保留，请从历史记录处理。"; }
            }
            else if (report.ClientWasRunning && !client.Running(op.Target))
            {
                try { await client.Start(op.Target); }
                catch (Exception ex) { report.Events.Add(new(DateTimeOffset.UtcNow, "重新打开未完成", KumiryoConfiguration.SafeError(ex))); }
            }
            report.Finished = true; report.Phase = report.Restored ? "已恢复原设置" : "未完成，需查看记录"; KumiryoConfiguration.Save(report); return 1;
        }
        finally { guard?.Dispose(); }
    }
    public async Task<int> Recover(string id)
    {
        var op = KumiryoConfiguration.ReadOperation(id); var report = KumiryoConfiguration.ReadReport(id) ?? throw new InvalidDataException("缺少操作记录。");
        if (report.Restored) return 0;
        using var guard = Lock(op.Target);
        try
        {
            validate(op.Target);
            report.WorkerPid = Environment.ProcessId; report.WorkerStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
            if (report.BackupReady && report.WriteStarted) await Rollback(op, report);
            else
            {
                if (report.ClientWasRunning && !client.Running(op.Target)) await client.Start(op.Target);
                report.Summary = "中断发生在写入前，原设置保留；客户端状态已重新核对。";
            }
            report.Success = false; report.Finished = true; report.Phase = "中断恢复完成"; KumiryoConfiguration.Save(report); return 0;
        }
        catch (Exception ex)
        {
            report.Finished = true; report.Success = false; report.Phase = "中断恢复未完成";
            report.Summary = KumiryoConfiguration.SafeError(ex); report.Events.Add(new(DateTimeOffset.UtcNow, report.Phase, report.Summary)); KumiryoConfiguration.Save(report); return 1;
        }
    }
    private async Task Rollback(KumiryoOperation op, KumiryoReport report)
    {
        string folder = KumiryoConfiguration.JobFolder(op.Id);
        var before = KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Unprotect(Path.Combine(folder, "before.dpapi")));
        var after = KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Unprotect(Path.Combine(folder, "after.dpapi")));
        // Check conflicts before closing a functioning client.
        _ = KumiryoConfigPlan.RestoreBetween(KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Read(op.Target.Preferences)), before, after);
        Step(report, "正在恢复原设置", 90, "仅恢复本次涉及的字段；发现后续改动就停止，不覆盖账号或订阅。");
        await StopClient(op.Target, report);
        string currentText = KumiryoPrivateFiles.Read(op.Target.Preferences); var current = KumiryoConfigPlan.ReadPreferences(currentText);
        var restored = KumiryoConfigPlan.RestoreBetween(current, before, after);
        if (!JsonNode.DeepEquals(current, restored)) GuardedWrite(op.Target.Preferences, Contract.Hash(currentText), restored.ToJsonString());
        var read = KumiryoConfigPlan.ReadPreferences(KumiryoPrivateFiles.Read(op.Target.Preferences));
        if (!ManagedEqual(read, restored)) throw new IOException("恢复后的文件校验失败。");
        if (report.ClientWasRunning) await client.Start(op.Target);
        report.Restored = true; report.FileComplete = report.RuntimeComplete = false;
        report.Checks = [new("恢复文件读回", "通过", "本次涉及字段已恢复，不再声明符合注入目标。")];
        report.Summary += " 已恢复本次修改前的设置。";
        report.Events.Add(new(DateTimeOffset.UtcNow, "原设置已恢复", "加密备份已应用并通过文件读回检查。")); KumiryoConfiguration.Save(report);
    }
    private static bool ManagedEqual(JsonObject left, JsonObject right)
    {
        var a = KumiryoConfigPlan.Settings(left); var b = KumiryoConfigPlan.Settings(right);
        return KumiryoConfigPlan.Modules.All(m => JsonNode.DeepEquals(a[m.Key], b[m.Key]));
    }
    private async Task StopClient(KumiryoTarget target, KumiryoReport report)
    {
        try { await client.Stop(target); }
        finally
        {
            if (client is KumiryoClient native)
            {
                report.Events.AddRange(native.ExitEvents);
                report.UiStopFallbackUsed |= native.LastStopUsedFallback;
                report.ClientExitMethod = native.LastExitMethod;
                KumiryoConfiguration.Save(report);
            }
        }
    }
    private static void GuardedWrite(string path, string expectedHash, string text)
    {
        if (Contract.Hash(KumiryoPrivateFiles.Read(path)) != expectedHash) throw new InvalidOperationException("写入前配置又发生变化，已停止覆盖。");
        KumiryoPrivateFiles.Atomic(path, text);
    }
}
