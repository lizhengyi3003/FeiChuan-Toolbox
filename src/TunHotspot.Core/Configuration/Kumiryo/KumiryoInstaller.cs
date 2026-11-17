using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace TunHotspot.Core;

public static class KumiryoInstaller
{
    private sealed record Package(string Version, string File, string Sha256, string Source);
    private static readonly Package Bundled = ReadPackage();
    private static readonly SemaphoreSlim Installation = new(1, 1);

    public static string Version => Bundled.Version;

    public static bool ShouldOffer(KumiryoTarget target) =>
        string.IsNullOrEmpty(target.Executable) ||
        System.Version.TryParse(target.Version, out var installed) && installed < System.Version.Parse(Version);

    public static async Task<KumiryoTarget> InstallOrUpdateAsync()
    {
        if (!await Installation.WaitAsync(0))
            throw new InvalidOperationException("已有一个 Kumiryo 安装向导正在运行。");
        try
        {
            var target = KumiryoConfiguration.Discover();
            if (!ShouldOffer(target))
                throw new InvalidOperationException("当前版本无需使用内置安装器，请重新检测。");

            string directory = Path.Combine(AppContext.BaseDirectory, "Installers", "Kumiryo");
            string executable = Path.Combine(directory, Bundled.File);
            KumiryoPrivateFiles.NoLinks(executable);
            using var payload = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(payload));
            if (!actualHash.Equals(Bundled.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(FileVersionInfo.GetVersionInfo(executable).ProductVersion?.Trim(), Version, StringComparison.Ordinal))
                throw new InvalidDataException("内置 Kumiryo 安装器的 SHA-256 或文件版本不一致。");

            var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = true };
            using var process = Process.Start(start) ?? throw new IOException("无法启动 Kumiryo 安装向导。");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new IOException($"Kumiryo 安装向导退出，返回码 {process.ExitCode}。");
            target = KumiryoConfiguration.Discover();
            if (target.Version != Version || !KumiryoConfiguration.IsClientExecutable(target.Executable))
                throw new IOException($"安装结束后未找到完整的 Kumiryo {Version}，请核对安装位置。");
            return target;
        }
        finally { Installation.Release(); }
    }

    private static Package ReadPackage()
    {
        using var stream = typeof(KumiryoInstaller).Assembly.GetManifestResourceStream("TunHotspot.Core.KumiryoInstaller.json")
            ?? throw new FileNotFoundException("内置 Kumiryo 安装器清单缺失。");
        return JsonSerializer.Deserialize<Package>(stream, Contract.Json)
            ?? throw new InvalidDataException("内置 Kumiryo 安装器清单无效。");
    }
}
