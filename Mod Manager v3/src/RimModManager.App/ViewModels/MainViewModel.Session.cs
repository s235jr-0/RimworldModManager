using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RimModManager.Core;

namespace RimModManager.App.ViewModels;

// RimWorld Session tab: inventory, load order and the LLM diagnostic bundle.
public partial class MainViewModel
{
    [ObservableProperty]
    public partial string RimWorldDataFolder { get; set; } = AppPaths.DefaultRimWorldUserData();

    public ObservableCollection<RimWorldModRecord> SessionMods { get; } = new();

    [ObservableProperty]
    public partial string SessionSummary { get; set; } = "";

    private string _lastBundle = "";

    [RelayCommand]
    private async Task BrowseDataFolder()
    {
        string? path = await _dialogs.PickFolder(
            "Choose the 'RimWorld by Ludeon Studios' user-data folder", Normalize(RimWorldDataFolder));
        if (path != null)
            RimWorldDataFolder = path;
    }

    [RelayCommand]
    private async Task ReadSession()
    {
        string modsRoot = Normalize(ModsFolder);
        string dataRoot = Normalize(RimWorldDataFolder);

        if (!Directory.Exists(modsRoot))
        {
            await _dialogs.Info("Choose and scan your RimWorld Mods folder first.");
            return;
        }

        if (!Directory.Exists(dataRoot))
        {
            await _dialogs.Info("The RimWorld user-data folder does not exist.");
            return;
        }

        SessionResult? result = null;

        await RunAsync(
            () => result = SessionReader.Read(modsRoot, dataRoot, _steam, _sources, SetStatus),
            () =>
            {
                SessionMods.Clear();
                foreach (RimWorldModRecord r in result!.Mods) SessionMods.Add(r);
                SessionSummary = result.Summary;
                _lastBundle = result.Bundle;

                SetStatus("RimWorld session read: " + SessionMods.Count + " known mod records.");
                _log.Add("SESSION", "Read RimWorld session: " + SessionMods.Count + " mod records.");
                return Task.CompletedTask;
            });
    }

    [RelayCommand]
    private async Task ExportBundle()
    {
        if (String.IsNullOrWhiteSpace(_lastBundle))
        {
            await _dialogs.Info("Read the RimWorld session first.");
            return;
        }

        string? file = await _dialogs.SaveFile(
            "Export LLM diagnostic bundle",
            "RimWorld_LLM_Diagnostic_" + DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".txt",
            "Text files", "*.txt");
        if (file == null) return;

        await File.WriteAllTextAsync(file, _lastBundle, new UTF8Encoding(false));
        _log.Add("EXPORT", "LLM diagnostic bundle -> " + file);
        SetStatus("LLM diagnostic bundle exported.");
    }

    [RelayCommand]
    private async Task CopyBundle()
    {
        if (String.IsNullOrWhiteSpace(_lastBundle))
        {
            await _dialogs.Info("Read the RimWorld session first.");
            return;
        }

        await _dialogs.SetClipboardText(_lastBundle);
        SetStatus("Diagnostic bundle copied to clipboard.");
    }
}
