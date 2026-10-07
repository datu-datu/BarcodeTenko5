using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using BarcodeTenko.Offline.Models;

namespace BarcodeTenko.Offline.Services;

/// <summary>
/// 端末ローカルの SQLite。未確定の点呼データと、前回選択した点呼場所を保持する。
/// オフライン版ではサーバ同期を行わないため、bin 書き出しの復旧元としても使う。
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
  student_number INTEGER NOT NULL,
  location_id    INTEGER NOT NULL,
  location_name  TEXT,
  created_at     TEXT    NOT NULL,
  completed      INTEGER NOT NULL DEFAULT 0,
  completed_at   TEXT,
  bin_file       TEXT
);
CREATE TABLE IF NOT EXISTS app_settings (
  key   TEXT PRIMARY KEY,
  value TEXT
);";
        cmd.ExecuteNonQuery();
    }

    private string? GetValue(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private void SetValue(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO app_settings (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 前回選択した点呼場所を app_settings から読み込む。
    /// 未保存・破損・Id 不正の場合は null を返し、呼び出し側で選択画面に戻す。
    /// </summary>
    public Location? LoadLocation()
    {
        try
        {
            var json = GetValue("location");
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            var location = JsonSerializer.Deserialize<Location>(json);
            return location is { Id: > 0 } ? location : null;
        }
        catch
        {
            // 壊れた値は無視して選択画面に戻す
            return null;
        }
    }

    /// <summary>選択した点呼場所を app_settings に保存する。</summary>
    public void SaveLocation(Location location)
    {
        SetValue("location", JsonSerializer.Serialize(location));
    }

    /// <summary>保存されている点呼場所設定をクリアする（次回起動時または完了後に再選択を促すため）。</summary>
    public void ClearLocation()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM app_settings WHERE key = 'location'";
        cmd.ExecuteNonQuery();
    }

    /// <summary>スキャンを記録し、採番された id を返す。</summary>
    public long AddScan(ScanRecord record)
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                @"INSERT INTO scans (student_number, location_id, location_name, created_at, completed)
                  VALUES ($num, $loc, $locName, $created, 0)";
            cmd.Parameters.AddWithValue("$num", record.StudentNumber);
            cmd.Parameters.AddWithValue("$loc", record.LocationId);
            cmd.Parameters.AddWithValue("$locName", record.LocationName);
            cmd.Parameters.AddWithValue("$created", record.CreatedAt.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(idCmd.ExecuteScalar());
    }

    public void DeleteScan(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scans WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<ScanRecord> GetPendingExport()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scans WHERE completed = 0 ORDER BY id";
        return ReadScans(cmd);
    }

    /// <summary>作業中 bin 書き出し用。未確定の学籍番号のみを id 順で返す。</summary>
    public List<int> GetPendingStudentNumbers()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT student_number FROM scans WHERE completed = 0 ORDER BY id";
        var list = new List<int>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetInt32(0));
        }
        return list;
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

    /// <summary>未確定（表示対象）の点呼データを id 降順で全件返す。</summary>
    public List<ScanRecord> GetPending()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scans WHERE completed = 0 ORDER BY id DESC";
        return ReadScans(cmd);
    }

    public List<ScanRecord> SearchScans(string query)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM scans WHERE completed = 0 AND student_number LIKE $q ORDER BY id DESC";
        cmd.Parameters.AddWithValue("$q", $"%{query}%");
        return ReadScans(cmd);
    }

    /// <summary>未確定（出力待ち・履歴表示対象）の点呼件数</summary>
    public int CountPending()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM scans WHERE completed = 0";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 未確定（未完了）の点呼履歴のみを物理削除する。
    /// 過去に「点呼完了」して確定・出力済みのデータは保護され、削除されない。
    /// </summary>
    public void DeletePending()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM scans WHERE completed = 0";
        cmd.ExecuteNonQuery();
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
                StudentNumber = reader.GetInt32(reader.GetOrdinal("student_number")),
                LocationId = reader.GetInt32(reader.GetOrdinal("location_id")),
                LocationName = reader.IsDBNull(reader.GetOrdinal("location_name"))
                    ? ""
                    : reader.GetString(reader.GetOrdinal("location_name")),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                Completed = reader.GetInt32(reader.GetOrdinal("completed")) != 0
            });
        }
        return list;
    }
}
