using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using CG.Web.MegaApiClient;

namespace RimModManager.Core.Sources;

public enum LinkKind
{
    Steam,
    GitHub,
    GitLab,
    GitFile,      // one specific file on GitHub/GitLab (release asset, archive, raw)
    Nexus,        // nexusmods.com mod page
    NexusNxm,     // nxm:// link from Nexus' "Mod Manager Download" button
    LoversLab,
    Mega,
    MediaFire,
    GoogleDrive,
    Dropbox,
    Direct,
    Invalid,
}

public static class LinkClassifier
{
    public static LinkKind Classify(string text)
    {
        string s = (text ?? "").Trim();
        if (s.Length == 0) return LinkKind.Invalid;
        if (Regex.IsMatch(s, @"^\d+$")) return LinkKind.Steam;
        if (s.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)) return LinkKind.NexusNxm;

        if (!Uri.TryCreate(s, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return LinkKind.Invalid;

        string host = uri.Host.ToLowerInvariant();
        bool Is(string domain) => host == domain || host.EndsWith("." + domain);

        string path = uri.AbsolutePath;

        if (Is("steamcommunity.com")) return LinkKind.Steam;

        // GitHub: the repository page itself, or one specific file on it.
        if (host is "codeload.github.com" or "raw.githubusercontent.com" or "objects.githubusercontent.com")
            return LinkKind.GitFile;
        if (host is "github.com" or "www.github.com")
            return Regex.IsMatch(path, @"/(releases/download|archive|raw|blob)/") ? LinkKind.GitFile : LinkKind.GitHub;

        if (Is("gitlab.com") || Is("gitgud.io") || host.Contains("gitlab"))
            return Regex.IsMatch(path, @"/-/(archive|raw|jobs|package_files)/|/uploads/|/api/v4/") ? LinkKind.GitFile : LinkKind.GitLab;
        if (Is("nexusmods.com")) return LinkKind.Nexus;
        if (Is("loverslab.com")) return LinkKind.LoversLab;
        if (Is("mega.nz") || Is("mega.co.nz")) return LinkKind.Mega;
        if (Is("mediafire.com")) return LinkKind.MediaFire;
        if (Is("drive.google.com") || Is("drive.usercontent.google.com")) return LinkKind.GoogleDrive;
        if (Is("dropbox.com")) return LinkKind.Dropbox;
        return LinkKind.Direct;
    }

    // For a file on GitHub/GitLab: the repository it belongs to, as
    // ("github:owner/repo" or "gitlab:host/path", repository page URL).
    // Nulls when it can't be told (e.g. GitHub's storage servers).
    public static (string? RemoteId, string? RepoUrl) RepoOfFile(string url)
    {
        Uri uri = new(url);
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');

        if (uri.Host is "github.com" or "www.github.com" or "codeload.github.com" or "raw.githubusercontent.com" && parts.Length >= 2)
            return ("github:" + parts[0] + "/" + parts[1], "https://github.com/" + parts[0] + "/" + parts[1]);

        int dash = Array.IndexOf(parts, "-");
        if (dash >= 2)
        {
            string projectPath = String.Join("/", parts.Take(dash));
            return ("gitlab:" + uri.Host + "/" + projectPath, "https://" + uri.Host + "/" + projectPath);
        }

        return (null, null);
    }

    // Google Drive file id from the usual link shapes.
    public static string? GoogleDriveId(string url)
    {
        Match m = Regex.Match(url, @"/file/d/([A-Za-z0-9_-]{10,})");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(url, @"[?&]id=([A-Za-z0-9_-]{10,})");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Dropbox share links download directly with dl=1.
    public static string DropboxDirect(string url)
    {
        UriBuilder b = new(url);
        string query = Regex.Replace(b.Query.TrimStart('?'), @"(^|&)dl=\d", "");
        b.Query = (query.Length > 0 ? query.Trim('&') + "&" : "") + "dl=1";
        return b.Uri.ToString();
    }
}

// Downloads files over HTTP(S) into a folder, with progress and a sensible
// file name. Refuses web pages (a link to a page, not a file).
public static class HttpDownloader
{
    internal static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RimModManager/3.3");
        return http;
    }

    public static string Download(
        string url, string destDir, Action<string>? status, string? fileName = null,
        IDictionary<string, string>? headers = null)
    {
        Directory.CreateDirectory(destDir);

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (headers != null)
            foreach (KeyValuePair<string, string> h in headers)
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);

        using HttpResponseMessage response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new Exception("The download failed: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + " (" + ShortUrl(url) + ")");

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            throw new Exception("This link opens a web page, not a file (" + ShortUrl(url) + "). Use the page's own download link instead.");

        string name = SafeFileName(
            fileName ??
            FromContentDisposition(response.Content.Headers.ContentDisposition) ??
            Uri.UnescapeDataString(Path.GetFileName(response.RequestMessage?.RequestUri?.AbsolutePath ?? new Uri(url).AbsolutePath)));
        string target = Path.Combine(destDir, name);

        long? total = response.Content.Headers.ContentLength;
        using Stream input = response.Content.ReadAsStream();
        using FileStream output = File.Create(target);

        byte[] buffer = new byte[81920];
        long done = 0;
        DateTime lastReport = DateTime.MinValue;
        int read;

        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            done += read;

            if (status != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 400)
            {
                lastReport = DateTime.UtcNow;
                status("Downloading " + name + ": " + Cleanup.FormatBytes(done) +
                       (total is > 0 ? " of " + Cleanup.FormatBytes(total.Value) + " (" + done * 100 / total.Value + "%)" : ""));
            }
        }

        return target;
    }

