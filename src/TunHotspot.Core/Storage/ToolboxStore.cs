using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace TunHotspot.Core;

public sealed class ToolboxStore
{
    public string Root { get; }
    private readonly string _connection;
    public ToolboxStore(string root) : this(root, false) { }
    public ToolboxStore(string root, bool verifyIntegrity)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        // Resolve once so .db, journal and maintenance leases use one namespace.
        Root = KumiryoPrivateFiles.Physical(Root);
        // Closing each operation also closes its file handle. An offline repair must
        // never leave a pooled connection pointing at a replaced database generation.
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "toolbox.db"), DefaultTimeout = 10, Pooling = false }.ToString();
        // Check a future schema through a read-only connection before WAL or DDL changes.
        if (File.Exists(Path.Combine(Root, "toolbox.db")))
        {
            for(int retry=0;;retry++)
            {
                try{ProbeExisting(verifyIntegrity);break;}
                catch(SqliteException ex)when(ex.SqliteExtendedErrorCode==776&&retry<50)
                {
                    try{RecoverInterruptedTransaction();}
                    catch(IOException locked)when(retry<50&&SharingConflict(locked)){Thread.Sleep(100);continue;}
                }
                catch(IOException locked)when(retry<50&&SharingConflict(locked)){Thread.Sleep(100);}
            }
        }
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=DELETE;
            CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS snapshots (id TEXT PRIMARY KEY, time TEXT NOT NULL, hash TEXT NOT NULL, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS watches (id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS alerts (id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS events (id TEXT PRIMARY KEY, time TEXT NOT NULL, data TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS event_time ON events(time);
            CREATE TABLE IF NOT EXISTS pending (id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS pending_ignore (id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS batches (id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS decisions (id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS control_history (id TEXT PRIMARY KEY, data TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS decision_history (id TEXT PRIMARY KEY, entryId TEXT NOT NULL, time TEXT NOT NULL, data TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        var version = Get<string>("schema");
        if (version is not null && version != "1") throw new InvalidDataException("数据库版本不兼容，未修改现有数据。");
        Set("schema", "1");
    }
    private void ProbeExisting(bool verifyIntegrity)
    {
        using var probe=Connect(new SqliteConnectionStringBuilder{DataSource=Path.Combine(Root,"toolbox.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        using var check=probe.CreateCommand();
        if(verifyIntegrity){check.CommandText="PRAGMA quick_check(1)";if(check.ExecuteScalar() as string!="ok")throw new SqliteException("本地数据库完整性检查未通过，已停止加载并保留原文件。",11);}
        check.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='metadata'";
        if(Convert.ToInt64(check.ExecuteScalar())>0)
        {check.CommandText="SELECT value FROM metadata WHERE key='schema'";string? schema=check.ExecuteScalar() as string;if(schema is not null&&Contract.Decode<string>(schema)!="1")throw new InvalidDataException("数据库版本不兼容，未写入应用数据。");}
        else{check.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";if(Convert.ToInt64(check.ExecuteScalar())>0)throw new InvalidDataException("数据库结构未知，未写入应用数据。");}
    }
    private void RecoverInterruptedTransaction()
    {
        using var lease=new FileStream(MaintenanceFile(Root),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        string path=Path.Combine(Root,"toolbox.db"),journal=path+"-journal";
        // A simultaneous client may already have finished the native recovery.
        // The caller always reruns the complete read-only probe afterward.
        if(!File.Exists(journal)||new FileInfo(journal).Length<512)return;
        using(var guard=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)){}
        string archive=Path.Combine(Root,"transaction-recovery",DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));KumiryoPrivateFiles.Directory(archive);
        foreach(string suffix in new[]{"","-journal","-wal","-shm"})if(File.Exists(path+suffix))File.Copy(path+suffix,Path.Combine(archive,"toolbox.db"+suffix));
        // This connection does not perform DDL or application writes. SQLite
        // itself rolls back the interrupted transaction before reading schema.
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=10}.ToString()))
        {db.Open();using var read=db.CreateCommand();read.CommandText="SELECT COUNT(*) FROM sqlite_master";read.ExecuteScalar();}
        new DiagnosticLog(Root).Event("数据库","警告","已恢复异常退出遗留的未提交事务","数据库及旁日志原件已成套归档："+archive+"；随后重新核验完整性及结构，未创建空库。");
    }
    private static bool SharingConflict(Exception error)=>error is IOException&&((error.HResult&0xffff) is 32 or 33||error.InnerException is Exception inner&&SharingConflict(inner));
    public static string MaintenanceFile(string root) => Path.Combine(Path.GetFullPath(root), "toolbox-maintenance.lock");
    private SqliteConnection Connect(string connection)
    {
        FileStream lease;
        try { lease = new(MaintenanceFile(Root), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite); }
        catch (IOException ex) { throw new IOException("启动数据库正在离线维护，请等待维护完成后重新核验；未写入数据库。", ex); }
        var db = new SqliteConnection(connection);
        db.StateChange += (_, e) => { if (e.CurrentState == System.Data.ConnectionState.Closed) lease.Dispose(); };
        try
        {
            db.Open();
            if (!connection.Contains("Mode=ReadOnly", StringComparison.OrdinalIgnoreCase))
            {
                using var safety = db.CreateCommand();
                safety.CommandText = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=10000;";
                safety.ExecuteNonQuery();
            }
            return db;
        }
        catch { db.Dispose(); lease.Dispose(); throw; }
    }
    private SqliteConnection Open() => Connect(_connection);
    public string CreateVerifiedBackup()
    {
        using var gate=new Mutex(false,@"Local\TunHotspot.Backup."+Contract.Hash(Root.ToUpperInvariant())[..20]);
        bool held;try{held=gate.WaitOne(0);}catch(AbandonedMutexException){held=true;}
        if(!held)throw new IOException("另一进程正在生成数据库备份，请稍后重试。");
        try
        {
        string folder=Path.Combine(Root,"database-backups");KumiryoPrivateFiles.Directory(folder);
        string target=Path.Combine(folder,$"toolbox-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db");
        string partial=Path.Combine(folder,"partial-"+Guid.NewGuid().ToString("N")+".db");
        using(var source=Open())using(var backup=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=partial,Pooling=false}.ToString()))
        {backup.Open();source.BackupDatabase(backup);using var check=backup.CreateCommand();check.CommandText="PRAGMA integrity_check";if(check.ExecuteScalar() as string!="ok")throw new InvalidDataException("备份完整性检查失败，未将其列为可恢复备份。");check.CommandText="PRAGMA journal_mode=DELETE";check.ExecuteNonQuery();}
        File.Move(partial,target);
        new DiagnosticLog(Root).Event("数据库","信息","已生成并核验数据库备份",Path.GetFileName(target));
        // Keep all originals/repair archives; only retain the newest five owned online backups.
        foreach(string old in Directory.GetFiles(folder,"toolbox-*.db").OrderDescending().Skip(5))try{File.Delete(old);}catch(IOException){}
        return target;
        }
        finally{gate.ReleaseMutex();}
    }
    private void Execute(string sql, params (string Key, object? Value)[] values)
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = sql;
        foreach (var (key, value) in values) c.Parameters.AddWithValue(key, value ?? DBNull.Value);
        c.ExecuteNonQuery();
    }
    private List<string> Query(string sql, params (string Key, object? Value)[] values)
    {
        using var db = Open(); using var c = db.CreateCommand(); c.CommandText = sql;
        foreach (var (key, value) in values) c.Parameters.AddWithValue(key, value ?? DBNull.Value);
        using var r = c.ExecuteReader(); var output = new List<string>();
        while (r.Read()) output.Add(r.GetString(0)); return output;
    }
    public T? Get<T>(string key)
    {
        var raw = Query("SELECT value FROM metadata WHERE key=$key", ("$key", key)).FirstOrDefault();
        return raw is null ? default : Contract.Decode<T>(raw);
    }
    public void Set<T>(string key, T value) => Execute("INSERT INTO metadata VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", Contract.Encode(value)));
    public ScanSnapshot? Latest { get { var snapshot=Get<ScanSnapshot>("latest");if(snapshot is not null){AttachNotes(snapshot);AttachOrigins(snapshot);}return snapshot; } }
    public void AttachOrigins(ScanSnapshot snapshot)
    {
        var marks=Query("SELECT value FROM metadata WHERE key LIKE 'origin:%'").Select(Contract.Decode<CreationOriginMark>).ToLookup(x=>x.EntryId);
        foreach(var entry in snapshot.Entries)entry.CreationMarks=marks[entry.Id].ToList();
    }
    public void SaveOrigin(StartupEntry entry,string agent,string state,string evidence)
    {
        if(!CreationOrigins.Agents.Contains(agent)||state is not ("用户确认" or "已排除" or "清除标记")||evidence.Length>2000||entry.Id.Length!=64)throw new InvalidDataException("来源标记无效。");
        string key="origin:"+entry.Id+":"+agent;
        if(state=="清除标记")Execute("DELETE FROM metadata WHERE key=$key",("$key",key));
        else{if(string.IsNullOrWhiteSpace(evidence))throw new InvalidDataException("请填写确认或排除的依据。");Set(key,new CreationOriginMark(entry.Id,agent,state,evidence,CreationOrigins.TargetKey(entry),DateTimeOffset.UtcNow));}
        AddEvents([new(){Kind="来源标记",Name=entry.Name,Source=agent,Detail=state+"；"+evidence}]);
    }
    private void AttachNotes(ScanSnapshot snapshot)
    {
        var notes=Query("SELECT value FROM metadata WHERE key LIKE 'note:%'").Select(Contract.Decode<PersonalNote>).ToDictionary(x=>x.EntryId,x=>x.Text);
        foreach(var entry in snapshot.Entries)entry.UserNote=notes.GetValueOrDefault(entry.Id,"");
    }
    public void SaveNote(string entryId,string text)
    {if(entryId.Length!=64||text.Length>2000)throw new InvalidDataException("用途备注长度无效。");Set("note:"+entryId,new PersonalNote(entryId,text));Enqueue(entryId);}
    public UserSettings Settings { get => Get<UserSettings>("settings") ?? new(); set => Set("settings", value); }
    public List<WatchRule> Watches => Query("SELECT data FROM watches").Select(Contract.Decode<WatchRule>).ToList();
    public List<AlertEvent> Alerts => Query("SELECT data FROM alerts").Select(Contract.Decode<AlertEvent>).OrderByDescending(x => x.LastSeen).ToList();
    public List<ObservedEvent> Events(int limit = 1000) => Query("SELECT data FROM events ORDER BY time DESC LIMIT $limit", ("$limit", Math.Clamp(limit, 1, 5000))).Select(Contract.Decode<ObservedEvent>).ToList();
    public List<string> Pending => Query("SELECT id FROM pending");
    public List<SnapshotSummary> History => Query("SELECT json_object('id',id,'time',time,'count',json_array_length(data,'$.entries')) FROM snapshots ORDER BY time DESC LIMIT 200").Select(Contract.Decode<SnapshotSummary>).ToList();
    public ScanSnapshot? GetSnapshot(string id) => Query("SELECT data FROM snapshots WHERE id=$id",("$id",id)).Select(Contract.Decode<ScanSnapshot>).FirstOrDefault();
    public void Enqueue(string id) => Execute("DELETE FROM pending_ignore WHERE id=$id; INSERT OR IGNORE INTO pending VALUES($id)", ("$id", id));
    public void RemovePending(string id)
    {var entry=Latest?.Entries.FirstOrDefault(x=>x.Id==id);if(entry is not null)Execute("INSERT INTO pending_ignore VALUES($id,$fingerprint) ON CONFLICT(id) DO UPDATE SET fingerprint=excluded.fingerprint",("$id",id),("$fingerprint",entry.Fingerprint));Execute("DELETE FROM pending WHERE id=$id",("$id",id));}
    public List<StartupControlRecord> Controls=>Query("SELECT data FROM control_history").Select(Contract.Decode<StartupControlRecord>).OrderByDescending(x=>x.Created).ToList();
    public void SaveControl(StartupControlRecord record)=>Execute("INSERT INTO control_history VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=excluded.data",("$id",record.Id),("$data",Contract.Encode(record)));
    public ScanSnapshot? MergeControlEntry(StartupEntry entry)
    {
        var snapshot=Latest;if(snapshot is null)return null;snapshot.Id=Guid.NewGuid().ToString("N");
        snapshot.Entries.RemoveAll(x=>x.Id==entry.Id);snapshot.Entries.Add(entry);
        snapshot.BootObservation="已合入单项管理员复核结果；其他条目仍对应各自的观察时刻。";
        SaveSnapshot(snapshot);return snapshot;
    }
    public int ReconcilePending(ScanSnapshot? snapshot=null)
    {
        snapshot??=Latest;if(snapshot is null)return 0;
        var decisions=Decisions.ToDictionary(x=>x.EntryId);int added=0;var pending=Pending.ToHashSet();
        var ignored=Query("SELECT json_object('entryId',id,'text',fingerprint) FROM pending_ignore").Select(Contract.Decode<PersonalNote>).ToDictionary(x=>x.EntryId,x=>x.Text);
        foreach(var e in snapshot.Entries)
        {
            bool valid=decisions.TryGetValue(e.Id,out var decision)&&e.MatchesFingerprint(decision.Fingerprint);
            if((valid?decision!.Advice:e.Advice)=="待分析"&&(!ignored.TryGetValue(e.Id,out string? fingerprint)||!e.MatchesFingerprint(fingerprint))&&pending.Add(e.Id)){Enqueue(e.Id);added++;}
        }
        return added;
    }
    public void SaveWatch(WatchRule rule)
    {
        if (rule.EntryId.Length != 64 || rule.Expected.Length > 300 || rule.Name.Length > 1024) throw new InvalidDataException("关注规则无效。");
        Execute("INSERT INTO watches VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=excluded.data", ("$id", rule.EntryId), ("$data", Contract.Encode(rule)));
    }
    public void RemoveWatch(string id) => Execute("DELETE FROM watches WHERE id=$id", ("$id", id));
    public void SaveAlert(AlertEvent alert) => Execute("INSERT INTO alerts VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=excluded.data", ("$id", alert.Id), ("$data", Contract.Encode(alert)));
    public void MergeRemoteAlerts(IEnumerable<AlertEvent> alerts)
    {
        var existing = Alerts.ToDictionary(x => x.Id);
        foreach (var alert in alerts)
        {
            if (existing.TryGetValue(alert.Id, out var old)) { alert.Delivery = old.Delivery; alert.IsRead = old.IsRead; }
            SaveAlert(alert);
        }
    }
    public void SaveSnapshot(ScanSnapshot snapshot)
    {
        if (snapshot.Protocol != Contract.Version || snapshot.DeviceId != Contract.DeviceId) throw new InvalidDataException("快照不属于本机或协议不兼容。");
        if(snapshot.Cancelled){Set("lastCancelledScan",new{snapshot.Id,snapshot.Started,snapshot.Finished,snapshot.Privilege});AddEvents([new(){Kind="扫描取消",Name="配置采集",Detail="未将部分结果发布为新的当前基线。"}]);return;}
        AttachNotes(snapshot);
        AttachOrigins(snapshot);
        var previous = Latest;
        if (previous is not null)
        {
            var ids = snapshot.Entries.Select(x => x.Id).ToHashSet();
            var successful = snapshot.Coverage.Where(x => x.Status == "成功").Select(x => x.Provider).ToHashSet();
            foreach (var old in previous.Entries.Where(x => !ids.Contains(x.Id) && !successful.Contains(x.Provider)))
            {
                var retained = Contract.Decode<StartupEntry>(Contract.Encode(old));
                retained.ObservationStatus = "保留上次结果，本次未确认";
                snapshot.Entries.Add(retained);
            }
        }
        using (var db = Open())
        using (var tx = db.BeginTransaction())
        {
            using var c = db.CreateCommand(); c.Transaction = tx;
            c.CommandText = "INSERT INTO metadata VALUES('latest',$data) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            c.Parameters.AddWithValue("$data", Contract.Encode(snapshot)); c.ExecuteNonQuery();
            if (!snapshot.Cancelled && (previous is null || previous.ConfigurationHash != snapshot.ConfigurationHash))
            {
                c.CommandText = "INSERT INTO snapshots VALUES($id,$time,$hash,$data)";
                c.Parameters.AddWithValue("$id", snapshot.Id); c.Parameters.AddWithValue("$time", snapshot.Finished.ToString("O")); c.Parameters.AddWithValue("$hash", snapshot.ConfigurationHash); c.ExecuteNonQuery();
            }
            tx.Commit();
        }
        EvaluateWatches(snapshot);
        ReconcilePending(snapshot);
        if (previous is not null && !snapshot.Cancelled)
        {
            var before = previous.Entries.ToDictionary(x => x.Id);
            var after = snapshot.Entries.Select(x => x.Id).ToHashSet();
            var successful = snapshot.Coverage.Where(x => x.Status == "成功").Select(x => x.Provider).ToHashSet();
            foreach(var removed in previous.Entries.Where(x=>!after.Contains(x.Id)&&successful.Contains(x.Provider)))
            {RemovePending(removed.Id);AddEvents([new(){Kind="入口消失",Name=removed.Name,Source=removed.Provider,Detail=removed.OneShot?"一次性配置不再存在；不证明执行成功。":removed.Source}]);}
            foreach (var entry in snapshot.Entries)
            {
                if (!before.TryGetValue(entry.Id, out var old)) AddEvents([new() { Kind = "入口出现", Name = entry.Name, Source = entry.Provider, Detail = entry.Source }]);
                else if (old.ControlValue != entry.ControlValue || old.Command != entry.Command)
                    AddEvents([new() { Kind = "配置变化", Name = entry.Name, Source = entry.Provider, Detail = $"{old.ControlValue} → {entry.ControlValue}；{entry.Source}" }]);
            }
        }
    }
    public void EvaluateWatches(ScanSnapshot snapshot)
    {
        if (snapshot.Cancelled) return;
        var entries = snapshot.Entries.ToDictionary(x => x.Id);
        var complete = snapshot.Coverage.Where(x => x.Status == "成功").Select(x => x.Provider).ToHashSet();
        var alerts = Alerts;
        foreach (var rule in Watches)
        {
            if(snapshot.Started<rule.Created)continue;
            string actual;
            entries.TryGetValue(rule.EntryId, out var current);
            if (current is null)
            {
                if (!complete.Contains(rule.Provider) || rule.OneShot) continue;
                actual = "入口不存在";
            }
            else { if (current.ObservationStatus != "本次读取") continue; actual = current.ControlValue; if (actual is "" or "未知" or "状态未确认") continue; }
            var active = alerts.Where(x => x.EntryId == rule.EntryId && !x.Resolved).ToList();
            if (actual == rule.Expected)
            {
                foreach (var item in active) { item.Resolved = true; item.LastSeen = DateTimeOffset.UtcNow; SaveAlert(item); }
                continue;
            }
            var same = active.FirstOrDefault(x => x.Actual == actual);
            if (same is not null) { same.LastSeen = DateTimeOffset.UtcNow; same.Count++; SaveAlert(same); continue; }
            foreach (var item in active) { item.Resolved = true; SaveAlert(item); }
            SaveAlert(new() { EntryId = rule.EntryId, Title = $"{rule.Name} 偏离关注状态", Expected = rule.Expected, Actual = actual,
                Evidence = $"首次发现于 {snapshot.Finished.ToLocalTime():yyyy-MM-dd HH:mm:ss}；扫描 {snapshot.Id}；权限 {snapshot.Privilege}。修改来源及实际写入时刻未确认。\n{current?.Source}\n{current?.Command}" });
        }
    }
    public void AddEvents(IEnumerable<ObservedEvent> events)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); using var c = db.CreateCommand(); c.Transaction = tx;
        c.CommandText = "INSERT OR IGNORE INTO events VALUES($id,$time,$data)";
        var id = c.Parameters.Add("$id", SqliteType.Text); var time = c.Parameters.Add("$time", SqliteType.Text); var data = c.Parameters.Add("$data", SqliteType.Text);
        foreach (var e in events) { id.Value = e.Id; time.Value = e.Time.ToString("O"); data.Value = Contract.Encode(e); c.ExecuteNonQuery(); }
        tx.Commit();
    }
    public void Maintain()
    {
        Execute("DELETE FROM events WHERE time < $time", ("$time", DateTimeOffset.UtcNow.AddDays(-7).ToString("O")));
        Execute("DELETE FROM snapshots WHERE time < $time", ("$time", DateTimeOffset.UtcNow.AddDays(-30).ToString("O")));
        var size = Query("SELECT CAST(COALESCE(SUM(length(data)),0) AS TEXT) FROM events").FirstOrDefault();
        if (long.TryParse(size, out var bytes) && bytes > 450L * 1024 * 1024)
        {
            Execute("DELETE FROM events WHERE id IN (SELECT id FROM events ORDER BY time LIMIT (SELECT MAX(1,COUNT(*)/4) FROM events))");
            Set("eventRetention", "高流量事件达到容量阈值，已清理最旧记录；长期告警不受影响。");
        }
    }
    public static WatchRule Watch(StartupEntry entry) => new() { EntryId = entry.Id, Provider = entry.Provider, Name = entry.Name, Expected = entry.ControlValue, OwnerSid = entry.OwnerSid, OneShot = entry.OneShot };
    public AnalysisBatch CreateBatch()
    {
        var snapshot = Latest ?? throw new InvalidOperationException("请先完成扫描。");
        ReconcilePending(snapshot);
        var selected = Pending.ToHashSet(); var batch = new AnalysisBatch { SnapshotId = snapshot.Id };
        var reviewed=Decisions.ToDictionary(x=>x.EntryId);
        batch.Items = snapshot.Entries.Where(x => selected.Contains(x.Id)&&x.ObservationStatus=="本次读取").OrderBy(x=>reviewed.TryGetValue(x.Id,out var d)&&x.MatchesFingerprint(d.Fingerprint)?1:0).Take(100).Select(x => new AnalysisItem(x.Id, x.Fingerprint, Redact(x))).ToList();
        if (batch.Items.Count == 0) throw new InvalidOperationException("没有已重新确认的待分析条目；历史保留项目需要先重新扫描确认。");
        Execute("INSERT INTO batches VALUES($id,$data)", ("$id", batch.Id), ("$data", Contract.Encode(batch)));
        return batch;
    }
    public AnalysisBatch? GetBatch(string id) => Query("SELECT data FROM batches WHERE id=$id", ("$id", id)).Select(Contract.Decode<AnalysisBatch>).FirstOrDefault();
    public List<AnalysisDecision> Decisions => Query("SELECT data FROM decisions").Select(Contract.Decode<AnalysisDecision>).ToList();
    public void ApplyAnalysis(AnalysisResult result)
    {
        if (result.Protocol != Contract.Version || result.DeviceId != Contract.DeviceId) throw new InvalidDataException("分析结果不属于本机或协议不兼容。");
        // Reserve the writer before reading fingerprints so another process cannot change notes/latest between validation and commit.
        using var db = Open(); using var tx = db.BeginTransaction();
        var batch = GetBatch(result.BatchId) ?? throw new InvalidDataException("找不到对应分析批次。");
        var current = Latest?.Entries.ToDictionary(x => x.Id) ?? throw new InvalidOperationException("没有当前快照。");
        if (result.Decisions.Count is < 1 or > 100) throw new InvalidDataException("分析结果数量无效。");
        if (result.Decisions.Select(x => x.EntryId).Distinct().Count() != result.Decisions.Count) throw new InvalidDataException("结果含重复条目。");
        var advice = new[] { "建议保留", "按需保留", "可考虑取消", "需修复", "待分析" };
        foreach (var d in result.Decisions)
        {
            if (!batch.Items.Any(x => x.EntryId == d.EntryId && x.Fingerprint == d.Fingerprint) || !current.TryGetValue(d.EntryId, out var entry) || !entry.MatchesFingerprint(d.Fingerprint) || entry.ObservationStatus != "本次读取")
                throw new InvalidDataException("分析已过期或条目不匹配，请重新分析。");
            if (!advice.Contains(d.Advice) || d.Reason.Length is < 1 or > 4000 || d.Impact.Length > 4000 || !new[] { "高", "中", "低" }.Contains(d.Confidence)) throw new InvalidDataException("建议字段不符合协议。");
        }
        foreach (var d in result.Decisions)
        {
            using var c = db.CreateCommand(); c.Transaction = tx; c.CommandText = "INSERT INTO decisions VALUES($id,$data) ON CONFLICT(id) DO UPDATE SET data=excluded.data;"+(d.Advice=="待分析"?"INSERT OR IGNORE INTO pending VALUES($id)":"DELETE FROM pending WHERE id=$id");
            c.Parameters.AddWithValue("$id", d.EntryId); c.Parameters.AddWithValue("$data", Contract.Encode(d)); c.ExecuteNonQuery();
            c.Parameters.Clear(); c.CommandText = "INSERT OR IGNORE INTO decision_history VALUES($id,$entry,$time,$data)";
            c.Parameters.AddWithValue("$id", result.BatchId + ":" + d.EntryId); c.Parameters.AddWithValue("$entry", d.EntryId);
            c.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O")); c.Parameters.AddWithValue("$data", Contract.Encode(d)); c.ExecuteNonQuery();
        }
        tx.Commit();
        Set("lastAnalysis", DateTimeOffset.UtcNow);
    }
    private static StartupEntry Redact(StartupEntry value)
    {
        var copy = Contract.Decode<StartupEntry>(Contract.Encode(value));
        static string Clean(string text)
        {
            var clean = Regex.Replace(text,
                """(?i)((?:--?|/)?(?:token|password|passwd|secret|api[_-]?key)\s*(?:=|:|\s)\s*)(?:"[^"]*"|'[^']*'|[^\s;,<]+)""",
                "$1[REDACTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
            clean = Regex.Replace(clean, """(?i)(-EncodedCommand\s+)[A-Za-z0-9+/=]+""", "$1[ENCODED_CONTENT_OMITTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? clean : clean.Replace(home, "[USERPROFILE]", StringComparison.OrdinalIgnoreCase);
        }
        copy.Command = Clean(copy.Command);
        copy.UserNote=Clean(copy.UserNote);
        foreach (var key in copy.Configuration.Keys.ToList()) copy.Configuration[key] = Clean(copy.Configuration[key]);
        foreach (var key in copy.Evidence.Keys.ToList()) copy.Evidence[key] = Clean(copy.Evidence[key]);
        return copy;
    }
}
