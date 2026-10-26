using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.State;

/// <summary>
/// The plugin's own SQLite file (<c>state.db</c>, WAL). The schema version lives in
/// <c>PRAGMA user_version</c>; <see cref="Migrations"/> index n brings version n to n + 1.
/// </summary>
public sealed class StateDb
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE pending (
          id INTEGER PRIMARY KEY,
          created_utc TEXT NOT NULL, source TEXT NOT NULL,
          jf_item_id TEXT NOT NULL, item_name TEXT NOT NULL, item_kind TEXT NOT NULL,
          cc_id TEXT NULL, match_keys TEXT NOT NULL,
          watched_at_utc TEXT NOT NULL,
          status TEXT NOT NULL,
          attempts INTEGER NOT NULL DEFAULT 0, last_attempt_utc TEXT NULL, last_error TEXT NULL,
          finished_utc TEXT NULL, cc_entry TEXT NULL);
        CREATE INDEX pending_status ON pending(status);
        CREATE INDEX pending_item ON pending(jf_item_id);

        CREATE TABLE history (
          id INTEGER PRIMARY KEY, ts_utc TEXT NOT NULL, run_id TEXT NULL, kind TEXT NOT NULL,
          jf_item_id TEXT NULL, cc_id TEXT NULL, item_name TEXT NULL, item_kind TEXT NULL,
          field TEXT NULL, old_value TEXT NULL, new_value TEXT NULL,
          detail TEXT NULL, success INTEGER NOT NULL);
        CREATE INDEX history_ts ON history(ts_utc);
        CREATE INDEX history_kind ON history(kind);
        CREATE INDEX history_run ON history(run_id);

        CREATE TABLE runs (
          id TEXT PRIMARY KEY, kind TEXT NOT NULL,
          started_utc TEXT NOT NULL, finished_utc TEXT NULL,
          outcome TEXT NOT NULL,
          summary TEXT NOT NULL);
        CREATE INDEX runs_kind ON runs(kind, started_utc);

        CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        """,
    ];

    private readonly string _path;
    private readonly Lock _initLock = new();
    private bool _initialized;

    public StateDb(PluginPaths paths)
        : this(paths.StateDb)
    {
    }

    internal StateDb(string path)
    {
        _path = path;
    }

    public static int SchemaVersion => Migrations.Length;

    public SqliteConnection Open()
    {
        EnsureInitialized();
        return OpenRaw();
    }

    public static string FormatTime(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static DateTime ParseTime(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static DateTime? ParseTimeOrNull(object? value)
        => value is string s && s.Length > 0 ? ParseTime(s) : null;

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString());
        connection.Open();
        return connection;
    }

    private void EnsureInitialized()
    {
        lock (_initLock)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var connection = OpenRaw();

            using (var wal = connection.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode = WAL";
                wal.ExecuteNonQuery();
            }

            var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version"), CultureInfo.InvariantCulture);
            if (version > Migrations.Length)
            {
                throw new InvalidOperationException($"state.db has schema version {version}, newer than this plugin ({Migrations.Length}).");
            }

            for (; version < Migrations.Length; version++)
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = Migrations[version] + $"\nPRAGMA user_version = {version + 1};";
                command.ExecuteNonQuery();
                transaction.Commit();
            }

            _initialized = true;
        }
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
