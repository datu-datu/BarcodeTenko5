using System.IO;
using Microsoft.Data.Sqlite;
using BarcodeTenko.Client.Models;

namespace BarcodeTenko.Client.Services;

/// <summary>
/// 端末ローカルの SQLite。未送信キュー(outbox)と点呼データ、取消待ちを保持する。
/// </summary>
public sealed class LocalStore
{
    private readonly string _connectionString;

    public LocalStore(AppConfig config)
    {
        Directory.CreateDirectory(config.DataDirectory);
        var dbPath = Path.Combine(config.DataDirectory, "client.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS scans (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  client_scan_id TEXT    NOT NULL UNIQUE,
  student_number INTEGER NOT NULL,
  location_id    INTEGER NOT NULL,
  location_name  TEXT,
  created_at     TEXT    NOT NULL,
  sent           INTEGER NOT NULL DEFAULT 0,
  sent_at        TEXT,
  completed      INTEGER NOT NULL DEFAULT 0,
  completed_at   TEXT,
  bin_file       TEXT
);
CREATE TABLE IF NOT EXISTS pending_cancels (
  client_scan_id TEXT PRIMARY KEY,
  created_at     TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS app_settings (
  key   TEXT PRIMARY KEY,
  value TEXT
);";
        cmd.ExecuteNonQuery();
    }

    public string GetOrCreateClientId()
    {
        using var conn = Open();
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT value FROM app_settings WHERE key = 'client_id'";
            if (select.ExecuteScalar() is string existing && existing.Length > 0)
            {
                return existing;
            }
        }

        var id = Guid.NewGuid().ToString("N");
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT OR REPLACE INTO app_settings (key, value) VALUES ('client_id', $value)";
        insert.Parameters.AddWithValue("$value", id);
        insert.ExecuteNonQuery();
        return id;
    }

    public void AddScan(ScanRecord record)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            @"INSERT INTO scans (client_scan_id, student_number, location_id, location_name, created_at, sent, completed)
              VALUES ($cid, $num, $loc, $locName, $created, 0, 0)";
        cmd.Parameters.AddWithValue("$cid", record.ClientScanId);
        cmd.Parameters.AddWithValue("$num", record.StudentNumber);
        cmd.Parameters.AddWithValue("$loc", record.LocationId);
        cmd.Parameters.AddWithValue("$locName", record.LocationName);
        cmd.Parameters.AddWithValue("$created", record.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<ScanRecord> GetUnsent(int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scans WHERE sent = 0 AND completed = 0 ORDER BY id LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadScans(cmd);
    }

    public void MarkSent(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE scans SET sent = 1, sent_at = $at WHERE id = $id";
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteScan(string clientScanId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scans WHERE client_scan_id = $cid";
        cmd.Parameters.AddWithValue("$cid", clientScanId);
        cmd.ExecuteNonQuery();
    }

    public void EnqueueCancel(string clientScanId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO pending_cancels (client_scan_id, created_at) VALUES ($cid, $at)";
        cmd.Parameters.AddWithValue("$cid", clientScanId);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public List<string> GetPendingCancels()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT client_scan_id FROM pending_cancels ORDER BY created_at";
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    public void RemovePendingCancel(string clientScanId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM pending_cancels WHERE client_scan_id = $cid";
        cmd.Parameters.AddWithValue("$cid", clientScanId);
        cmd.ExecuteNonQuery();
    }

    public List<ScanRecord> GetPendingExport()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scans WHERE completed = 0 ORDER BY id";
        return ReadScans(cmd);
    }

    public void MarkCompleted(IEnumerable<long> ids, string binFile)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE scans SET completed = 1, completed_at = $at, bin_file = $file WHERE id = $id";
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("o"));
            cmd.Parameters.AddWithValue("$file", binFile);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public List<ScanRecord> GetRecent(int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scans WHERE completed = 0 ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadScans(cmd);
    }

    public List<ScanRecord> SearchScans(string query, int limit = 100)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scans WHERE completed = 0 AND student_number LIKE $q ORDER BY id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", $"%{query}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadScans(cmd);
    }

    public int CountUnsent()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scans WHERE sent = 0 AND completed = 0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>累計の点呼済み人数 (学籍番号の重複は除く)</summary>
    public int CountDistinctStudents()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(DISTINCT student_number) FROM scans";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 全履歴を物理削除する。
    /// 送信済み(sent = 1)のレコードは pending_cancels にキューイングしてサーバ側にも取消を伝播する。
    /// 未送信のレコードは破棄される。
    /// </summary>
    public void DeleteAll()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // 1. 送信済み(sent = 1)の client_scan_id を pending_cancels に追加
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO pending_cancels (client_scan_id, created_at)
                SELECT client_scan_id, $now FROM scans WHERE sent = 1";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        // 2. scans テーブルを全削除
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM scans";
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static List<ScanRecord> ReadScans(SqliteCommand cmd)
    {
        var list = new List<ScanRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ScanRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ClientScanId = reader.GetString(reader.GetOrdinal("client_scan_id")),
                StudentNumber = reader.GetInt32(reader.GetOrdinal("student_number")),
                LocationId = reader.GetInt32(reader.GetOrdinal("location_id")),
                LocationName = reader.IsDBNull(reader.GetOrdinal("location_name"))
                    ? ""
                    : reader.GetString(reader.GetOrdinal("location_name")),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                Sent = reader.GetInt32(reader.GetOrdinal("sent")) != 0,
                Completed = reader.GetInt32(reader.GetOrdinal("completed")) != 0
            });
        }
        return list;
    }
}
