using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RimModManager.Core.Sources;

// The newest downloadable version of a repository.
//   Version: release tag, or commit sha when the repo has no releases.
//   Published: unix seconds.
public sealed record RemoteVersion(string Version, long Published, string DownloadUrl, string FileName, string Label);

// github.com, anonymously (60 API requests/hour) or with a free personal
// token (5,000/hour). Uses releases when the repo has them, otherwise the
// latest commit of the default branch (or the branch in the link).
public static class GitHubSource
{
    public sealed record Repo(string Owner, string Name, string? Tag, string? Branch)
    {
        public string Id => Owner + "/" + Name;
    }

    public static Repo Parse(string url)
    {
        Match m = Regex.Match(url, @"github\.com/([^/\s]+)/([^/\s#?]+)(?:/(releases/tag|tree)/([^\s?#]+))?", RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new Exception("This doesn't look like a GitHub repository link: " + url);

        string name = Regex.Replace(m.Groups[2].Value, @"\.git$", "", RegexOptions.IgnoreCase);
        string? kind = m.Groups[3].Success ? m.Groups[3].Value.ToLowerInvariant() : null;
        string? value = m.Groups[4].Success ? Uri.UnescapeDataString(m.Groups[4].Value.TrimEnd('/')) : null;

        return new Repo(m.Groups[1].Value, name,
            kind == "releases/tag" ? value : null,
            kind == "tree" ? value : null);
    }

    private static Dictionary<string, string> Headers(string? token)
    {
        Dictionary<string, string> h = new()
        {
            ["Accept"] = "application/vnd.github+json",
            ["X-GitHub-Api-Version"] = "2022-11-28",
        };
        if (!String.IsNullOrWhiteSpace(token)) h["Authorization"] = "Bearer " + token.Trim();
        return h;
    }

    private static JsonElement Api(string path, string? token)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(HttpDownloader.GetText("https://api.github.com" + path, Headers(token)));
            return doc.RootElement.Clone();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            throw new Exception(
                "GitHub's request limit was reached (60 per hour without a token). " +
                "Add a free GitHub token under Accounts on the Install tab, or try again later.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new Exception("GitHub rejected the saved token. Check it under Accounts on the Install tab.", ex);
        }
    }

    public static RemoteVersion Latest(Repo repo, string? token)
    {
        if (repo.Branch == null)
        {
            try
            {
                JsonElement release = Api("/repos/" + repo.Owner + "/" + repo.Name +
                    (repo.Tag != null ? "/releases/tags/" + Uri.EscapeDataString(repo.Tag) : "/releases/latest"), token);
                return FromRelease(repo, release);
            }
            catch (Exception ex) when (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound } && repo.Tag == null)
            {
                // No releases: fall through to the latest commit.
            }
        }

        string branch = repo.Branch ?? Api("/repos/" + repo.Owner + "/" + repo.Name, token).GetProperty("default_branch").GetString()!;
        JsonElement commit = Api("/repos/" + repo.Owner + "/" + repo.Name + "/commits/" + Uri.EscapeDataString(branch), token);

        string sha = commit.GetProperty("sha").GetString()!;
        DateTimeOffset date = commit.GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTimeOffset();

        return new RemoteVersion(
            sha,
            date.ToUnixTimeSeconds(),
            "https://codeload.github.com/" + repo.Owner + "/" + repo.Name + "/zip/" + sha,
            repo.Name + "-" + sha.Substring(0, 7) + ".zip",
            "commit " + sha.Substring(0, 7) + " on " + branch);
    }

    private static RemoteVersion FromRelease(Repo repo, JsonElement release)
    {
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        long published = release.TryGetProperty("published_at", out JsonElement p) && p.ValueKind == JsonValueKind.String
            ? p.GetDateTimeOffset().ToUnixTimeSeconds() : 0;

        // Prefer an uploaded archive; skip ones that look like source-only.
        JsonElement? pick = null;
        if (release.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!ArchiveTools.IsArchive(name)) continue;
                if (pick == null || (!name.Contains("source", StringComparison.OrdinalIgnoreCase) &&
                                     (pick.Value.GetProperty("name").GetString() ?? "").Contains("source", StringComparison.OrdinalIgnoreCase)))
                    pick = a;
            }
        }

        if (pick is { } asset)
            return new RemoteVersion(tag, published,
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("name").GetString()!,
                "release " + tag);

        // No uploaded archive: the release's source code zip.
        return new RemoteVersion(tag, published,
            "https://codeload.github.com/" + repo.Owner + "/" + repo.Name + "/zip/refs/tags/" + Uri.EscapeDataString(tag),
            repo.Name + "-" + tag + ".zip",
            "release " + tag + " (source)");
    }
}

// GitLab servers (gitlab.com, gitgud.io, ...), anonymously or with an
// optional personal token.
public static class GitLabSource
{
    public sealed record Project(string Host, string Path, string? Tag, string? Branch)
    {
        public string Id => Host + "/" + Path;
        public string Api => "https://" + Host + "/api/v4/projects/" + Uri.EscapeDataString(Path);
    }

