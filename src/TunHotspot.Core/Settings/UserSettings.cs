using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TunHotspot.Core;

public sealed class UserSettings
{
    public string Backdrop { get; set; } = "Mica";
    public string Theme { get; set; } = "跟随系统";
    public string Font { get; set; } = "Segoe UI Variable";
    public double FontSize { get; set; } = 14;
    public bool CloseToTray { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool Paused { get; set; }
    public int ScanMinutes { get; set; } = 5;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
}
