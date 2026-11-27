using Microsoft.Win32;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TunHotspot.Core;

public static class DataPaths
{
    private static readonly Lazy<string> UserDirectory = new(ResolveUserDirectory, LazyThreadSafetyMode.PublicationOnly);
    public static string UserRoot => UserDirectory.Value;

    public static string DiagnosticRoot
    {
        get
        {
            try { return UserRoot; }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Path.Combine(Path.GetTempPath(), "FeiChuanToolbox-Diagnostics", Contract.Sid);
            }
        }
    }

    private static string ResolveUserDirectory()
    {
        string application = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (Path.GetFileName(application).Equals("Engine", StringComparison.OrdinalIgnoreCase))
            application = Path.GetDirectoryName(application)!;
        string pointer = Path.Combine(application, $"user-data-{Contract.Sid}.path");
        if (!File.Exists(pointer))
        {
            if (IsMachineInstallation(application))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".FeiChuanToolbox-AllUsers");
            return application + ".Data";
        }

        string directory = KumiryoPrivateFiles.Read(pointer).Trim();
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("安装记录中的数据目录无效。");
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        KumiryoPrivateFiles.NoLinks(directory);
        string owner = Path.Combine(directory, ".feichuan-data-owner");
        if (!File.Exists(owner) || KumiryoPrivateFiles.Read(owner).Trim() != Contract.Sid)
            throw new InvalidDataException("所选的数据目录不可用或不属于当前用户，请重新安装并选择有效的数据目录。");
        return directory;
    }

    private static bool IsMachineInstallation(string application)
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{8EE42691-81B4-4A6F-A115-9961835E1D45}_is1";
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var registration = registry.OpenSubKey(key);
        return registration?.GetValue("InstallLocation") is string installed &&
            Path.TrimEndingDirectorySeparator(installed).Equals(application, StringComparison.OrdinalIgnoreCase);
    }

    public static string MachineRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TunHotspot", "Monitor");
    public static string ComponentRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TunHotspot", "Monitor");
    public const string ServiceName = "TunHotspotStartupMonitor";
    public const string ServicePipe = "TunHotspot.StartupMonitor.v1";
}
