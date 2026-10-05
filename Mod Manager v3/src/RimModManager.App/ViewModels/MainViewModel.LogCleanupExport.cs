using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimModManager.Core;

namespace RimModManager.App.ViewModels;

// Manager Log, Cleanup and Export tabs.
public partial class MainViewModel
{
    // ---------------- Manager Log ----------------

    public ObservableCollection<LogLine> LogLines { get; } = new();

    private void InitLogTab()
    {
        foreach (LogLine line in _log.ReadToday()) LogLines.Add(line);
        _log.LineAdded += line => Dispatcher.UIThread.Post(() => LogLines.Add(line));
    }

    [RelayCommand]
    private void RefreshLog()
    {
        LogLines.Clear();
        foreach (LogLine line in _log.ReadToday()) LogLines.Add(line);
    }

    [RelayCommand]
    private void OpenLogFolder() => _dialogs.OpenInFileManager(_log.Directory);

    [RelayCommand]
    private async Task ClearLog()
    {
        if (!await _dialogs.Confirm("Clear today's RimWorld Mod Manager activity log?", "RimWorld Mod Manager"))
            return;

        try
        {
            _log.ClearToday();
            LogLines.Clear();
        }
        catch (Exception ex)
        {
            await _dialogs.Error(ex.Message);
        }
    }

    // ---------------- Cleanup ----------------

    private bool _loadingCleanupSettings;

    [ObservableProperty]
    public partial decimal? CleanupDays { get; set; }

    [ObservableProperty]
    public partial bool CleanupBackups { get; set; }

    [ObservableProperty]
    public partial bool CleanupCache { get; set; }

    [ObservableProperty]
    public partial bool CleanupAutomatically { get; set; }

    [ObservableProperty]
    public partial string CleanupInfo { get; set; } =
        "Press \"Refresh sizes\" to see how much space backups and the cache use.";

    private void InitCleanupTab()
    {
        _loadingCleanupSettings = true;
        CleanupDays = Math.Clamp(_state.Data.CleanupOlderThanDays, 1, 3650);
        CleanupBackups = _state.Data.CleanupBackups;
        CleanupCache = _state.Data.CleanupCache;
        CleanupAutomatically = _state.Data.CleanupAutomatically;
        _loadingCleanupSettings = false;
    }

    private int Days => (int)Math.Clamp(CleanupDays ?? 14, 1, 3650);

    partial void OnCleanupDaysChanged(decimal? value) => SaveCleanupSetting(s => s.CleanupOlderThanDays = Days);
    partial void OnCleanupBackupsChanged(bool value) => SaveCleanupSetting(s => s.CleanupBackups = value);
    partial void OnCleanupCacheChanged(bool value) => SaveCleanupSetting(s => s.CleanupCache = value);
    partial void OnCleanupAutomaticallyChanged(bool value) => SaveCleanupSetting(s => s.CleanupAutomatically = value);

    private void SaveCleanupSetting(Action<StateData> apply)
    {
        if (_loadingCleanupSettings) return;
        apply(_state.Data);
        SaveStateQuietly();
    }

    [RelayCommand]
    private async Task RefreshCleanup()
    {
        string modsRoot = Normalize(ModsFolder);
        int days = Days;
        bool backups = CleanupBackups, cache = CleanupCache;
        Cleanup.Plan? plan = null;
        List<BackupArchive.BackupFolder> found = new();

        await RunAsync(
            () =>
            {
                plan = _cleanup.BuildPlan(modsRoot, days, backups, cache, SetStatus);
                found = BackupArchive.ListBackups(modsRoot);
            },
            () =>
            {
                CleanupInfo = Cleanup.Describe(plan!);
                ShowBackups(found);
                SetStatus("Cleanup sizes updated.");
                return Task.CompletedTask;
            });
    }

    // ---------------- Keep backups permanently ----------------

    public ObservableCollection<BackupItem> Backups { get; } = new();

    [ObservableProperty]
    public partial bool DeleteBackupAfterArchiving { get; set; }

    [ObservableProperty]
    public partial string ArchiveInfo { get; set; } = "";

