namespace Ghud;

// A link that can't be fully automated: the user has to act in the browser
// (free Nexus accounts, LoversLab). The window opens OpenUrl and waits for
// the file (Nexus: an nxm:// link; LoversLab: the Downloads folder).
public sealed class BrowserStepRequired : Exception
{
    public ModSource SourceKind { get; }
    public string OpenUrl { get; }

    public BrowserStepRequired(ModSource source, string openUrl, string message) : base(message)
    {
        SourceKind = source;
        OpenUrl = openUrl;
    }
}

public enum UpdateState { Current, UpdateAvailable, Unknown, NoCheck, CheckOnPage, Missing }

// Result of an update check: the state, the newest version's label, and a
// short text for the list.
public sealed record UpdateResult(UpdateState State, string Latest)
{
    public string Text => State switch
    {
        UpdateState.Current => "Current",
        UpdateState.UpdateAvailable => "Update: " + Latest,
        UpdateState.Unknown => "Unknown (update once)",
        UpdateState.CheckOnPage => "Check the page",
        UpdateState.Missing => "Removed at source",
        _ => "No update check",
    };
}

// Turns a pasted link into an archive in the instance's download library,
// with where it came from (source tag, version, update-check ID).
public sealed class LinkDownloader
{
    private readonly ISecretStore _secrets;
    private readonly string? _nexusGame;
    private bool? _nexusPremium;

    public LinkDownloader(ISecretStore secrets, string? nexusGame)
    {
        _secrets = secrets;
        _nexusGame = nexusGame;
    }

    private string? Secret(string name)
    {
        string? v = _secrets.Get(name);
        return String.IsNullOrWhiteSpace(v) ? null : v;
    }

    // Forget the cached Premium flag (after the key changes).
    public void ResetNexusAccount() => _nexusPremium = null;

    public (string Archive, ArchiveOrigin Origin) Download(string link, string libraryDir, Action<string> status)
    {
        link = link.Trim();
        switch (LinkClassifier.Classify(link))
        {
            case LinkKind.GitHub:
            {
                GitHubSource.Repo repo = GitHubSource.Parse(link);
                status("GitHub: finding the newest version of " + repo.Id + "...");
                RemoteVersion v = GitHubSource.Latest(repo, Secret(SecretNames.GitHubToken));
                return (Fetch(v.DownloadUrl, v.FileName, null, libraryDir, status),
                        new ArchiveOrigin(ModSource.Git, link, "github:" + repo.Id, v.Version, v.Published));
            }

            case LinkKind.GitLab:
            {
                GitLabSource.Project p = GitLabSource.Parse(link);
                status("GitLab: finding the newest version of " + p.Id + "...");
                string? token = Secret(SecretNames.GitLabToken);
                RemoteVersion v = GitLabSource.Latest(p, token);
                return (Fetch(v.DownloadUrl, v.FileName, token == null ? null : new Dictionary<string, string> { ["PRIVATE-TOKEN"] = token },
                              libraryDir, status),
                        new ArchiveOrigin(ModSource.Git, link, "gitlab:" + p.Id, v.Version, v.Published));
            }

            case LinkKind.GitFile:
            {
                // Exactly the file linked, tied to its repository for update checks.
                (string? remoteId, string? repoUrl) = LinkClassifier.RepoOfFile(link);
                return (Fetch(link, null, null, libraryDir, status), new ArchiveOrigin(ModSource.Git, repoUrl ?? link, remoteId));
            }

            case LinkKind.Nexus:
            {
                Nexus.ModRef mod = Nexus.ParseModPage(link);
                CheckNexusGame(mod.Game);
                return DownloadNexus(mod.ModId, libraryDir, status);
            }

            case LinkKind.NexusNxm:
                return DownloadNxm(link, libraryDir, status);

            case LinkKind.LoversLab:
                throw new BrowserStepRequired(ModSource.LoversLab, link,
                    "LoversLab only allows downloads from a browser. Download the file on the page that opens; " +
                    "GHUD picks it up from your Downloads folder.");

            case LinkKind.Mega:
                return (Library(FileHosts.Mega(link, TempDir(libraryDir), status), libraryDir), ArchiveOrigin.Manual(link));
            case LinkKind.MediaFire:
                return (Library(FileHosts.MediaFire(link, TempDir(libraryDir), status), libraryDir), ArchiveOrigin.Manual(link));
            case LinkKind.GoogleDrive:
                return (Library(FileHosts.GoogleDrive(link, TempDir(libraryDir), status), libraryDir), ArchiveOrigin.Manual(link));
            case LinkKind.Dropbox:
                return (Library(FileHosts.Dropbox(link, TempDir(libraryDir), status), libraryDir), ArchiveOrigin.Manual(link));
            case LinkKind.Direct:
                return (Fetch(link, null, null, libraryDir, status), ArchiveOrigin.Manual(link));

            case LinkKind.Steam:
                throw new Exception("Steam Workshop downloads aren't in GHUD yet.");
            default:
                throw new Exception("Not a link GHUD understands: " + link);
        }
    }

