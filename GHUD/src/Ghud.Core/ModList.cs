using System.Text.Json;

namespace Ghud;

// One mod GHUD has staged for an instance.
public sealed class StagedMod
{
    // Folder name under the staging root; never changes once created.
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    // Stable identity from the game (RimWorld packageId), used to recognise updates.
    public string? ModId { get; set; }

    public bool Enabled { get; set; } = true;
    public ModSource Source { get; set; } = ModSource.Manual;
    public string? Url { get; set; }
    public string? Version { get; set; }
    public string? FileName { get; set; }
    public DateTime InstalledUtc { get; set; }
}

// The instance's mods in priority order: a mod LOWER in the list wins when
// two mods ship the same file (like Mod Organizer's left pane).
public sealed class ModList
{
    private readonly string _file;

    public List<StagedMod> Mods { get; private set; } = new();

    public ModList(string file)
    {
        _file = file;
        if (File.Exists(file))
            Mods = JsonSerializer.Deserialize<List<StagedMod>>(File.ReadAllText(file), InstanceStore.Json) ?? new();
    }

    public StagedMod? Find(string key) => Mods.FirstOrDefault(m => m.Key == key);

    // Moves a mod to a new position (0 = top = lowest priority).
    public void Move(string key, int newIndex)
    {
        StagedMod? m = Find(key);
        if (m == null) return;
        Mods.Remove(m);
        Mods.Insert(Math.Clamp(newIndex, 0, Mods.Count), m);
    }

    public void Save() => JsonFile.WriteAtomic(_file, JsonSerializer.Serialize(Mods, InstanceStore.Json));
}
