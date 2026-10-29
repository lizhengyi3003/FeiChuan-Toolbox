using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace TunHotspot.Core;

public sealed class ToolboxStore
{
    public string Root { get; }
    private readonly string _connection;
    public ToolboxStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "toolbox.db"), DefaultTimeout = 10 }.ToString();
        // Check a future schema through a read-only connection before WAL or DDL changes.
        if (File.Exists(Path.Combine(Root, "toolbox.db")))
        {
            using var probe = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root,"toolbox.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            probe.Open(); using var check = probe.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='metadata'";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
            {
                check.CommandText = "SELECT value FROM metadata WHERE key='schema'";
                string? schema = check.ExecuteScalar() as string;
                if (schema is not null && Contract.Decode<string>(schema) != "1") throw new InvalidDataException("数据库版本不兼容，未修改现有数据。");
            }
            else
            {
                check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt64(check.ExecuteScalar()) > 0) throw new InvalidDataException("数据库结构未知，未修改现有数据。");
            }
        }
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
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
    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
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
    public ScanSnapshot? Latest { get { var snapshot=Get<ScanSnapshot>("latest");if(snapshot is not null)AttachNotes(snapshot);return snapshot; } }
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
            bool valid=decisions.TryGetValue(e.Id,out var decision)&&decision.Fingerprint==e.Fingerprint;
            if((valid?decision!.Advice:e.Advice)=="待分析"&&(!ignored.TryGetValue(e.Id,out string? fingerprint)||fingerprint!=e.Fingerprint)&&pending.Add(e.Id)){Enqueue(e.Id);added++;}
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
        batch.Items = snapshot.Entries.Where(x => selected.Contains(x.Id)).Take(100).Select(x => new AnalysisItem(x.Id, x.Fingerprint, Redact(x))).ToList();
        if (batch.Items.Count == 0) throw new InvalidOperationException("没有可分析条目。请将条目加入待分析区。");
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
            if (!batch.Items.Any(x => x.EntryId == d.EntryId && x.Fingerprint == d.Fingerprint) || !current.TryGetValue(d.EntryId, out var entry) || entry.Fingerprint != d.Fingerprint || entry.ObservationStatus != "本次读取")
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
