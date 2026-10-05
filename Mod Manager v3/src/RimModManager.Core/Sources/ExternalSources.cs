namespace RimModManager.Core.Sources;

// Saved tokens / keys. The app provides an encrypted implementation
// (Windows: DPAPI; Linux: the desktop keyring); tests use a dictionary.
public interface ISecretStore
{
    string? Get(string name);
    void Set(string name, string? value);
}

public static class SecretNames
{
    public const string GitHubToken = "github_token";
    public const string GitLabToken = "gitlab_token";
    public const string NexusApiKey = "nexus_api_key";
}

// A link that can't be fully automated: the user has to act in the browser
// (Nexus free accounts, LoversLab). The UI opens OpenUrl and waits.
public sealed class BrowserStepRequired : Exception
{
    public ModSource SourceKind { get; }
    public string OpenUrl { get; }
    public string PageUrl { get; }
    public int NexusModId { get; }

    public BrowserStepRequired(ModSource source, string openUrl, string pageUrl, int nexusModId, string message)
        : base(message)
    {
        SourceKind = source;
        OpenUrl = openUrl;
        PageUrl = pageUrl;
        NexusModId = nexusModId;
    }
}

public sealed record UpdateCheck(string Status, string Latest);

// Installs mods from links / files of every non-Steam source, and checks
// them for updates. Steam keeps using ModInstaller + SteamCMD.
public sealed class ExternalSources
{
    public const string StatusNoUpdateCheck = "Manual (no update check)";
    public const string StatusLoversLab = "Check page (LoversLab)";

    private readonly ArchiveInstaller _installer;
    private readonly SourceRegistry _registry;
    private readonly ISecretStore _secrets;
    private readonly ActivityLog _log;
    private readonly string _downloadDir;
    private bool? _nexusPremium;

    public ExternalSources(ArchiveInstaller installer, SourceRegistry registry, ISecretStore secrets, ActivityLog log, string downloadDir)
    {
        _installer = installer;
        _registry = registry;
        _secrets = secrets;
        _log = log;
        _downloadDir = downloadDir;
    }

    private string? Secret(string name)
    {
        string? v = _secrets.Get(name);
        return String.IsNullOrWhiteSpace(v) ? null : v;
    }

    private NexusClient Nexus() => new(Secret(SecretNames.NexusApiKey) ?? "");

    // Forget the cached Premium flag (after the key changes).
    public void ResetNexusAccount() => _nexusPremium = null;

    public List<ArchiveInstallResult> InstallLink(string link, string modsRoot, bool backup, Action<string> status)
    {
        link = link.Trim();

        switch (LinkClassifier.Classify(link))
        {
            case LinkKind.GitHub:
            {
                GitHubSource.Repo repo = GitHubSource.Parse(link);
                status("GitHub: finding the newest version of " + repo.Id + "...");
                RemoteVersion v = GitHubSource.Latest(repo, Secret(SecretNames.GitHubToken));
                return DownloadAndInstall(v.DownloadUrl, v.FileName, null, modsRoot, backup, status, new SourceRecord
                {
                    Source = ModSource.Git, Url = link, RemoteId = "github:" + repo.Id,
                    Version = v.Version, RemoteUpdated = v.Published,
                });
            }

            case LinkKind.GitLab:
            {
                GitLabSource.Project p = GitLabSource.Parse(link);
                status("GitLab: finding the newest version of " + p.Id + "...");
                string? token = Secret(SecretNames.GitLabToken);
                RemoteVersion v = GitLabSource.Latest(p, token);
                return DownloadAndInstall(v.DownloadUrl, v.FileName,
                    token == null ? null : new Dictionary<string, string> { ["PRIVATE-TOKEN"] = token },
                    modsRoot, backup, status, new SourceRecord
                    {
                        Source = ModSource.Git, Url = link, RemoteId = "gitlab:" + p.Id,
                        Version = v.Version, RemoteUpdated = v.Published,
                    });
            }

            case LinkKind.GitFile:
            {
                // Exactly the file linked. Tagged Git and tied to its
                // repository, so a later update fetches the newest version.
                (string? remoteId, string? repoUrl) = LinkClassifier.RepoOfFile(link);
                return DownloadAndInstall(link, null, null, modsRoot, backup, status, new SourceRecord
                {
                    Source = ModSource.Git, Url = repoUrl ?? link, RemoteId = remoteId ?? "",
                });
            }

            case LinkKind.Nexus:
                return InstallNexusMod(NexusSource.ParseModPage(link), modsRoot, backup, status);

            case LinkKind.NexusNxm:
                return InstallNxm(link, modsRoot, backup, status);

            case LinkKind.LoversLab:
                throw new BrowserStepRequired(ModSource.LoversLab, link, link, 0,
                    "LoversLab only allows downloads from a browser. Download the file there; " +
                    "the manager picks it up from your Downloads folder and installs it.");

            case LinkKind.Mega:
                return InstallDownloaded(FileHosts.Mega(link, NewDownloadDir(), status), link, modsRoot, backup, status);
            case LinkKind.MediaFire:
                return InstallDownloaded(FileHosts.MediaFire(link, NewDownloadDir(), status), link, modsRoot, backup, status);
            case LinkKind.GoogleDrive:
                return InstallDownloaded(FileHosts.GoogleDrive(link, NewDownloadDir(), status), link, modsRoot, backup, status);
            case LinkKind.Dropbox:
                return InstallDownloaded(FileHosts.Dropbox(link, NewDownloadDir(), status), link, modsRoot, backup, status);
            case LinkKind.Direct:
                return InstallDownloaded(HttpDownloader.Download(link, NewDownloadDir(), status), link, modsRoot, backup, status);

            case LinkKind.Steam:
                throw new Exception("Steam links are installed through SteamCMD, not this path.");
            default:
                throw new Exception("Not a link the manager understands: " + link);
        }
    }

