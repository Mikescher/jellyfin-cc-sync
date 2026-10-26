using System.Globalization;

namespace Jellyfin.Plugin.CcSync.Configuration;

/// <summary>Parsing of configuration values that are stored as text.</summary>
public static class ConfigValues
{
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
