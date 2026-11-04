using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TunHotspot.Core;

public sealed class ObservedEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = "";
    public int Pid { get; set; }
    public int ParentPid { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Source { get; set; } = "";
    public string Association { get; set; } = "来源未确认";
    public string Detail { get; set; } = "";
    [JsonIgnore] public string Display => $"{Time.ToLocalTime():MM-dd HH:mm:ss} · {Kind} · {Name} · PID {Pid}";
}

public sealed class WatchRule
{
    public string EntryId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    public string Expected { get; set; } = "";
    public string OwnerSid { get; set; } = "";
    public bool OneShot { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string Display => $"{Name} · 期望 {Expected}";
}

public sealed class AlertEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string EntryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public string Evidence { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public int Count { get; set; } = 1;
    public bool IsRead { get; set; }
    public bool Resolved { get; set; }
    public string Delivery { get; set; } = "待发送";
    [JsonIgnore] public string Display => $"{(IsRead ? "" : "● ")}{Title}";
    [JsonIgnore] public string Detail => $"{FirstSeen.ToLocalTime():MM-dd HH:mm} · 期望 {Expected} → 实测 {Actual} · {(Resolved ? "已恢复" : "待处理")} · {Delivery}";
}
