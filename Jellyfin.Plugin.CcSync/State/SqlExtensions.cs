using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.CcSync.State;

internal static class SqlExtensions
{
    public static SqliteCommand Command(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    public static int Execute(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    public static List<T> Query<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
        {
            result.Add(map(reader));
        }

        return result;
    }

    public static string? StringOrNull(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
