using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace TunHotspot.Core;

public static class DatabaseRecovery
{
    public static string RestoreLatest(string root)
    {
        root=KumiryoPrivateFiles.Physical(root);KumiryoPrivateFiles.NoLinks(root);
        using var lease=new FileStream(ToolboxStore.MaintenanceFile(root),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        string folder=Path.Combine(root,"database-backups");
        string? selected=Directory.Exists(folder)?Directory.GetFiles(folder,"toolbox-*.db").OrderDescending().FirstOrDefault(Valid):null;
        if(selected is null)throw new InvalidDataException("没有找到完整性核验通过的本地备份，未重置数据库。请保留原文件进行离线恢复。");
        string db=Path.Combine(root,"toolbox.db");
        // Never guess which hot journal is authoritative. Offline recovery must
        // preserve and reconcile that set before a replacement is possible.
        if(new[]{"-wal","-journal"}.Any(s=>File.Exists(db+s)&&new FileInfo(db+s).Length>0))throw new IOException("仍存在非空旁日志，已停止恢复。请关闭本软件的其他进程后进行成套离线核验，不能删除旁日志。");
        if(File.Exists(db))using(var probe=new FileStream(db,FileMode.Open,FileAccess.Read,FileShare.None)){}
        string archive=Path.Combine(root,"recovery-backups","restore-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));KumiryoPrivateFiles.Directory(archive);
        foreach(string suffix in new[]{"","-wal","-shm","-journal"})if(File.Exists(db+suffix))File.Copy(db+suffix,Path.Combine(archive,"toolbox.db"+suffix));
        string temp=Path.Combine(root,"restore-"+Guid.NewGuid().ToString("N")+".db");File.Copy(selected,temp);
        if(!Valid(temp))throw new InvalidDataException("恢复临时副本未通过核验，原库保留。");
        if(File.Exists(db))
        {
            bool encrypted=(File.GetAttributes(db)&FileAttributes.Encrypted)!=0;
            if(encrypted&&(File.GetAttributes(temp)&FileAttributes.Encrypted)==0)throw new IOException("恢复副本未继承原库加密属性，原库保留。");
            new FileInfo(temp).SetAccessControl(new FileInfo(db).GetAccessControl());
        }
        if(!MoveFileEx(KumiryoPrivateFiles.Physical(temp),db,9))throw new Win32Exception(Marshal.GetLastWin32Error());
        new DiagnosticLog(root).Event("数据库","警告","已从核验备份恢复数据库",$"备份 {Path.GetFileName(selected)}；替换前原件归档 {Path.GetFileName(archive)}。备份之后的变化需要复扫。");
        return archive;
    }
    private static bool Valid(string path)
    {
        try
        {
            KumiryoPrivateFiles.NoLinks(path);using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());db.Open();using var check=db.CreateCommand();check.CommandText="PRAGMA integrity_check";if(check.ExecuteScalar() as string!="ok")return false;
            check.CommandText="SELECT value FROM metadata WHERE key='schema'";return check.ExecuteScalar() as string is string value&&Contract.Decode<string>(value)=="1";
        }
        catch(Exception ex)when(ex is SqliteException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException){return false;}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileEx(string source,string target,uint flags);
}
