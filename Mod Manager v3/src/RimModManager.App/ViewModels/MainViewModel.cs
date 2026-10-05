using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimModManager.App.Services;
using RimModManager.Core;
using RimModManager.Core.Sources;

namespace RimModManager.App.ViewModels;

// State shared by all tabs. Each tab's commands live in its own
// MainViewModel.<Tab>.cs file; they all bind to this one object.
public partial class MainViewModel : ObservableObject
{
    public const string AppTitle = "RimWorld Mod Manager v3.3.0";

    private readonly IDialogs _dialogs;
    private readonly StateStore _state;
    private readonly ActivityLog _log;
    private readonly SteamCmd _steam;
    private readonly Cleanup _cleanup;
    private readonly ModInstaller _installer;
    private readonly IModEntrySink _sink;
    private readonly SourceRegistry _sources;
    private readonly HashSet<string> _persistencePrompted = new(StringComparer.OrdinalIgnoreCase);

    public string Title => AppTitle;

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    public bool IsNotBusy => !IsBusy;

    // One Mods folder for the Install and Installed / Updates tabs.
    [ObservableProperty]
    public partial string ModsFolder { get; set; } = "";

    [ObservableProperty]
    public partial bool BackupBeforeReplace { get; set; } = true;

    public MainViewModel(IDialogs dialogs, ThemeManager theme)
    {
        _dialogs = dialogs;
        _state = new StateStore(AppPaths.StateFile, AppPaths.LegacyStateFile);
        _log = new ActivityLog(AppPaths.LogsDir);
        _steam = new SteamCmd(AppPaths.SteamCmdRoot);
        _cleanup = new Cleanup(_steam, _state, _log);
        _sources = new SourceRegistry(AppPaths.SourcesFile);
        _installer = new ModInstaller(_steam, _state, _log, _cleanup) { Sources = _sources };
        _sink = new UiEntrySink();

        InitLogTab();
        InitCleanupTab();
        InitAppearanceTab(theme);
        InitSources();

        string saved = _state.Data.PersistentModsFolder;
        if (!String.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
        {
            ModsFolder = saved;
            SetStatus("Loaded persistent RimWorld Mods folder.");
        }

        _log.Add("INFO", AppTitle + " started.");
    }

    // Safe to call from any thread.
    private void SetStatus(string text) => Dispatcher.UIThread.Post(() => StatusText = text);

    private static string Normalize(string? path) => (path ?? "").Trim().Trim('"');

    // Runs work on a background thread. Only one operation at a time; errors
    // are logged and shown. `success` runs on the UI thread afterwards.
    private async Task RunAsync(Action work, Func<Task>? success = null)
    {
        if (IsBusy)
        {
            await _dialogs.Info("Another operation is still running. Please wait for it to finish.");
            return;
        }

        IsBusy = true;

        try
        {
            await Task.Run(work);
        }
        catch (Exception ex)
        {
            IsBusy = false;
            while (ex is AggregateException && ex.InnerException != null)
                ex = ex.InnerException;

            _log.Add("ERROR", ex.Message);
            SetStatus("Error.");
            await _dialogs.Error(ex.Message);
            return;
        }

        IsBusy = false;

        if (success != null)
            await success();
    }

    private async Task<string?> PickModsFolder()
    {
        string? path = await _dialogs.PickFolder("Choose your RimWorld Mods folder", Normalize(ModsFolder));
        if (path == null) return null;

        path = Normalize(path);
        ModsFolder = path;
        await AskWhetherFolderIsPersistent(path);
        return path;
    }

    [RelayCommand]
    private async Task BrowseModsFolder() => await PickModsFolder();

    private async Task AskWhetherFolderIsPersistent(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || !_persistencePrompted.Add(path))
            return;

        if (String.Equals(_state.Data.PersistentModsFolder ?? "", path, StringComparison.OrdinalIgnoreCase))
            return;

        bool remember = await _dialogs.Confirm(
            "Use this RimWorld Mods folder for this session:" + Environment.NewLine + Environment.NewLine +
            path + Environment.NewLine + Environment.NewLine +
            "Should this folder be PERSISTENT and automatically loaded the next time the manager starts?" +
            Environment.NewLine + Environment.NewLine +
            "Yes = remember it permanently" + Environment.NewLine +
            "No = use it for this session only",
            "Persistent RimWorld Mods Folder",
            defaultYes: true);

        if (remember)
        {
            _state.Data.PersistentModsFolder = path;
            SaveStateQuietly();
            _log.Add("INFO", "Persistent Mods folder set: " + path);
            SetStatus("Persistent RimWorld Mods folder saved.");
        }
        else
        {
            _log.Add("INFO", "Session-only Mods folder selected: " + path);
            SetStatus("Using RimWorld Mods folder for this session only.");
        }
    }

    private void SaveStateQuietly()
    {
        try { _state.Save(); }
        catch (Exception ex) { _log.Add("ERROR", "Could not save settings: " + ex.Message); }
    }

    private Task ShowInstallSummary(List<InstallJob> jobs, string header)
    {
        List<InstallJob> failed = jobs.Where(j => !j.Succeeded).ToList();

        StringBuilder sb = new();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine((jobs.Count - failed.Count) + " mod(s) installed/updated.");

        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(failed.Count + " mod(s) FAILED and were left at their previous version:");

            foreach (InstallJob j in failed.Take(15))
                sb.AppendLine("  - " + j.Details.Title + " [" + j.Details.Id + "]: " + j.FailureReason);

            if (failed.Count > 15)
                sb.AppendLine("  ... and " + (failed.Count - 15) + " more (see the Manager Log tab).");

            sb.AppendLine();
            sb.AppendLine("They stay flagged; \"Update Outdated / Unknown\" will retry them.");
        }

        return failed.Count > 0 ? _dialogs.Warning(sb.ToString()) : _dialogs.Info(sb.ToString());
    }

    // Background work reports grid-row changes through this; it moves them
    // onto the UI thread.
    private sealed class UiEntrySink : IModEntrySink
    {
        public void SetStatus(ModEntry entry, string status) =>
            Dispatcher.UIThread.Post(() => entry.Status = status);

        public void SetDetails(ModEntry entry, WorkshopDetails details) =>
            Dispatcher.UIThread.Post(() =>
            {
                entry.AppId = details.AppId;
                entry.Title = details.Title;
                entry.RemoteTimeUpdated = details.TimeUpdated;
            });
    }
}
