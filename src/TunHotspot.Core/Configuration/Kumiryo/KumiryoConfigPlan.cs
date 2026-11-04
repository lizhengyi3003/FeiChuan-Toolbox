using System.Text.Json;
using System.Text.Json.Nodes;

namespace TunHotspot.Core;

public sealed class KumiryoTemplate
{
    public int Schema { get; set; } = 1;
    public string Name { get; set; } = "本机完整设置";
    public string SourceVersion { get; set; } = "0.2.4";
    public DateTimeOffset Captured { get; set; } = DateTimeOffset.UtcNow;
    public JsonObject Settings { get; set; } = new();
}

public sealed record KumiryoModule(string Key, string Name);
public sealed record KumiryoDifference(string Path, string Before, string After);
public sealed record KumiryoCheck(string Name, string State, string Detail);
public sealed record KumiryoEvent(DateTimeOffset Time, string Phase, string Detail);

public sealed class KumiryoTarget
{
    public string Preferences { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Version { get; set; } = "";
}

public sealed class KumiryoPreview
{
    public KumiryoTarget Target { get; set; } = new();
    public string FileHash { get; set; } = "";
    public string TemplateHash { get; set; } = "";
    public List<KumiryoDifference> Differences { get; set; } = [];
    public List<KumiryoCheck> Checks { get; set; } = [];
    public List<string> Blocks { get; set; } = [];
    public int Rules { get; set; }
    public bool NeedsAdministrator { get; set; }
    public bool CanApply => Blocks.Count == 0 && !NeedsAdministrator;
}

public sealed class KumiryoOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OwnerSid { get; set; } = Contract.Sid;
    public string DeviceId { get; set; } = Contract.DeviceId;
    public string Kind { get; set; } = "apply";
    public string RestoreId { get; set; } = "";
    public KumiryoTarget Target { get; set; } = new();
    public string ExpectedHash { get; set; } = "";
    public KumiryoTemplate Template { get; set; } = new();
    public int CallerPid { get; set; } = Environment.ProcessId;
    public long CallerStarted { get; set; } = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    public bool AllowUiStopFallback { get; set; }
}

public sealed class KumiryoReport
{
    public string Id { get; set; } = "";
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.UtcNow;
    public string Phase { get; set; } = "等待执行";
    public int Progress { get; set; }
    public bool Finished { get; set; }
    public bool Success { get; set; }
    public bool FileComplete { get; set; }
    public bool RuntimeComplete { get; set; }
    public bool BackupReady { get; set; }
    public bool InitialBackupReady { get; set; }
    public bool UiStopFallbackAllowed { get; set; }
    public bool UiStopFallbackUsed { get; set; }
    public string ClientExitMethod { get; set; } = "尚未执行";
    public bool WriteStarted { get; set; }
    public bool Written { get; set; }
    public bool ClientWasRunning { get; set; }
    public bool WasConnected { get; set; }
    public bool Restored { get; set; }
    public int WorkerPid { get; set; }
    public long WorkerStarted { get; set; }
    public string ExecutionPrivilege { get; set; } = "尚未执行";
    public bool NeedsAdministrator { get; set; }
    public string Summary { get; set; } = "";
    public List<KumiryoCheck> Checks { get; set; } = [];
    public List<KumiryoEvent> Events { get; set; } = [];
}

