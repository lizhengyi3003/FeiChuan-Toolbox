using Microsoft.Data.Sqlite;
using TunHotspot.Core;

internal static class SnapshotHistoryTests
{
    internal static void Run(string root, Action<bool, string> Check)
    {
        StartupEntry Entry(bool enabled = true) => new()
        {
            Id = new string('a', 64), Provider = "run", Name = "Fixture", Enabled = enabled,
            Configuration = new() { ["enabled"] = enabled.ToString() }
        };
        ScanSnapshot Snapshot(StartupEntry entry) => new()
        {
            OwnerSid = Contract.Sid, Finished = DateTimeOffset.UtcNow, Entries = [entry],
            Coverage = [new() { Provider = "run", Status = "成功" }]
        };

        var historyStore = new ToolboxStore(Path.Combine(root, "snapshot-history"));
        var shared = Entry();
        shared.Id = new string('b', 64);
        var first = Snapshot(Entry());
        first.Entries.Add(shared);
        first.Processes.Add(new() { Pid = 123, Name = "fixture" });
        historyStore.SaveSnapshot(first);
        var second = Snapshot(Entry(false));
        second.Entries.Add(shared);
        historyStore.SaveSnapshot(second);
        using (var historyDb = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(historyStore.Root, "toolbox.db")))
        {
            historyDb.Open();
            using var statement = historyDb.CreateCommand();
            statement.CommandText = "SELECT COUNT(*) FROM snapshot_objects";
            Check((long)statement.ExecuteScalar()! == 3, "快照复用未变化的入口，只保存新增或修改的对象");
        }
        Check(historyStore.GetSnapshot(first.Id) is { } reconstructed &&
            Contract.Encode(reconstructed) == Contract.Encode(first), "历史提交完整重建原始入口、顺序与进程数据");
        for (int revision = 0; revision < 201; revision++)
        {
            var next = Snapshot(Entry());
            next.Entries[0].Configuration["revision"] = revision.ToString();
            next.Entries.Add(shared);
            historyStore.SaveSnapshot(next);
        }
        Check(historyStore.History.Count == 200 && historyStore.GetSnapshot(first.Id) is null &&
            historyStore.GetSnapshot(historyStore.History[^1].Id)?.Entries[1].Id == shared.Id,
            "快照达到上限时删除最旧提交，保留仍被引用的入口");
        VerifyCapacity(root, Check, Snapshot, Entry);
    }

    private static void VerifyCapacity(string root, Action<bool, string> check,
        Func<StartupEntry, ScanSnapshot> snapshot, Func<bool, StartupEntry> entry)
    {
        var store = new ToolboxStore(Path.Combine(root, "snapshot-capacity"));
        var first = snapshot(entry(true));
        var latest = snapshot(entry(false));
        var shared = entry(true);
        shared.Id = new string('b', 64);
        first.Entries.Add(shared);
        latest.Entries.Add(shared);
        store.SaveSnapshot(first);
        store.SaveSnapshot(latest);

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(store.Root, "toolbox.db"), Pooling = false
        }.ToString());
        db.Open();
        string legacyId = Guid.NewGuid().ToString("N");
        using (var transaction = db.BeginTransaction())
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO snapshots VALUES ($id, $time, 'fixture', $data)";
            command.Parameters.AddWithValue("$id", legacyId);
            command.Parameters.AddWithValue("$time", first.Finished.AddDays(-1).ToString("O"));
            command.Parameters.AddWithValue("$data", Contract.Encode(first));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = """
                SELECT length(header) + length(tree) +
                    (SELECT SUM(length(data)) FROM snapshot_objects WHERE last_used = sequence)
                FROM snapshot_revisions ORDER BY sequence DESC LIMIT 1;
                """;
            long newestBytes = (long)command.ExecuteScalar()!;
            check(SnapshotHistory.CapacityBytes == 1L << 30 &&
                SnapshotHistory.TrimCapacity(db, transaction, newestBytes), "容量超限会触发淘汰");
            transaction.Commit();
        }

        check(store.GetSnapshot(legacyId) is null && store.GetSnapshot(first.Id) is null &&
            store.GetSnapshot(latest.Id)?.Entries[1].Id == shared.Id,
            "按容量淘汰最旧历史，涵盖旧格式并保留共享对象");
        using (var transaction = db.BeginTransaction())
        {
            check(SnapshotHistory.TrimCapacity(db, transaction, 0), "单次提交超出容量时也会淘汰");
            transaction.Commit();
        }
        check(store.History.Count == 0 && store.Latest?.Id == latest.Id,
            "历史额度用尽时仍保留当前扫描基线");
    }
}
