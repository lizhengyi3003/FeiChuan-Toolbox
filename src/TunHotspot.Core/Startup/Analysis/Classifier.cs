using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace TunHotspot.Core;

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
