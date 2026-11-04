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
}
