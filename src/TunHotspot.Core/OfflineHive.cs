using System.Buffers.Binary;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TunHotspot.Core;

// Bounded read-only parser of copied hive cells. Transaction logs are not replayed.
public sealed class OfflineHive
{
    private readonly byte[] _data;
    private OfflineHive(byte[] data) { _data = data; if (data.Length < 4096 || Encoding.ASCII.GetString(data, 0, 4) != "regf") throw new InvalidDataException("不是有效 hive 副本。"); }
    private uint U32(int pos) { Bounds(pos, 4); return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos, 4)); }
    private ushort U16(int pos) { Bounds(pos, 2); return BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos, 2)); }
    private void Bounds(int pos, int size) { if (pos < 0 || size < 0 || pos > _data.Length - size) throw new InvalidDataException("hive 单元越界。"); }
    private int Cell(uint offset) { if (offset > int.MaxValue - 4100) throw new InvalidDataException("hive 单元引用无效。"); int p = (int)offset + 4100; Bounds(p, 2); return p; }
    private string Name(int nk) { int count = U16(nk + 0x48); Bounds(nk + 0x4C, count); return ((U16(nk + 2) & 0x20) != 0 ? Encoding.Latin1 : Encoding.Unicode).GetString(_data, nk + 0x4C, count); }
    private IEnumerable<int> List(uint offset, int depth = 0)
    {
        if (depth > 16) throw new InvalidDataException("hive 子键索引过深。");
        int p = Cell(offset); string sig = Encoding.ASCII.GetString(_data, p, 2); int count = U16(p + 2);
        if (count > 30000) throw new InvalidDataException("hive 子键数量过大。");
        for (int i = 0; i < count; i++)
        {
            if (sig == "ri") foreach (int item in List(U32(p + 4 + i * 4), depth + 1)) yield return item;
            else if (sig is "lh" or "lf") yield return Cell(U32(p + 4 + i * 8));
            else if (sig == "li") yield return Cell(U32(p + 4 + i * 4));
            else throw new InvalidDataException("未知 hive 索引。");
        }
    }
    private int? Open(string path)
    {
        int node = Cell(U32(0x24));
        foreach (string part in path.Split('\\'))
        {
            if (U32(node + 0x14) == 0) return null;
            int found = List(U32(node + 0x1C)).FirstOrDefault(x => Name(x).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (found == 0) return null; node = found;
        }
        return node;
    }
    public sealed record HiveValue(string Key, string Name, string Value);
    public static List<HiveValue> ReadStartup(string source)
    {
        if(CommandInspector.LocalPathGap(source) is string gap)throw new IOException(gap);
        var file = new FileInfo(source); if (file.Length > 128L * 1024 * 1024) throw new InvalidDataException("离线 hive 超出只读解析上限。");
        var temporary = Path.Combine(Path.GetTempPath(), "TunHotspot-hive-" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            var acl=new FileSecurity();acl.SetAccessRuleProtection(true,false);
            foreach(string sid in new[]{Contract.Sid,"S-1-5-18","S-1-5-32-544"}.Distinct())acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,AccessControlType.Allow));
            using(var target=new FileInfo(temporary).Create(FileMode.CreateNew,FileSystemRights.Read|FileSystemRights.Write,FileShare.None,65536,FileOptions.None,acl))
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {
                var buffer=new byte[65536];int count;
                while((count=input.Read(buffer))>0){if(target.Length+count>128L*1024*1024)throw new InvalidDataException("复制期间 hive 超出解析上限。");target.Write(buffer,0,count);}
            }
            var hive = new OfflineHive(File.ReadAllBytes(temporary)); var output = new List<HiveValue>();
            foreach (string key in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce", @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run", @"Software\Microsoft\Windows NT\CurrentVersion\Windows" })
            {
                int? node = hive.Open(key); if (!node.HasValue) continue;
                uint count = hive.U32(node.Value + 0x24); if (count > 20000) throw new InvalidDataException("hive 值列表过大。");
                if (count == 0) continue; int list = hive.Cell(hive.U32(node.Value + 0x28));
                for (int i = 0; i < count; i++)
                {
                    int vk = hive.Cell(hive.U32(list + i * 4)); int length = hive.U16(vk + 2); hive.Bounds(vk + 0x14, length);
                    string name = ((hive.U16(vk + 0x10) & 1) != 0 ? Encoding.Latin1 : Encoding.Unicode).GetString(hive._data, vk + 0x14, length);
                    if (key.EndsWith(@"CurrentVersion\Windows") && !new[] { "load", "run" }.Contains(name.ToLowerInvariant())) continue;
                    uint type = hive.U32(vk + 0xC); if (type is not (1 or 2 or 7)) continue;
                    uint rawSize = hive.U32(vk + 4); int size = checked((int)(rawSize & 0x7FFFFFFF)); if (size > 1_000_000) throw new InvalidDataException("hive 值超出上限。");
                    int data = (rawSize & 0x80000000) != 0 ? vk + 8 : hive.Cell(hive.U32(vk + 8)); hive.Bounds(data, size);
                    output.Add(new(key, name, Encoding.Unicode.GetString(hive._data, data, size).TrimEnd('\0')));
                }
            }
            return output;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