    private List<ArchiveInstallResult> InstallNexusMod(int modId, string modsRoot, bool backup, Action<string> status)
    {
        NexusClient nexus = Nexus();
        _nexusPremium ??= nexus.Validate().IsPremium;

        if (_nexusPremium != true)
        {
            throw new BrowserStepRequired(ModSource.Nexus, NexusSource.FilesPageUrl(modId), NexusSource.ModPageUrl(modId), modId,
                "Nexus requires free accounts to start downloads on its website. " +
                "On the Files tab that opens, click \"Mod Manager Download\"; the manager takes it from there.");
        }

        status("Nexus: finding the newest file of mod " + modId + "...");
        NexusSource.NexusFile file = nexus.LatestMainFile(modId)
            ?? throw new Exception("This Nexus mod has no main file to download.");
        string url = nexus.DownloadUrl(modId, file.FileId, null, null);

        return DownloadAndInstall(url, file.FileName, null, modsRoot, backup, status, NexusRecord(modId, file));
    }

    public List<ArchiveInstallResult> InstallNxm(string nxm, string modsRoot, bool backup, Action<string> status)
    {
        NexusSource.NxmLink link = NexusSource.ParseNxm(nxm);
        NexusClient nexus = Nexus();

        status("Nexus: preparing download of mod " + link.ModId + "...");
        NexusSource.NexusFile? file = nexus.Files(link.ModId).FirstOrDefault(f => f.FileId == link.FileId);
        string url = nexus.DownloadUrl(link.ModId, link.FileId, link.Key, link.Expires);

        return DownloadAndInstall(url, file?.FileName, null, modsRoot, backup, status,
            NexusRecord(link.ModId, file ?? new NexusSource.NexusFile(link.FileId, "", "", "", 0, "")));
    }

    private static SourceRecord NexusRecord(int modId, NexusSource.NexusFile file) => new()
    {
        Source = ModSource.Nexus,
        Url = NexusSource.ModPageUrl(modId),
        RemoteId = modId.ToString(),
        Version = file.FileId + (String.IsNullOrWhiteSpace(file.Version) ? "" : " (v" + file.Version + ")"),
        RemoteUpdated = file.Uploaded,
    };

    // An archive already on disk: LoversLab pick-ups (source LoversLab, page
    // link) or a file the user chose (Manual).
    public List<ArchiveInstallResult> InstallFile(
        string archivePath, ModSource source, string pageUrl, string modsRoot, bool backup, Action<string> status)
    {
        return _installer.Install(archivePath, modsRoot, backup, new SourceRecord
        {
            Source = source,
            Url = pageUrl,
            FileName = Path.GetFileName(archivePath),
            RemoteUpdated = new DateTimeOffset(File.GetLastWriteTimeUtc(archivePath)).ToUnixTimeSeconds(),
        }, status);
    }

    private List<ArchiveInstallResult> InstallDownloaded(
        string file, string link, string modsRoot, bool backup, Action<string> status)
    {
        try
        {
            return _installer.Install(file, modsRoot, backup,
                new SourceRecord { Source = ModSource.Manual, Url = link, FileName = Path.GetFileName(file) }, status);
        }
        finally
        {
            TryDeleteDownload(file);
        }
    }

    private List<ArchiveInstallResult> DownloadAndInstall(
        string url, string? fileName, IDictionary<string, string>? headers,
        string modsRoot, bool backup, Action<string> status, SourceRecord origin)
    {
        string file = HttpDownloader.Download(url, NewDownloadDir(), status, fileName, headers);
        origin.FileName = Path.GetFileName(file);

        try
        {
            return _installer.Install(file, modsRoot, backup, origin, status);
        }
        finally
        {
            TryDeleteDownload(file);
        }
    }

    private string NewDownloadDir() => Path.Combine(_downloadDir, Guid.NewGuid().ToString("N").Substring(0, 8));

    private static void TryDeleteDownload(string file)
    {
        try { SafeFileSystem.DeleteDirectory(Path.GetDirectoryName(file)!); } catch { }
    }

    // ------------------------------------------------------------------
    // Updates
    // ------------------------------------------------------------------

