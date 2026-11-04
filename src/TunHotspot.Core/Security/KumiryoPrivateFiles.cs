using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace TunHotspot.Core;

internal static class KumiryoPrivateFiles
{
    internal static void Directory(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.Exists) { NoLinks(path); return; }
        info.Create();
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { Contract.Sid, "S-1-5-18" }.Distinct())
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(acl); NoLinks(path);
    }
    internal static void NoLinks(string path)
    {
        string? part = Path.GetFullPath(path);
        while (part is not null)
        {
            if ((File.Exists(part) || System.IO.Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("路径包含重解析点，已停止操作。");
            part = Path.GetDirectoryName(part);
        }
    }
    internal static string Read(string path)
    {
        NoLinks(path); var info = new FileInfo(path);
        if (info.Length > 4 * 1024 * 1024) throw new InvalidDataException("设置文件超出大小上限。");
        return File.ReadAllText(path, Encoding.UTF8);
    }
    internal static void Atomic(string path, string text)
    {
        NoLinks(path); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = Encoding.UTF8.GetBytes(text); file.Write(bytes); file.Flush(true); }
            if (File.Exists(path))
            {
                string destination = Physical(path), source = Physical(temp);
                if (!Path.GetDirectoryName(source)!.Equals(Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("临时副本与目标映射到不同实际目录，已停止替换。");
                bool encrypted = (File.GetAttributes(destination) & FileAttributes.Encrypted) != 0;
                if (encrypted || !destination.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                {
                    if (encrypted && (File.GetAttributes(source) & FileAttributes.Encrypted) == 0) throw new IOException("原文件已加密但副本未继承加密，已停止替换。");
                    var access = new FileInfo(destination).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
                    new FileInfo(source).SetAccessControl(access);
                    // No COPY_ALLOWED flag: a cross-volume operation must fail before
                    // changing the original, rather than falling back to a partial copy.
                    if (!MoveFileEx(source, destination, 9)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                else File.Replace(source, destination, null);
            }
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static string Physical(string path)
    {
        using var file = OpenPath(path, 0, 7, 0, 3, 0x02000000, 0);
        if(file.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file.DangerousGetHandle(), buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        string value = buffer.ToString();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new IOException("实际路径是网络共享，已停止替换。");
        return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }
    internal static void Protect(string path, string text)
    {
        byte[] encrypted = Transform(Encoding.UTF8.GetBytes(text), true);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(encrypted); file.Flush(true);
    }
    internal static string Unprotect(string path)
    {
        NoLinks(path); if (new FileInfo(path).Length > 5 * 1024 * 1024) throw new InvalidDataException("备份过大。");
        return Encoding.UTF8.GetString(Transform(File.ReadAllBytes(path), false));
    }
    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length); Blob output;
            bool ok = protect ? CryptProtectData(ref input, null, 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref Blob data, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob data, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint data);
    [DllImport("kernel32.dll", EntryPoint="CreateFileW", CharSet=CharSet.Unicode, SetLastError=true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenPath(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(nint file, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string source, string destination, uint flags);
}
