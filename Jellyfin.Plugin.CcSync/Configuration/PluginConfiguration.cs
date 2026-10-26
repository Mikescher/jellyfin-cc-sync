using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CcSync.Configuration;

/// <summary>
/// Plugin settings, persisted by the server as XML. Collections are arrays: the XML
/// serializer appends to a pre-filled list instead of replacing it.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the path of the <c>ClipCornDB</c> folder as the Jellyfin process sees it.</summary>
    public string ClipCornDirectory { get; set; } = string.Empty;

    /// <summary>Gets or sets the IANA zone of jClipCorn's local timestamps.</summary>
    public string ClipCornTimeZone { get; set; } = "Europe/Berlin";

    /// <summary>Gets or sets the local time (<c>HH:mm</c>) given to date-only history entries.</summary>
    public string DateOnlyTimeOfDay { get; set; } = "12:00";

    /// <summary>Gets or sets the age in minutes after which a lock file counts as stale.</summary>
    public int LockStaleMinutes { get; set; } = 180;

    public int BackupCount { get; set; } = 20;

    /// <summary>Gets or sets the id of the one Jellyfin user that is synced.</summary>
    public string SyncUserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the library ids taken into account; empty means all.</summary>
    public string[] LibraryIds { get; set; } = [];

    public bool WriteBackEnabled { get; set; } = true;

    /// <summary>Gets or sets the played fraction (0..1) at which a stop counts as watched.</summary>
    public double WatchedThreshold { get; set; } = 0.90;

    public int DedupMinutes { get; set; } = 60;

    /// <summary>Gets or sets the gap in minutes within which a new playback start keeps the original start time.</summary>
    public int StartCoalesceMinutes { get; set; } = 10;

    public bool ManualMarkCreatesPending { get; set; }

    /// <summary>Gets or sets a value indicating whether writing a watch removes jClipCorn's "Watch later" tag.</summary>
    public bool ResetWatchLaterOnWatch { get; set; } = true;

    public bool SyncEnabled { get; set; } = true;

    public bool SyncPlayed { get; set; } = true;

    public bool SyncPlayCount { get; set; } = true;

    public bool SyncLastPlayed { get; set; } = true;

    public bool SyncFavorites { get; set; } = true;

    /// <summary>Gets or sets the stored jClipCorn score values (not ranks) that mean favourite.</summary>
    public int[] FavoriteScores { get; set; } = [4, 5];

    public bool RatingTagsEnabled { get; set; } = true;

    public bool GroupTagsEnabled { get; set; } = true;

    public bool GroupTagsIncludeParents { get; set; }

    public bool ApplyRatingsToMovies { get; set; } = true;

    public bool ApplyRatingsToSeries { get; set; } = true;

    public bool ApplyRatingsToSeasons { get; set; } = true;

    public bool ApplyRatingsToEpisodes { get; set; } = true;

    public bool LockTagsField { get; set; }

    public bool DryRun { get; set; }

    public int MaxChangesPerRun { get; set; } = 100;

    public int MaxUnwatchPerRun { get; set; } = 20;

    /// <summary>Gets or sets how long history entries are kept; 0 keeps them forever.</summary>
    public int HistoryRetentionDays { get; set; }

    public bool WriteToActivityLog { get; set; } = true;
}
