using System.Text.Json;

namespace TunHotspot;

internal static class AppLog
{
    internal static string DataRoot {get;private set;}=TunHotspot.Core.DataPaths.UserRoot;
    internal static void Configure(string root)=>DataRoot=root;
    internal static string Folder => DataRoot.Equals(TunHotspot.Core.DataPaths.UserRoot,StringComparison.OrdinalIgnoreCase)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TunHotspot.WinUI") : Path.Combine(DataRoot,"legacy-operations");
    private static readonly object Gate = new();

    internal static void Write(string action, object details)
    {
        try
        {
            var level=action.Contains("error")||action.Contains("failed")||action.Contains("unavailable")?"错误":action.Contains("optional-")||action.Contains("warning")?"警告":"信息";
            new TunHotspot.Core.DiagnosticLog(DataRoot).Event(TunHotspot.Core.DiagnosticLog.CategoryFor(action),level,action,TunHotspot.Core.DiagnosticLog.Clean(JsonSerializer.Serialize(details)));
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                // Only operational metadata is logged; no SSID, password, MAC address or subscription.
                var entry = JsonSerializer.Serialize(new { time = DateTimeOffset.Now, action, details });
                string file=Path.Combine(Folder,"operations.jsonl");
                if(File.Exists(file)&&new FileInfo(file).Length>5*1024*1024)File.Move(file,file+".previous",true);
                File.AppendAllText(file, TunHotspot.Core.DiagnosticLog.Clean(entry) + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (Exception) { /* Diagnostics must never escape a native callback. */ }
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
