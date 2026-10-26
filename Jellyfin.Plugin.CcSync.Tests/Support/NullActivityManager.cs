using Jellyfin.Data.Events;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.CcSync.Tests.Support;

public sealed class NullActivityManager : IActivityManager
{
    public event EventHandler<GenericEventArgs<ActivityLogEntry>>? EntryCreated
    {
        add { }
        remove { }
    }

    public Task CreateAsync(ActivityLog entry) => Task.CompletedTask;

    public Task<QueryResult<ActivityLogEntry>> GetPagedResultAsync(ActivityLogQuery query) => Task.FromResult(new QueryResult<ActivityLogEntry>());

    public Task CleanAsync(DateTime startDate) => Task.CompletedTask;
}