    private void ShowBackups(List<BackupArchive.BackupFolder> found)
    {
        Backups.Clear();
        foreach (BackupArchive.BackupFolder b in found)
            Backups.Add(new BackupItem(b));

        string modsRoot = Normalize(ModsFolder);
        ArchiveInfo = String.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot)
            ? "Choose your Mods folder first."
            : "Zips go to " + BackupArchive.ArchiveRootFor(modsRoot) + " and are never cleaned up." +
              (found.Count == 0 ? " No dated backups right now." : "");
    }

    [RelayCommand]
    private async Task ArchiveSelectedBackups()
    {
        List<BackupArchive.BackupFolder> chosen = Backups.Where(b => b.Selected).Select(b => b.Folder).ToList();
        if (chosen.Count == 0)
        {
            await _dialogs.Info("Tick the backups to keep first (press \"Refresh sizes\" to list them).");
            return;
        }

        string modsRoot = Normalize(ModsFolder);
        string root = BackupArchive.ArchiveRootFor(modsRoot);
        bool deleteAfter = DeleteBackupAfterArchiving;
        BackupArchive archive = new(_log);
        List<string> made = new();
        List<string> failed = new();
        List<BackupArchive.BackupFolder> found = new();

        await RunAsync(
            () =>
            {
                foreach (BackupArchive.BackupFolder b in chosen)
                {
                    try { made.Add(archive.Archive(b, root, deleteAfter, SetStatus)); }
                    catch (Exception ex)
                    {
                        failed.Add(Path.GetFileName(b.Path) + ": " + ex.Message);
                        _log.Add("ERROR", "Could not archive " + b.Path + ": " + ex.Message);
                    }
                }
                found = BackupArchive.ListBackups(modsRoot);
            },
            async () =>
            {
                ShowBackups(found);
                string text = made.Count + " backup(s) zipped into " + root + "." +
                              (deleteAfter && made.Count > 0 ? " The original folders were removed." : "");
                SetStatus(text);
                if (failed.Count > 0)
                    await _dialogs.Warning(text + Environment.NewLine + Environment.NewLine + "Failed:" + Environment.NewLine +
                                           String.Join(Environment.NewLine, failed));
                else
                    await _dialogs.Info(text);
            });
    }

    [RelayCommand]
    private async Task OpenArchiveFolder()
    {
        string modsRoot = Normalize(ModsFolder);
        if (String.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot))
        {
            await _dialogs.Info("Choose your Mods folder first.");
            return;
        }

        string root = BackupArchive.ArchiveRootFor(modsRoot);
        Directory.CreateDirectory(root);
        _dialogs.OpenInFileManager(root);
    }

    [RelayCommand]
    private async Task CleanNow()
    {
        string modsRoot = Normalize(ModsFolder);
        int days = Days;
        bool backups = CleanupBackups, cache = CleanupCache;

        if (!backups && !cache)
        {
            await _dialogs.Info("Tick backups and/or the download cache first.");
            return;
        }

        Cleanup.Plan? plan = null;

        await RunAsync(
            () => plan = _cleanup.BuildPlan(modsRoot, days, backups, cache, SetStatus),
            async () =>
            {
                CleanupInfo = Cleanup.Describe(plan!);

                if (plan!.ToDelete.Count == 0)
                {
                    SetStatus("Nothing to clean up.");
                    await _dialogs.Info("Nothing selected is older than " + days + " days.");
                    return;
                }

                int backupCount = plan.ToDelete.Count(t => t.Kind == "backup");
                int cacheCount = plan.ToDelete.Count(t => t.Kind == "cache");

                bool yes = await _dialogs.Confirm(
                    "Permanently delete:" + Environment.NewLine + Environment.NewLine +
                    (backups ? "  " + backupCount + " backup day folder(s)" + Environment.NewLine : "") +
                    (cache ? "  " + cacheCount + " cached download(s)" + Environment.NewLine : "") +
                    Environment.NewLine +
                    "older than " + days + " days, freeing " + Cleanup.FormatBytes(plan.ToDelete.Sum(t => t.Bytes)) + "?" +
                    Environment.NewLine + Environment.NewLine +
                    "Installed mods are not affected.",
                    "Clean Up");

                if (!yes) return;

                string summary = "";
                await RunAsync(
                    () => summary = _cleanup.Execute(plan, SetStatus),
                    async () =>
                    {
                        SetStatus(summary);
                        await _dialogs.Info(summary);
                        await RefreshCleanup();
                    });
            });
    }

    // ---------------- Export ----------------

    private async Task<bool> RequireScan()
    {
        if (Mods.Count > 0) return true;
        await _dialogs.Info("Scan a mod folder first.");
        return false;
    }

    [RelayCommand]
    private async Task ExportTxt()
    {
        if (!await RequireScan()) return;

        string? file = await _dialogs.SaveFile("Export mod list", "Workshop_Mod_List.txt", "Text files", "*.txt");
        if (file == null) return;

        await File.WriteAllTextAsync(file, Exports.Text(Mods), Encoding.UTF8);
        SetStatus("TXT mod list exported.");
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (!await RequireScan()) return;

        string? file = await _dialogs.SaveFile("Export mod list", "Workshop_Mod_List.csv", "CSV files", "*.csv");
        if (file == null) return;

        // UTF-8 with BOM so spreadsheet apps detect the encoding.
        await File.WriteAllTextAsync(file, Exports.Csv(Mods), Encoding.UTF8);
        SetStatus("CSV mod list exported.");
    }

    [RelayCommand]
    private async Task CopyLinks()
    {
        await _dialogs.SetClipboardText(Exports.Links(Mods));
        SetStatus("Workshop links copied to clipboard.");
    }
}

// One dated backup folder in the Cleanup tab's "keep permanently" list.
public sealed partial class BackupItem : ObservableObject
{
    public BackupItem(BackupArchive.BackupFolder folder) => Folder = folder;

    public BackupArchive.BackupFolder Folder { get; }

    [ObservableProperty]
    public partial bool Selected { get; set; }

    public string Text =>
        Folder.Date.ToString("yyyy-MM-dd") + "   " + Folder.ModCount + " mod folder(s), " + Cleanup.FormatBytes(Folder.Bytes);
}
