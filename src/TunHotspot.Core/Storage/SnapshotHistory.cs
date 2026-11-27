using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text;

namespace TunHotspot.Core;

internal static class SnapshotHistory
{
    private const int Limit = 200;
    internal const long CapacityBytes = 1L << 30;

    internal const string Schema = """
        CREATE TABLE IF NOT EXISTS snapshot_revisions (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT, id TEXT UNIQUE NOT NULL,
            time TEXT NOT NULL, entry_count INTEGER NOT NULL, header BLOB NOT NULL, tree BLOB NOT NULL);
        CREATE TABLE IF NOT EXISTS snapshot_objects (
            hash TEXT PRIMARY KEY, data BLOB NOT NULL, last_used INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS snapshot_object_last_use ON snapshot_objects(last_used);
        """;

    internal static bool Write(SqliteConnection db, SqliteTransaction transaction, ScanSnapshot snapshot)
    {
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        var tree = new List<string>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries)
        {
            string data = Contract.Encode(entry);
            string hash = Contract.Hash(data);
            tree.Add(hash);
            objects.TryAdd(hash, data);
        }

        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshot_revisions(id, time, entry_count, header, tree)
            VALUES ($id, $time, $count, $header, $tree) RETURNING sequence;
            """;
        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$time", snapshot.Finished.ToString("O"));
        command.Parameters.AddWithValue("$count", tree.Count);
        command.Parameters.AddWithValue("$header", Compress(EncodeHeader(snapshot)));
        command.Parameters.AddWithValue("$tree", Compress(Contract.Encode(tree)));
        long sequence = (long)command.ExecuteScalar()!;

        command.Parameters.Clear();
        command.CommandText = "SELECT hash FROM snapshot_objects WHERE hash IN (SELECT value FROM json_each($hashes))";
        command.Parameters.AddWithValue("$hashes", Contract.Encode(objects.Keys));
        using (var reader = command.ExecuteReader())
            while (reader.Read()) objects.Remove(reader.GetString(0));

        command.CommandText = "UPDATE snapshot_objects SET last_used=$sequence WHERE hash IN (SELECT value FROM json_each($hashes))";
        command.Parameters.AddWithValue("$sequence", sequence);
        command.ExecuteNonQuery();

        command.Parameters.Clear();
        command.CommandText = "INSERT INTO snapshot_objects VALUES ($hash, $data, $sequence)";
        var id = command.Parameters.Add("$hash", SqliteType.Text);
        var content = command.Parameters.Add("$data", SqliteType.Blob);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Prepare();
        foreach (var (hash, data) in objects)
        {
            id.Value = hash;
            content.Value = Compress(data);
            command.ExecuteNonQuery();
        }

        return Prune(db, transaction, keepRecent: true);
    }

    internal static ScanSnapshot? Read(SqliteConnection db, string id)
    {
        using var transaction = db.BeginTransaction(deferred: true);
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT header, tree FROM snapshot_revisions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        ScanSnapshot snapshot;
        List<string> tree;
        using (var revision = command.ExecuteReader())
        {
            if (!revision.Read()) return null;
            snapshot = Contract.Decode<ScanSnapshot>(Decompress((byte[])revision[0]));
            tree = Contract.Decode<List<string>>(Decompress((byte[])revision[1]));
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT hash, data FROM snapshot_objects WHERE hash IN (SELECT value FROM json_each($hashes))";
        command.Parameters.AddWithValue("$hashes", Contract.Encode(tree));
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) objects.Add(reader.GetString(0), Decompress((byte[])reader[1]));

        foreach (string hash in tree)
        {
            if (!objects.TryGetValue(hash, out var data))
                throw new InvalidDataException("扫描历史引用了缺失的入口记录。");
            snapshot.Entries.Add(Contract.Decode<StartupEntry>(data));
        }
        return snapshot;
    }

    internal static bool Prune(SqliteConnection db, SqliteTransaction transaction, bool keepRecent)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        // Remove a prefix so an object's most recent reference is enough to decide if it is still live.
        command.CommandText = """
            DELETE FROM snapshot_revisions WHERE sequence < (
                SELECT sequence FROM snapshot_revisions ORDER BY sequence DESC LIMIT 1 OFFSET $offset);
            DELETE FROM snapshot_revisions WHERE sequence < COALESCE((
                SELECT MIN(sequence) FROM snapshot_revisions WHERE time >= $cutoff), $end);
            DELETE FROM snapshot_objects WHERE last_used < COALESCE((SELECT MIN(sequence) FROM snapshot_revisions), $end);
            """;
        command.Parameters.AddWithValue("$offset", Limit - 1);
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        command.Parameters.AddWithValue("$end", keepRecent
            ? ReadLatestSequence(db, transaction)
            : long.MaxValue);
        command.ExecuteNonQuery();
        return TrimCapacity(db, transaction);
    }

    internal static bool TrimCapacity(SqliteConnection db, SqliteTransaction transaction, long capacity = CapacityBytes)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE((SELECT SUM(octet_length(data)) FROM snapshots), 0)
                 + COALESCE((SELECT SUM(length(header) + length(tree)) FROM snapshot_revisions), 0)
                 + COALESCE((SELECT SUM(length(data)) FROM snapshot_objects), 0);
            """;
        if ((long)command.ExecuteScalar()! <= capacity) return false;

