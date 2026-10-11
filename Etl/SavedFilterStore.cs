using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EtlApp.Etl;

/// <param name="Columns">Selected export columns (null for entries saved before columns were stored).</param>
/// <param name="Renames">Original column name -> header used in the export (only the renamed ones).</param>
public sealed record SavedFilter(
    long Id, string Name, string FileName, string Mode, List<FilterSpec> Filters, DateTime SavedAt,
    List<string>? Columns, Dictionary<string, string>? Renames);

/// <summary>
/// Remembers the filters (plus selected / renamed columns) used for a download in a single SQLite file (DATA_DIR/etl.db).
/// Each saved filter has a name (case-insensitive, unique) and remembers the file name it was used for;
/// saving under an existing name replaces the earlier entry.
/// Connections are not pooled, so the .db file is closed between calls and safe to copy for backups.
/// </summary>
public sealed record SavedWorkflow(long Id, string Name, string Json, DateTime SavedAt);

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
            saved_at     TEXT NOT NULL,
            columns_json TEXT NULL,
            renames_json TEXT NULL)";

    static List<string> TableColumns(SqliteConnection conn)
    {
        var cols = new List<string>();
        using var info = conn.CreateCommand();
        info.CommandText = "PRAGMA table_info(saved_filters)";
        using var r = info.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }

    SqliteConnection Open()
    {
        Directory.CreateDirectory(_dir);
        var conn = new SqliteConnection(_connStr);
        conn.Open();

        // upgrade from the first version of the table (no "name" column; file_name was the unique key)
        var cols = TableColumns(conn);
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

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = CreateSql;
            cmd.ExecuteNonQuery();
        }

        // upgrade from the second version (name, but no stored columns / renames)
        cols = TableColumns(conn);
        foreach (var extra in new[] { "columns_json", "renames_json" })
        {
            if (cols.Contains(extra)) continue;
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE saved_filters ADD COLUMN {extra} TEXT NULL";
            alter.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"CREATE TABLE IF NOT EXISTS saved_workflows (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                workflow_json TEXT NOT NULL,
                saved_at TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    public void SaveWorkflow(string name, string json)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO saved_workflows(name, workflow_json, saved_at) VALUES($n,$j,$t)
            ON CONFLICT(name) DO UPDATE SET workflow_json=excluded.workflow_json, saved_at=excluded.saved_at";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$j", json);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public List<SavedWorkflow> ListWorkflows()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,workflow_json,saved_at FROM saved_workflows ORDER BY saved_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<SavedWorkflow>();
        while (r.Read()) list.Add(new SavedWorkflow(r.GetInt64(0), r.GetString(1), r.GetString(2),
            DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        return list;
    }

    public SavedWorkflow? GetWorkflow(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id,name,workflow_json,saved_at FROM saved_workflows WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new SavedWorkflow(r.GetInt64(0), r.GetString(1), r.GetString(2),
            DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)) : null;
    }

    public void Save(string name, string fileName, string mode, List<FilterSpec> filters,
                     List<string> columns, Dictionary<string, string> renames)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO saved_filters (name, file_name, mode, filters_json, saved_at, columns_json, renames_json)
            VALUES ($name, $n, $m, $f, $t, $c, $r)
            ON CONFLICT(name) DO UPDATE SET
                file_name = excluded.file_name, mode = excluded.mode, filters_json = excluded.filters_json,
                saved_at = excluded.saved_at, columns_json = excluded.columns_json, renames_json = excluded.renames_json";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$n", fileName);
        cmd.Parameters.AddWithValue("$m", mode);
        cmd.Parameters.AddWithValue("$f", JsonSerializer.Serialize(filters));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$c", JsonSerializer.Serialize(columns));
        cmd.Parameters.AddWithValue("$r", JsonSerializer.Serialize(renames));
        cmd.ExecuteNonQuery();
    }

    public List<SavedFilter> List()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT id, name, file_name, mode, filters_json, saved_at, columns_json, renames_json
                            FROM saved_filters ORDER BY saved_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<SavedFilter>();
        while (r.Read())
        {
            var filters = JsonSerializer.Deserialize<List<FilterSpec>>(r.GetString(4)) ?? new();
            var at = DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var cols = r.IsDBNull(6) ? null : JsonSerializer.Deserialize<List<string>>(r.GetString(6));
            var renames = r.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(7));
            list.Add(new SavedFilter(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), filters, at, cols, renames));
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
