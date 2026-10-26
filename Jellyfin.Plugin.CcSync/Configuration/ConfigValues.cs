using System.Globalization;

namespace Jellyfin.Plugin.CcSync.Configuration;

/// <summary>Parsing of configuration values that are stored as text.</summary>
public static class ConfigValues
{
    /// <summary>
    /// jClipCorn refreshes its lock every 60 minutes and treats a foreign lock as stale after 180;
    /// a shorter setting would declare a running jClipCorn dead between two refreshes.
    /// </summary>
    public const int MinLockStaleMinutes = 180;

    public static TimeSpan LockStaleAfter(int configuredMinutes)
        => TimeSpan.FromMinutes(Math.Max(MinLockStaleMinutes, configuredMinutes));

    public static bool TryResolveTimeZone(string? id, out TimeZoneInfo zone, out string? error)
    {
        zone = TimeZoneInfo.Utc;
        error = null;

        if (string.IsNullOrWhiteSpace(id))
        {
            error = "No jClipCorn time zone is configured.";
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = $"Unknown time zone \"{id}\" (expected an IANA id such as Europe/Berlin).";
            return false;
        }
    }

    public static bool TryParseTimeOfDay(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(value?.Trim(), ["HH:mm", "H:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
