using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace TunHotspot.Core;

// Retain a duplicated ordinary token before the UI can close. Elevation is used
// for the bounded configuration operation; it is never inherited by Kumiryo.
internal sealed class KumiryoOrdinaryLauncher : IDisposable
{
    private nint _token;
    internal KumiryoOrdinaryLauncher(int callerPid, long callerStarted)
    {
        if (callerPid > 0) TryCapture(callerPid, callerStarted);
        if (_token != 0) return;
        foreach (var record in ProcessSecurity.Processes().Where(x => x.Path.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)))
        {
            TryCapture(record.Pid, record.Created?.UtcDateTime.Ticks ?? 0); if (_token != 0) return;
        }
        throw new InvalidOperationException("未找到当前账户的普通权限启动令牌，尚未修改配置。请正常打开工具箱后重试。");
    }
    private void TryCapture(int pid, long started)
    {
        nint process = 0, token = 0;
        try
        {
            using var candidate = Process.GetProcessById(pid);
            if (started == 0 || candidate.StartTime.ToUniversalTime().Ticks != started || candidate.SessionId != Process.GetCurrentProcess().SessionId || ProcessSecurity.UserSid(pid) != Contract.Sid) return;
            process = OpenProcess(0x1000, false, pid);
            if (process == 0 || !OpenProcessToken(process, 0x000B, out token)) return;
            using var identity = new WindowsIdentity(token);
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || Integrity(token) < 8192) return;
            // Preserve the access Windows grants on this ordinary token. A hand-picked
            // partial mask can make CreateProcessWithTokenW fail with access denied.
            if (!DuplicateTokenEx(token, 0x02000000, 0, 2, 1, out _token)) { _token = 0; return; }
            // SAFER/restricted tokens can retain elevation metadata from their origin.
            // Check effective groups and lower only the duplicate to medium integrity.
            if (Integrity(_token) > 8192) SetMediumIntegrity(_token);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        { if (_token != 0) { CloseHandle(_token); _token = 0; } }
        finally { if (token != 0) CloseHandle(token); if (process != 0) CloseHandle(process); }
    }
    internal void Start(string executable)
    {
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
        if (!CreateProcessWithTokenW(_token, 0, executable, new StringBuilder('"' + executable + '"'), 0, 0, Path.GetDirectoryName(executable)!, ref startup, out var info)) throw new InvalidOperationException($"普通权限客户端重开失败（Windows 错误 {Marshal.GetLastWin32Error()}）。备份已保留。");
        CloseHandle(info.Thread); CloseHandle(info.Process);
    }
    internal static bool IsElevated(int pid)
    {
        nint process = OpenProcess(0x1000, false, pid), token = 0;
        if (process == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!OpenProcessToken(process, 0x000A, out token)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var identity = new WindowsIdentity(token);
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || Integrity(token) > 8192;
        }
        finally { if (token != 0) CloseHandle(token); CloseHandle(process); }
    }
    public void Dispose() { if (_token != 0) { CloseHandle(_token); _token = 0; } }
    private static int Integrity(nint token)
    {
        GetTokenInfoBuffer(token, 25, 0, 0, out int length); nint data = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInfoBuffer(token, 25, data, length, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            string value = new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value;
            return int.Parse(value.Split('-')[^1]);
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    private static void SetMediumIntegrity(nint token)
    {
        var sid = new SecurityIdentifier("S-1-16-8192"); byte[] bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        int header = IntPtr.Size == 8 ? 16 : 8; nint data = Marshal.AllocHGlobal(header + bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, data + header, bytes.Length); Marshal.WriteIntPtr(data, data + header); Marshal.WriteInt32(data + IntPtr.Size, 0x20);
            if (!SetTokenInformation(token, 25, data, header + bytes.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, XCount, YCount, Fill, Flags; public short Show, Reserved2; public nint Data, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public int Pid, Tid; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int kind, out int value, int length, out int returned);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] private static extern bool GetTokenInfoBuffer(nint token, int kind, nint value, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(nint token, int kind, nint value, int length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(nint original, uint access, nint attributes, int impersonation, int type, out nint token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithTokenW(nint token, uint logon, string executable, StringBuilder command, uint flags, nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
}