    public static string GetText(string url, IDictionary<string, string>? headers = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (headers != null)
            foreach (KeyValuePair<string, string> h in headers)
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);

        using HttpResponseMessage response = Http.Send(request);
        string body = new StreamReader(response.Content.ReadAsStream()).ReadToEnd();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("HTTP " + (int)response.StatusCode + " from " + ShortUrl(url) + ": " + Shorten(body), null, response.StatusCode);
        return body;
    }

    private static string? FromContentDisposition(ContentDispositionHeaderValue? cd)
    {
        string? n = cd?.FileNameStar ?? cd?.FileName;
        return String.IsNullOrWhiteSpace(n) ? null : n.Trim('"');
    }

    internal static string SafeFileName(string name)
    {
        name = Regex.Replace(name ?? "", @"[<>:""/\\|?*\x00-\x1F]", "_").Trim();
        return name.Length == 0 ? "download.zip" : name;
    }

    internal static string ShortUrl(string url) => url.Length > 80 ? url.Substring(0, 80) + "..." : url;

    private static string Shorten(string s) => s.Length > 200 ? s.Substring(0, 200) + "..." : s;
}

// File hosts that need a small extra step before the real download.
public static class FileHosts
{
    // MediaFire: the file page contains the real download link.
    public static string MediaFire(string pageUrl, string destDir, Action<string>? status)
    {
        status?.Invoke("Reading MediaFire page...");
        string html = HttpDownloader.GetText(pageUrl);

        Match m = Regex.Match(html, @"href=""(https?://download\d*\.mediafire\.com/[^""]+)""");
        if (!m.Success)
            m = Regex.Match(html, @"id=""downloadButton""[^>]*href=""([^""]+)""");
        if (!m.Success)
            throw new Exception("Couldn't find the download on this MediaFire page. Is the file still available?");

        return HttpDownloader.Download(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), destDir, status);
    }

    // Google Drive: the usercontent endpoint with confirm=t skips the
    // "can't scan for viruses" page shown for larger files.
    public static string GoogleDrive(string url, string destDir, Action<string>? status)
    {
        string id = LinkClassifier.GoogleDriveId(url)
            ?? throw new Exception("This doesn't look like a Google Drive file link.");
        return HttpDownloader.Download(
            "https://drive.usercontent.google.com/download?id=" + id + "&export=download&confirm=t", destDir, status);
    }

    public static string Dropbox(string url, string destDir, Action<string>? status) =>
        HttpDownloader.Download(LinkClassifier.DropboxDirect(url), destDir, status);

    // MEGA encrypts files; the key is in the link after '#'. Public file
    // links only (folder links hold many files and aren't supported).
    public static string Mega(string url, string destDir, Action<string>? status)
    {
        if (url.Contains("/folder/") || url.Contains("#F!"))
            throw new Exception("MEGA folder links aren't supported; open the folder and copy the link of the file itself.");

        Uri link = new(url);
        MegaApiClient client = new();
        client.LoginAnonymous();

        try
        {
            INode node = client.GetNodeFromLink(link);
            string target = Path.Combine(destDir, HttpDownloader.SafeFileName(node.Name));
            Directory.CreateDirectory(destDir);

            Progress<double> progress = new(p => status?.Invoke("Downloading " + node.Name + " from MEGA: " + (int)p + "%"));
            client.DownloadFileAsync(link, target, progress).GetAwaiter().GetResult();
            return target;
        }
        finally
        {
            try { client.Logout(); } catch { }
        }
    }
}