    public static Project Parse(string url)
    {
        Uri uri = new(url);
        string path = uri.AbsolutePath.Trim('/');
        string? tag = null, branch = null;

        // ".../-/tags/X", ".../-/releases/X", ".../-/tree/branch"
        Match m = Regex.Match(path, @"^(.+?)/-/(tags|releases|tree|archive)(?:/([^/]+))?");
        if (m.Success)
        {
            path = m.Groups[1].Value;
            string kind = m.Groups[2].Value;
            string? value = m.Groups[3].Success ? Uri.UnescapeDataString(m.Groups[3].Value) : null;
            if (kind is "tags" or "releases") tag = value;
            if (kind == "tree") branch = value;
        }

        path = Regex.Replace(path, @"\.git$", "", RegexOptions.IgnoreCase);
        if (!path.Contains('/'))
            throw new Exception("This doesn't look like a GitLab project link: " + url);

        return new Project(uri.Host, path, tag, branch);
    }

    private static Dictionary<string, string>? Headers(string? token) =>
        String.IsNullOrWhiteSpace(token) ? null : new() { ["PRIVATE-TOKEN"] = token.Trim() };

    private static JsonElement Api(string url, string? token)
    {
        using JsonDocument doc = JsonDocument.Parse(HttpDownloader.GetText(url, Headers(token)));
        return doc.RootElement.Clone();
    }

    public static RemoteVersion Latest(Project p, string? token)
    {
        if (p.Branch == null)
        {
            JsonElement? release = null;

            if (p.Tag != null)
            {
                try { release = Api(p.Api + "/releases/" + Uri.EscapeDataString(p.Tag), token); }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
            }
            else
            {
                JsonElement list = Api(p.Api + "/releases?per_page=1&order_by=released_at&sort=desc", token);
                if (list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                    release = list[0];

                // Some projects stopped making "releases" and only tag new
                // versions (RJW's last release is from 2021, its tags are
                // current). Use whichever is newer.
                RemoteVersion? tagged = LatestTag(p, token);
                RemoteVersion? released = release is { } rel ? FromRelease(p, rel) : null;

                if (tagged != null && (released == null || tagged.Published > released.Published))
                    return tagged;
                if (released != null)
                    return released;
            }

            if (release is { } r)
                return FromRelease(p, r);
        }

        string branch = p.Branch ?? Api(p.Api, token).GetProperty("default_branch").GetString()!;
        JsonElement commit = Api(p.Api + "/repository/commits/" + Uri.EscapeDataString(branch), token);
        string sha = commit.GetProperty("id").GetString()!;
        long date = commit.GetProperty("committed_date").GetDateTimeOffset().ToUnixTimeSeconds();
        string name = p.Path.Split('/').Last();

        return new RemoteVersion(sha, date,
            p.Api + "/repository/archive.zip?sha=" + sha,
            name + "-" + sha.Substring(0, 7) + ".zip",
            "commit " + sha.Substring(0, 7) + " on " + branch);
    }

    private static RemoteVersion? LatestTag(Project p, string? token)
    {
        JsonElement tags = Api(p.Api + "/repository/tags?order_by=updated&sort=desc&per_page=1", token);
        if (tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() == 0) return null;

        JsonElement tag = tags[0];
        string name = tag.GetProperty("name").GetString() ?? "";
        long date = tag.TryGetProperty("commit", out JsonElement c) && c.TryGetProperty("committed_date", out JsonElement d)
            ? d.GetDateTimeOffset().ToUnixTimeSeconds() : 0;
        string project = p.Path.Split('/').Last();

        return new RemoteVersion(name, date,
            p.Api + "/repository/archive.zip?sha=" + Uri.EscapeDataString(name),
            project + "-" + name + ".zip",
            "tag " + name);
    }

    private static RemoteVersion FromRelease(Project p, JsonElement release)
    {
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        long published = release.TryGetProperty("released_at", out JsonElement d) && d.ValueKind == JsonValueKind.String
            ? d.GetDateTimeOffset().ToUnixTimeSeconds() : 0;
        string name = p.Path.Split('/').Last();

        if (release.TryGetProperty("assets", out JsonElement assets) &&
            assets.TryGetProperty("links", out JsonElement links))
        {
            foreach (JsonElement link in links.EnumerateArray())
            {
                string linkName = link.GetProperty("name").GetString() ?? "";
                string url = link.TryGetProperty("direct_asset_url", out JsonElement du) ? du.GetString()! : link.GetProperty("url").GetString()!;
                if (ArchiveTools.IsArchive(linkName) || ArchiveTools.IsArchive(url))
                    return new RemoteVersion(tag, published, url, ArchiveTools.IsArchive(linkName) ? linkName : Path.GetFileName(new Uri(url).AbsolutePath), "release " + tag);
            }
        }

        return new RemoteVersion(tag, published,
            p.Api + "/repository/archive.zip?sha=" + Uri.EscapeDataString(tag),
            name + "-" + tag + ".zip",
            "release " + tag + " (source)");
    }
}
