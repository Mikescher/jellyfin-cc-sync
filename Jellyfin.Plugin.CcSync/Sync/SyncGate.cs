namespace Jellyfin.Plugin.CcSync.Sync;

/// <summary>
/// Serialises jClipCorn → Jellyfin runs and pending writes, so a pending update cannot move from
/// <c>pending</c> to <c>written</c> while a sync plans with it. Watch detection never waits here.
/// </summary>
public sealed class SyncGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
