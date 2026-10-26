using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.ClipCorn;

/// <summary>
/// Connections to jClipCorn files. Pooling is off everywhere: a pooled connection keeps the file
/// open after <c>Close()</c>.
/// </summary>
public static class CcSqlite
{
    /// <summary>
    /// Opens a private snapshot copy. <c>immutable=1</c> skips all locking and change detection, so
    /// it must never be used on a file someone else may write.
    /// </summary>
    public static SqliteConnection OpenSnapshot(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Reads the "file change counter" of a database header (offset 24, big-endian). In rollback
    /// journal mode every committed write transaction increments it.
    /// </summary>
    public static uint? ReadChangeCounter(string path)
    {
        var header = new byte[100];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
            {
                return null;
            }
        }

        if (!header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
        {
            return null;
        }

        return (uint)((header[24] << 24) | (header[25] << 16) | (header[26] << 8) | header[27]);
    }

    public static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
