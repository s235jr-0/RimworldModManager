using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ghud.App.ViewModels;

// A download waiting for the user to finish it in the browser.
public sealed record PendingDownload(ModSource Source, string PageUrl, string Text);

// One row of a game's mod list (the active profile).
public partial class ModRow : ObservableObject
{
    private GameViewModel? _owner;

    public string Key { get; }
    public StagedMod Mod { get; }
    public string Name => Mod.Name;
    public string SourceTag => ModSourceTags.Tag(Mod.Source);
    public string Version => Mod.Version ?? "";

    [ObservableProperty] public partial int Position { get; set; }
    [ObservableProperty] public partial bool Enabled { get; set; }
    [ObservableProperty] public partial string ConflictText { get; set; } = "";
    [ObservableProperty] public partial string UpdateText { get; set; } = "";
    public UpdateState? UpdateState { get; set; }

    public ModRow(StagedMod mod, bool enabled, int position)
    {
        Key = mod.Key;
        Mod = mod;
        Enabled = enabled;
        Position = position;
    }

    public void Attach(GameViewModel owner) => _owner = owner;

    partial void OnEnabledChanged(bool value) => _owner?.OnRowEnabledChanged(this);
}

// A game's own screen: profiles, the mod list (priority order), installing,
// conflicts, deploying into the game.
public partial class GameViewModel : ObservableObject
{
    public MainViewModel Main { get; }
    public GameInstance Instance { get; }

    private readonly IGameModule _module;
    private readonly ModSet _mods;
    private readonly Staging _staging;
    private readonly Deployer _deployer;
    private readonly LinkDownloader _links;
    private bool _switchingProfile;

    // Per mod key: conflicting files it wins / loses, and with whom.
    private Dictionary<string, (List<string> Wins, List<string> Loses)> _conflicts = new();

    public ObservableCollection<ModRow> Rows { get; } = new();
    public ObservableCollection<string> Profiles { get; } = new();

    // Downloads the user has to finish in the browser (LoversLab, Nexus free).
    public ObservableCollection<PendingDownload> Pending { get; } = new();
    public bool HasPending => Pending.Count > 0;
    private DownloadWatcher? _watcher;

    public string? NexusGame => _module.NexusDomain;

    public string GameName => Instance.Name;
    public string ModuleName => _module.DisplayName + (Instance.ModuleId == "general" ? " (General)" : "");
    public string FoldersText =>
        "Game: " + Instance.GameFolder + "    Mods go to: " + Instance.DeployFolder + "    Staged in: " + Instance.StagingRoot;
    public string LoadOrderHint => _module.LoadOrder == LoadOrderKind.FilePriority
        ? "Lower in the list wins when two mods ship the same file."
        : _module.DisplayName + " sorts its mods itself; the order here only matters for clashing files.";