        // Attribute each shared object to its newest referencing revision. Removing an older
        // revision only releases objects whose last reference has also been removed.
        command.CommandText = """
            WITH objects AS (
                SELECT last_used, SUM(length(data)) AS bytes FROM snapshot_objects GROUP BY last_used
            ), candidates AS (
                SELECT 0 AS kind, rowid AS number, time, octet_length(data) AS bytes FROM snapshots
                UNION ALL
                SELECT 1, sequence, MAX(time) OVER (ORDER BY sequence ROWS UNBOUNDED PRECEDING),
                       length(header) + length(tree) + COALESCE(objects.bytes, 0)
                FROM snapshot_revisions LEFT JOIN objects ON objects.last_used = sequence
            ), ranked AS (
                SELECT kind, number, SUM(bytes) OVER (
                    ORDER BY time DESC, kind DESC, number DESC ROWS UNBOUNDED PRECEDING
                ) AS retained_bytes FROM candidates
            )
            SELECT kind, number FROM ranked WHERE retained_bytes > $capacity;
            """;
        command.Parameters.AddWithValue("$capacity", capacity);
        long lastRevisionToRemove = 0;
        var legacyRowsToRemove = new List<long>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                long number = reader.GetInt64(1);
                if (reader.GetInt64(0) == 0) legacyRowsToRemove.Add(number);
                else lastRevisionToRemove = Math.Max(lastRevisionToRemove, number);
            }

        command.Parameters.Clear();
        command.CommandText = """
            DELETE FROM snapshots WHERE rowid IN (SELECT value FROM json_each($legacy));
            DELETE FROM snapshot_revisions WHERE sequence <= $revision;
            DELETE FROM snapshot_objects WHERE last_used <= $revision;
            """;
        command.Parameters.AddWithValue("$legacy", Contract.Encode(legacyRowsToRemove));
        command.Parameters.AddWithValue("$revision", lastRevisionToRemove);
        return command.ExecuteNonQuery() > 0;
    }

    private static long ReadLatestSequence(SqliteConnection db, SqliteTransaction transaction)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(sequence) FROM snapshot_revisions";
        return (long)command.ExecuteScalar()!;
    }

    private static string EncodeHeader(ScanSnapshot snapshot)
    {
        return Contract.Encode(new ScanSnapshot
        {
            Protocol = snapshot.Protocol, Id = snapshot.Id, DeviceId = snapshot.DeviceId,
            OwnerSid = snapshot.OwnerSid, Privilege = snapshot.Privilege, Started = snapshot.Started,
            Finished = snapshot.Finished, Cancelled = snapshot.Cancelled, Coverage = snapshot.Coverage,
            Processes = snapshot.Processes, BootObservation = snapshot.BootObservation
        });
    }

    private static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var compress = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            compress.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    private static string Decompress(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var decompress = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(decompress, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
