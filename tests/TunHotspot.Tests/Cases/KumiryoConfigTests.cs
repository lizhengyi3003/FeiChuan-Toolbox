using System.Text;
using System.Text.Json.Nodes;
using TunHotspot.Core;

internal static class KumiryoConfigTests
{
    public static async Task Run(string testRoot, Action<bool, string> check)
    {
        var template = KumiryoConfigPlan.Embedded();
        var previewRoundTrip = Contract.Decode<KumiryoPreview>(Contract.Encode(new KumiryoPreview { NeedsAdministrator = true }));
        check(previewRoundTrip.NeedsAdministrator && !previewRoundTrip.CanApply, "管理员核验状态往返后仍要求授权，不误标普通预检通过");
        check(!new KumiryoOperation().AllowUiStopFallback && Contract.Decode<KumiryoOperation>(Contract.Encode(new KumiryoOperation { AllowUiStopFallback = true })).AllowUiStopFallback, "界面结束兜底默认不授权，预览授权可随操作跨进程保存");
        check(template.Settings.Count == 9 && ((JsonArray)template.Settings["globalAddedRules"]!).Count == 95, "完整 Kumiryo 模板含九个模块与 95 条规则");
        var original = Fixture(template); var originalConfig = KumiryoConfigPlan.Settings(original);
        var captured = KumiryoConfigPlan.Capture(original.ToJsonString(), "0.2.4");
        check(!Contract.Encode(captured).Contains("QA-secret") && !Contract.Encode(captured).Contains("subscription.invalid"), "模板捕获不携带登录凭据与订阅地址");
        var applied = KumiryoConfigPlan.Merge(original, template);
        check(KumiryoConfigPlan.ProtectedContentEqual(original, applied), "完整迁移保留账号、订阅、当前订阅及范围外字段");
        check(KumiryoConfigPlan.Settings(applied)["appSettingProps"]!["futureFlag"]!.GetValue<bool>(), "合并保留目标客户端新增的未知设置");
        check(KumiryoConfigPlan.Differences(KumiryoConfigPlan.Settings(applied), template).Count == 0, "迁移后的九个模块全部与模板一致");
        check(JsonNode.DeepEquals(applied, KumiryoConfigPlan.Merge(applied, template)), "重复迁移幂等且不会重复追加规则");
        var newSession = (JsonObject)applied.DeepClone(); newSession["flutter.auth_token"] = "QA-secret-new";
        var changedOutside = KumiryoConfigPlan.Settings(newSession); changedOutside["outsideModule"] = new JsonObject { ["later"] = true }; newSession["flutter.config"] = changedOutside.ToJsonString();
        var restored = KumiryoConfigPlan.RestoreBetween(newSession, original, applied);
        check(restored["flutter.auth_token"]!.GetValue<string>() == "QA-secret-new" && KumiryoConfigPlan.Settings(restored)["outsideModule"]!["later"]!.GetValue<bool>(), "恢复保留后来更新的登录状态和无关设置");
        check(KumiryoConfigPlan.Settings(restored)["patchClashConfig"]!["mixed-port"]!.GetValue<int>() == 17890, "恢复准确还原本次涉及的原设置");
        var conflict = (JsonObject)applied.DeepClone(); var conflictConfig = KumiryoConfigPlan.Settings(conflict); conflictConfig["patchClashConfig"]!["mixed-port"] = 18888; conflict["flutter.config"] = conflictConfig.ToJsonString();
        Reject(() => KumiryoConfigPlan.RestoreBetween(conflict, original, applied), check, "后续修改与恢复范围冲突时拒绝覆盖");
        check(JsonNode.DeepEquals(original, KumiryoConfigPlan.RestoreBetween(original, original, applied)), "原子写入前中断可识别原文件仍在且不重复修改");
        var unsafeTemplate = Contract.Decode<KumiryoTemplate>(Contract.Encode(template)); unsafeTemplate.Settings["patchClashConfig"]!["secret"] = "do-not-copy";
        Reject(() => KumiryoConfigPlan.Validate(unsafeTemplate), check, "拒绝含控制凭据字段的迁移模板");
        var incomplete = Contract.Decode<KumiryoTemplate>(Contract.Encode(template)); incomplete.Settings.Remove("vpnProps");
        Reject(() => KumiryoConfigPlan.Validate(incomplete), check, "缺失模块不能冒充完整迁移模板");
        string folder = Path.Combine(testRoot, "kumiryo-fixtures"); Directory.CreateDirectory(folder);
        KumiryoConfiguration.FixtureRoot.Value = Path.Combine(folder, "history");
        try
        {
            string prefsFolder = Path.Combine(folder, "client"); Directory.CreateDirectory(prefsFolder);
            var target = new KumiryoTarget { Preferences = Path.Combine(prefsFolder, "shared_preferences.json"), Executable = Path.Combine(prefsFolder, "kumiryo.exe"), Version = "0.2.4" };
            File.WriteAllText(target.Preferences, original.ToJsonString());
            string cipher = Path.Combine(folder, "backup.dpapi"); KumiryoPrivateFiles.Protect(cipher, original.ToJsonString());
            check(KumiryoPrivateFiles.Unprotect(cipher) == original.ToJsonString() && !Encoding.UTF8.GetString(File.ReadAllBytes(cipher)).Contains("QA-secret"), "DPAPI 备份可由本用户恢复且不以明文保存凭据");
            var fake = new FakeClient(target); var runner = new KumiryoJobRunner(fake, t => KumiryoConfiguration.ValidateTarget(t, false));
            string NewJob() => KumiryoConfiguration.SaveOperation(new() { Target = target, Template = template, ExpectedHash = Contract.Hash(File.ReadAllText(target.Preferences)) });
            string id = NewJob(); int exit = await runner.Run(id); var report = KumiryoConfiguration.ReadReport(id)!;
            check(exit == 0 && report.Success && report.FileComplete && report.BackupReady && report.Written && fake.Starts == 1 && fake.Stops == 1, "隔离工作流程完成退出、加密备份、写入、重开和全部模块检查");
            check(report.InitialBackupReady && KumiryoPrivateFiles.Unprotect(Path.Combine(KumiryoConfiguration.JobFolder(id), "initial.dpapi")) == original.ToJsonString(), "退出前初始原件备份读回校验并单独记录就绪状态");
            check(KumiryoConfigPlan.ProtectedContentEqual(original, KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences))), "实际文件写入后凭据与订阅保持不变");
            check(!Contract.Encode(report).Contains("QA-secret") && !Contract.Encode(report).Contains("subscription.invalid"), "可导出操作日志不包含凭据或订阅原文");
            bool repeatRejected = false; try { await runner.Run(id); } catch (InvalidOperationException) { repeatRejected = true; }
            check(repeatRejected, "同一操作编号不能重复执行");
            string restoreId = KumiryoConfiguration.SaveOperation(new() { Kind = "restore", RestoreId = id, Target = target, Template = template, ExpectedHash = Contract.Hash(File.ReadAllText(target.Preferences)) });
            check(await runner.Run(restoreId) == 0 && JsonNode.DeepEquals(original, KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences))), "实际恢复事务可还原原设置并留下自身备份");
            File.WriteAllText(target.Preferences, original.ToJsonString());
            string stale = NewJob(); File.AppendAllText(target.Preferences, " "); int stops = fake.Stops;
            check(await runner.Run(stale) == 1 && fake.Stops == stops && !KumiryoConfiguration.ReadReport(stale)!.Written, "预览后文件变化时在退出客户端前拒绝写入");
            File.WriteAllText(target.Preferences, original.ToJsonString()); fake.RefuseStop = true; string refused = NewJob();
            check(await runner.Run(refused) == 1 && JsonNode.DeepEquals(original, KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences))) && !KumiryoConfiguration.ReadReport(refused)!.Written, "客户端拒绝退出时保留原文件并给出失败记录");
            var refusedReport = KumiryoConfiguration.ReadReport(refused)!;
            check(refusedReport.InitialBackupReady && !refusedReport.BackupReady && !refusedReport.WriteStarted && !refusedReport.NeedsAdministrator, "退出失败仍保留已校验初始备份，不误报写入恢复备份或权限不足");
            fake.RefuseStop = false; fake.FailNextStart = true; File.WriteAllText(target.Preferences, original.ToJsonString()); string failed = NewJob();
            check(await runner.Run(failed) == 1 && KumiryoConfiguration.ReadReport(failed)!.Restored && JsonNode.DeepEquals(original, KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences))), "重新打开失败时自动恢复本次原设置");
            File.WriteAllText(target.Preferences, original.ToJsonString()); fake.OverwriteAtNextStart = true; string overwritten = NewJob();
            check(await runner.Run(overwritten) == 1 && !KumiryoConfiguration.ReadReport(overwritten)!.Restored && KumiryoConfiguration.ReadReport(overwritten)!.Events.Any(x => x.Phase == "恢复未完成"), "客户端覆盖配置后不盲目强写恢复并保留失败明细");
            File.WriteAllText(target.Preferences, original.ToJsonString()); string interrupted = NewJob();
            string jobFolder = KumiryoConfiguration.JobFolder(interrupted);
            KumiryoPrivateFiles.Protect(Path.Combine(jobFolder, "before.dpapi"), original.ToJsonString()); KumiryoPrivateFiles.Protect(Path.Combine(jobFolder, "after.dpapi"), applied.ToJsonString());
            var interruptedReport = KumiryoConfiguration.ReadReport(interrupted)!; interruptedReport.BackupReady = interruptedReport.WriteStarted = true; interruptedReport.ClientWasRunning = true; KumiryoConfiguration.Save(interruptedReport);
            File.WriteAllText(target.Preferences, applied.ToJsonString());
            check(await runner.Recover(interrupted) == 0 && KumiryoConfiguration.ReadReport(interrupted)!.Restored && JsonNode.DeepEquals(original, KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences))), "写入完成但完成标记丢失时可依据日志和加密副本恢复");
        }
        finally
        {
            KumiryoConfiguration.FixtureRoot.Value = null;
            string full = Path.GetFullPath(folder), boundary = Path.GetFullPath(testRoot) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理边界无效。");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
    private static void Reject(Action run, Action<bool, string> check, string message)
    { try { run(); } catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { check(true, message); return; } throw new Exception("未拒绝：" + message); }
    private static JsonObject Fixture(KumiryoTemplate template)
    {
        var config = (JsonObject)template.Settings.DeepClone(); config["profiles"] = new JsonArray(new JsonObject { ["url"] = "https://subscription.invalid/?token=QA-secret" }); config["currentProfileId"] = 773;
        config["outsideModule"] = new JsonObject { ["preserve"] = true }; config["appSettingProps"]!["futureFlag"] = true;
        config["globalAddedRules"] = new JsonArray(); config["overrideDns"] = false; config["patchClashConfig"]!["mixed-port"] = 17890;
        return new JsonObject { ["flutter.config"] = config.ToJsonString(), ["flutter.auth_token"] = "QA-secret", ["flutter.version"] = 1, ["other.plugin"] = new JsonObject { ["preserve"] = true } };
    }
    private sealed class FakeClient(KumiryoTarget target) : IKumiryoClient
    {
        public int Starts, Stops; public bool RefuseStop, FailNextStart, OverwriteAtNextStart;
        private bool _running = true;
        public bool Running(KumiryoTarget t) => _running;
        public bool Connected(KumiryoTarget t) => false;
        public Task Stop(KumiryoTarget t) { Stops++; if (RefuseStop) throw new InvalidOperationException("模拟拒绝退出"); _running = false; return Task.CompletedTask; }
        public Task Start(KumiryoTarget t)
        {
            Starts++; if (FailNextStart) { FailNextStart = false; throw new IOException("fixture start failure"); }
            _running = true;
            if (OverwriteAtNextStart)
            {
                OverwriteAtNextStart = false; var root = KumiryoConfigPlan.ReadPreferences(File.ReadAllText(target.Preferences)); var config = KumiryoConfigPlan.Settings(root); config["patchClashConfig"]!["mixed-port"] = 18888; root["flutter.config"] = config.ToJsonString(); File.WriteAllText(target.Preferences, root.ToJsonString());
            }
            return Task.CompletedTask;
        }
        public List<KumiryoCheck> Runtime(KumiryoTarget t, KumiryoTemplate template) => [new("隔离模拟运行", "通过", "不操作真实进程、服务或网络。")];
    }
}
