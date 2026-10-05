using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimModManager.App.Services;
using RimModManager.Core;
using RimModManager.Core.Sources;

namespace RimModManager.App.ViewModels;

// A download the user has to finish in the browser (free Nexus accounts,
// LoversLab); the manager takes over when the file / nxm link arrives.
public sealed record PendingDownload(ModSource Source, string PageUrl, int NexusModId, string Text);

// Non-Steam sources on the Install tab: accounts, links, browser hand-offs.
public partial class MainViewModel
{
    private SecretStore _secrets = null!;
    private ExternalSources _external = null!;
    private DownloadWatcher? _watcher;

    public ObservableCollection<PendingDownload> Pending { get; } = new();

    public bool HasPending => Pending.Count > 0;

    [ObservableProperty]
    public partial string GitHubToken { get; set; } = "";

    [ObservableProperty]
    public partial string GitLabToken { get; set; } = "";

    [ObservableProperty]
    public partial string NexusApiKey { get; set; } = "";

    [ObservableProperty]
    public partial string DownloadsFolder { get; set; } = "";

    [ObservableProperty]
    public partial string NexusAccountText { get; set; } = "";

    [ObservableProperty]
    public partial string NexusHandlerText { get; set; } = "";

    public string SecretsInfo => _secrets.Description;