    [ObservableProperty] public partial string? SelectedProfile { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ModRow? SelectedRow { get; set; }
    [ObservableProperty] public partial string Details { get; set; } = "";
    [ObservableProperty] public partial string LinksInput { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeployHint))]
    public partial bool NeedsDeploy { get; set; }
    [ObservableProperty] public partial string DeployedText { get; set; } = "";

    public bool HasSelection => SelectedRow != null;
    public bool CanRollBack => SelectedRow?.Mod.History.Count > 0;

    public bool SelectedKeepAll
    {
        get => SelectedRow?.Mod.KeepAllVersions ?? false;
        set
        {
            if (SelectedRow == null || SelectedRow.Mod.KeepAllVersions == value) return;
            SelectedRow.Mod.KeepAllVersions = value;
            _mods.Save();
            OnPropertyChanged();
        }
    }
    public string DeployHint => NeedsDeploy ? "Changes aren't in the game yet: press Deploy." : "";

    public GameViewModel(MainViewModel main, GameInstance instance)
    {
        Main = main;
        Instance = instance;
        _module = instance.CreateModule();
        _mods = new ModSet(instance);
        _staging = new Staging(instance, _module, _mods, main.Log);
        _deployer = new Deployer(instance, main.Log);
        _links = new LinkDownloader(main.Secrets, _module.NexusDomain);

        Pending.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPending));
        ReloadProfiles();
        Refresh();
        UpdateDeployedText();
        main.StatusText = instance.Name + ": " + _mods.Catalog.Count + " mod(s) staged.";

        // First visit to a game that already has mods: offer to adopt them.
        if (_mods.Catalog.Count == 0)
            Dispatcher.UIThread.Post(() => _ = OfferAdoption(firstTime: true), DispatcherPriority.Background);
    }

    // ---------------- List ----------------

    private void ReloadProfiles()
    {
        _switchingProfile = true;
        Profiles.Clear();
        foreach (string p in _mods.Profiles) Profiles.Add(p);
        SelectedProfile = _mods.ActiveProfile;
        _switchingProfile = false;
    }

    // Rebuilds the rows from the active profile, keeping the selection.
    private void Refresh()
    {
        string? selected = SelectedRow?.Key;
        Dictionary<string, (string Text, UpdateState? State)> updates = Rows.ToDictionary(r => r.Key, r => (r.UpdateText, r.UpdateState));
        Rows.Clear();
        int n = 1;
        foreach ((StagedMod mod, ProfileEntry entry) in _mods.Current())
        {
            ModRow row = new(mod, entry.Enabled, n++);
            if (updates.TryGetValue(mod.Key, out var u)) { row.UpdateText = u.Text; row.UpdateState = u.State; }
            row.Attach(this);
            Rows.Add(row);
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Key == selected);
        _ = RefreshConflicts();
    }

    // Reads staging on a background thread and fills the conflict column.
    private async Task RefreshConflicts()
    {
        List<ProfileEntry> order = _mods.Order.Select(e => new ProfileEntry(e.Key, e.Enabled)).ToList();
        List<Conflict> conflicts = await Task.Run(() => _deployer.Plan(order).Conflicts);

        Dictionary<string, (List<string> Wins, List<string> Loses)> map = new();
        foreach (Conflict c in conflicts)
            foreach (string key in c.Mods)
            {
                if (!map.TryGetValue(key, out var v)) map[key] = v = (new List<string>(), new List<string>());
                (key == c.Winner ? v.Wins : v.Loses).Add(c.Path + (key == c.Winner
                    ? "  (over " + String.Join(", ", c.Mods.Where(m => m != key).Select(NameOf)) + ")"
                    : "  (lost to " + NameOf(c.Winner) + ")"));
            }

        _conflicts = map;
        foreach (ModRow r in Rows)
        {
            r.ConflictText = map.TryGetValue(r.Key, out var v)
                ? (v.Wins.Count > 0 ? "wins " + v.Wins.Count : "") + (v.Wins.Count > 0 && v.Loses.Count > 0 ? ", " : "") +
                  (v.Loses.Count > 0 ? "loses " + v.Loses.Count : "")
                : "";
        }
        UpdateDetails();
    }

    private string NameOf(string key) => _mods.Find(key)?.Name ?? key;

    partial void OnSelectedRowChanged(ModRow? value)
    {
        UpdateDetails();
        OnPropertyChanged(nameof(SelectedKeepAll));
        OnPropertyChanged(nameof(CanRollBack));
    }

    private void UpdateDetails()
    {
        ModRow? r = SelectedRow;
        if (r == null)
        {
            Details = "Select a mod to see its details and conflicts.";
            return;
        }

        StagedMod m = r.Mod;
        StringBuilder sb = new();
        sb.AppendLine(m.Name);
        sb.AppendLine();
        sb.AppendLine("Source:     " + ModSourceTags.Tag(m.Source));
        if (!String.IsNullOrEmpty(m.Url)) sb.AppendLine("Link:       " + m.Url);
        if (!String.IsNullOrEmpty(m.Version)) sb.AppendLine("Version:    " + m.Version);
        if (!String.IsNullOrEmpty(m.ModId)) sb.AppendLine("Game ID:    " + m.ModId);
        sb.AppendLine("Installed:  " + m.InstalledUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (!String.IsNullOrEmpty(m.FileName)) sb.AppendLine("Archive:    " + m.FileName);
        if (!String.IsNullOrEmpty(r.UpdateText)) sb.AppendLine("Update:     " + r.UpdateText);
        if (m.History.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Earlier versions (Roll back):");
            foreach (ArchiveVersion h in m.History.Take(10)) sb.AppendLine("  " + h.Label);
        }

        if (_conflicts.TryGetValue(r.Key, out var v))
        {
            if (v.Wins.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Wins " + v.Wins.Count + " file(s):");
                foreach (string f in v.Wins.Take(40)) sb.AppendLine("  " + f);
            }
            if (v.Loses.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Loses " + v.Loses.Count + " file(s):");
                foreach (string f in v.Loses.Take(40)) sb.AppendLine("  " + f);
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("No file conflicts.");
        }

        Details = sb.ToString();
    }

    internal void OnRowEnabledChanged(ModRow row)
    {
        _mods.SetEnabled(row.Key, row.Enabled);
        _mods.Save();
        NeedsDeploy = true;
        _ = RefreshConflicts();
    }

    private void Changed()
    {
        _mods.Save();
        NeedsDeploy = true;
        Refresh();
    }

    // ---------------- Order ----------------

    [RelayCommand] private void MoveUp() => MoveSelected(i => i - 1);
    [RelayCommand] private void MoveDown() => MoveSelected(i => i + 1);
    [RelayCommand] private void MoveTop() => MoveSelected(_ => 0);
    [RelayCommand] private void MoveBottom() => MoveSelected(_ => Rows.Count - 1);

    private void MoveSelected(Func<int, int> target)
    {
        if (SelectedRow == null) return;
        int index = Rows.IndexOf(SelectedRow);
        int to = Math.Clamp(target(index), 0, Rows.Count - 1);
        if (to == index) return;
        _mods.Move(SelectedRow.Key, to);
        Changed();
    }

    // ---------------- Profiles ----------------

    partial void OnSelectedProfileChanged(string? value)
    {
        if (_switchingProfile || value == null || value == _mods.ActiveProfile) return;
        _mods.SwitchProfile(value);
        _mods.Save();
        NeedsDeploy = true;
        Refresh();
        Main.StatusText = "Profile \"" + value + "\": press Deploy to put it into the game.";
    }

    [RelayCommand]
    private async Task NewProfile()
    {
        string? name = await Main.Dialogs.Prompt("Name of the new profile:", "New profile", "");
        if (String.IsNullOrWhiteSpace(name)) return;
        bool copy = await Main.Dialogs.Confirm("Start with the same mods switched on as \"" + _mods.ActiveProfile + "\"?\n\n" +
                                               "No = every mod switched off.", "New profile", defaultYes: true);
        try
        {
            _mods.CreateProfile(name, copy);
            _mods.SwitchProfile(name.Trim());
            _mods.Save();
        }
        catch (Exception ex) { await Main.Dialogs.Info(ex.Message); return; }

        NeedsDeploy = true;
        ReloadProfiles();
        Refresh();
    }

    [RelayCommand]
    private async Task RenameProfile()
    {
        string old = _mods.ActiveProfile;
        string? name = await Main.Dialogs.Prompt("New name for \"" + old + "\":", "Rename profile", old);
        if (String.IsNullOrWhiteSpace(name) || name.Trim() == old) return;
        try { _mods.RenameProfile(old, name); _mods.Save(); }
        catch (Exception ex) { await Main.Dialogs.Info(ex.Message); return; }
        ReloadProfiles();
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        string name = _mods.ActiveProfile;
        if (!await Main.Dialogs.Confirm("Delete the profile \"" + name + "\"?\n\nIts mods stay installed for the other profiles.", "Delete profile"))
            return;
        try { _mods.DeleteProfile(name); _mods.Save(); }
        catch (Exception ex) { await Main.Dialogs.Info(ex.Message); return; }
        NeedsDeploy = true;
        ReloadProfiles();
        Refresh();
    }

    // ---------------- Installing ----------------

    [RelayCommand]
    private async Task Paste()
    {
        string? text = await Main.Dialogs.GetClipboardText();
        if (!String.IsNullOrWhiteSpace(text))
            LinksInput = String.IsNullOrWhiteSpace(LinksInput) ? text.Trim() : LinksInput.TrimEnd() + Environment.NewLine + text.Trim();
    }

    [RelayCommand]
    private async Task InstallLinks()
    {
        List<string> links = (LinksInput ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Distinct().ToList();
        if (links.Count == 0)
        {
            await Main.Dialogs.Info("Paste one or more links first (one per line).");
            return;
        }

        List<string> installed = new(), failed = new();
        List<BrowserStepRequired> steps = new();

        await Main.RunAsync(
            () =>
            {
                foreach (string link in links)
                {
                    try
                    {
                        (string archive, ArchiveOrigin origin) = _links.Download(link, Instance.DownloadsRoot, Main.SetStatus);
                        foreach (StagedMod m in _staging.InstallArchive(archive, origin, Main.SetStatus))
                            installed.Add("[" + ModSourceTags.Tag(m.Source) + "] " + m.Name);
                    }
                    catch (BrowserStepRequired step) { steps.Add(step); }
                    catch (Exception ex)
                    {
                        failed.Add(link + ": " + ex.Message);
                        Main.Log.Add("ERROR", link + ": " + ex.Message);
                    }
                }
            },
            async () =>
            {
                if (installed.Count > 0) LinksInput = "";
                foreach (BrowserStepRequired s in steps) BeginBrowserStep(s);
                AfterInstall();
                await ShowInstallSummary(installed, failed, steps);
            });
    }

    [RelayCommand]
    private async Task InstallFromFile()
    {
        string? file = await Main.Dialogs.OpenFile("Install a mod from an archive", "Archives", "*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz");
        if (file == null) return;

        List<string> installed = new();
        await Main.RunAsync(
            () =>
            {
                foreach (StagedMod m in _staging.InstallArchive(file, ArchiveOrigin.Manual(), Main.SetStatus))
                    installed.Add("[Manual] " + m.Name);
            },
            async () =>
            {
                AfterInstall();
                await ShowInstallSummary(installed, new(), new());
            });
    }

    private void AfterInstall()
    {
        NeedsDeploy = true;
        Refresh();
        Main.StatusText = "Installed into staging: press Deploy to put the mods into the game.";
    }

    private Task ShowInstallSummary(List<string> installed, List<string> failed, List<BrowserStepRequired> steps)
    {
        StringBuilder sb = new();
        if (installed.Count > 0)
        {
            sb.AppendLine("Staged (press Deploy to put them into the game):");
            foreach (string s in installed) sb.AppendLine("  " + s);
        }
        if (steps.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("Finish these in your browser (the pages were opened); GHUD takes it from there:");
            foreach (BrowserStepRequired s in steps) sb.AppendLine("  - " + s.Message);
        }
        if (failed.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("These failed:");
            foreach (string f in failed) sb.AppendLine("  " + f);
        }
        if (sb.Length == 0) sb.Append("Nothing was installed.");

        return failed.Count > 0 ? Main.Dialogs.Warning(sb.ToString()) : Main.Dialogs.Info(sb.ToString());
    }

    // ---------------- Browser steps ----------------

    // Opens the page and waits: Nexus sends an nxm:// link back to GHUD;
    // LoversLab files are picked up from the Downloads folder.
    private void BeginBrowserStep(BrowserStepRequired step)
    {
        Pending.Add(new PendingDownload(step.SourceKind, step.OpenUrl,
            (step.SourceKind == ModSource.LoversLab ? "LoversLab: download the file in your browser" : "Nexus: click \"Mod Manager Download\"") +
            " (" + step.OpenUrl + ")"));
        Main.Dialogs.OpenUrl(step.OpenUrl);
        if (step.SourceKind == ModSource.LoversLab) StartWatcher();
    }

    private void StartWatcher()
    {
        if (_watcher != null) return;
        string folder = Main.Settings.DownloadsFolder ?? GhudPaths.DefaultDownloadsFolder();
        try
        {
            _watcher = new DownloadWatcher(folder);
            _watcher.ArchiveArrived += path => Dispatcher.UIThread.Post(() => _ = OnArchiveArrived(path));
            Main.StatusText = "Watching " + folder + " for the LoversLab download...";
        }
        catch (Exception ex)
        {
            _watcher = null;
            Main.Log.Add("ERROR", "Can't watch the Downloads folder (" + folder + "): " + ex.Message);
        }
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // A finished archive in Downloads: the oldest waiting LoversLab item.
    private async Task OnArchiveArrived(string path)
    {
        PendingDownload? item = Pending.FirstOrDefault(p => p.Source == ModSource.LoversLab);
        if (item == null) return;
        Pending.Remove(item);
        if (!Pending.Any(p => p.Source == ModSource.LoversLab)) StopWatcher();

        Main.Log.Add("DOWNLOAD", "[LoversLab] picked up " + Path.GetFileName(path));
        List<string> installed = new();
        await Main.RunAsync(
            () =>
            {
                foreach (StagedMod m in _staging.InstallArchive(path, new ArchiveOrigin(ModSource.LoversLab, item.PageUrl), Main.SetStatus))
                    installed.Add("[LoversLab] " + m.Name);
            },
            async () =>
            {
                AfterInstall();
                await ShowInstallSummary(installed, new(), new());
            });
    }

    // A Nexus "Mod Manager Download" link for this game.
    public async Task InstallNxm(string link)
    {
        PendingDownload? item = Pending.FirstOrDefault(p => p.Source == ModSource.Nexus);
        if (item != null) Pending.Remove(item);

        List<string> installed = new(), failed = new();
        await Main.RunAsync(
            () =>
            {
                (string archive, ArchiveOrigin origin) = _links.DownloadNxm(link, Instance.DownloadsRoot, Main.SetStatus);
                Nexus.NxmLink parsed = Nexus.ParseNxm(link);
                StagedMod? existing = _mods.Catalog.FirstOrDefault(m => m.Source == ModSource.Nexus && m.RemoteId == parsed.ModId.ToString());
                foreach (StagedMod m in _staging.InstallArchive(archive, origin, Main.SetStatus, existing?.Key))
                    installed.Add("[Nexus] " + m.Name);
            },
            async () =>
            {
                AfterInstall();
                await ShowInstallSummary(installed, failed, new());
            });
    }

    // ---------------- Adopting mods already in the game ----------------

    [RelayCommand]
    private Task AdoptExisting() => OfferAdoption(firstTime: false);

    private async Task OfferAdoption(bool firstTime)
    {
        List<string> found = _staging.FindUnmanaged(_deployer.LoadManifest());
        if (found.Count == 0)
        {
            if (!firstTime)
                await Main.Dialogs.Info(_module.LoadOrder == LoadOrderKind.FilePriority
                    ? "Mods that merge into the game folder can't be told apart from the game's own files, so they can't be adopted. Reinstall them through GHUD instead."
                    : "Every mod folder in " + Instance.DeployFolder + " is already managed by GHUD.");
            return;
        }

        List<int>? picked = await Main.Dialogs.ChooseMany(
            (firstTime ? Instance.Name + " already has " + found.Count + " mod(s) that GHUD doesn't manage yet. " : "") +
            "Adopt the ticked ones? GHUD moves them into its staging folder (tagged Manual) and deploys them straight back, " +
            "so the game keeps them. Unticked ones stay where they are and are never touched.",
            "Adopt existing mods", found.Select(Path.GetFileName).ToList()!);
        if (picked == null || picked.Count == 0) return;

        List<string> folders = picked.Select(i => found[i]).ToList();
        List<string> failed = new();
        List<ProfileEntry> order = new();
        DeployResult? r = null;

        await Main.RunAsync(
            () =>
            {
                int n = 0;
                foreach (string f in folders)
                {
                    Main.SetStatus("Adopting " + (++n) + "/" + folders.Count + ": " + Path.GetFileName(f) + "...");
                    try { _staging.Adopt(f); }
                    catch (Exception ex) { failed.Add(Path.GetFileName(f) + ": " + ex.Message); }
                }
                order = _mods.Order.Select(e => new ProfileEntry(e.Key, e.Enabled)).ToList();
                r = _deployer.Deploy(order, Main.SetStatus);
            },
            async () =>
            {
                NeedsDeploy = false;
                Refresh();
                UpdateDeployedText();
                Main.StatusText = (folders.Count - failed.Count) + " mod(s) adopted and deployed back into the game.";
                if (failed.Count > 0)
                    await Main.Dialogs.Warning("These could not be adopted:\n\n" + String.Join("\n", failed));
            });
    }

    [RelayCommand]
    private void ClearPending()
    {
        Pending.Clear();
        StopWatcher();
    }

    public void Leave() => StopWatcher();

    [RelayCommand]
    private async Task RemoveMod()
    {
        if (SelectedRow == null) return;
        string key = SelectedRow.Key, name = SelectedRow.Name;
        if (!await Main.Dialogs.Confirm("Remove \"" + name + "\" from this game (all profiles)?\n\n" +
                                        "Its archive stays in the download library. Press Deploy afterwards to take its files out of the game.",
                                        "Remove mod"))
            return;

        await Main.RunAsync(() => _staging.Remove(key), () =>
        {
            NeedsDeploy = true;
            Refresh();
            return Task.CompletedTask;
        });
    }

    // ---------------- Updates / versions / library ----------------

    [RelayCommand]
    private async Task CheckUpdates()
    {
        List<ModRow> rows = Rows.ToList();
        Dictionary<string, UpdateResult> results = new();
        List<string> errors = new();

        await Main.RunAsync(
            () =>
            {
                int n = 0;
                foreach (ModRow r in rows)
                {
                    Main.SetStatus("Checking for updates " + (++n) + "/" + rows.Count + ": " + r.Name + "...");
                    try { results[r.Key] = _links.Check(r.Mod); }
                    catch (Exception ex) { errors.Add(r.Name + ": " + ex.Message); }
                }
            },
            async () =>
            {
                foreach (ModRow r in Rows)
                {
                    if (results.TryGetValue(r.Key, out UpdateResult? u)) { r.UpdateText = u.Text; r.UpdateState = u.State; }
                    else if (errors.Any(e => e.StartsWith(r.Name + ":"))) { r.UpdateText = "Check failed"; r.UpdateState = null; }
                }
                UpdateDetails();
                int available = results.Values.Count(u => u.State == UpdateState.UpdateAvailable);
                Main.StatusText = "Update check: " + available + " update(s) available" + (errors.Count > 0 ? ", " + errors.Count + " check(s) failed." : ".");
                Main.Log.Add("CHECK", Instance.Name + ": " + rows.Count + " mod(s) checked, " + available + " update(s) available.");
                if (errors.Count > 0)
                    await Main.Dialogs.Warning("Some checks failed:\n\n" + String.Join("\n", errors.Take(12)));
            });
    }

    [RelayCommand]
    private Task UpdateSelected() => SelectedRow == null ? Task.CompletedTask : UpdateMods(new List<ModRow> { SelectedRow });

    [RelayCommand]
    private async Task UpdateOutdated()
    {
        List<ModRow> rows = Rows.Where(r => r.UpdateState is UpdateState.UpdateAvailable or UpdateState.Unknown).ToList();
        if (rows.Count == 0)
        {
            await Main.Dialogs.Info("Nothing marked \"Update\" or \"Unknown\". Press \"Check updates\" first.");
            return;
        }
        await UpdateMods(rows);
    }

    private async Task UpdateMods(List<ModRow> rows)
    {
        List<string> updated = new(), failed = new();
        List<BrowserStepRequired> steps = new();

        await Main.RunAsync(
            () =>
            {
                foreach (ModRow r in rows)
                {
                    try
                    {
                        (string archive, ArchiveOrigin origin) = _links.DownloadUpdate(r.Mod, Instance.DownloadsRoot, Main.SetStatus);
                        foreach (StagedMod m in _staging.InstallArchive(archive, origin, Main.SetStatus, r.Key))
                            updated.Add("[" + ModSourceTags.Tag(m.Source) + "] " + m.Name + (String.IsNullOrEmpty(m.Version) ? "" : " " + m.Version));
                    }
                    catch (BrowserStepRequired step) { steps.Add(step); }
                    catch (Exception ex)
                    {
                        failed.Add(r.Name + ": " + ex.Message);
                        Main.Log.Add("ERROR", "Update of " + r.Name + " failed: " + ex.Message);
                    }
                }
            },
            async () =>
            {
                foreach (BrowserStepRequired s in steps) BeginBrowserStep(s);
                foreach (ModRow r in Rows.Where(r => rows.Any(x => x.Key == r.Key) && updated.Count > 0)) { r.UpdateText = ""; r.UpdateState = null; }
                AfterInstall();
                await ShowInstallSummary(updated, failed, steps);
            });
    }

    [RelayCommand]
    private async Task RollBack()
    {
        ModRow? row = SelectedRow;
        if (row == null || row.Mod.History.Count == 0) return;

        int pick = await Main.Dialogs.Choose("Roll " + row.Name + " back to which version? (The current one stays in the library.)",
                                             "Roll back", row.Mod.History.Select(h => h.Label).ToList());
        if (pick < 0) return;
        ArchiveVersion version = row.Mod.History[pick];

        await Main.RunAsync(() => _staging.RollBack(row.Mod, version, Main.SetStatus), () =>
        {
            AfterInstall();
            Main.StatusText = row.Name + " rolled back to " + (version.Version ?? version.FileName) + ": press Deploy.";
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private async Task CleanupLibrary()
    {
        string? text = await Main.Dialogs.Prompt("Delete archives older than how many days?\n" +
            "A mod's current archive is always kept, and so is every archive of a mod marked \"Keep all versions\".",
            "Clean up download library", "30");
        if (text == null) return;
        if (!int.TryParse(text.Trim(), out int days) || days < 0) { await Main.Dialogs.Info("Enter a number of days."); return; }

        Library.CleanupResult? preview = null;
        long total = 0;
        await Main.RunAsync(() => { preview = Library.Cleanup(Instance, _mods, days, dryRun: true); total = Library.Size(Instance); }, async () =>
        {
            if (preview!.Deleted == 0)
            {
                await Main.Dialogs.Info("Nothing to delete. The library holds " + Sizes.Format(total) + ".");
                return;
            }
            if (!await Main.Dialogs.Confirm("Delete " + preview.Deleted + " archive(s), " + Sizes.Format(preview.Bytes) + " of " + Sizes.Format(total) + "?\n\n" +
                                            String.Join("\n", preview.Files.Take(15)) + (preview.Files.Count > 15 ? "\n..." : ""), "Clean up download library"))
                return;

            Library.CleanupResult? done = null;
            await Main.RunAsync(() => done = Library.Cleanup(Instance, _mods, days, dryRun: false, Main.Log), () =>
            {
                Main.StatusText = "Library cleanup: " + done!.Deleted + " archive(s) deleted, " + Sizes.Format(done.Bytes) + " freed.";
                UpdateDetails();
                OnPropertyChanged(nameof(CanRollBack));
                return Task.CompletedTask;
            });
        });
    }

    [RelayCommand]
    private async Task Export()
    {
        int pick = await Main.Dialogs.Choose("Export the mod list of profile \"" + _mods.ActiveProfile + "\" as:", "Export",
            new[] { "Text list (.txt)", "Spreadsheet (.csv)", "Links only (.txt), to paste into GHUD elsewhere", "Copy links to the clipboard" });
        if (pick < 0) return;

        if (pick == 3)
        {
            await Main.Dialogs.SetClipboardText(Library.ExportLinks(_mods));
            Main.StatusText = "Links copied to the clipboard.";
            return;
        }

        string baseName = ArchiveTools.SafeFolderName(Instance.Name + " - " + _mods.ActiveProfile, "mods");
        (string suffix, string filter, string pattern, string content) = pick switch
        {
            1 => (".csv", "CSV", "*.csv", Library.ExportCsv(_mods)),
            2 => (" - links.txt", "Text", "*.txt", Library.ExportLinks(_mods)),
            _ => (".txt", "Text", "*.txt", Library.ExportText(Instance, _mods)),
        };

        string? file = await Main.Dialogs.SaveFile("Export mod list", baseName + suffix, filter, pattern);
        if (file == null) return;
        await File.WriteAllTextAsync(file, content);
        Main.StatusText = "Exported to " + file;
        Main.Log.Add("INFO", "Exported " + Instance.Name + " (" + _mods.ActiveProfile + ") to " + file);
    }

    // ---------------- Deploying ----------------

    [RelayCommand]
    private async Task Deploy()
    {
        List<ProfileEntry> order = _mods.Order.Select(e => new ProfileEntry(e.Key, e.Enabled)).ToList();
        DeployResult? r = null;
        await Main.RunAsync(
            () => r = _deployer.Deploy(order, Main.SetStatus),
            () =>
            {
                NeedsDeploy = false;
                UpdateDeployedText();
                Main.StatusText = "Deployed: " + r!.Placed + " file(s) placed, " + r.Removed + " removed, " + r.Unchanged + " unchanged" +
                                  (r.Conflicts.Count > 0 ? ", " + r.Conflicts.Count + " conflict(s)." : ".");
                return Task.CompletedTask;
            });
    }

    [RelayCommand]
    private async Task Undeploy()
    {
        if (!await Main.Dialogs.Confirm("Take every GHUD mod file out of the game folder and put back the game's own files?\n\n" +
                                        "Your mods stay staged; Deploy puts them back.", "Undeploy"))
            return;

        DeployResult? r = null;
        await Main.RunAsync(
            () => r = _deployer.Undeploy(Main.SetStatus),
            () =>
            {
                NeedsDeploy = Rows.Any(x => x.Enabled);
                UpdateDeployedText();
                Main.StatusText = "Undeployed: " + r!.Removed + " file(s) removed.";
                return Task.CompletedTask;
            });
    }

    private void UpdateDeployedText()
    {
        int files = _deployer.LoadManifest().Files.Count;
        DeployedText = files == 0 ? "Nothing deployed." : files + " file(s) deployed.";
    }

    // ---------------- Folders / navigation ----------------

    [RelayCommand]
    private void Back()
    {
        Leave();
        Main.GoHome();
    }
    [RelayCommand] private void OpenGameFolder() => Main.Dialogs.OpenInFileManager(Instance.DeployFolder);
    [RelayCommand] private void OpenStaging() => Main.Dialogs.OpenInFileManager(Instance.StagingRoot);
    [RelayCommand] private void OpenLibrary() => Main.Dialogs.OpenInFileManager(Instance.DownloadsRoot);

    [RelayCommand]
    private async Task RemoveGame()
    {
        int what = await Main.Dialogs.Choose(
            "Remove \"" + Instance.Name + "\" from GHUD. What should happen to the mods GHUD put into the game?",
            "Remove game", new[]
            {
                "Leave them in the game: they stay installed, GHUD just stops managing them",
                "Take them out of the game and put back the game's own files",
            });
        if (what < 0) return;
        bool undeploy = what == 1;

        bool deleteStorage = await Main.Dialogs.Confirm(
            "Also delete GHUD's copies (staged mods and download library in " + Instance.StorageRoot + ")?\n\n" +
            (undeploy ? "Mods taken out of the game then only exist in your downloads elsewhere, if at all.\n\n"
                      : "The mods left in the game are not affected.\n\n") +
            "No = keep them on disk.", "Remove game");

        string? keptOriginals = null;
        await Main.RunAsync(
            () =>
            {
                if (undeploy) _deployer.Undeploy(Main.SetStatus);
                keptOriginals = Main.Store.Delete(Instance, deleteStorage);
                Main.Log.Add("DELETE", "Removed " + Instance.Name + " from GHUD (" + (undeploy ? "mods taken out of the game" : "mods left in the game") +
                                       (deleteStorage ? ", staged copies deleted)." : ")."));
                if (keptOriginals != null) Main.Log.Add("BACKUP", "Game files the mods had replaced were kept in " + keptOriginals);
            },
            async () =>
            {
                Main.GoHome();
                if (keptOriginals != null)
                    await Main.Dialogs.Info("The game's own files that mods had replaced were kept here, in case you want them back:\n\n" + keptOriginals);
            });
    }
}
