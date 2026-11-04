using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TunHotspot.Core;

public static class DataPaths
{
    // AppData can be virtualized when launched by a packaged host. UI, MCP and
    // elevated workers must share the same per-user namespace on every launch.
    public static string UserRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".FeiChuanToolbox");
    public static string LegacyUserRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TunHotspot.Toolbox");
    public static string MachineRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TunHotspot", "Monitor");
    public static string ComponentRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TunHotspot", "Monitor");
    public const string ServiceName = "TunHotspotStartupMonitor";
    public const string ServicePipe = "TunHotspot.StartupMonitor.v1";
}
