using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace TunHotspot.Core;

public sealed class DiagnosticRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public string Operation { get; set; } = "";
    public string Version { get; set; } = Contract.ProductVersion;
    public string Type { get; set; } = "";
    public string Code { get; set; } = "";
    public string Guidance { get; set; } = "";
    public string Details { get; set; } = "";
    public bool ReadRetryAllowed { get; set; }
    public string LogPath { get; set; } = "";
    public string StorageWarning { get; set; } = "";
    public string Category { get; set; } = "应用";
    public string Severity { get; set; } = "错误";
    public int ProcessId { get; set; } = Environment.ProcessId;
    public string Display => $"{Time.ToLocalTime():MM-dd HH:mm:ss} · {Severity} · {Category} · {Operation} · {Id}";
}

public sealed class DiagnosticLog(string root)
{
    private static readonly System.Collections.Concurrent.ConcurrentQueue<DiagnosticRecord> Memory = new();
    public static readonly string[] Categories=["应用","启动管理","数据库","热点","配置迁移","网络","托盘","通知","设置","权限","分析","系统控制"];
    public static readonly string[] Severities=["调试","信息","警告","错误","严重"];
    public string Folder { get; } = Path.Combine(Path.GetFullPath(root), "diagnostics");
    private string FallbackFolder=>Path.Combine(Path.GetTempPath(),"TunHotspot-Diagnostics-"+Contract.Sid+"-"+Contract.Hash(Folder.ToUpperInvariant())[..12]);
    public static string Clean(string text)
    {
        bool truncated=text.Length>20000;if(truncated)text=text[..20000];
        text=Regex.Replace(text,"(?i)(https?://)[^\\s/@:]+:[^\\s/@]+@","$1[REDACTED]@",RegexOptions.None,TimeSpan.FromSeconds(1));
        text=Regex.Replace(text,"(?i)([?&](?:token|key|secret|password|access_token|refresh_token)=)[^\\s&#]+","$1[REDACTED]",RegexOptions.None,TimeSpan.FromSeconds(1));
        text = Regex.Replace(text, "(?i)((?:token|password|passwd|secret|authorization|api[_-]?key)(?:\\s*[=:]\\s*|\\s+))(?:\"[^\"]*\"|'[^']*'|(?:Bearer\\s+)?[^\\s&;,]+)", "$1[REDACTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) text = text.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return truncated ? text+"\n[已截断]" : text;
    }
    public DiagnosticRecord Record(string operation, Exception error, bool readRetryAllowed = false)
    {
        var item = new DiagnosticRecord { Operation = operation, Category=CategoryFor(operation), Type = error.GetType().Name, Code = $"0x{error.HResult:X8}", Details = Clean(error.ToString()), ReadRetryAllowed = readRetryAllowed };
        if(error is Win32Exception win32)item.Code=$"Windows {win32.NativeErrorCode} · {item.Code}";
        if(error is SqliteException sqlite)item.Code=$"SQLite {sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode} · {item.Code}";
        item.Guidance = error switch
        {
            SqliteException sql when sql.SqliteErrorCode is 11 or 26 => "数据库损坏或格式异常。已停止相关读写；保留原件与备份，重试仅重新核验，不会重置数据。",
            SqliteException sql when sql.SqliteErrorCode is 5 or 6 => "数据库正在被占用。等待当前操作结束后重试读取；不要手动删除 WAL 或日志文件。",
            UnauthorizedAccessException => "访问被拒绝。核对文件权限或使用功能提供的管理员核验；没有自动扩大权限。",
            Win32Exception native when native.NativeErrorCode == 1223 => "Windows 管理员授权已取消，未自动重试系统修改。",
            IOException => "文件或设备访问失败。检查剩余空间、文件是否被占用及路径是否仍可访问。",
            _ => "当前操作未完成。可查看详细日志；重新读取前先核对状态，系统修改需要重新预览确认。"
        };
        return Save(item);
    }
    public DiagnosticRecord Event(string category,string severity,string operation,string details="",string code="")
    {
        var item=new DiagnosticRecord{Category=category,Severity=severity,Operation=Clean(operation),Details=Clean(details),Code=code};
        return Save(item);
    }
    public static string CategoryFor(string operation)
    {
        string value=operation.ToLowerInvariant();
        if(value.Contains("hotspot")||value.Contains("热点")||value.Contains("network-profile"))return "热点";
        if(value.Contains("kumiryo")||value.Contains("注入"))return "配置迁移";
        if(value.Contains("database")||value.Contains("数据库")||value.Contains("startup-data"))return "数据库";
        if(value.Contains("tray")||value.Contains("托盘"))return "托盘";
        if(value.Contains("notification")||value.Contains("通知"))return "通知";
        if(value.Contains("网络")||value.Contains("store-network"))return "网络";
        if(value.Contains("preferences")||value.Contains("font")||value.Contains("设置"))return "设置";
        if(value.Contains("scan")||value.Contains("snapshot")||value.Contains("monitor"))return "启动管理";
        if(value.Contains("control")||value.Contains("启停"))return "系统控制";
        return "应用";
    }
    private DiagnosticRecord Save(DiagnosticRecord item)
    {
        foreach (string folder in new[] { Folder, FallbackFolder })
        {
            try
            {
                KumiryoPrivateFiles.Directory(folder);
                item.LogPath = Path.Combine(folder, $"{item.Time:yyyyMMdd-HHmmss}-{item.Id}.json");
                KumiryoPrivateFiles.Atomic(item.LogPath, Contract.Encode(item)); return item;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
            { item.StorageWarning = "主日志目录不可写，已尝试备用目录。"; item.LogPath = ""; }
        }
        item.StorageWarning = "日志文件无法写入；本次仅保留在内存，请导出或复制错误信息。";
        Memory.Enqueue(item);while(Memory.Count>100)Memory.TryDequeue(out _);return item;
    }
    public IReadOnlyList<DiagnosticRecord> Recent(string? category=null,string? severity=null,string? search=null,int limit=500)
    {
        var result = new List<DiagnosticRecord>();
        try
        {
            foreach(string folder in new[]{Folder,FallbackFolder}.Distinct())
            {
            if (!Directory.Exists(folder)) continue;
            foreach (string file in Directory.EnumerateFiles(folder, "*.json").OrderDescending().Take(2500))
                try { if (new FileInfo(file).Length <= 100000) result.Add(Contract.Decode<DiagnosticRecord>(File.ReadAllText(file))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result.Concat(Memory).Where(x=>(category is null||x.Category==category)&&(severity is null||x.Severity==severity)&&(string.IsNullOrWhiteSpace(search)||$"{x.Operation} {x.Id} {x.Code} {x.Details}".Contains(search,StringComparison.OrdinalIgnoreCase))).DistinctBy(x=>x.Id).OrderByDescending(x=>x.Time).Take(Math.Clamp(limit,1,2500)).ToList();
    }
}
