using Jellyfin.Plugin.CcSync.State;

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
