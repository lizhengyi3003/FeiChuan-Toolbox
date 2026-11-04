using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TunHotspot.Core;
using Windows.Storage.Pickers;

namespace TunHotspot;

public sealed partial class ConfigurationPage : UserControl, IDisposable
{
    private readonly Window _owner;
    private readonly bool _qa;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly List<Button> _actions = [];
    private readonly Button _apply, _restore, _recover, _exportLog;
    private KumiryoTemplate _template;
    private KumiryoTarget? _target;
    private KumiryoPreview? _preview;
    private string? _job;
    private Process? _worker;
    private DateTimeOffset _launchTime;
    private bool _busy, _loaded;
    private sealed record Row(string Title, string Detail);
    private sealed record HistoryRow(KumiryoReport Report)
    { public string Label => $"{Report.Started.ToLocalTime():MM-dd HH:mm} · {Report.Phase} · {Report.Id[..8]}"; }

    public ConfigurationPage(Window owner, bool qa = false)
    {
        _owner = owner; _qa = qa; InitializeComponent();
        try { _template = qa ? KumiryoConfigPlan.Embedded() : KumiryoConfiguration.CurrentTemplate(); }
        catch (Exception ex) { _template = KumiryoConfigPlan.Embedded(); Tell("本机模板不可读，已使用内置模板", KumiryoConfiguration.SafeError(ex), InfoBarSeverity.Warning); }
        var buttons = new FlowPanel();
        Add(buttons, "重新检测", "Configuration.Refresh", () => Check());
        Add(buttons, "管理员核验", "Configuration.AdminCheck", AdministratorCheck);
        _apply = Add(buttons, "预览并一键注入", "Configuration.Apply", Apply);
        Add(buttons, "核验完整性", "Configuration.Verify", Verify);
        Add(buttons, "从本机更新模板", "Configuration.Capture", CaptureTemplate);
        Add(buttons, "导入模板", "Configuration.Import", ImportTemplate);
        Add(buttons, "导出模板", "Configuration.Export", ExportTemplate);
        Add(buttons, "使用内置模板", "Configuration.Reset", async () => { _template = KumiryoConfigPlan.Embedded(); if (!_qa) KumiryoConfiguration.SaveTemplate(_template); await Check(); });
        ActionHost.Content = buttons;
        var historyActions = new FlowPanel();
        _restore = Add(historyActions, "恢复所选备份", "Configuration.Restore", Restore);
        _recover = Add(historyActions, "恢复中断操作", "Configuration.Recover", Recover);
        _exportLog = Add(historyActions, "导出脱敏日志", "Configuration.ExportLog", ExportLog);
        Add(historyActions, "打开操作目录", "Configuration.OpenLogs", () => { if (!_qa) { Directory.CreateDirectory(KumiryoConfiguration.Root); Process.Start(new ProcessStartInfo(KumiryoConfiguration.Root) { UseShellExecute = true }); } return Task.CompletedTask; });
        HistoryActionHost.Content = historyActions;
        _poll.Tick += async (_, _) => await Poll();
        Loaded += async (_, _) =>
        {
            if (_loaded) return; _loaded = true; await Check(); RefreshHistory();
            if (!_qa && KumiryoConfiguration.History().FirstOrDefault(x => !x.Finished) is KumiryoReport pending)
            {
                _job = pending.Id;
                if (KumiryoConfiguration.WorkerAlive(pending)) { SetBusy(true); _poll.Start(); }
                else Tell("发现中断的操作", "请在下方选择该记录并恢复；新注入会等待这条记录处理完毕。", InfoBarSeverity.Warning);
            }
        };
    }
    private Button Add(Panel panel, string label, string id, Func<Task> run)
    {
        var button = new Button { Content = label };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) => { try { await run(); } catch (Exception ex) { var incident=new DiagnosticLog(DataPaths.UserRoot).Record("Kumiryo："+label,ex);Tell("操作未完成", KumiryoConfiguration.SafeError(ex)+$"\n错误编号：{incident.Id}\n日志：{incident.LogPath}", InfoBarSeverity.Error); } };
        _actions.Add(button); panel.Children.Add(button); return button;
    }
    private async Task Check()
    {
        if (_busy) return;
        TemplateLabel.Text = $"{_template.Name} · {_template.SourceVersion}";
        TemplateDetails.Text = $"9 个设置模块 · {(_template.Settings["globalAddedRules"] as JsonArray)?.Count ?? 0} 条自定义规则 · {_template.Captured.ToLocalTime():yyyy-MM-dd HH:mm}\n网络、TUN、DNS、应用、规则与外观一起迁移；目标账号、订阅、当前订阅选择和登录凭据保留。";
        TemplateJson.Text = _template.Settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (_qa)
        {
            _target = new() { Executable = @"C:\Apps\Kumiryo\kumiryo.exe", Preferences = @"C:\Users\Example\AppData\Roaming\com.kumiryo\Kumiryo\shared_preferences.json", Version = "0.2.4" };
            _preview = new() { Target = _target, Rules = 95, Checks = KumiryoConfigPlan.Modules.Select(x => new KumiryoCheck(x.Name, "待配置", "隔离界面验证示例，不操作真实客户端。")).ToList(), Differences = [new("/patchClashConfig/tun/enable", "关闭", "开启"), new("/globalAddedRules", "列表（0 项）", "列表（95 项）")] };
        }
        else _preview = await Task.Run(() => { _target ??= KumiryoConfiguration.Discover(); return KumiryoConfiguration.Preview(_target, _template); });
        ExecutablePath.Text = _target?.Executable ?? ""; PreferencesPath.Text = _target?.Preferences ?? "";
        BlockingReasons.Text = string.Join("\n", _preview.Blocks.Select(x => "• " + x));
        StatusTitle.Text = _preview.NeedsAdministrator ? "需要管理员核验" : !_preview.CanApply ? "需要先处理预检问题" : _preview.Differences.Count == 0 ? "九个设置模块已经一致" : $"发现 {_preview.Differences.Count} 处设置差异";
        StatusDetail.Text = "配置文件完整性与运行状态分别核验。只有点“确认并执行”才会退出客户端和写入设置。";
        ShowChecks(_preview.Checks);
        DifferenceItems.ItemsSource = _preview.Differences.Take(200).Select(x => $"{x.Path}\n{x.Before} → {x.After}").ToList();
        UpdateButtons();
    }
    private async Task Verify()
    {
        await Check(); if (_qa || _target is null || _preview?.CanApply != true) return;
        var runtime = await Task.Run(() => new KumiryoClient().Runtime(_target, _template));
        ShowChecks(_preview.Checks.Concat(runtime));
        StatusTitle.Text = _preview.Differences.Count == 0 ? "持久设置完整，运行状态见下方明细" : "配置仍有差异";
        StatusDetail.Text = "本次只读核验，没有退出客户端、写入设置或切换网络。";
    }
    private async Task Apply()
    {
        if (_qa || _busy) return; await Check();
        if (_preview is { NeedsAdministrator: true } && UseAdministrator.IsChecked == true) await AdministratorCheck();
        if (_preview is not { CanApply: true }) return;
        bool same = _preview.Differences.Count == 0;
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"将完整迁移 9 个设置模块、{_preview.Rules} 条规则，处理 {_preview.Differences.Count} 处差异。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "目标电脑已有的同类设置和自定义规则会按预览替换；账号、登录凭据、订阅及当前订阅选择保留。窗口位置会使用模板中的自动定位设置。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "执行时会自动退出并重新打开 Kumiryo，代理可能短暂中断。先建立当前用户加密备份，失败尝试恢复；后续设置冲突会停止恢复并保留记录。", TextWrapping = TextWrapping.Wrap });
        var uiFallback = UiStopFallbackOption(); content.Children.Add(uiFallback);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = same ? "设置已一致，可核验或重新加载" : "确认 Kumiryo 配置变更", PrimaryButtonText = same ? "备份后重新加载" : "确认并执行", SecondaryButtonText = same ? "仅核验" : "", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, Content = content };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary) { await Verify(); return; }
        if (choice != ContentDialogResult.Primary) return;
        await Launch(KumiryoConfiguration.Create(_preview, _template, allowUiStopFallback: uiFallback.IsChecked == true), false);
    }
    private async Task AdministratorCheck()
    {
        if (_qa || _busy || _target is null) return;
        string id = KumiryoConfiguration.CreateAdministratorPreview(_target, _template); SetBusy(true);
        try
        {
            var start = new ProcessStartInfo(AdminClient.EnginePath) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string value in new[] { "--kumiryo-preview", id, "--owner", Contract.Sid }) start.ArgumentList.Add(value);
            using var process = await Task.Run(() => Process.Start(start)) ?? throw new IOException("管理员核验未启动。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)); await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 43) throw new InvalidOperationException("管理员授权必须使用发起操作的同一 Windows 账户，未读取其他账户配置。");
            if (process.ExitCode != 0) throw new InvalidOperationException("管理员核验未完成，未执行配置修改。请核对标准配置目录及客户端安装。");
            _preview = KumiryoConfiguration.ReadAdministratorPreview(id);
            ShowChecks(_preview.Checks); BlockingReasons.Text = string.Join("\n", _preview.Blocks);
            DifferenceItems.ItemsSource = _preview.Differences.Take(200).Select(x => $"{x.Path}\n{x.Before} → {x.After}").ToList();
            StatusTitle.Text = _preview.CanApply ? "管理员核验已完成" : "管理员核验发现待处理项";
            StatusDetail.Text = $"{_preview.Differences.Count} 处差异；本次只读核验，没有退出客户端或写入设置。";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Tell("管理员核验已取消", "Windows UAC 授权已取消，没有修改客户端配置。", InfoBarSeverity.Warning); }
        finally { SetBusy(false); }
    }
    private async Task Launch(string id, bool recover)
    {
        bool administrator = UseAdministrator.IsChecked == true;
        if (Contract.Elevated && !administrator) { if (!recover) KumiryoConfiguration.CancelUnstarted(id); throw new InvalidOperationException("工具箱当前以管理员运行，普通执行请先正常打开工具箱。"); }
        string engine = AdminClient.EnginePath;
        if (!File.Exists(engine)) { if (!recover) KumiryoConfiguration.CancelUnstarted(id); RefreshHistory(id); throw new FileNotFoundException("配置工作组件缺失，请重新解压完整程序目录或修复安装。"); }
        KumiryoConfiguration.SetCaller(id);
        var start = new ProcessStartInfo(engine) { UseShellExecute = administrator, CreateNoWindow = !administrator, WorkingDirectory = Path.GetDirectoryName(engine)!, WindowStyle = ProcessWindowStyle.Hidden };
        if (administrator) start.Verb = "runas";
        foreach (string value in new[] { recover ? "--kumiryo-recover" : "--kumiryo-job", id, "--owner", Contract.Sid }) start.ArgumentList.Add(value);
        _worker?.Dispose(); _launchTime = DateTimeOffset.UtcNow;
        SetBusy(true); OperationProgress.Value = 0; StatusTitle.Text = administrator ? "等待 Windows 管理员授权" : "正在启动配置工作组件";
        StatusDetail.Text = "执行组件启动前不写入客户端配置；请处理 Windows 授权窗口。";
        try { _worker = await Task.Run(() => Process.Start(start)) ?? throw new IOException("配置工作组件未能启动。"); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { if (!recover) KumiryoConfiguration.CancelUnstarted(id, true); SetBusy(false); RefreshHistory(id); Tell("管理员授权已取消", "尚未执行本次配置操作。", InfoBarSeverity.Warning); return; }
        catch { if (!recover) KumiryoConfiguration.CancelUnstarted(id); SetBusy(false); RefreshHistory(id); throw; }
        _job = id; RefreshHistory(id); SetBusy(true); _poll.Start();
        AppLog.Write("kumiryo-operation-started", new { id, recover, administrator });
    }
    private async Task Poll()
    {
        if (_job is null) return;
        try
        {
            var report = KumiryoConfiguration.ReadReport(_job); if (report is null) return;
            bool workerExited = _worker is not null && !WorkerExists(_worker.Id);
            if (workerExited && !report.WriteStarted)
            {
                try { if (_worker!.ExitCode == 43) { KumiryoConfiguration.CancelUnstarted(_job, wrongAccount: true); report = KumiryoConfiguration.ReadReport(_job)!; } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            StatusTitle.Text = report.Phase; StatusDetail.Text = report.Summary;
            OperationProgress.Value = report.Progress; ShowChecks(report.Checks); ShowReport(report);
            if (report.Finished && (report.Updated >= _launchTime || _worker is null))
            {
                _poll.Stop(); SetBusy(false); RefreshHistory(report.Id);
                Tell(report.Phase, report.Summary, report.Success ? report.RuntimeComplete ? InfoBarSeverity.Success : InfoBarSeverity.Warning : report.Restored ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
                _worker?.Dispose(); _worker = null;
                AppLog.Write("kumiryo-operation-finished", new { report.Id, report.Success, report.FileComplete, report.RuntimeComplete, report.Restored });
            }
            else if (workerExited && !KumiryoConfiguration.WorkerAlive(report))
            {
                _poll.Stop(); SetBusy(false); RefreshHistory(report.Id);
                Tell("操作进程已经退出", "独立观察进程会检查是否需要恢复；也可以选择此记录，点击“恢复中断操作”。", InfoBarSeverity.Warning);
            }
        }
        catch (IOException) { }
        catch (Exception ex) { _poll.Stop(); SetBusy(false); Tell("无法读取操作进度", KumiryoConfiguration.SafeError(ex), InfoBarSeverity.Error); }
        await Task.CompletedTask;
    }
    private static bool WorkerExists(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return true; }
        catch (ArgumentException) { return false; }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; ChooseExe.IsEnabled = ChoosePreferences.IsEnabled = !busy && !_qa; UpdateButtons();
    }
    private void UpdateButtons()
    {
        foreach (var button in _actions) button.IsEnabled = !_busy && !_qa;
        _apply.IsEnabled = !_qa && !_busy && (_preview?.CanApply == true || _preview is { NeedsAdministrator: true } && UseAdministrator.IsChecked == true && _preview.Blocks.Count == 0);
        UseAdministrator.IsEnabled = !_busy && !_qa;
        var selected = (OperationHistory.SelectedItem as HistoryRow)?.Report;
        _restore.IsEnabled = !_qa && !_busy && selected is { BackupReady: true, Written: true, Restored: false } && selected.Finished;
        _recover.IsEnabled = !_qa && !_busy && selected is not null && !KumiryoConfiguration.WorkerAlive(selected) && (!selected.Finished || selected.BackupReady && selected.WriteStarted && !selected.Restored && !selected.Success);
        _exportLog.IsEnabled = !_qa && selected is not null;
    }
    private void ShowChecks(IEnumerable<KumiryoCheck> checks) => CheckItems.ItemsSource = checks.Select(x => new Row($"{x.Name} · {x.State}", x.Detail)).ToList();
    private void RefreshHistory(string? selectedId = null)
    {
        var rows = _qa ? new List<HistoryRow>() : KumiryoConfiguration.History().Select(x => new HistoryRow(x)).ToList();
        string? previous = selectedId ?? (OperationHistory.SelectedItem as HistoryRow)?.Report.Id;
        OperationHistory.ItemsSource = rows; OperationHistory.SelectedItem = rows.FirstOrDefault(x => x.Report.Id == previous) ?? rows.FirstOrDefault(); UpdateButtons();
    }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (OperationHistory.SelectedItem is HistoryRow row) ShowReport(row.Report); if (_loaded) UpdateButtons(); }
    private void ShowReport(KumiryoReport report)
    {
        HistorySummary.Text = $"{report.Summary}\n执行权限：{report.ExecutionPrivilege}；本次目标文件：{(report.FileComplete ? "已核验" : "未完成或已撤回")}；完整运行核验：{(report.RuntimeComplete ? "已确认" : "仍有待核对项")}；原设置恢复：{(report.Restored ? "已核验" : "未执行或未完成")}。\n初始原件备份：{(report.InitialBackupReady ? "已建立并校验" : "未完成或旧记录未区分")}；写入恢复备份：{(report.BackupReady ? "已建立并校验" : "尚未进入写入准备")}。\n客户端退出：{report.ClientExitMethod}；界面兜底已授权：{report.UiStopFallbackAllowed}；实际使用：{report.UiStopFallbackUsed}。";
        LogItems.ItemsSource = report.Events.Select(x => $"{x.Time.ToLocalTime():HH:mm:ss} · {x.Phase}\n{x.Detail}").ToList();
    }
    private async Task Restore()
    {
        if (_qa || OperationHistory.SelectedItem is not HistoryRow row) return;
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "会退出并重新打开 Kumiryo，只恢复该次修改涉及的字段。若这些字段之后又发生改动，会拒绝覆盖；本次恢复也会建立自己的加密备份。", TextWrapping = TextWrapping.Wrap });
        var uiFallback = UiStopFallbackOption(); content.Children.Add(uiFallback);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "恢复所选操作的原设置", PrimaryButtonText = "核对后恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, Content = content };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Launch(KumiryoConfiguration.CreateRestore(row.Report.Id, uiFallback.IsChecked == true), false);
    }
    private static CheckBox UiStopFallbackOption()
    {
        var box = new CheckBox { IsChecked = true, Content = new TextBlock { Text = "允许退出兜底：先核验备份与进程身份，再仅结束 Kumiryo 客户端界面。内核和辅助服务不在结束范围；未保存的界面状态可能丢失。", TextWrapping = TextWrapping.Wrap } };
        AutomationProperties.SetAutomationId(box, "Configuration.AllowUiStopFallback"); return box;
    }
    private async Task Recover()
    {
        if (_qa || OperationHistory.SelectedItem is not HistoryRow row) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "处理未完成操作", PrimaryButtonText = "检查并恢复", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock { Text = "根据加密备份核对实际文件。写入前中断则保留原文件；已写入则仅恢复本次字段。发现后续改动会停止并保留资料。", TextWrapping = TextWrapping.Wrap } };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Launch(row.Report.Id, true);
    }
    private async Task CaptureTemplate()
    {
        if (_qa || _target is null) return;
        _template = await Task.Run(() => KumiryoConfiguration.Capture(_target)); KumiryoConfiguration.SaveTemplate(_template); await Check();
        Tell("本机模板已更新", "九个设置模块已保存，未导出账号、订阅或登录凭据。", InfoBarSeverity.Success);
    }
    private async Task ImportTemplate()
    {
        if (_qa) return; var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitPicker(picker);
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        if (new FileInfo(file.Path).Length > 1024 * 1024) throw new InvalidDataException("模板过大。");
        var template = Contract.Decode<KumiryoTemplate>(await File.ReadAllTextAsync(file.Path)); KumiryoConfigPlan.Validate(template);
        KumiryoConfiguration.SaveTemplate(template); _template = template; await Check();
    }
    private async Task ExportTemplate()
    {
        if (_qa) return; var path = await SavePath("Kumiryo-完整设置"); if (path is null) return;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(_template, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
        Tell("模板已导出", "可带到另一台电脑，在本页导入。该模板不包含账号、登录凭据或订阅。", InfoBarSeverity.Success);
    }
    private async Task ExportLog()
    {
        if (_qa || OperationHistory.SelectedItem is not HistoryRow row) return;
        var path = await SavePath("Kumiryo-操作日志-" + row.Report.Id[..8]); if (path is null) return;
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(row.Report, new JsonSerializerOptions(Contract.Json) { WriteIndented = true }));
        Tell("脱敏日志已导出", "仅包含步骤、检查结论和本软件操作编号，不含配置原文、账号或订阅。", InfoBarSeverity.Success);
    }
    private async Task<string?> SavePath(string name)
    {
        var picker = new FileSavePicker { SuggestedFileName = name }; picker.FileTypeChoices.Add("JSON 文件", new List<string> { ".json" }); InitPicker(picker); return (await picker.PickSaveFileAsync())?.Path;
    }
    private void InitPicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_owner));
    private async void ChooseExe_Click(object sender, RoutedEventArgs e)
    {
        try { var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); InitPicker(picker); var file = await picker.PickSingleFileAsync(); if (file is null) return; _target ??= KumiryoConfiguration.Discover(); _target.Executable = file.Path; await Check(); }
        catch (Exception ex) { Tell("无法选择程序", KumiryoConfiguration.SafeError(ex), InfoBarSeverity.Error); }
    }
    private async void ChoosePreferences_Click(object sender, RoutedEventArgs e)
    {
        try { var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); InitPicker(picker); var file = await picker.PickSingleFileAsync(); if (file is null) return; _target ??= KumiryoConfiguration.Discover(); _target.Preferences = file.Path; await Check(); }
        catch (Exception ex) { Tell("无法选择配置", KumiryoConfiguration.SafeError(ex), InfoBarSeverity.Error); }
    }
    private void Tell(string title, string detail, InfoBarSeverity severity) { Notice.Title = title; Notice.Message = detail; Notice.Severity = severity; Notice.IsOpen = true; }
    private void Privilege_Changed(object sender, RoutedEventArgs e) { if (_loaded) UpdateButtons(); }
    public void Dispose() { _poll.Stop(); _worker?.Dispose(); }
}
