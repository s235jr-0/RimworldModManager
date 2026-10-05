using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using RimModManager.Core;
using RimModManager.Core.Sources;

namespace RimModManager.App.ViewModels;

// Installed / Updates tab: scan, check Steam, update, delete.
public partial class MainViewModel
{
    public ObservableCollection<ModEntry> Mods { get; } = new();

    [RelayCommand]
    private async Task BrowseAndScan()
    {
        if (await PickModsFolder() != null)
            await Scan();
    }

    [RelayCommand]
    private async Task Scan()
    {
        string root = Normalize(ModsFolder);
        ModsFolder = root;

        if (!String.IsNullOrWhiteSpace(root))
            await AskWhetherFolderIsPersistent(root);

        if (!Directory.Exists(root))
        {
            await _dialogs.Info("Choose a valid mod folder first.");
            return;
        }

        List<ModEntry> scanned = new();

        await RunAsync(
            () =>
            {
                SetStatus("Scanning mod folders...");
                scanned = ModMetadata.ScanMods(root, _state, _sources, SetStatus);
            },
            () =>
            {
                Mods.Clear();
                foreach (ModEntry e in scanned) Mods.Add(e);

                SetStatus("Scanned " + Mods.Count + " folders.");
                _log.Add("SCAN", "Scanned " + Mods.Count + " folders in " + root);
                return Task.CompletedTask;
            });
    }

    // Steam mods against Steam; Git / Nexus mods against their source;
    // LoversLab and manual installs get a note (they can't be checked).
    [RelayCommand]
    private async Task CheckSteam()
    {
        List<ModEntry> steam = Mods.Where(m => m.Source == ModSource.Steam).ToList();
        List<ModEntry> other = Mods.Where(m => m.Source != ModSource.Steam).ToList();
        if (steam.Count + other.Count == 0)
        {
            await _dialogs.Info("Scan a mod folder first.");
            return;
        }

        foreach (ModEntry e in steam.Concat(other)) e.Status = "Queued";
        _log.Add("CHECK", "Checking " + steam.Count + " Steam and " + other.Count + " other mod(s).");

        await RunAsync(
            () =>
            {
                if (other.Count > 0)
                    _external.CheckEntries(other, _sink, SetStatus);
                if (steam.Count > 0)
                    _installer.CheckForUpdates(steam, _sink, SetStatus);
            },
            () =>
            {
                SetStatus("Update check complete: " + (steam.Count + other.Count) + " mod(s) checked.");
                return Task.CompletedTask;
            });
    }

    [RelayCommand]
    private Task UpdateSelected() => Update(neededOnly: false);

    [RelayCommand]
    private Task UpdateNeeded() => Update(neededOnly: true);

    private async Task Update(bool neededOnly)
    {
        // Steam keeps v2's rule (also "Not checked"). Other sources only when
        // a check found something: re-downloading every Git mod, or opening a
        // browser tab per Nexus/LoversLab mod, on an unchecked list would be a
        // surprise.
        List<ModEntry> steam = neededOnly
            ? Mods.Where(m => m.Source == ModSource.Steam &&
                              (m.Status == "Update available" ||
                               m.Status.StartsWith("Unknown") ||
                               m.Status.StartsWith("FAILED") ||
                               m.Status == "Not checked" ||
                               m.Status == "Check error")).ToList()
            : Mods.Where(m => m.Selected && m.Source == ModSource.Steam).ToList();

        List<ModEntry> other = neededOnly
            ? Mods.Where(m => m.Source != ModSource.Steam &&
                              (m.Status == "Update available" ||
                               m.Status.StartsWith("Unknown") ||
                               m.Status.StartsWith("FAILED"))).ToList()
            : Mods.Where(m => m.Selected && m.Source != ModSource.Steam).ToList();

        if (steam.Count + other.Count == 0)
        {
            await _dialogs.Info(neededOnly
                ? "Nothing needs updating. (Check for Updates first to find updates for non-Steam mods.)"
                : "No mods were selected.");
            return;
        }

        bool backup = BackupBeforeReplace;
        string modsRoot = Normalize(ModsFolder);

        foreach (ModEntry e in steam.Concat(other)) e.Status = "Queued for update";

        List<InstallJob> jobs = new();
        ExternalSources.ListUpdateResult? result = null;

        await RunAsync(
            () =>
            {
                if (steam.Count > 0)
                    jobs = _installer.UpdateMods(steam, modsRoot, backup, _sink, SetStatus);
                if (other.Count > 0)
                    result = _external.UpdateEntries(other, modsRoot, backup, _sink, SetStatus);
            },
            async () =>
            {
                SetStatus("Update complete.");
                _log.Add("SUCCESS", "Selected mod update operation completed.");

                foreach (BrowserStepRequired step in result?.BrowserSteps ?? new())
                    BeginBrowserStep(step);

                await ShowSourcesSummary("Update finished.", jobs,
                    result?.Installed ?? new(), result?.BrowserSteps ?? new(), result?.Failures ?? new());
            });
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (ModEntry e in Mods) e.Selected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (ModEntry e in Mods) e.Selected = false;
    }

    [RelayCommand]
    private async Task DeleteSelected()
    {
        List<ModEntry> selected = Mods.Where(m => m.Selected).ToList();
        if (selected.Count == 0)
        {
            await _dialogs.Info("Select one or more installed mod folders first.");
            return;
        }

        string root = Normalize(ModsFolder);
        if (!Directory.Exists(root))
        {
            await _dialogs.Warning("Choose a valid mod folder first.");
            return;
        }

        List<string> preview = selected
            .Take(8)
            .Select(m => String.IsNullOrWhiteSpace(m.Title) ? m.FolderName : m.Title + " [" + m.FolderName + "]")
            .ToList();

        string previewText = String.Join(Environment.NewLine, preview);
        if (selected.Count > preview.Count)
            previewText += Environment.NewLine + "... and " + (selected.Count - preview.Count) + " more";

        bool yes = await _dialogs.Confirm(
            "Permanently delete " + selected.Count + " selected mod folder(s) from:" + Environment.NewLine +
            root + Environment.NewLine + Environment.NewLine +
            previewText + Environment.NewLine + Environment.NewLine +
            "This removes the installed mod folders only." + Environment.NewLine +
            "It does NOT unsubscribe from Steam Workshop and does NOT delete the SteamCMD download cache." +
            Environment.NewLine + Environment.NewLine + "Continue?",
            "Delete Selected Mods");

        if (!yes) return;

        List<ModEntry> deleted = new();
        List<string> failures = new();

        await RunAsync(
            () => (deleted, failures) = _installer.DeleteMods(selected, root, SetStatus),
            async () =>
            {
                foreach (ModEntry e in deleted) Mods.Remove(e);
                SetStatus("Deleted " + deleted.Count + "/" + selected.Count + " selected mod folder(s).");

                if (failures.Count > 0)
                {
                    await _dialogs.Warning(
                        "Deleted " + deleted.Count + " mod folder(s)." + Environment.NewLine + Environment.NewLine +
                        "Could not delete:" + Environment.NewLine +
                        String.Join(Environment.NewLine, failures.Take(12)) +
                        (failures.Count > 12 ? Environment.NewLine + "... and " + (failures.Count - 12) + " more" : ""),
                        "Delete Selected Mods");
                }
            });
    }
}
