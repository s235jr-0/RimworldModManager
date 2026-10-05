using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RimModManager.Core;

// Steam's metadata for one Workshop item.
public sealed class WorkshopDetails
{
    public string Id { get; set; } = "";
    public string AppId { get; set; } = "";
    public string Title { get; set; } = "";
    public long TimeUpdated { get; set; }
    public int FileType { get; set; } = -1;   // 2 = collection
    public int Result { get; set; }           // 1 = OK
}

// One installed mod folder on the Installed / Updates tab. Raises
// PropertyChanged so the UI grid updates; only change it on the UI thread
// (the core reports changes through IModEntrySink instead).
public sealed class ModEntry : INotifyPropertyChanged
{
    private bool _selected;
    private string _title = "";
    private string _appId = "";
    private string _status = "";
    private long _remoteTimeUpdated;

    public string FolderName { get; init; } = "";
    public string FolderPath { get; init; } = "";

    // Set only for mods that come from Steam.
    public string WorkshopId { get; init; } = "";
    public bool IsLocalOnly { get; init; }

    // Where the mod came from (Steam / Nexus / Git / LoversLab / Manual), and
    // for non-Steam mods the link it was installed from.
    public Sources.ModSource Source { get; init; } = Sources.ModSource.Manual;
    public string SourceTag => Sources.ModSourceTags.Tag(Source);
    public string SourceUrl { get; init; } = "";

    public string Link => !String.IsNullOrWhiteSpace(WorkshopId)
        ? "https://steamcommunity.com/sharedfiles/filedetails/?id=" + WorkshopId
        : SourceUrl;

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string AppId { get => _appId; set => Set(ref _appId, value); }
    public string Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(StatusRole));
        }
    }

    // Colour role for the status text ("" = normal text); see ColorRoles.
    public string StatusRole => ColorRoles.ForStatus(Status);

    public long RemoteTimeUpdated
    {
        get => _remoteTimeUpdated;
        set
        {
            if (Set(ref _remoteTimeUpdated, value))
                OnPropertyChanged(nameof(UpdatedText));
        }
    }

    public string UpdatedText => RemoteTimeUpdated > 0 ? UnixTime.Format(RemoteTimeUpdated) : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// How background work reports changes to grid rows. The UI implements it by
// marshalling onto its own thread.
public interface IModEntrySink
{
    void SetStatus(ModEntry entry, string status);
    void SetDetails(ModEntry entry, WorkshopDetails details);
}

// One mod as seen by the RimWorld Session tab.
public sealed class RimWorldModRecord
{
    public string FolderName { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string WorkshopId { get; set; } = "";
    public bool Downloaded { get; set; }
    public bool Installed { get; set; }
    public bool Active { get; set; }
    public bool GameContent { get; set; }
    public int LoadOrder { get; set; } = -1;

    // Steam / Nexus / Git / LoversLab / Manual ("" for game content and
    // active entries that aren't installed).
    public string Source { get; set; } = "";
    public List<string> Dependencies { get; set; } = new();
    public List<string> LoadAfter { get; set; } = new();
    public List<string> LoadBefore { get; set; } = new();
    public List<string> IncompatibleWith { get; set; } = new();
    public List<string> SupportedVersions { get; set; } = new();

    public RimWorldModRecord Clone()
    {
        RimWorldModRecord r = (RimWorldModRecord)MemberwiseClone();
        r.Dependencies = new List<string>(Dependencies);
        r.LoadAfter = new List<string>(LoadAfter);
        r.LoadBefore = new List<string>(LoadBefore);
        r.IncompatibleWith = new List<string>(IncompatibleWith);
        r.SupportedVersions = new List<string>(SupportedVersions);
        return r;
    }

    // Display helpers for the Session grid.
    public string LoadOrderText => Active ? (LoadOrder + 1).ToString() : "";
    public string DownloadedText => Downloaded ? "Yes" : "";
    public string InstalledText => Installed ? "Yes" : "";
    public string ActiveText => Active ? "Yes" : "";
    public string GameContentText => GameContent ? "Yes" : "";
}

// Everything saved in state.json. Property names match v2's JSON exactly.
public sealed class StateData
{
    public Dictionary<string, long> InstalledSteamTimes { get; set; } = new();
    public string PersistentModsFolder { get; set; } = "";

    // Workshop ID -> why its last update failed (cleared on success).
    public Dictionary<string, string> FailedUpdates { get; set; } = new();

    // Cleanup tab settings.
    public int CleanupOlderThanDays { get; set; } = 14;
    public bool CleanupBackups { get; set; } = true;
    public bool CleanupCache { get; set; }
    public bool CleanupAutomatically { get; set; }

    // Folder watched for browser downloads (LoversLab); "" = system default.
    public string DownloadsFolder { get; set; } = "";
}

public static class UnixTime
{
    public static DateTime ToLocal(long seconds) =>
        DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime;

    public static string Format(long seconds) => ToLocal(seconds).ToString("yyyy-MM-dd HH:mm");
}
