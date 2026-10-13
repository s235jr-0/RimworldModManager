using System.Text.RegularExpressions;

namespace DownloadKit.Steam;

// One Workshop item as Steam's web API describes it.
public sealed class WorkshopDetails
{
    public string Id { get; set; } = "";
    public string AppId { get; set; } = "";
    public string Title { get; set; } = "";
    public long TimeUpdated { get; set; }
    public int FileType { get; set; } = -1;   // 2 = collection
    public int Result { get; set; }           // 1 = OK
}

public static class WorkshopIds
{
    // Workshop IDs from pasted text: plain IDs, ?id= links, /filedetails/ links.
    public static List<string> Parse(string text)
    {
        HashSet<string> ids = new();

        foreach (Match m in Regex.Matches(text, @"(?i)[?&]id=(\d+)"))
            ids.Add(m.Groups[1].Value);

        foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string s = line.Trim();
            if (Regex.IsMatch(s, @"^\d+$")) ids.Add(s);

            Match m = Regex.Match(s, @"(?i)filedetails/(\d+)");
            if (m.Success) ids.Add(m.Groups[1].Value);
        }

        return ids.ToList();
    }

    public static string Url(string id) => "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id;
}

public static class UnixTime
{
    public static DateTime ToLocal(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    public static string Format(long seconds) => ToLocal(seconds).ToString("yyyy-MM-dd HH:mm");
}
