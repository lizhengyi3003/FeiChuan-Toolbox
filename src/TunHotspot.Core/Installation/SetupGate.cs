using System.Diagnostics;

namespace TunHotspot.Core;

public static class SetupGate
{
    public const string MarkerName = ".toolbox-installing.json";
    public const string BlockedMessage = "工具箱正在更新，或上次更新尚未完成。请等待安装结束；若安装中断，请重新运行安装包或使用安装备份中的恢复入口。";

    public static string InstallRoot(string executable)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        return Path.GetFileName(directory).Equals("Engine", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory)! : directory;
    }

    public static bool IsBlocked(string executable) => File.Exists(Path.Combine(InstallRoot(executable), MarkerName));

    public static string ExitEventName(int pid, long started) => $"Local\\TunHotspot.Setup.Exit.{pid}.{started}";

    public static IDisposable WatchForExit(Action exit)
    {
        using var process = Process.GetCurrentProcess();
        return new ExitWatch(ExitEventName(process.Id, process.StartTime.ToUniversalTime().Ticks), exit);
    }

    private sealed class ExitWatch : IDisposable
    {
        private readonly EventWaitHandle _signal;
        private readonly RegisteredWaitHandle _registration;
        public ExitWatch(string name, Action exit)
        {
            _signal = new(false, EventResetMode.ManualReset, name);
            _registration = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => exit(), null, Timeout.Infinite, true);
        }
        public void Dispose() { _registration.Unregister(null); _signal.Dispose(); }
    }
}