    // ---------------- Updates ----------------

    // Asks the mod's source for its newest version. Mods from a file or a
    // plain link have nothing to ask; LoversLab only answers browsers.
    public UpdateResult Check(StagedMod m)
    {
        string remote = m.RemoteId ?? "";
        switch (m.Source)
        {
            case ModSource.Git when remote.StartsWith("github:") && m.Url != null:
            {
                RemoteVersion v = GitHubSource.Latest(GitHubSource.Parse(m.Url), Secret(SecretNames.GitHubToken));
                return new UpdateResult(GitState(m, v), v.Label);
            }

            case ModSource.Git when remote.StartsWith("gitlab:") && m.Url != null:
            {
                RemoteVersion v = GitLabSource.Latest(GitLabSource.Parse(m.Url), Secret(SecretNames.GitLabToken));
                return new UpdateResult(GitState(m, v), v.Label);
            }

            case ModSource.Nexus when int.TryParse(remote, out int modId) && _nexusGame != null:
            {
                Nexus.NexusFile? latest = new NexusClient(Secret(SecretNames.NexusApiKey) ?? "", _nexusGame).LatestMainFile(modId);
                if (latest == null) return new UpdateResult(UpdateState.Missing, "");
                string label = String.IsNullOrWhiteSpace(latest.Version) ? "file " + latest.FileId : latest.Version;
                return new UpdateResult(latest.Uploaded > m.RemoteUpdated ? UpdateState.UpdateAvailable : UpdateState.Current, label);
            }

            case ModSource.LoversLab:
                return new UpdateResult(UpdateState.CheckOnPage, "");

            default:
                return new UpdateResult(UpdateState.NoCheck, "");
        }
    }

    // A file link doesn't know its version: "Unknown" until updated once.
    private static UpdateState GitState(StagedMod m, RemoteVersion latest) =>
        String.IsNullOrEmpty(m.Version) ? UpdateState.Unknown :
        latest.Version == m.Version ? UpdateState.Current :
        UpdateState.UpdateAvailable;

    // Downloads the newest version from where the mod came from.
    public (string Archive, ArchiveOrigin Origin) DownloadUpdate(StagedMod m, string libraryDir, Action<string> status)
    {
        if (m.Source == ModSource.LoversLab)
            throw new BrowserStepRequired(ModSource.LoversLab, m.Url ?? "https://www.loverslab.com/",
                "Download the new version of " + m.Name + " from LoversLab in your browser, then install it from the file.");

        if (m.Source == ModSource.Nexus && int.TryParse(m.RemoteId, out int modId))
        {
            if (_nexusGame == null) throw new Exception("This game has no Nexus Mods name set.");
            return DownloadNexus(modId, libraryDir, status);
        }

        if (String.IsNullOrWhiteSpace(m.Url) || !Uri.TryCreate(m.Url, UriKind.Absolute, out _))
            throw new Exception(m.Name + " was installed from a file on this computer; there is no link to update it from.");

        return Download(m.Url, libraryDir, status);
    }

