using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Security;
using System.Xml.Linq;

namespace TunHotspot.Core;

internal static class HotspotWindowsEvents
{
    private const string WlanLog = "Microsoft-Windows-WLAN-AutoConfig/Operational";

    internal static async Task RecordNearLoss(DiagnosticLog log, DiagnosticRecord incident)
    {
        // WLAN reconnect and driver events can arrive after the state read.
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var start = incident.Time.UtcDateTime.AddSeconds(-15);
        var end = incident.Time.UtcDateTime.AddSeconds(5);
        string time = $"TimeCreated[@SystemTime >= '{start:O}' and @SystemTime <= '{end:O}']";
        var lines = new List<string> { $"关联掉线日志 {incident.Id} · UTC {start:O} 至 {end:O}" };
        Collect(WlanLog, $"*[System[{time} and (EventID=8001 or EventID=8003 or EventID=8006 or EventID=8008 or EventID=8010)]]", lines);
        Collect("System", $"*[System[{time} and (EventID=6062 or Provider[@Name='Microsoft-Windows-SharedAccess_NAT'] or (Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=42 or EventID=107 or EventID=506 or EventID=507)))]]", lines);
        log.Event("热点", "信息", "热点掉线附近的 Windows 事件", string.Join("\n", lines));
    }

    private static void Collect(string channel, string query, List<string> lines)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(channel, PathType.LogName, query) { ReverseDirection = true });
            int before = lines.Count;
            for (int i = 0; i < 24; i++)
            {
                using var entry = reader.ReadEvent(TimeSpan.FromSeconds(1));
                if (entry is null) break;
                if (entry.Id == 6062 && !entry.ProviderName.StartsWith("Netwtw", StringComparison.OrdinalIgnoreCase)) continue;
                string codes = channel == WlanLog && entry.Id is 8003 or 8010 ? WlanCodes(entry.ToXml()) : "";
                lines.Add($"{entry.TimeCreated?.ToUniversalTime():O} · {entry.ProviderName} · ID {entry.Id} · 记录 {entry.RecordId}{codes}");
            }
            if (lines.Count == before) lines.Add($"{channel} · 时间窗口内无匹配事件。");
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or SecurityException or System.Xml.XmlException)
        {
            lines.Add($"{channel} · 读取中断 ({ex.GetType().Name}, 0x{ex.HResult:X8})。");
        }
    }

    internal static string WlanCodes(string xml)
    {
        XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        var data = XElement.Parse(xml).Element(ns + "EventData");
        var codes = new List<string>();
        foreach (var field in data?.Elements(ns + "Data") ?? [])
        {
            string? name = (string?)field.Attribute("Name");
            if (name is not ("ReasonCode" or "ErrorCode")) continue;
            string value = field.Value;
            bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code)
                : uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
            if (parsed) codes.Add($" · {name}={code}");
        }
        return string.Concat(codes);
    }
}

internal sealed class HotspotLossTracker
{
    private HotspotSnapshot? _lastOn;

    internal void StopCompleted() => _lastOn = null;

    internal HotspotSnapshot? Observe(HotspotSnapshot current)
    {
        if (current.IsOn) _lastOn = current;
        if (current.State is not ("Off" or "Unavailable")) return null;

        var lastOn = _lastOn;
        _lastOn = null;
        return lastOn;
    }
}
