using System.Diagnostics;
using System.Text.Json.Nodes;
using TunHotspot.Core;

if (args.Length is not (3 or 4)) return 2;
bool administrator = args.Length == 4 && args[3] == "elevated";
string engine = Path.GetFullPath(args[0]), fixtureSource = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
var checks = new List<string>(); var ids = new List<string>();
string root = Path.Combine(Path.GetTempPath(), "TunHotspot-Kumiryo-QA-" + Guid.NewGuid().ToString("N"));
KumiryoTarget? target = null;
System.Security.AccessControl.FileSecurity? originalAcl = null;
Dictionary<string, uint> Services() => new[] { "GameViewerService", "KumiryoHelperService", "TunHotspotStartupMonitor" }.ToDictionary(x => x, x => { try { return ProcessSecurity.ServicePid(x); } catch { return 0u; } });
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
var protectedBefore = Services();
try
{
    if (Contract.Elevated) throw new InvalidOperationException("Integration driver must run unelevated.");
    if (administrator && (Environment.UserName != "WDAGUtilityAccount" || !File.Exists(@"C:\TestScripts\run.ps1"))) throw new InvalidOperationException("Elevated fixture runs only in the dedicated Sandbox.");
    if (!FileVersionInfo.GetVersionInfo(Path.Combine(fixtureSource, "kumiryo.exe")).FileDescription!.Contains("QA Fixture")) throw new InvalidOperationException("Only the project's explicit QA client fixture is allowed.");
    if (Directory.Exists(root)) throw new InvalidOperationException("Fixture directory already exists.");
    Directory.CreateDirectory(root);
    foreach (string source in Directory.EnumerateFiles(fixtureSource, "*", SearchOption.AllDirectories))
    {
        string destination = Path.GetFullPath(Path.Combine(root, Path.GetRelativePath(fixtureSource, source)));
        if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture path boundary.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
    }
    File.WriteAllBytes(Path.Combine(root, "KumiryoCore.exe"), [0]); // Presence fixture only; never executed.
    var template = KumiryoConfigPlan.Embedded(); template.Name = "隔离原生窗口验证模板";
    template.Settings["patchClashConfig"]!["mixed-port"] = 0;
    template.Settings["patchClashConfig"]!["dns"]!["listen"] = "127.0.0.1:0";
    template.Settings["patchClashConfig"]!["tun"]!["enable"] = false;
    var config = (JsonObject)template.Settings.DeepClone(); config["appSettingProps"]!["minimizeOnExit"] = true; config["overrideDns"] = false; config["globalAddedRules"] = new JsonArray();
    config["profiles"] = new JsonArray(new JsonObject { ["url"] = "https://fixture.invalid/?token=qa-only" }); config["currentProfileId"] = 773;
    var before = new JsonObject { ["flutter.config"] = config.ToJsonString(), ["flutter.auth_token"] = "qa-only-secret", ["flutter.version"] = 1 };
    Directory.CreateDirectory(output);
    JsonNode ElevatedRequest(string kind, string id)
    {
        string nonce = Guid.NewGuid().ToString("N"), request = Path.Combine(output, "elevation-request.json"), temporary = request + ".tmp";
        File.WriteAllText(temporary, Contract.Encode(new { kind, id, nonce, ownerSid = Contract.Sid })); File.Move(temporary, request, true);
        string response = Path.Combine(output, "elevation-response-" + nonce + ".json"); var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!File.Exists(response) && DateTimeOffset.UtcNow < deadline) Thread.Sleep(50);
        if (!File.Exists(response)) throw new IOException("Sandbox elevation relay timeout.");
        return JsonNode.Parse(File.ReadAllText(response))!;
    }
    target = new() { Executable = Path.Combine(root, "kumiryo.exe"), Preferences = administrator ? KumiryoConfiguration.DefaultPreferences : Path.Combine(root, "shared_preferences.json"), Version = "0.2.4" };
    if (administrator && File.Exists(target.Preferences)) throw new InvalidOperationException("Never overwrite an existing Sandbox profile.");
    Directory.CreateDirectory(Path.GetDirectoryName(target.Preferences)!);
    File.WriteAllText(target.Preferences, before.ToJsonString());
    if (administrator)
    {
        originalAcl = new FileInfo(target.Preferences).GetAccessControl();
        var acl = new System.Security.AccessControl.FileSecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(Contract.Sid), System.Security.AccessControl.FileSystemRights.Read, System.Security.AccessControl.AccessControlType.Allow));
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier("S-1-5-32-544"), System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(target.Preferences).SetAccessControl(acl);
    }
    using var first = Process.Start(new ProcessStartInfo(target.Executable) { UseShellExecute = true })!;
    for (int i = 0; i < 50 && first.MainWindowHandle == 0; i++) { await Task.Delay(100); first.Refresh(); }
    Check(first.MainWindowHandle != 0, "原生隔离客户端窗口已启动");
    string NewJob(bool allowUiFallback = true)
    {
        var preview = KumiryoConfiguration.Preview(target, template);
        if (administrator && preview.NeedsAdministrator)
        {
            string request = KumiryoConfiguration.CreateAdministratorPreview(target, template);
            var reply = ElevatedRequest("--kumiryo-preview", request);
            if (reply["exitCode"]?.GetValue<int>() != 0) throw new InvalidOperationException("Elevated preview failed.");
            preview = KumiryoConfiguration.ReadAdministratorPreview(request);
        }
        if (!preview.CanApply) throw new InvalidOperationException(string.Join(";", preview.Blocks));
        string id = KumiryoConfiguration.Create(preview, template, allowUiStopFallback: allowUiFallback); ids.Add(id); return id;
    }
    Process? Start(string id)
    {
        if (administrator) { _ = ElevatedRequest("--kumiryo-job", id); return null; }
        var start = new ProcessStartInfo(engine) { UseShellExecute = administrator, CreateNoWindow = !administrator, WindowStyle = ProcessWindowStyle.Hidden };
        if (administrator) start.Verb = "runas";
        foreach (string value in new[] { "--kumiryo-job", id, "--owner", Contract.Sid }) start.ArgumentList.Add(value);
        return Process.Start(start)!;
    }
    async Task Wait(Process? process, string id, int expectedExit = 0)
    {
        if (administrator)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (KumiryoConfiguration.ReadReport(id) is not { Finished: true } && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            if (KumiryoConfiguration.ReadReport(id) is not { Finished: true }) throw new IOException("Privileged worker report timeout.");
            await Task.Delay(100); return;
        }
        if (process is null) throw new InvalidOperationException("Worker missing.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35)); await process.WaitForExitAsync(timeout.Token);
        if (!administrator && process.ExitCode != expectedExit) throw new InvalidOperationException("Worker exit " + process.ExitCode);
    }
    string denied = NewJob(false); using (var worker = Start(denied)) await Wait(worker, denied, 1);
    var deniedReport = KumiryoConfiguration.ReadReport(denied)!;
    Check(!deniedReport.Success && deniedReport.InitialBackupReady && !deniedReport.WriteStarted && !first.HasExited, "未授权兜底时保留托盘客户端及原设置，初始备份状态明确");
    string apply = NewJob(); using (var worker = Start(apply)) await Wait(worker, apply);
    var applied = KumiryoConfiguration.ReadReport(apply)!;
    Check(applied.Success && applied.FileComplete && !applied.RuntimeComplete && first.HasExited, "独立工作进程可退出会收起到托盘的窗口、注入设置并重新打开");
    Check(applied.UiStopFallbackAllowed && applied.UiStopFallbackUsed && !File.Exists(Path.Combine(root, "fixture-unsafe-quit.log")), "经明确授权仅结束已核实界面，不发送不安全的线程 WM_QUIT");
    if (administrator) Check(applied.ExecutionPrivilege == "管理员", "受限驱动可申请提升权限，管理员操作仍使用原账户数据");
    Check(File.ReadAllLines(Path.Combine(root, "fixture-starts.log")).Length == 2, "实际重新打开一次且没有重复拉起实例");
    var after = KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences));
    Check(KumiryoConfigPlan.ProtectedContentEqual(before, after) && KumiryoConfigPlan.Differences(KumiryoConfigPlan.Settings(after), template).Count == 0, "实际文件九模块匹配且模拟账号订阅不变");
    string restore = KumiryoConfiguration.CreateRestore(apply, true); ids.Add(restore);
    using (var worker = Start(restore)) await Wait(worker, restore);
    Check(JsonNode.DeepEquals(before, KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences))), "独立恢复操作实际还原全部改动");
    string interrupted = NewJob();
    using (var worker = Start(interrupted))
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (KumiryoConfiguration.ReadReport(interrupted)?.Written != true && (administrator || !worker!.HasExited) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Check((administrator || !worker!.HasExited) && KumiryoConfiguration.ReadReport(interrupted)?.Written == true, "已在原子写入后进入故障注入窗口");
        if (administrator) await File.WriteAllTextAsync(Path.Combine(output, "kill-request.json"), Contract.Encode(new { pid = KumiryoConfiguration.ReadReport(interrupted)!.WorkerPid, jobId = interrupted }));
        else worker!.Kill();
        if (worker is not null) await worker.WaitForExitAsync(); // Only this exact test-owned worker handle.
    }
    DateTimeOffset recoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(35);
    while (KumiryoConfiguration.ReadReport(interrupted) is not { Finished: true, Restored: true } && DateTimeOffset.UtcNow < recoveryDeadline) await Task.Delay(100);
    Check(KumiryoConfiguration.ReadReport(interrupted) is { Finished: true, Restored: true } && JsonNode.DeepEquals(before, KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences))), "真实工作进程被终止后独立观察进程自动恢复原设置");
    Check(Services().OrderBy(x => x.Key).SequenceEqual(protectedBefore.OrderBy(x => x.Key)), "隔离环境既有服务状态保持不变");
    Directory.CreateDirectory(output);
    await File.WriteAllTextAsync(Path.Combine(output, "integration.json"), Contract.Encode(new { success = true, time = DateTimeOffset.UtcNow, checks, realClientModified = false, realNetworkModified = false }));
    Console.WriteLine(Contract.Encode(new { success = true, checks })); return 0;
}
catch (Exception ex)
{
    foreach (string id in ids)
    {
        var report = KumiryoConfiguration.ReadReport(id);
        if (report is not null) { Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "failed-operation-" + id + ".json"), Contract.Encode(report)); }
    }
    Directory.CreateDirectory(output); await File.WriteAllTextAsync(Path.Combine(output, "integration.json"), Contract.Encode(new { success = false, checks, error = ex.ToString() }));
    Console.WriteLine(ex); return 1;
}
finally
{
    if (target is not null)
    {
        foreach (var record in ProcessSecurity.Processes().Where(x => x.Path.Equals(target.Executable, StringComparison.OrdinalIgnoreCase)))
        {
            using var owned = Process.GetProcessById(record.Pid);
            if (owned.StartTime.ToUniversalTime() == record.Created?.UtcDateTime && ProcessSecurity.UserSid(record.Pid) == Contract.Sid) { owned.Kill(); await owned.WaitForExitAsync(); }
        }
        if (administrator && File.Exists(target.Preferences))
        {
            if (originalAcl is not null) new FileInfo(target.Preferences).SetAccessControl(originalAcl);
            File.Delete(target.Preferences);
        }
    }
    // Delete only unique fixture paths and the exact operation IDs created above, after checking boundaries.
    foreach (string id in ids)
    {
        string job = KumiryoConfiguration.JobFolder(id);
        if (!job.StartsWith(Path.GetFullPath(KumiryoConfiguration.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Job cleanup boundary.");
        if (Directory.Exists(job)) Directory.Delete(job, true);
    }
    if (Path.GetFileName(root).StartsWith("TunHotspot-Kumiryo-QA-", StringComparison.Ordinal) && root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
}
