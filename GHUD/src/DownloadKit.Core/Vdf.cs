using System.Text;

namespace DownloadKit;

// Minimal reader for Valve's KeyValues text format (.acf/.vdf):
//   "key" "value"   or   "key" { ...nested... }
public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        int pos = 0;
        return ParseObject(text, ref pos);
    }

    public static Dictionary<string, object>? Child(Dictionary<string, object>? node, string key)
    {
        if (node == null || !node.TryGetValue(key, out object? value)) return null;
        return value as Dictionary<string, object>;
    }

    private static Dictionary<string, object> ParseObject(string text, ref int pos)
    {
        Dictionary<string, object> result = new(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            string? key = NextToken(text, ref pos, out bool quoted);
            if (key == null || (!quoted && key == "}")) return result;

            string? value = NextToken(text, ref pos, out quoted);
            if (value == null) return result;

            if (!quoted && value == "{")
                result[key] = ParseObject(text, ref pos);
            else
                result[key] = value;
        }
    }

    private static string? NextToken(string text, ref int pos, out bool quoted)
    {
        quoted = false;

        while (pos < text.Length)
        {
            char c = text[pos];

            if (Char.IsWhiteSpace(c)) { pos++; continue; }

            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }

            if (c == '{' || c == '}')
            {
                pos++;
                return c.ToString();
            }

            quoted = true;

            if (c == '"')
            {
                pos++;
                StringBuilder sb = new();
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length) pos++;
                    sb.Append(text[pos]);
                    pos++;
                }
                pos++;
                return sb.ToString();
            }

            int start = pos;
            while (pos < text.Length && !Char.IsWhiteSpace(text[pos]) &&
                   text[pos] != '{' && text[pos] != '}' && text[pos] != '"')
                pos++;
            return text.Substring(start, pos - start);
        }

        return null;
    }
}
