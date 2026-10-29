using System.Text.Json;

namespace TunHotspot;

internal static class AppLog
{
    internal static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TunHotspot.WinUI");
    private static readonly object Gate = new();

    internal static void Write(string action, object details)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                // Only operational metadata is logged; no SSID, password, MAC address or subscription.
                var entry = JsonSerializer.Serialize(new { time = DateTimeOffset.Now, action, details });
                File.AppendAllText(Path.Combine(Folder, "operations.jsonl"), entry + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static void SaveState(object state)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var temp = Path.Combine(Folder, "state.tmp");
                File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, Path.Combine(Folder, "state.json"), true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
