using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownloadKit.Sources;

// Nexus Mods through its official API. Needs the user's personal API key
// (nexusmods.com > Settings > API keys); Nexus has no username/password login
// for other programs.
//
// Free accounts can't request download links on their own: the user clicks
// "Mod Manager Download" on the site, the browser passes an nxm:// link
// (carrying a one-time key) to this app, and the app downloads with that.
public static class Nexus
{
    // Game "domain" as used in Nexus URLs: rimworld, skyrimspecialedition, ...
    public sealed record ModRef(string Game, int ModId);

    public sealed record NxmLink(string Game, int ModId, int FileId, string? Key, string? Expires);

    public sealed record NexusFile(int FileId, string Name, string Version, string Category, long Uploaded, string FileName);

    public static string ModPageUrl(string game, int modId) => "https://www.nexusmods.com/" + game + "/mods/" + modId;

    public static string FilesPageUrl(string game, int modId) => ModPageUrl(game, modId) + "?tab=files";

    // https://www.nexusmods.com/rimworld/mods/123 (also /games/rimworld/...)
    public static ModRef ParseModPage(string url)
    {
        Match m = Regex.Match(url, @"nexusmods\.com/(?:games/)?([^/]+)/mods/(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new Exception("This doesn't look like a Nexus mod page link: " + url);
        return new ModRef(m.Groups[1].Value.ToLowerInvariant(), int.Parse(m.Groups[2].Value));
    }

    // nxm://rimworld/mods/123/files/456?key=...&expires=...&user_id=...
    public static NxmLink ParseNxm(string link)
    {
        Match m = Regex.Match(link, @"^nxm://([^/]+)/mods/(\d+)/files/(\d+)(?:\?(.*))?$", RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new Exception("Not a valid Nexus download link.");

        Dictionary<string, string> q = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in (m.Groups[4].Value ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) q[part.Substring(0, eq)] = Uri.UnescapeDataString(part.Substring(eq + 1));
        }

        return new NxmLink(m.Groups[1].Value.ToLowerInvariant(), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
            q.GetValueOrDefault("key"), q.GetValueOrDefault("expires"));
    }
}

public sealed class NexusClient
{
    private readonly string _apiKey;
    private readonly string _game;

    public NexusClient(string apiKey, string game)
    {
        _game = game;
        if (String.IsNullOrWhiteSpace(apiKey))
            throw new Exception("Nexus needs your personal API key. Add it under Accounts on the Install tab " +
                                "(nexusmods.com > Settings > API keys).");
        _apiKey = apiKey.Trim();
    }

    private JsonElement Api(string path)
    {
        Dictionary<string, string> headers = new()
        {
            ["apikey"] = _apiKey,
            ["Accept"] = "application/json",
            ["Application-Name"] = ClientInfo.Name,
            ["Application-Version"] = ClientInfo.Version,
        };

        try
        {
            using JsonDocument doc = JsonDocument.Parse(HttpDownloader.GetText("https://api.nexusmods.com/v1" + path, headers));
            return doc.RootElement.Clone();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new Exception("Nexus rejected the API key. Check it under Accounts on the Install tab.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new Exception("Nexus' hourly request limit was reached; try again later.", ex);
        }
    }

    // Account name and whether it's Premium.
    public (string Name, bool IsPremium) Validate()
    {
        JsonElement u = Api("/users/validate.json");
        return (u.GetProperty("name").GetString() ?? "", u.TryGetProperty("is_premium", out JsonElement p) && p.GetBoolean());
    }

    public string ModName(int modId) =>
        Api("/games/" + _game + "/mods/" + modId + ".json").GetProperty("name").GetString() ?? ("Nexus mod " + modId);

    public List<Nexus.NexusFile> Files(int modId)
    {
        JsonElement root = Api("/games/" + _game + "/mods/" + modId + "/files.json");
        List<Nexus.NexusFile> files = new();

        foreach (JsonElement f in root.GetProperty("files").EnumerateArray())
        {
            files.Add(new Nexus.NexusFile(
                f.GetProperty("file_id").GetInt32(),
                f.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                f.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "" : "",
                f.TryGetProperty("category_name", out JsonElement c) ? c.GetString() ?? "" : "",
                f.TryGetProperty("uploaded_timestamp", out JsonElement t) ? t.GetInt64() : 0,
                f.TryGetProperty("file_name", out JsonElement fn) ? fn.GetString() ?? "" : ""));
        }

        return files;
    }

    // Newest main file: what "up to date" is measured against.
    public Nexus.NexusFile? LatestMainFile(int modId) =>
        Files(modId)
            .Where(f => f.Category.Equals("MAIN", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Uploaded)
            .FirstOrDefault();

    // Free accounts must pass the key/expires from an nxm:// link.
    public string DownloadUrl(int modId, int fileId, string? key, string? expires)
    {
        string path = "/games/" + _game + "/mods/" + modId + "/files/" + fileId + "/download_link.json";
        if (!String.IsNullOrEmpty(key))
            path += "?key=" + Uri.EscapeDataString(key) + "&expires=" + Uri.EscapeDataString(expires ?? "");

        try
        {
            JsonElement links = Api(path);
            return links[0].GetProperty("URI").GetString()!;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new Exception("Nexus only gives free accounts downloads started from its website: " +
                                "click \"Mod Manager Download\" on the mod's Files tab.", ex);
        }
    }
}
