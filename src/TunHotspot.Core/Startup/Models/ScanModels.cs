using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TunHotspot.Core;

public sealed class StartupEntry
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerSid { get; set; } = "";
    public string Source { get; set; } = "";
    public string View { get; set; } = "";
    public string Command { get; set; } = "";
    public string Executable { get; set; } = "";
    public string Payload { get; set; } = "";
    public string App { get; set; } = "未确认所属应用";
    public string Publisher { get; set; } = "未确认";
    public string Signature { get; set; } = "未检查";
    public string Health { get; set; } = "待检查";
    public string Mode { get; set; } = "";
    public bool? Enabled { get; set; }
    public string State { get; set; } = "";
    public int ProcessId { get; set; }
    public bool OneShot { get; set; }
    public bool UserInstance { get; set; }
    public string Advice { get; set; } = "待分析";
    public string Reason { get; set; } = "需要结合用途和依赖判断。";
    public string Confidence { get; set; } = "低";
    public string FileIdentity { get; set; } = "";
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ObservationStatus { get; set; } = "本次读取";
    public string UserNote { get; set; } = "";
    [JsonIgnore] public AnalysisDecision? Analysis { get; set; }
    [JsonIgnore] public List<CreationOriginMark> CreationMarks {get;set;}=[];
    [JsonIgnore] public string EffectiveAdvice => Analysis?.Advice ?? Advice;
    public SortedDictionary<string, string> Configuration { get; set; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Evidence { get; set; } = new(StringComparer.Ordinal);
    [JsonIgnore] public string Fingerprint => FingerprintWith(StringComparer.Ordinal);
    // Older MCP clients used culture-sorted dictionaries. Accept an equivalent full-data digest during migration.
    [JsonIgnore] public string LegacyFingerprint => FingerprintWith(StringComparer.InvariantCulture);
    public bool MatchesFingerprint(string fingerprint)=>fingerprint==Fingerprint||fingerprint==LegacyFingerprint;
    private string FingerprintWith(IComparer<string> order)=>Contract.Hash(Contract.Encode(new SortedDictionary<string,string>(Configuration,order))+"|"+FileIdentity+"|"+UserNote);
    [JsonIgnore] public string ControlValue => Enabled.HasValue ? (Enabled.Value ? "启用" : "禁用")
        : Mode=="自动"&&Configuration.GetValueOrDefault("delayedAutoStart")=="1"?"自动（延迟）" : Mode is "自动" or "手动" or "禁用" or "引导" or "系统" ? Mode : "状态未确认";
    [JsonIgnore] public string StateText => $"{ControlValue} · {State}".Trim(' ', '·');
    [JsonIgnore] public string Display => $"{Name} · {App}";
    [JsonIgnore] public string Detail => (ObservationStatus=="本次读取"?"":"历史记录（本次未确认）；上次：")+$"{Category} · {StateText} · {EffectiveAdvice} · {Health}";
    public void Seal()
    {
        Id = Contract.Hash($"{Provider}|{Category}|{OwnerSid}|{View}|{Source}|{Name}".ToLowerInvariant());
    }
}

public sealed class Coverage
{
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "等待";
    public int Count { get; set; }
    public double Seconds { get; set; }
    public List<string> Issues { get; set; } = [];
    public string Note { get; set; } = "";
    [JsonIgnore] public string Display => $"{Name}：{Status} · {Count} 项 · {Seconds:F1} 秒";
}

public sealed class ProcessRecord
{
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Command { get; set; } = "";
    public DateTimeOffset? Created { get; set; }
    public string Association { get; set; } = "先前已存在；历史启动原因未确认";
    public string Source { get; set; } = "当前进程快照";
    [JsonIgnore] public string Display => $"{Name} · PID {Pid}";
    [JsonIgnore] public string Detail => $"父 PID {ParentPid} · {Created?.ToLocalTime():MM-dd HH:mm:ss} · {Association}";
}

public sealed class ScanSnapshot
{
    public int Protocol { get; set; } = Contract.Version;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; set; } = Contract.DeviceId;
    public string OwnerSid { get; set; } = "";
    public string Privilege { get; set; } = Contract.Privilege;
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset Finished { get; set; }
    public bool Cancelled { get; set; }
    public List<StartupEntry> Entries { get; set; } = [];
    public List<Coverage> Coverage { get; set; } = [];
    public List<ProcessRecord> Processes { get; set; } = [];
    public string BootObservation { get; set; } = "采集开始前的启动不保证完整；本次为配置与当前进程快照。";
    [JsonIgnore] public bool Complete => !Cancelled && Coverage.Count > 0 && Coverage.All(x => x.Status is "成功" or "不适用");
    [JsonIgnore] public string ConfigurationHash => Contract.Hash(string.Join("\n", Entries.OrderBy(x => x.Id).Select(x => x.Id + Contract.Encode(new SortedDictionary<string,string>(x.Configuration,StringComparer.Ordinal)) + x.FileIdentity)));
}

public sealed record ScanProgress(string Stage, int CompletedStages, int TotalStages, int Discovered, string Message);
public sealed record SnapshotSummary(string Id, string Time, int Count)
{
    public string Display => $"{DateTimeOffset.Parse(Time).ToLocalTime():MM-dd HH:mm:ss} · {Count:N0} 个入口 · {Id[..8]}";
}
public sealed record PersonalNote(string EntryId,string Text);
