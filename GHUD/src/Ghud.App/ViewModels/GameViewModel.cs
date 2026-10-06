using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ghud.App.ViewModels;

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

        ReloadProfiles();
        Refresh();
        UpdateDeployedText();
        main.StatusText = instance.Name + ": " + _mods.Catalog.Count + " mod(s) staged.";
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
        Rows.Clear();
        int n = 1;
        foreach ((StagedMod mod, ProfileEntry entry) in _mods.Current())
        {
            ModRow row = new(mod, entry.Enabled, n++);
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

    partial void OnSelectedRowChanged(ModRow? value) => UpdateDetails();

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
                foreach (BrowserStepRequired s in steps) Main.Dialogs.OpenUrl(s.OpenUrl);
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
            sb.AppendLine("Finish these in your browser (the pages were opened), then use \"Install from file...\":");
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

    [RelayCommand] private void Back() => Main.GoHome();
    [RelayCommand] private void OpenGameFolder() => Main.Dialogs.OpenInFileManager(Instance.DeployFolder);
    [RelayCommand] private void OpenStaging() => Main.Dialogs.OpenInFileManager(Instance.StagingRoot);
    [RelayCommand] private void OpenLibrary() => Main.Dialogs.OpenInFileManager(Instance.DownloadsRoot);

    [RelayCommand]
    private async Task RemoveGame()
    {
        if (!await Main.Dialogs.Confirm("Remove \"" + Instance.Name + "\" from GHUD?\n\n" +
                                        "GHUD first takes its mod files out of the game folder (Undeploy). The game itself is not touched.",
                                        "Remove game"))
            return;
        bool deleteStorage = await Main.Dialogs.Confirm("Also delete its staged mods and download library (" + Instance.StorageRoot + ")?\n\n" +
                                                        "No = keep them on disk.", "Remove game");

        await Main.RunAsync(
            () =>
            {
                _deployer.Undeploy(Main.SetStatus);
                Main.Store.Delete(Instance, deleteStorage);
                Main.Log.Add("DELETE", "Removed " + Instance.Name + " from GHUD" + (deleteStorage ? " with its staged mods." : "."));
            },
            () =>
            {
                Main.GoHome();
                return Task.CompletedTask;
            });
    }
}
