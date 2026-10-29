using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace TunHotspot.Core;

public sealed record FileFact(string Health, string Identity, string Company, string Product, string Signature);

public static class CommandInspector
{
    public static string ResolveExecutable(string command, string? profile = null, string workingDirectory = "")
    {
        var text = command.Trim(); if (text.Length == 0 || text.Length > 32768) return "";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        text = Regex.Replace(text, "%USERPROFILE%", _ => profile ?? "%USERPROFILE%", RegexOptions.IgnoreCase);
        if (!string.IsNullOrEmpty(profile))
        {
            text = Regex.Replace(text, "%LOCALAPPDATA%", _ => Path.Combine(profile, @"AppData\Local"), RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "%APPDATA%", _ => Path.Combine(profile, @"AppData\Roaming"), RegexOptions.IgnoreCase);
        }
        text = Regex.Replace(text, "%SYSTEMROOT%|%WINDIR%", _ => windows, RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"%PROGRAMFILES\(X86\)%", _ => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "%PROGRAMFILES%", _ => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), RegexOptions.IgnoreCase);
        if (text.StartsWith(@"\??\")) text = text[4..];
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) text = windows + text[11..];
        var match = Regex.Match(text, "^\"([^\"]+)\"|^(.+?\\.(?:exe|dll|sys|com|bat|cmd|ps1|vbs|js))(?=\\s|,|$)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!match.Success) return "";
        string path = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        if (path.Contains('%')) return "";
        if (path.StartsWith("system32\\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path);
        if (!Path.IsPathRooted(path))
        {
            if (!string.IsNullOrEmpty(workingDirectory) && Path.IsPathRooted(workingDirectory)) path = Path.Combine(workingDirectory, path);
            else if (!path.Contains('\\') && (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || new[] { "cmd.exe", "rundll32.exe", "ctfmon.exe", "userinit.exe", "explorer.exe" }.Contains(path, StringComparer.OrdinalIgnoreCase)))
                path = Path.Combine(path.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ? windows : Environment.SystemDirectory, path);
            else return "";
        }
        try { return Path.GetFullPath(path); } catch { return ""; }
    }
    public static FileFact Inspect(string path, StartupEntry? cached, bool signature = true)
    {
        try
        {
            if(LocalPathGap(path) is string gap)return new(gap,"","","","未检查");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0) return new("目标为目录，载荷未确认", "", "", "", "未检查");
            var info = new FileInfo(path); var stamp = $"{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            if (cached is not null && cached.FileIdentity == stamp) return new(cached.Health, stamp, cached.Publisher == "未确认" ? "" : cached.Publisher, cached.App == "未确认所属应用" ? "" : cached.App, cached.Signature);
            var version = FileVersionInfo.GetVersionInfo(path);
            var sig = signature ? VerifySignature(path) : "未检查";
            info.Refresh(); if (stamp != $"{info.Length}|{info.LastWriteTimeUtc.Ticks}") return new("检查期间文件变化", "", "", "", "未确认");
            return new("文件存在", stamp, version.CompanyName ?? "", version.ProductName ?? "", sig);
        }
        catch (FileNotFoundException) { return new("文件确认缺失", "missing", "", "", "不可检查"); }
        catch (DirectoryNotFoundException) { return new("路径确认缺失", "missing", "", "", "不可检查"); }
        catch (UnauthorizedAccessException) { return new("文件拒绝访问", "", "", "", "不可检查"); }
        catch (Exception ex) { return new("检查失败：" + ex.GetType().Name, "", "", "", "未确认"); }
    }
    internal static string? LocalPathGap(string path)
    {
        if(path.StartsWith(@"\\"))return "网络路径未访问";
        string full=Path.GetFullPath(path),root=Path.GetPathRoot(full)!;
        if(!Path.IsPathFullyQualified(path))return "路径未完全解析";
        if(new DriveInfo(root).DriveType==DriveType.Network)return "网络映射盘未访问";
        string current=root;
        // Inspect ancestors from the volume root so a local junction cannot silently lead SYSTEM to a remote target.
        foreach(string part in full[root.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {
            current=Path.Combine(current,part);
            try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)return "重解析路径未访问";}
            catch(FileNotFoundException){break;}catch(DirectoryNotFoundException){break;}
        }
        return null;
    }
    private static string VerifySignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), FilePath = path };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>()); Marshal.StructureToPtr(file, ptr, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = ptr, StateAction = 1, ProviderFlags = 0x1000 };
        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            int result = WinVerifyTrust(new nint(-1), ref action, ref data);
            if (result == 0)
            {
                // Signed PE extraction still uses this framework entry point; certificate decoding uses the modern loader.
#pragma warning disable SYSLIB0057
                try { using var signed = X509Certificate.CreateFromSignedFile(path); using var certificate = X509CertificateLoader.LoadCertificate(signed.GetRawCertData()); return "有效嵌入签名 · " + certificate.GetNameInfo(X509NameType.SimpleName, false); }
#pragma warning restore SYSLIB0057
                catch { return "Windows 信任验证通过"; }
            }
            return unchecked((uint)result) == 0x800B0100 ? "未发现嵌入签名；目录签名未核实" : $"离线信任未通过 0x{unchecked((uint)result):X8}（不代表恶意）";
        }
        finally { data.StateAction = 2; _ = WinVerifyTrust(new nint(-1), ref action, ref data); Marshal.DestroyStructure<TrustFile>(ptr); Marshal.FreeHGlobal(ptr); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct TrustFile { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string FilePath; public nint Handle; public nint Subject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public nint Policy; public nint Sip; public uint UiChoice; public uint Revocation; public uint UnionChoice;
        public nint FileInfo; public uint StateAction; public nint StateData; public nint Url; public uint ProviderFlags; public uint UiContext;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref TrustData data);
}

