using Jellyfin.Plugin.CcSync.State;

namespace Jellyfin.Plugin.CcSync.Tests.Support;

public sealed class TempStateDb : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccsync-tests", "state-" + Guid.NewGuid().ToString("N"));

    public TempStateDb()
    {
        Path = System.IO.Path.Combine(_directory, "state.db");
        Db = new StateDb(Path);
        Pending = new PendingStore(Db);
        History = new HistoryStore(Db);
        Runs = new RunStore(Db);
    }

    public string Path { get; }

    public StateDb Db { get; }

    public PendingStore Pending { get; }

    public HistoryStore History { get; }

    public RunStore Runs { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