    public UpdateCheck Check(SourceRecord r)
    {
        switch (r.Source)
        {
            case ModSource.Git when r.RemoteId.StartsWith("github:"):
            {
                RemoteVersion v = GitHubSource.Latest(GitHubSource.Parse(r.Url), Secret(SecretNames.GitHubToken));
                return new UpdateCheck(GitStatus(r, v), v.Label);
            }

            case ModSource.Git when r.RemoteId.StartsWith("gitlab:"):
            {
                RemoteVersion v = GitLabSource.Latest(GitLabSource.Parse(r.Url), Secret(SecretNames.GitLabToken));
                return new UpdateCheck(GitStatus(r, v), v.Label);
            }

            case ModSource.Nexus:
            {
                NexusSource.NexusFile? latest = Nexus().LatestMainFile(int.Parse(r.RemoteId));
                if (latest == null) return new UpdateCheck("Missing / removed", "");
                bool newer = latest.Uploaded > r.RemoteUpdated;
                return new UpdateCheck(newer ? "Update available" : "Current", "file " + latest.FileId + (latest.Version.Length > 0 ? " (v" + latest.Version + ")" : ""));
            }

            case ModSource.LoversLab:
                return new UpdateCheck(StatusLoversLab, "");

            default:
                return new UpdateCheck(StatusNoUpdateCheck, "");
        }
    }

    // A single-file install doesn't know its version: "Unknown" until the
    // first update brings it to the newest one.
    private static string GitStatus(SourceRecord r, RemoteVersion latest) =>
        String.IsNullOrEmpty(r.Version) ? "Unknown (update once)" :
        latest.Version == r.Version ? "Current" :
        "Update available";

    // Re-installs from the remembered link. Git, Nexus Premium and plain links
    // download again; Nexus free and LoversLab need the browser step.
    public List<ArchiveInstallResult> Update(SourceRecord r, string modsRoot, bool backup, Action<string> status)
    {
        if (r.Source == ModSource.LoversLab)
            throw new BrowserStepRequired(ModSource.LoversLab, r.Url, r.Url, 0,
                "Download the new version from LoversLab in your browser; the manager picks it up from your Downloads folder.");

        if (r.Source == ModSource.Nexus)
            return InstallNexusMod(int.Parse(r.RemoteId), modsRoot, backup, status);

        if (String.IsNullOrWhiteSpace(r.Url) || !Uri.TryCreate(r.Url, UriKind.Absolute, out _))
            throw new Exception(r.Folder + " was installed from a file on this computer; there is no link to update it from.");

        return InstallLink(r.Url, modsRoot, backup, status);
    }

    // ------------------------------------------------------------------
    // Whole lists (Installed / Updates tab)
    // ------------------------------------------------------------------

    public sealed class ListUpdateResult
    {
        public List<ArchiveInstallResult> Installed { get; } = new();
        public List<BrowserStepRequired> BrowserSteps { get; } = new();
        public List<string> Failures { get; } = new();
    }

    public void CheckEntries(List<ModEntry> entries, IModEntrySink sink, Action<string> status)
    {
        int n = 0;
        foreach (ModEntry e in entries)
        {
            n++;
            SourceRecord? r = _registry.Find(e.FolderName);
            if (r == null)
            {
                sink.SetStatus(e, StatusNoUpdateCheck);
                continue;
            }

            status("Checking " + ModSourceTags.Tag(r.Source) + " " + n + "/" + entries.Count + ": " + e.FolderName);
            sink.SetStatus(e, "Checking...");

            try
            {
                UpdateCheck c = Check(r);
                sink.SetStatus(e, c.Status);
            }
            catch (Exception ex)
            {
                sink.SetStatus(e, "Check error");
                _log.Add("ERROR", "[" + ModSourceTags.Tag(r.Source) + "] " + e.FolderName + ": update check failed: " + ex.Message);
            }
        }
    }

    public ListUpdateResult UpdateEntries(
        List<ModEntry> entries, string modsRoot, bool backup, IModEntrySink sink, Action<string> status)
    {
        ListUpdateResult result = new();

        foreach (ModEntry e in entries)
        {
            SourceRecord? r = _registry.Find(e.FolderName);
            if (r == null)
            {
                sink.SetStatus(e, StatusNoUpdateCheck);
                continue;
            }

            string tag = ModSourceTags.Tag(r.Source);
            sink.SetStatus(e, "Downloading...");

            try
            {
                List<ArchiveInstallResult> installed = Update(r, modsRoot, backup, status);
                result.Installed.AddRange(installed);
                bool ok = installed.All(i => i.Succeeded);
                sink.SetStatus(e, ok ? "Updated" : "FAILED - " + installed.First(i => !i.Succeeded).Message);
                if (!ok) result.Failures.Add(e.FolderName + ": " + installed.First(i => !i.Succeeded).Message);
            }
            catch (BrowserStepRequired step)
            {
                result.BrowserSteps.Add(step);
                sink.SetStatus(e, "Waiting for browser download");
            }
            catch (Exception ex)
            {
                sink.SetStatus(e, "FAILED - previous version kept");
                result.Failures.Add(e.FolderName + ": " + ex.Message);
                _log.Add("ERROR", "[" + tag + "] " + e.FolderName + ": update failed, previous version kept: " + ex.Message);
            }
        }

        return result;
    }
}