    private void InitSources()
    {
        _secrets = new SecretStore();
        ArchiveInstaller archives = new(_state, _sources, _log, AppPaths.DownloadWorkDir);
        _external = new ExternalSources(archives, _sources, _secrets, _log, AppPaths.DownloadWorkDir);

        GitHubToken = _secrets.Get(SecretNames.GitHubToken) ?? "";
        GitLabToken = _secrets.Get(SecretNames.GitLabToken) ?? "";
        NexusApiKey = _secrets.Get(SecretNames.NexusApiKey) ?? "";
        DownloadsFolder = String.IsNullOrWhiteSpace(_state.Data.DownloadsFolder)
            ? AppPaths.DefaultDownloadsFolder()
            : _state.Data.DownloadsFolder;
        RefreshNexusHandlerText();

        Pending.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPending));

        // nxm:// links forwarded by a second copy of the program (Nexus
        // "Mod Manager Download"), and the link we were started with.
        NexusLinks.Listen(message => Dispatcher.UIThread.Post(() => OnForwardedMessage(message)));
        if (Program.StartupLink is { } link)
            Dispatcher.UIThread.Post(() => OnForwardedMessage(link), DispatcherPriority.Background);
    }

    private void RefreshNexusHandlerText() =>
        NexusHandlerText = NexusLinks.IsRegisteredToUs()
            ? "Nexus \"Mod Manager Download\" buttons open this manager."
            : "Nexus \"Mod Manager Download\" buttons don't open this manager yet.";

    // ------------------------------------------------------------------
    // Accounts
    // ------------------------------------------------------------------

    [RelayCommand]
    private void SaveAccounts()
    {
        _secrets.Set(SecretNames.GitHubToken, GitHubToken);
        _secrets.Set(SecretNames.GitLabToken, GitLabToken);
        _secrets.Set(SecretNames.NexusApiKey, NexusApiKey);
        _external.ResetNexusAccount();

        _state.Data.DownloadsFolder = Normalize(DownloadsFolder);
        SaveStateQuietly();

        SetStatus("Accounts saved. " + _secrets.Description);
        _log.Add("INFO", "Accounts saved (tokens/keys are not written to the log).");
    }

    [RelayCommand]
    private async Task CheckNexusKey()
    {
        SaveAccounts();
        string key = NexusApiKey;
        (string Name, bool IsPremium) account = default;

        await RunAsync(
            () => account = NexusSource.Client(key).Validate(),
            () =>
            {
                NexusAccountText = "Nexus key works: " + account.Name + " (" + (account.IsPremium ? "Premium" : "free") + " account).";
                SetStatus(NexusAccountText);
                return Task.CompletedTask;
            });
    }

    [RelayCommand]
    private async Task RegisterNexusHandler()
    {
        string current = NexusLinks.CurrentHandler() ?? "nothing";
        string owner =
            current.Contains("ModOrganizer", StringComparison.OrdinalIgnoreCase) || current.Contains("nxmhandler", StringComparison.OrdinalIgnoreCase) ? "Mod Organizer" :
            current.Contains("Vortex", StringComparison.OrdinalIgnoreCase) ? "Vortex" :
            current == "nothing" ? "no program" : "another program";

        if (!await _dialogs.Confirm(
                "Make Nexus \"Mod Manager Download\" buttons open this manager?" + Environment.NewLine + Environment.NewLine +
                "Right now they open: " + owner + "." + Environment.NewLine +
                "Afterwards that program won't receive Nexus downloads for ANY game until you switch it back " +
                "in its own settings (for Mod Organizer: its \"Associate with download links\" button).",
                "Nexus download links"))
            return;

        try
        {
            NexusLinks.Register();
            RefreshNexusHandlerText();
            _log.Add("INFO", "Registered as the handler for Nexus (nxm://) links.");
        }
        catch (Exception ex)
        {
            await _dialogs.Error("Couldn't register for Nexus links: " + ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLink(string url)
    {
        try { NexusLinks.OpenInBrowser(url); }
        catch (Exception ex) { _log.Add("ERROR", "Couldn't open the browser: " + ex.Message); }
    }

    [RelayCommand]
    private async Task BrowseDownloadsFolder()
    {
        string? path = await _dialogs.PickFolder("Folder your browser downloads to", Normalize(DownloadsFolder));
        if (path == null) return;

        DownloadsFolder = path;
        SaveAccounts();
        if (_watcher != null) StartWatcher();
    }

    // ------------------------------------------------------------------
    // Install from links / files
    // ------------------------------------------------------------------

    // Runs the non-Steam links of an Install All (after the Steam part).
    private (List<ArchiveInstallResult> Installed, List<BrowserStepRequired> Steps, List<string> Failures)
        InstallLinks(List<string> links, string modsRoot, bool backup)
    {
        List<ArchiveInstallResult> installed = new();
        List<BrowserStepRequired> steps = new();
        List<string> failures = new();

        foreach (string link in links)
        {
            try
            {
                installed.AddRange(_external.InstallLink(link, modsRoot, backup, SetStatus));
            }
            catch (BrowserStepRequired step)
            {
                steps.Add(step);
            }
            catch (Exception ex)
            {
                failures.Add(link + Environment.NewLine + "    " + ex.Message);
                _log.Add("ERROR", "Link failed: " + link + " : " + ex.Message);
            }
        }

        return (installed, steps, failures);
    }

    [RelayCommand]
    private async Task InstallFromFile()
    {
        string modsRoot = Normalize(ModsFolder);
        if (!Directory.Exists(modsRoot))
        {
            await _dialogs.Info("Choose your RimWorld Mods folder first.");
            return;
        }

        string? file = await _dialogs.OpenFile("Install a mod from an archive", "Archives", "*.zip;*.7z;*.rar;*.tar;*.gz");
        if (file == null) return;

        bool backup = BackupBeforeReplace;
        List<ArchiveInstallResult> results = new();

        await RunAsync(
            () => results = _external.InstallFile(file, ModSource.Manual, "", modsRoot, backup, SetStatus),
            async () =>
            {
                _ = Scan();
                await ShowSourcesSummary("Installed from " + Path.GetFileName(file) + ".", null, results, new(), new());
            });
    }

    // A browser step: open the page and remember what we're waiting for.
    private void BeginBrowserStep(BrowserStepRequired step)
    {
        string text = step.SourceKind == ModSource.Nexus
            ? "Nexus mod " + step.NexusModId + ": click \"Mod Manager Download\" on its Files tab"
            : "LoversLab: download the file in your browser (" + ShortLink(step.PageUrl) + ")";

        if (!Pending.Any(p => p.PageUrl == step.PageUrl))
            Pending.Add(new PendingDownload(step.SourceKind, step.PageUrl, step.NexusModId, text));

        try { NexusLinks.OpenInBrowser(step.OpenUrl); }
        catch (Exception ex) { _log.Add("ERROR", "Couldn't open the browser: " + ex.Message); }

        if (step.SourceKind == ModSource.LoversLab)
            StartWatcher();
    }

    private static string ShortLink(string url) => url.Length > 70 ? url.Substring(0, 70) + "..." : url;

    [RelayCommand]
    private void ClearPending()
    {
        Pending.Clear();
        StopWatcher();
    }

    private void StartWatcher()
    {
        StopWatcher();
        string folder = Normalize(DownloadsFolder);

        try
        {
            _watcher = new DownloadWatcher(folder);
            _watcher.ArchiveArrived += path => Dispatcher.UIThread.Post(() => _ = OnArchiveArrived(path));
            SetStatus("Watching " + folder + " for the LoversLab download...");
        }
        catch (Exception ex)
        {
            _watcher = null;
            _log.Add("ERROR", "Can't watch the Downloads folder (" + folder + "): " + ex.Message);
        }
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // A finished archive appeared in Downloads while a LoversLab download
    // was pending: install it as that mod.
    private async Task OnArchiveArrived(string path)
    {
        PendingDownload? item = Pending.FirstOrDefault(p => p.Source == ModSource.LoversLab);
        if (item == null) return;

        Pending.Remove(item);
        if (!Pending.Any(p => p.Source == ModSource.LoversLab))
            StopWatcher();

        _log.Add("DOWNLOAD", "[LoversLab] picked up " + Path.GetFileName(path) + " from " + Path.GetDirectoryName(path));
        string modsRoot = Normalize(ModsFolder);
        bool backup = BackupBeforeReplace;
        List<ArchiveInstallResult> results = new();

        await RunWhenIdle(
            () => results = _external.InstallFile(path, ModSource.LoversLab, item.PageUrl, modsRoot, backup, SetStatus),
            async () =>
            {
                _ = Scan();
                await ShowSourcesSummary("LoversLab download installed from " + Path.GetFileName(path) +
                    " (the file stays in your Downloads folder).", null, results, new(), new());
            });
    }

    private void OnForwardedMessage(string message)
    {
        if (message == "activate")
        {
            _dialogs.BringToFront();
            return;
        }

        if (message.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.BringToFront();
            _ = HandleNxm(message);
        }
    }

    private async Task HandleNxm(string link)
    {
        string modsRoot = Normalize(ModsFolder);
        if (!Directory.Exists(modsRoot))
        {
            await _dialogs.Info("A Nexus download arrived, but no RimWorld Mods folder is set. Choose it on the Install tab and click the button on Nexus again.");
            return;
        }

        try
        {
            int modId = NexusSource.ParseNxm(link).ModId;
            foreach (PendingDownload p in Pending.Where(p => p.Source == ModSource.Nexus && p.NexusModId == modId).ToList())
                Pending.Remove(p);
        }
        catch (Exception ex)
        {
            await _dialogs.Error(ex.Message);
            return;
        }

        bool backup = BackupBeforeReplace;
        List<ArchiveInstallResult> results = new();

        await RunWhenIdle(
            () => results = _external.InstallNxm(link, modsRoot, backup, SetStatus),
            async () =>
            {
                _ = Scan();
                await ShowSourcesSummary("Nexus download finished.", null, results, new(), new());
            });
    }

    // Browser hand-offs can arrive while something else runs; queue them.
    private async Task RunWhenIdle(Action work, Func<Task> success)
    {
        while (IsBusy)
            await Task.Delay(500);
        await RunAsync(work, success);
    }

    // One summary for Steam + other sources.
    private Task ShowSourcesSummary(
        string header, List<InstallJob>? steamJobs, List<ArchiveInstallResult> installed,
        List<BrowserStepRequired> steps, List<string> linkFailures)
    {
        StringBuilder sb = new();
        sb.AppendLine(header);

        if (steamJobs is { Count: > 0 })
        {
            int ok = steamJobs.Count(j => j.Succeeded);
            sb.AppendLine();
            sb.AppendLine("Steam: " + ok + " installed/updated" + (ok < steamJobs.Count ? ", " + (steamJobs.Count - ok) + " FAILED (see Manager Log)" : "") + ".");
        }

        foreach (ArchiveInstallResult r in installed)
        {
            string tag = ModSourceTags.Tag(_sources.Find(r.Folder, r.PackageId)?.Source ?? ModSource.Manual);
            sb.AppendLine((r.Succeeded ? "  OK   [" : "  FAIL [") + tag + "] " + r.Name + " -> " + r.Folder +
                          (String.IsNullOrEmpty(r.Message) ? "" : " (" + r.Message + ")"));
        }

        if (linkFailures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("These links failed:");
            foreach (string f in linkFailures.Take(12)) sb.AppendLine("  " + f);
        }

        if (steps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Finish these in your browser (the pages were opened):");
            foreach (BrowserStepRequired s in steps) sb.AppendLine("  - " + s.Message);

            if (steps.Any(s => s.SourceKind == ModSource.Nexus) && !NexusLinks.IsRegisteredToUs())
            {
                sb.AppendLine();
                sb.AppendLine("Note: Nexus \"Mod Manager Download\" buttons don't open this manager yet. " +
                              "Use \"Handle Nexus links\" under Accounts first.");
            }
        }

        bool problems = linkFailures.Count > 0 || installed.Any(r => !r.Succeeded) || steamJobs?.Any(j => !j.Succeeded) == true;
        return problems ? _dialogs.Warning(sb.ToString()) : _dialogs.Info(sb.ToString());
    }
}
