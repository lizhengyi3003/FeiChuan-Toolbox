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
}
