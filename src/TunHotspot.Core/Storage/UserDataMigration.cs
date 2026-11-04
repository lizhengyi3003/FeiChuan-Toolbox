using Microsoft.Data.Sqlite;

namespace TunHotspot.Core;

public static class UserDataMigration
{
    // Copies a verified online SQLite snapshot; never renames or deletes an old
    // database while an older GUI/MCP process may still have it open.
    public static void Ensure(string? targetRoot=null,string? legacyRoot=null)
    {
        string target=targetRoot??DataPaths.UserRoot,source=legacyRoot??DataPaths.LegacyUserRoot;
        using var gate=new Mutex(false,@"Local\TunHotspot.DataMigration."+Contract.Hash(target.ToUpperInvariant())[..20]);
        try{if(!gate.WaitOne(TimeSpan.FromSeconds(15)))throw new IOException("另一个进程正在迁移工具箱数据，请稍后重试。");}
        catch(AbandonedMutexException){ }
        try
        {
            KumiryoPrivateFiles.Directory(target);KumiryoPrivateFiles.NoLinks(source);
            if(File.Exists(Path.Combine(target,"toolbox.db")))return;
            foreach(string name in new[]{"preferences.json","preferences.json.previous"})
                if(File.Exists(Path.Combine(source,name))&&!File.Exists(Path.Combine(target,name)))File.Copy(Path.Combine(source,name),Path.Combine(target,name));
            foreach(string folder in new[]{"diagnostics","KumiryoConfiguration"})
            {
                string from=Path.Combine(source,folder);if(!Directory.Exists(from))continue;
                foreach(string file in Directory.EnumerateFiles(from,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}).Take(10000))
                {
                    KumiryoPrivateFiles.NoLinks(file);if(new FileInfo(file).Length>100*1024*1024)throw new IOException("历史配置文件过大，已暂停迁移并保留原件。");
                    string copy=Path.Combine(target,Path.GetRelativePath(source,file));KumiryoPrivateFiles.Directory(Path.GetDirectoryName(copy)!);if(!File.Exists(copy))File.Copy(file,copy);
                }
            }
            string old=Path.Combine(source,"toolbox.db");if(!File.Exists(old))return;
            using var read=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=KumiryoPrivateFiles.Physical(old),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());read.Open();
            using var check=read.CreateCommand();check.CommandText="PRAGMA integrity_check";
            if(check.ExecuteScalar() as string!="ok")throw new InvalidDataException("旧启动数据库完整性检查失败。原件保留，未创建空库；请先从已核验备份恢复到新的统一数据目录。");
            string temp=Path.Combine(target,"migration-"+Guid.NewGuid().ToString("N")+".db");
            using(var write=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=temp,Pooling=false}.ToString())){write.Open();read.BackupDatabase(write);using var verify=write.CreateCommand();verify.CommandText="PRAGMA integrity_check";if(verify.ExecuteScalar() as string!="ok")throw new InvalidDataException("迁移副本完整性检查失败，原库未变。");}
            File.Move(temp,Path.Combine(target,"toolbox.db"));
            new DiagnosticLog(target).Event("数据库","信息","旧数据已迁入统一目录","使用 SQLite 在线备份；原目录保留。" );
        }
        finally{gate.ReleaseMutex();}
    }
}
