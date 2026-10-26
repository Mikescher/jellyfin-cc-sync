using Jellyfin.Plugin.CcSync.ClipCorn;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.Tests.Support;

/// <summary>A temp copy of the v38 fixture pair; tests never open the checked-in files.</summary>
public sealed class FixtureDirectory : IDisposable
{
    private readonly string _base;

    public FixtureDirectory(bool includeHistoryDb = false)
    {
        _base = Path.Combine(Path.GetTempPath(), "ccsync-tests", Guid.NewGuid().ToString("N"));
        Root = Path.Combine(_base, "ClipCornDB");
        SnapshotRoot = Path.Combine(_base, "snapshots");
        Directory.CreateDirectory(Root);

        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v38");
        File.Copy(Path.Combine(source, CcFiles.MainDbName), Path.Combine(Root, CcFiles.MainDbName));
        File.Copy(Path.Combine(source, CcFiles.UserDbName), Path.Combine(Root, CcFiles.UserDbName));
        if (includeHistoryDb)
        {
            File.Copy(Path.Combine(source, CcFiles.HistoryDbName), Path.Combine(Root, CcFiles.HistoryDbName));
        }

        Files = new CcFiles(Root);
    }

    public string Root { get; }

    public string SnapshotRoot { get; }

    public CcFiles Files { get; }

    public string PathOf(string file) => Path.Combine(Root, file);

    /// <summary>Changes a fixture copy directly, the way a test setup would, outside the plugin's code paths.</summary>
    public void Execute(string file, string sql)
    {
        using var connection = Open(file, SqliteOpenMode.ReadWrite);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public object? Scalar(string file, string sql)
    {
        using var connection = Open(file, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SqliteConnection Open(string file, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathOf(file), Mode = mode, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