public static class Classifier
{
    public static void Apply(StartupEntry e)
    {
        e.Evidence["建议规则版本"]="1.1.0.1；名称与类别为启发式，不是安全认证。";
        if (e.Enabled == false || e.Mode == "禁用") { e.Advice = "按需保留"; e.Reason = "此入口已禁用；其他服务、任务和运行进程需要分别查看。"; e.Confidence = "高"; return; }
        if (e.Health is "文件确认缺失" or "路径确认缺失")
        { e.Advice = "需修复"; e.Reason = "已解析的目标路径缺失。需核对安装或系统组件，不直接删除；未加引号路径及其他载荷仍需复核。"; e.Confidence = "中"; return; }
        var n = e.Name.ToLowerInvariant(); var text = (e.Name + " " + e.App + " " + e.Command).ToLowerInvariant();
        if (n.StartsWith("microsoftedgeautolaunch"))
        { e.Advice = "可考虑取消"; e.Reason = "Edge 登录预加载；是否需要与浏览器更新服务分别决定。"; e.Confidence = "中"; return; }
        if (new[] { "qaxdefender.sdk", "qaxengmanager", "mbamainservice", "huntercampservice", "osdserver", "kumiryohelperservice", "codexsandboxservice.openai.codex", "securityhealth", "ctfmon" }.Contains(n))
        { e.Advice = "建议保留"; e.Reason = "涉及安全防护、硬件控制、输入或当前代理/开发工作流。先核对依赖，再考虑变更。名称规则不构成安全认证。"; e.Confidence = "中"; return; }
        if (new[] { "mateengine", "dsh-pet", "wallpaper", "translucenttb", "micaforeveryone", "microsoftpcmanager", "pcmanager service", "nahimic", "hnpcinferengine", "magicanimation" }.Any(text.Contains))
        { e.Advice = "按需保留"; e.Reason = "桌面外观、管理、AI 或音效等附加功能；需要这些功能时保留，依赖与其他入口需整组核对。"; e.Confidence = "中"; return; }
        if (e.Category == "驱动" || (e.Signature.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) && e.Executable.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)))
        { e.Advice = "建议保留"; e.Reason = "驱动或经签名核对的 Windows 组件；当前没有可安全停用的功能依赖证据。"; e.Confidence = "中"; return; }
        if (e.OneShot || e.UserInstance)
        { e.Advice = "按需保留"; e.Reason = "一次性配置或 Windows 用户服务实例；生命周期变化本身不等于配置被恶意覆盖。"; e.Confidence = "中"; return; }
        e.Advice = "待分析"; e.Reason = "已有配置证据，需要结合所属应用、实际用途与依赖判断；可加入 Codex 待分析区。";
    }
}
