using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TunHotspot.Core;

public sealed class AnalysisBatch
{
    public int Protocol { get; set; } = Contract.Version;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; set; } = Contract.DeviceId;
    public string SnapshotId { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public List<AnalysisItem> Items { get; set; } = [];
}
public sealed record AnalysisItem(string EntryId, string Fingerprint, StartupEntry Evidence);
public sealed class AnalysisResult
{
    public int Protocol { get; set; } = Contract.Version;
    public string BatchId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public List<AnalysisDecision> Decisions { get; set; } = [];
}
public sealed record AnalysisDecision(string EntryId, string Fingerprint, string Advice, string Reason, string Impact, string Confidence)
{
    [JsonIgnore] public string Display => $"{Advice} · {Confidence}";
    [JsonIgnore] public string Detail => Reason;
}
