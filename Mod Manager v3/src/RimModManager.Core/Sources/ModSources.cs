using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimModManager.Core.Sources;

// Where an installed mod came from. Shown as a tag everywhere in the app.
public enum ModSource
{
    Steam,
    Nexus,
    Git,
    LoversLab,
    Manual,
}

public static class ModSourceTags
{
    public static string Tag(ModSource source) => source switch
    {
        ModSource.Steam => "Steam",
        ModSource.Nexus => "Nexus",
        ModSource.Git => "Git",
        ModSource.LoversLab => "LoversLab",
        _ => "Manual",
    };
}

// Everything remembered about a mod installed from a non-Steam source, so it
// can be tagged, re-downloaded and checked for updates later.
public sealed class SourceRecord
{
    // Folder name inside RimWorld's Mods folder, and the mod's packageId
    // (used to find the record again if the folder is renamed).
    public string Folder { get; set; } = "";
    public string PackageId { get; set; } = "";

    public ModSource Source { get; set; } = ModSource.Manual;

    // The link the user gave (mod page, repo, file link).
    public string Url { get; set; } = "";

    // Source-specific id: GitHub "owner/repo", GitLab "host/path",
    // Nexus mod id. Empty for plain links.
    public string RemoteId { get; set; } = "";

    // Installed remote version: release tag, commit sha, Nexus file id...
    public string Version { get; set; } = "";

    // When that version was published (unix seconds, 0 = unknown).
    public long RemoteUpdated { get; set; }

    public string FileName { get; set; } = "";
    public DateTime InstalledUtc { get; set; }

    public SourceRecord Clone() => (SourceRecord)MemberwiseClone();
}

// sources.json: one record per non-Steam mod. Steam mods need no record
// (their Workshop ID says it all).
public sealed class SourceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;
    private readonly object _sync = new();
    private List<SourceRecord> _records = new();

    public SourceRegistry(string filePath)
    {
        _filePath = filePath;

        try
        {
            if (File.Exists(filePath))
                _records = JsonSerializer.Deserialize<List<SourceRecord>>(File.ReadAllText(filePath), JsonOptions) ?? new();
        }
        catch
        {
            _records = new();
        }
    }

    public List<SourceRecord> All()
    {
        lock (_sync) return _records.Select(r => r.Clone()).ToList();
    }

    // By folder name first; by packageId if the folder was renamed.
    public SourceRecord? Find(string folder, string? packageId = null)
    {
        lock (_sync)
        {
            SourceRecord? r = _records.FirstOrDefault(x => String.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase));
            if (r == null && !String.IsNullOrWhiteSpace(packageId))
                r = _records.FirstOrDefault(x => String.Equals(x.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
            return r?.Clone();
        }
    }

    public void Set(SourceRecord record)
    {
        lock (_sync)
        {
            _records.RemoveAll(x => String.Equals(x.Folder, record.Folder, StringComparison.OrdinalIgnoreCase));
            _records.Add(record.Clone());
            Save();
        }
    }

    public void Remove(string folder)
    {
        lock (_sync)
        {
            if (_records.RemoveAll(x => String.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase)) > 0)
                Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string temp = _filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_records, JsonOptions));
        File.Move(temp, _filePath, overwrite: true);
    }

    // The source of an installed mod folder: its record if it has one,
    // otherwise Steam when it has a Workshop ID, otherwise Manual.
    public ModSource SourceOf(string folder, string? packageId, string? workshopId)
    {
        SourceRecord? r = Find(folder, packageId);
        if (r != null) return r.Source;
        return String.IsNullOrWhiteSpace(workshopId) ? ModSource.Manual : ModSource.Steam;
    }
}
