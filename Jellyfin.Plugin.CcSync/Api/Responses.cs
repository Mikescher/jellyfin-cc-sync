using Jellyfin.Plugin.CcSync.ClipCorn;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Jellyfin.Plugin.CcSync.State;
using Jellyfin.Plugin.CcSync.Sync;

namespace Jellyfin.Plugin.CcSync.Api;

/// <summary>Values to probe with; anything left out comes from the saved configuration.</summary>
public class ProbeRequest
{
    public string? Directory { get; set; }

    public int? LockStaleMinutes { get; set; }

    public string? TimeZone { get; set; }
}

public class ProbeResponse
{
    /// <summary>Gets or sets a value indicating whether no check reported an error.</summary>
    public bool Ok { get; set; }

    public IReadOnlyList<ProbeCheck> Checks { get; set; } = [];
}

public class ProbeCheck
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>ok</c>, <c>warn</c> or <c>error</c>.</summary>
    public string Level { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

public class DeleteResponse
{
    public int Deleted { get; set; }
}

public class PendingResponse
{
    public int Total { get; set; }

    public IReadOnlyDictionary<string, int> Counts { get; set; } = new Dictionary<string, int>();

    public IReadOnlyList<PendingUpdate> Items { get; set; } = [];
}

public class StatusResponse
{
    /// <summary>Gets or sets configuration problems and notable settings, worst first.</summary>
    public IReadOnlyList<ProbeCheck> Warnings { get; set; } = [];

    public string Directory { get; set; } = string.Empty;

    public bool DirectoryExists { get; set; }

    public CcLockStatus? MainLock { get; set; }

    public CcLockStatus? HistoryLock { get; set; }

    public IReadOnlyList<string> Journals { get; set; } = [];

    public bool Blocked { get; set; }

    public RunRecord? LastSync { get; set; }

    public RunRecord? LastFlush { get; set; }

    public FlushWait? FlushWait { get; set; }

    public IReadOnlyDictionary<string, int> PendingCounts { get; set; } = new Dictionary<string, int>();

    public MatchSummary? Match { get; set; }

    public PreviewSummary? Preview { get; set; }

    public bool DryRun { get; set; }
}

public class MatchSummary
{
    public DateTime CreatedUtc { get; set; }

    public CcDatabaseInfo? Database { get; set; }

    public int JfItems { get; set; }

    public int CcElements { get; set; }

    public int Matched { get; set; }

    public IReadOnlyDictionary<string, int> MatchedByKind { get; set; } = new Dictionary<string, int>();

    public int UnmatchedCc { get; set; }

    public int UnmatchedJf { get; set; }

    public int Ambiguous { get; set; }

    public int KindMismatches { get; set; }

    public int FallbackMatched { get; set; }

    public int MatchedSeveralTimes { get; set; }

    public int CcProblems { get; set; }

    public IReadOnlyList<string> TagWarnings { get; set; } = [];
}

public class PreviewSummary
{
    public string PlanId { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public int ItemsChanged { get; set; }

    public int Unwatches { get; set; }
}

public class SyncRunResponse
{
    public string Outcome { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? RunId { get; set; }
}
