using System.Globalization;
using Jellyfin.Plugin.CcSync.ClipCorn.Model;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.ClipCorn;

public sealed class CcSchemaException(string message) : Exception(message);

/// <summary>
/// Supported schema versions and the pairing of the two files. A new jClipCorn schema needs a
/// plugin update, checked against <c>database/migration/</c> in jClipCorn.
/// </summary>
public static class CcSchema
{
    public const int SupportedMainVersion = 38;
    public const int SupportedUserVersion = 3;

    public static CcDatabaseInfo ReadInfo(SqliteConnection main, SqliteConnection user)
    {
        var mainInfo = ReadKeyValues(main);
        var userInfo = ReadKeyValues(user);

        return new CcDatabaseInfo(
            MainVersion: ParseInt(mainInfo, "VERSION_DB", "main"),
            MainDuuid: Require(mainInfo, "DATABASE_UNIVERSALLY_UNIQUE_IDENTIFIER", "main"),
            UserVersion: ParseInt(userInfo, "VERSION_DB", "user data"),
            UserMainDuuid: Require(userInfo, "MAINDB_DUUID", "user data"),
            UserMainVersion: ParseInt(userInfo, "VERSION_MAINDB", "user data"),
            HistoryEnabled: userInfo.GetValueOrDefault("HISTORY_ENABLED") == "1");
    }

    public static int ReadMainVersion(SqliteConnection main) => ParseInt(ReadKeyValues(main), "VERSION_DB", "main");

    /// <summary>Everything that rules out reading or writing; empty when the pair is usable.</summary>
    public static IReadOnlyList<string> Check(CcDatabaseInfo info)
    {
        var problems = new List<string>();

        if (info.MainVersion != SupportedMainVersion)
        {
            problems.Add($"ClipCornDB.db has schema version {info.MainVersion}; this plugin supports {SupportedMainVersion}.");
        }

        if (info.UserVersion != SupportedUserVersion)
        {
            problems.Add($"ClipCornUserData.db has schema version {info.UserVersion}; this plugin supports {SupportedUserVersion}.");
        }

        if (info.UserMainVersion > info.MainVersion)
        {
            problems.Add($"ClipCornUserData.db was written for main schema {info.UserMainVersion}, newer than ClipCornDB.db ({info.MainVersion}).");
        }

        if (!string.Equals(info.UserMainDuuid, info.MainDuuid, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"ClipCornUserData.db belongs to database {info.UserMainDuuid}, not to this ClipCornDB.db ({info.MainDuuid}); jClipCorn would refuse to start.");
        }

        return problems;
    }

    private static Dictionary<string, string> ReadKeyValues(SqliteConnection connection)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IKEY, IVALUE FROM INFO";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? string.Empty] =
                Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return result;
    }

    private static string Require(Dictionary<string, string> info, string key, string file)
        => info.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new CcSchemaException($"The {file} database has no INFO.{key}.");

    private static int ParseInt(Dictionary<string, string> info, string key, string file)
        => int.TryParse(Require(info, key, file), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new CcSchemaException($"INFO.{key} of the {file} database is not a number.");
}