public static class KumiryoConfigPlan
{
    public static readonly KumiryoModule[] Modules =
    [
        new("overrideDns", "DNS 覆写开关"), new("globalAddedRules", "自定义分流规则"),
        new("appSettingProps", "应用设置"), new("networkProps", "系统代理与网络"),
        new("vpnProps", "VPN 与访问控制"), new("themeProps", "主题"),
        new("proxiesStyleProps", "节点列表外观"), new("windowProps", "窗口设置"),
        new("patchClashConfig", "全局覆写（TUN / DNS / 端口 / 策略）")
    ];
    private static readonly JsonSerializerOptions ReadOptions = new() { MaxDepth = 48 };
    public static JsonObject ReadPreferences(string json)
    {
        if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("配置文件过大，已停止处理。");
        var root = JsonNode.Parse(json, documentOptions: new() { MaxDepth = 48 }) as JsonObject ?? throw new InvalidDataException("配置根节点无效。");
        _ = Settings(root); return root;
    }
    public static JsonObject Settings(JsonObject root)
    {
        if (root["flutter.config"] is not JsonValue value || !value.TryGetValue<string>(out var text)) throw new InvalidDataException("未识别 Kumiryo 设置格式，请先正常运行并登录客户端。");
        return JsonNode.Parse(text, documentOptions: new() { MaxDepth = 48 }) as JsonObject ?? throw new InvalidDataException("Kumiryo 设置结构无效。");
    }
    public static KumiryoTemplate Capture(string preferences, string version)
    {
        var config = Settings(ReadPreferences(preferences));
        var template = new KumiryoTemplate { SourceVersion = version };
        foreach (var module in Modules)
            template.Settings[module.Key] = config[module.Key]?.DeepClone() ?? throw new InvalidDataException("配置缺少模块：" + module.Name);
        Validate(template); return template;
    }
    public static KumiryoTemplate Embedded()
    {
        using var stream = typeof(KumiryoConfigPlan).Assembly.GetManifestResourceStream("TunHotspot.Core.KumiryoTemplate.json") ?? throw new FileNotFoundException("内置设置模板缺失。");
        var template = JsonSerializer.Deserialize<KumiryoTemplate>(stream, Contract.Json) ?? throw new InvalidDataException("内置模板无效。");
        Validate(template); return template;
    }
    public static void Validate(KumiryoTemplate template)
    {
        if (template.Schema != 1 || template.Name.Length > 100 || template.SourceVersion.Length > 40) throw new InvalidDataException("模板版本或元数据无效。");
        if (template.Settings.Count != Modules.Length || Modules.Any(x => !template.Settings.ContainsKey(x.Key))) throw new InvalidDataException("模板必须完整包含九个设置模块，且不能携带订阅或账号字段。");
        foreach (var m in Modules)
        {
            var node = template.Settings[m.Key];
            bool valid = m.Key == "overrideDns" ? node is JsonValue value && value.TryGetValue<bool>(out _) : m.Key == "globalAddedRules" ? node is JsonArray : node is JsonObject;
            if (!valid) throw new InvalidDataException("模板模块类型不正确：" + m.Name);
        }
        if (template.Settings["globalAddedRules"] is JsonArray rules)
        {
            if (rules.Count > 5000) throw new InvalidDataException("规则数量超出上限。");
            foreach (var rule in rules)
                if (rule is not JsonObject obj || obj["value"] is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length > 4096 || text.Contains('\n')) throw new InvalidDataException("自定义规则结构无效。");
        }
        CheckSecrets(template.Settings, "");
        if (JsonSerializer.Serialize(template, Contract.Json).Length > 1024 * 1024) throw new InvalidDataException("模板过大。");
    }
    private static void CheckSecrets(JsonNode? node, string parent)
    {
        if (node is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                // DNS policy keys are domain names, including legitimate oauth/token subdomains.
                if (!parent.EndsWith("/nameserver-policy", StringComparison.Ordinal) && new[] { "password", "secret", "token", "auth_token", "auth_data", "authorization", "access_token", "refresh_token", "client_secret", "private-key", "private_key", "uuid", "cookie", "credential", "credentials", "subscription", "subscription-url", "subscriptionUrl", "proxies", "proxy-providers", "profiles", "currentProfileId", "script", "scripts" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("模板包含凭据、订阅或脚本字段，已拒绝导入。");
                CheckSecrets(value, parent + "/" + key);
            }
        else if (node is JsonArray array) foreach (var item in array) CheckSecrets(item, parent + "[]");
        else if (node is JsonValue v && v.TryGetValue<string>(out var text) && Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            if (uri.UserInfo.Length > 0 || System.Text.RegularExpressions.Regex.IsMatch(uri.Query, @"(?i)(?:token|key|password|auth|secret)=")) throw new InvalidDataException("模板 URL 中含凭据参数，已拒绝导入。");
        }
    }
    public static string TemplateHash(KumiryoTemplate template) => Contract.Hash(template.Settings.ToJsonString());
    public static List<KumiryoDifference> Differences(JsonObject config, KumiryoTemplate template)
    {
        Validate(template); var list = new List<KumiryoDifference>();
        foreach (var module in Modules) Diff(config[module.Key], template.Settings[module.Key], "/" + module.Key, list);
        return list;
    }
    private static void Diff(JsonNode? old, JsonNode? next, string path, List<KumiryoDifference> output)
    {
        if (old is JsonObject a && next is JsonObject b)
        {
            foreach (var (key, value) in b) Diff(a[key], value, path + "/" + Escape(key), output);
        }
        else if (!JsonNode.DeepEquals(old, next)) output.Add(new(path, Describe(old), Describe(next)));
    }
    public static JsonObject Merge(JsonObject preferences, KumiryoTemplate template)
    {
        var copy = (JsonObject)preferences.DeepClone(); var config = Settings(copy);
        foreach (var module in Modules) config[module.Key] = MergeNode(config[module.Key], template.Settings[module.Key]);
        copy["flutter.config"] = config.ToJsonString(); return copy;
    }
    private static JsonNode? MergeNode(JsonNode? old, JsonNode? next)
    {
        if (old is JsonObject a && next is JsonObject b)
        {
            var copy = (JsonObject)a.DeepClone();
            foreach (var (key, value) in b) copy[key] = MergeNode(copy[key], value);
            return copy;
        }
        return next?.DeepClone();
    }
    public static bool ProtectedContentEqual(JsonObject before, JsonObject after)
    {
        var a = (JsonObject)before.DeepClone(); var b = (JsonObject)after.DeepClone();
        var ca = Settings(a); var cb = Settings(b);
        foreach (var module in Modules) { ca.Remove(module.Key); cb.Remove(module.Key); }
        a.Remove("flutter.config"); b.Remove("flutter.config");
        return JsonNode.DeepEquals(a, b) && JsonNode.DeepEquals(ca, cb);
    }
    public static JsonObject Restore(JsonObject current, JsonObject before, KumiryoTemplate template)
    {
        var copy = (JsonObject)current.DeepClone(); var config = Settings(copy); var original = Settings(before);
        foreach (var delta in Differences(original, template))
        {
            var wanted = At(template.Settings, delta.Path);
            if (!JsonNode.DeepEquals(At(config, delta.Path), wanted)) throw new InvalidOperationException("设置已在注入后发生其他变化，已停止恢复，避免覆盖。");
        }
        foreach (var delta in Differences(original, template)) SetAt(config, delta.Path, At(original, delta.Path), Exists(original, delta.Path));
        copy["flutter.config"] = config.ToJsonString(); return copy;
    }
    public static JsonObject RestoreBetween(JsonObject current, JsonObject before, JsonObject after)
    {
        var config = Settings(current); var original = Settings(before); var applied = Settings(after);
        var paths = new List<string>();
        foreach (var module in Modules) Changed(original[module.Key], applied[module.Key], "/" + module.Key, paths);
        // A crash before atomic replacement can leave the original file in place.
        if (paths.All(p => SameAt(config, original, p))) return (JsonObject)current.DeepClone();
        if (paths.Any(p => !SameAt(config, applied, p))) throw new InvalidOperationException("注入涉及的设置已经被其他操作改变，已停止恢复，避免覆盖新设置。");
        foreach (string path in paths) SetAt(config, path, At(original, path), Exists(original, path));
        var copy = (JsonObject)current.DeepClone(); copy["flutter.config"] = config.ToJsonString(); return copy;
    }
    private static bool SameAt(JsonObject a, JsonObject b, string path) => Exists(a, path) == Exists(b, path) && JsonNode.DeepEquals(At(a, path), At(b, path));
    private static void Changed(JsonNode? before, JsonNode? after, string path, List<string> output)
    {
        if (before is JsonObject a && after is JsonObject b)
        {
            foreach (string key in a.Select(x => x.Key).Union(b.Select(x => x.Key)))
            {
                string child = path + "/" + Escape(key);
                if (a.ContainsKey(key) != b.ContainsKey(key)) output.Add(child); else Changed(a[key], b[key], child, output);
            }
        }
        else if (!JsonNode.DeepEquals(before, after)) output.Add(path);
    }
    private static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");
    private static string[] Parts(string pointer) => pointer.Split('/').Skip(1).Select(x => x.Replace("~1", "/").Replace("~0", "~")).ToArray();
    private static JsonNode? At(JsonObject root, string pointer)
    {
        JsonNode? node = root; foreach (var key in Parts(pointer)) node = node is JsonObject obj ? obj[key] : null; return node;
    }
    private static bool Exists(JsonObject root, string pointer)
    {
        var parts = Parts(pointer); JsonNode? node = root;
        foreach (var key in parts.SkipLast(1)) node = node is JsonObject obj ? obj[key] : null;
        return node is JsonObject parent && parent.ContainsKey(parts[^1]);
    }
    private static void SetAt(JsonObject root, string pointer, JsonNode? value, bool exists)
    {
        var parts = Parts(pointer); var parent = root;
        foreach (var key in parts.SkipLast(1)) parent = parent[key] as JsonObject ?? throw new InvalidDataException("恢复路径已变化。");
        if (exists) parent[parts[^1]] = value?.DeepClone(); else parent.Remove(parts[^1]);
    }
    public static string Describe(JsonNode? node)
    {
        if (node is null) return "未设置";
        if (node is JsonArray array) return $"列表（{array.Count} 项）";
        if (node is JsonObject obj) return $"对象（{obj.Count} 项）";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var flag)) return flag ? "开启" : "关闭";
            if (v.TryGetValue<int>(out var number)) return number.ToString();
            if (v.TryGetValue<string>(out var text)) return new[] { "gvisor", "system", "mixed", "fake-ip", "redir-host", "rule", "global", "direct", "config", "dark", "light" }.Contains(text) ? text : $"文本（{text.Length} 字符）";
        }
        return "数值";
    }
}
