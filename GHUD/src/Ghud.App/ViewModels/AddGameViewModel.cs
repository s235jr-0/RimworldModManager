using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ghud.Modules;

namespace Ghud.App.ViewModels;

// "Add a game": pick a module (RimWorld, or General for any other game),
// the game folder, and for General how its mods are laid out.
public partial class AddGameViewModel : ObservableObject
{
    public MainViewModel Main { get; }

    public string[] Modules { get; } = { "RimWorld", "Other game (General)" };
    public string[] Layouts { get; } = { "Each mod is its own folder in the mods folder", "Mods merge into the folder (later ones win)" };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral))]
    public partial int ModuleIndex { get; set; }

    public bool IsGeneral => ModuleIndex == 1;

    [ObservableProperty] public partial string GameName { get; set; } = "RimWorld";
    [ObservableProperty] public partial string GameFolder { get; set; } = "";
    [ObservableProperty] public partial string DeploySubfolder { get; set; } = "Mods";
    [ObservableProperty] public partial int LayoutIndex { get; set; }
    [ObservableProperty] public partial string Marker { get; set; } = "";
    [ObservableProperty] public partial string NexusDomain { get; set; } = "";
    [ObservableProperty] public partial string DetectText { get; set; } = "";

    public AddGameViewModel(MainViewModel main)
    {
        Main = main;
        Detect();
    }

    partial void OnModuleIndexChanged(int value)
    {
        GameName = value == 0 ? "RimWorld" : "";
        Detect();
    }

    private void Detect()
    {
        if (IsGeneral)
        {
            DetectText = "Choose the game's folder and where its mods go.";
            return;
        }

        string? found = new RimWorldModule().GuessGameFolders().FirstOrDefault();
        if (found != null) GameFolder = found;
        DetectText = found != null ? "Found RimWorld in " + found + "." : "RimWorld wasn't found in the usual places; choose its folder.";
    }

    [RelayCommand]
    private async Task Browse()
    {
        string? folder = await Main.Dialogs.PickFolder("Choose the game's folder", GameFolder);
        if (folder != null) GameFolder = folder;
    }

    [RelayCommand]
    private void Cancel() => Main.GoHome();

    [RelayCommand]
    private async Task Create()
    {
        string folder = (GameFolder ?? "").Trim().Trim('"');
        string name = (GameName ?? "").Trim();

        if (name.Length == 0) { await Main.Dialogs.Info("Give the game a name."); return; }
        if (!Directory.Exists(folder)) { await Main.Dialogs.Info("Choose the game's folder (it must exist)."); return; }

        GeneralGameSettings? general = null;
        IGameModule module;
        if (IsGeneral)
        {
            general = new GeneralGameSettings
            {
                Name = name,
                DeploySubfolder = (DeploySubfolder ?? "").Trim().Trim('\\', '/'),
                Layout = LayoutIndex == 1 ? GeneralLayout.MergeIntoDeployFolder : GeneralLayout.FolderPerMod,
                Marker = (Marker ?? "").Trim(),
                NexusDomain = String.IsNullOrWhiteSpace(NexusDomain) ? null : NexusDomain.Trim().ToLowerInvariant(),
            };
            module = new GeneralModule(general);
        }
        else
        {
            module = new RimWorldModule();
            if (!module.LooksLikeGameFolder(folder) &&
                !await Main.Dialogs.Confirm("This folder doesn't look like a RimWorld installation (no Data\\Core).\n\nAdd it anyway?", "Add game"))
                return;
        }

        GameInstance instance = Main.Store.Create(name, module, folder, general);
        Main.Log.Add("INFO", "Added " + name + " (" + module.DisplayName + ") at " + folder +
                             (instance.StorageRoot != instance.DataFolder ? "; mods are kept in " + instance.StorageRoot : ""));
        Main.OpenGame(instance);
    }
}
