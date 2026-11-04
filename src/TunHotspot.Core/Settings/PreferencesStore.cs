using System.Text;

namespace TunHotspot.Core;

// Shell preferences remain available even when startup history cannot be opened.
public sealed class PreferencesStore(string root)
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), "preferences.json");
    public string Warning { get; private set; } = "";
    private static UserSettings Read(string path)
    {
        KumiryoPrivateFiles.NoLinks(path);
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("设置文件过大。");
        var settings = Contract.Decode<UserSettings>(File.ReadAllText(path, Encoding.UTF8)); Validate(settings); return settings;
    }
    private static void Validate(UserSettings settings)
    {
        if (!new[] { "Acrylic", "Mica", "MicaAlt", "None" }.Contains(settings.Backdrop) ||
            !new[] { "跟随系统", "浅色", "深色" }.Contains(settings.Theme) ||
            string.IsNullOrWhiteSpace(settings.Font) || settings.Font.Length > 200 ||
            !double.IsFinite(settings.FontSize) || settings.FontSize is < 10 or > 28 ||
            settings.ScanMinutes is < 1 or > 1440)
            throw new InvalidDataException("应用设置内容无效。");
    }
    public UserSettings? Load()
    {
        Warning = "";
        foreach (string path in new[] { Path, Path + ".previous" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var value = Read(path);
                if (path != Path) Warning = "应用设置原件无法读取，已载入上一份有效设置；原件会保留。";
                return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            { Warning = "应用设置无法读取，已使用可用备份或默认外观；损坏文件不会直接删除。"; }
        }
        return null;
    }
    public void Save(UserSettings settings)
    {
        Validate(settings); KumiryoPrivateFiles.NoLinks(Path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (File.Exists(Path))
        {
            bool valid;
            try { _ = Read(Path); valid = true; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException) { valid = false; }
            if (valid) KumiryoPrivateFiles.Atomic(Path + ".previous", File.ReadAllText(Path, Encoding.UTF8));
            else File.Copy(Path, Path + ".damaged-" + Guid.NewGuid().ToString("N"));
        }
        KumiryoPrivateFiles.Atomic(Path, Contract.Encode(settings));
    }
}
