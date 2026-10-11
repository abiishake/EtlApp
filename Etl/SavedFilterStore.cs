using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EtlApp.Etl;

public sealed record SavedFilter(long Id, string Name, string FileName, string Mode, List<FilterSpec> Filters, DateTime SavedAt);

/// <summary>
/// Remembers the filters used for a downloaded file name in a single SQLite file (DATA_DIR/etl.db).
/// Each saved filter has a name (case-insensitive, unique) and remembers the file name it was used for;
/// saving under an existing name replaces the earlier filters.
/// Connections are not pooled, so the .db file is closed between calls and safe to copy for backups.
/// </summary>
public sealed class SavedFilterStore
{
    readonly string _dir;
    readonly string _connStr;

    public SavedFilterStore(IConfiguration cfg)
    {
        _dir = cfg["DATA_DIR"] ?? "data";
        _connStr = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_dir, "etl.db"),
            Pooling = false
        }.ToString();
    }

    const string CreateSql = @"CREATE TABLE IF NOT EXISTS saved_filters (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            name         TEXT NOT NULL UNIQUE COLLATE NOCASE,
            file_name    TEXT NOT NULL,
            mode         TEXT NOT NULL,
            filters_json TEXT NOT NULL,
            saved_at     TEXT NOT NULL)";

    SqliteConnection Open()
    {
        Directory.CreateDirectory(_dir);
        var conn = new SqliteConnection(_connStr);
        conn.Open();

        // upgrade from the first version of the table (no "name" column; file_name was the unique key)
        var cols = new List<string>();
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(saved_filters)";
            using var r = info.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (cols.Count > 0 && !cols.Contains("name"))
        {
            using var tx = conn.BeginTransaction();
            using var mig = conn.CreateCommand();
            mig.Transaction = tx;
            mig.CommandText = @"ALTER TABLE saved_filters RENAME TO saved_filters_old;
                " + CreateSql + @";
                INSERT INTO saved_filters (name, file_name, mode, filters_json, saved_at)
                    SELECT file_name, file_name, mode, filters_json, saved_at FROM saved_filters_old;
                DROP TABLE saved_filters_old;";
            mig.ExecuteNonQuery();
            tx.Commit();
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = CreateSql;
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void Save(string name, string fileName, string mode, List<FilterSpec> filters)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO saved_filters (name, file_name, mode, filters_json, saved_at)
            VALUES ($name, $n, $m, $f, $t)
            ON CONFLICT(name) DO UPDATE SET
                file_name = excluded.file_name, mode = excluded.mode, filters_json = excluded.filters_json, saved_at = excluded.saved_at";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$n", fileName);
        cmd.Parameters.AddWithValue("$m", mode);
        cmd.Parameters.AddWithValue("$f", JsonSerializer.Serialize(filters));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public List<SavedFilter> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, file_name, mode, filters_json, saved_at FROM saved_filters ORDER BY saved_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<SavedFilter>();
        while (r.Read())
        {
            var filters = JsonSerializer.Deserialize<List<FilterSpec>>(r.GetString(4)) ?? new();
            var at = DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            list.Add(new SavedFilter(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), filters, at));
        }
        return list;
    }

    public bool Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM saved_filters WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}
