using System.Text.Json;

namespace Ghud;

// One mod GHUD has staged for an instance (shared by all its profiles).
public sealed class StagedMod
{
    // Folder name under the staging root; never changes once created.
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";

    // Stable identity from the game (RimWorld packageId), used to recognise updates.
    public string? ModId { get; set; }

    public ModSource Source { get; set; } = ModSource.Manual;

    // The link it came from (repository, mod page, file link) and, for update
    // checks, what the source calls it ("github:owner/repo", Nexus mod ID).
    public string? Url { get; set; }
    public string? RemoteId { get; set; }
    public string? Version { get; set; }
    public long RemoteUpdated { get; set; }

    // The archive in the instance's download library (downloads\).
    public string? FileName { get; set; }
    public DateTime InstalledUtc { get; set; }

    // Earlier archives of this mod still in the library (newest first), for
    // rolling back.
    public List<ArchiveVersion> History { get; set; } = new();

    // Library cleanup never deletes this mod's archives.
    public bool KeepAllVersions { get; set; }
}

// One archive of a mod in the download library.
public sealed class ArchiveVersion
{
    public string FileName { get; set; } = "";
    public string? Version { get; set; }
    public long RemoteUpdated { get; set; }
    public DateTime InstalledUtc { get; set; }

    public string Label => (String.IsNullOrEmpty(Version) ? FileName : Version + "  (" + FileName + ")") +
                           "  installed " + InstalledUtc.ToLocalTime().ToString("yyyy-MM-dd");
}

// A mod's place and on/off state in one profile.
public sealed class ProfileEntry
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public ProfileEntry() { }
    public ProfileEntry(string key, bool enabled) { Key = key; Enabled = enabled; }
}

// The instance's staged mods (mods.json) and its profiles (profiles.json).
// Each profile is its own order + on/off list over the same staged mods, in
// priority order: a mod LOWER in the list wins when two ship the same file.
// New mods are added to every profile: enabled in the active one, disabled
// in the others.
public sealed class ModSet
{
    public const string DefaultProfile = "Default";

    private sealed class ProfilesFile
    {
        public string Active { get; set; } = DefaultProfile;
        public Dictionary<string, List<ProfileEntry>> Profiles { get; set; } = new();
    }

    private readonly GameInstance _instance;
    private ProfilesFile _profiles;

    public List<StagedMod> Catalog { get; private set; } = new();

    public ModSet(GameInstance instance)
    {
        _instance = instance;
        if (File.Exists(instance.ModListFile))
            Catalog = JsonSerializer.Deserialize<List<StagedMod>>(File.ReadAllText(instance.ModListFile), InstanceStore.Json) ?? new();

        _profiles = File.Exists(instance.ProfilesFile)
            ? JsonSerializer.Deserialize<ProfilesFile>(File.ReadAllText(instance.ProfilesFile), InstanceStore.Json) ?? new()
            : new ProfilesFile();
        if (_profiles.Profiles.Count == 0) _profiles.Profiles[DefaultProfile] = new();
        if (!_profiles.Profiles.ContainsKey(_profiles.Active)) _profiles.Active = _profiles.Profiles.Keys.First();
        Repair();
    }

    public string ActiveProfile => _profiles.Active;

    public IReadOnlyList<string> Profiles => _profiles.Profiles.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    // The active profile, in priority order.
    public List<ProfileEntry> Order => _profiles.Profiles[_profiles.Active];

    public StagedMod? Find(string key) => Catalog.FirstOrDefault(m => m.Key == key);

    public ProfileEntry? Entry(string key) => Order.FirstOrDefault(e => e.Key == key);

    // Active profile as (mod, entry) pairs, in priority order.
    public List<(StagedMod Mod, ProfileEntry Entry)> Current() =>
        Order.Select(e => (Find(e.Key)!, e)).Where(p => p.Item1 != null).ToList();

    public void Add(StagedMod mod)
    {
        Catalog.Add(mod);
        foreach ((string name, List<ProfileEntry> list) in _profiles.Profiles)
            list.Add(new ProfileEntry(mod.Key, name == _profiles.Active));
    }

    public void Remove(string key)
    {
        Catalog.RemoveAll(m => m.Key == key);
        foreach (List<ProfileEntry> list in _profiles.Profiles.Values)
            list.RemoveAll(e => e.Key == key);
    }

    public void SetEnabled(string key, bool enabled)
    {
        ProfileEntry? e = Entry(key);
        if (e != null) e.Enabled = enabled;
    }

    // Moves a mod to a new position in the active profile (0 = top = lowest priority).
    public void Move(string key, int newIndex)
    {
        ProfileEntry? e = Entry(key);
        if (e == null) return;
        Order.Remove(e);
        Order.Insert(Math.Clamp(newIndex, 0, Order.Count), e);
    }

    // ---------------- Profiles ----------------

    public void SwitchProfile(string name)
    {
        if (!_profiles.Profiles.ContainsKey(name)) throw new Exception("There is no profile called " + name + ".");
        _profiles.Active = name;
    }

    // A new profile: a copy of the active one, or every mod off.
    public void CreateProfile(string name, bool copyActive)
    {
        name = CheckName(name);
        _profiles.Profiles[name] = Order.Select(e => new ProfileEntry(e.Key, copyActive && e.Enabled)).ToList();
    }

    public void RenameProfile(string oldName, string newName)
    {
        if (!_profiles.Profiles.TryGetValue(oldName, out List<ProfileEntry>? list)) return;
        newName = CheckName(newName);
        _profiles.Profiles.Remove(oldName);
        _profiles.Profiles[newName] = list;
        if (_profiles.Active == oldName) _profiles.Active = newName;
    }

    public void DeleteProfile(string name)
    {
        if (_profiles.Profiles.Count <= 1) throw new Exception("A game needs at least one profile.");
        _profiles.Profiles.Remove(name);
        if (_profiles.Active == name) _profiles.Active = Profiles[0];
    }

    private string CheckName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new Exception("Give the profile a name.");
        if (_profiles.Profiles.Keys.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new Exception("There is already a profile called " + name + ".");
        return name;
    }

    public void Save()
    {
        JsonFile.WriteAtomic(_instance.ModListFile, JsonSerializer.Serialize(Catalog, InstanceStore.Json));
        JsonFile.WriteAtomic(_instance.ProfilesFile, JsonSerializer.Serialize(_profiles, InstanceStore.Json));
    }

    // Keeps profiles and catalog in step (after a crash or a hand edit):
    // unknown keys are dropped, missing mods are added switched off.
    private void Repair()
    {
        HashSet<string> keys = Catalog.Select(m => m.Key).ToHashSet();
        foreach (List<ProfileEntry> list in _profiles.Profiles.Values)
        {
            list.RemoveAll(e => !keys.Contains(e.Key));
            HashSet<string> present = list.Select(e => e.Key).ToHashSet();
            foreach (StagedMod m in Catalog.Where(m => !present.Contains(m.Key)))
                list.Add(new ProfileEntry(m.Key, false));
        }
    }
}
