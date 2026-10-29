using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace TunHotspot.Core;

public sealed class WireMessage
{
    public int Protocol { get; set; } = Contract.Version;
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public ScanProgress? Progress { get; set; }
    public ScanSnapshot? Snapshot { get; set; }
    public WatchRule? Rule { get; set; }
    public List<AlertEvent>? Alerts { get; set; }
    public List<ObservedEvent>? Events { get; set; }
    public string MonitorState { get; set; } = "";
    public bool ScanCancelled { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public StartupControlRequest? Control { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<StartupControlRecord>? Controls { get; set; }
}

public static class Wire
{
    public const int MaxBytes = 32 * 1024 * 1024;
    public static async Task Send(Stream stream, WireMessage message, CancellationToken token = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Contract.Json);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("采集消息超出协议上限。");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<WireMessage> Receive(Stream stream, CancellationToken token = default)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 2 or > MaxBytes) throw new InvalidDataException("采集消息长度无效。");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        var value = JsonSerializer.Deserialize<WireMessage>(bytes, Contract.Json) ?? throw new InvalidDataException("消息为空。");
        if (value.Protocol != Contract.Version) throw new InvalidDataException("采集组件协议不兼容。");
        return value;
    }
    public static string ConnectedUser(NamedPipeServerStream pipe)
    {
        string? sid = null; pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
        return sid ?? throw new UnauthorizedAccessException("无法核实连接账户。");
    }
}
