using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.CcSync.ClipCorn.Model;

public static class CcJson
{
    /// <summary>
    /// A JSON string literal exactly as org.json's <c>JSONObject.quote</c> writes it, which is what
    /// jClipCorn's <c>JSONStringer</c> produces: <c>/</c> is escaped only after <c>&lt;</c>, control
    /// characters and the ranges U+0080–U+009F and U+2000–U+20FF become lowercase <c>\u</c> escapes,
    /// everything else is written literally.
    /// </summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        var previous = '\0';
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                case '"':
                    builder.Append('\\').Append(c);
                    break;
                case '/':
                    if (previous == '<')
                    {
                        builder.Append('\\');
                    }

                    builder.Append(c);
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                default:
                    if (c < ' ' || c is >= '\u0080' and < '\u00a0' || c is >= '\u2000' and < '\u2100')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }

            previous = c;
        }

        return builder.Append('"').ToString();
    }
}