    private (string, ArchiveOrigin) DownloadNexus(int modId, string libraryDir, Action<string> status)
    {
        string game = _nexusGame!;
        NexusClient nexus = new(Secret(SecretNames.NexusApiKey) ?? "", game);
        _nexusPremium ??= nexus.Validate().IsPremium;

        if (_nexusPremium != true)
            throw new BrowserStepRequired(ModSource.Nexus, Nexus.FilesPageUrl(game, modId),
                "Nexus requires free accounts to start downloads on its website. On the Files tab that opens, " +
                "click \"Mod Manager Download\"; GHUD takes it from there.");

        status("Nexus: finding the newest file of mod " + modId + "...");
        Nexus.NexusFile file = nexus.LatestMainFile(modId) ?? throw new Exception("This Nexus mod has no main file to download.");
        string url = nexus.DownloadUrl(modId, file.FileId, null, null);
        return (Fetch(url, file.FileName, null, libraryDir, status), NexusOrigin(game, modId, file));
    }

    public (string Archive, ArchiveOrigin Origin) DownloadNxm(string nxm, string libraryDir, Action<string> status)
    {
        Nexus.NxmLink link = Nexus.ParseNxm(nxm);
        CheckNexusGame(link.Game);
        NexusClient nexus = new(Secret(SecretNames.NexusApiKey) ?? "", link.Game);

        status("Nexus: preparing the download of mod " + link.ModId + "...");
        Nexus.NexusFile? file = nexus.Files(link.ModId).FirstOrDefault(f => f.FileId == link.FileId);
        string url = nexus.DownloadUrl(link.ModId, link.FileId, link.Key, link.Expires);
        return (Fetch(url, file?.FileName, null, libraryDir, status),
                NexusOrigin(link.Game, link.ModId, file ?? new Nexus.NexusFile(link.FileId, "", "", "", 0, "")));
    }

    private void CheckNexusGame(string game)
    {
        if (_nexusGame == null)
            throw new Exception("This game has no Nexus Mods name set, so Nexus links can't be used for it.");
        if (!game.Equals(_nexusGame, StringComparison.OrdinalIgnoreCase))
            throw new Exception("This Nexus link is for " + game + ", not this game (" + _nexusGame + ").");
    }

    private static ArchiveOrigin NexusOrigin(string game, int modId, Nexus.NexusFile file) => new(
        ModSource.Nexus, Nexus.ModPageUrl(game, modId), modId.ToString(),
        String.IsNullOrWhiteSpace(file.Version) ? "file " + file.FileId : file.Version, file.Uploaded);

    // Downloads into a temporary folder, then moves the file into the library.
    private static string Fetch(string url, string? fileName, IDictionary<string, string>? headers, string libraryDir, Action<string> status) =>
        Library(HttpDownloader.Download(url, TempDir(libraryDir), status, fileName, headers), libraryDir);

    private static string TempDir(string libraryDir) => Path.Combine(libraryDir, ".partial", Guid.NewGuid().ToString("N")[..8]);

    // Moves a finished download from its temporary folder into the library
    // (a name already taken gets " (2)" etc.).
    private static string Library(string downloaded, string libraryDir)
    {
        string name = Path.GetFileName(downloaded);
        string target = Path.Combine(libraryDir, name);
        for (int n = 2; File.Exists(target); n++)
            target = Path.Combine(libraryDir, Path.GetFileNameWithoutExtension(name) + " (" + n + ")" + Path.GetExtension(name));

        File.Move(downloaded, target);
        try { Directory.Delete(Path.GetDirectoryName(downloaded)!, recursive: false); } catch { }
        return target;
    }
}
