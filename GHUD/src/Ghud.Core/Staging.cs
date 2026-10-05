namespace Ghud;

// Puts mods from a downloaded archive into the instance's staging folder:
// staging\<key>\<files exactly as they will appear in the deploy folder>.
// Nothing in the game folder changes here; that is the Deployer's job.
public sealed class Staging
{
    private readonly GameInstance _instance;
    private readonly IGameModule _module;
    private readonly ModList _list;
    private readonly ActivityLog _log;

    public Staging(GameInstance instance, IGameModule module, ModList list, ActivityLog log)
    {
        _instance = instance;
        _module = module;
        _list = list;
        _log = log;
    }

    // Unpacks the archive and stages every mod in it. A mod that is already
    // staged (same game ID, else same name) is replaced in place and keeps
    // its position and enabled state; new mods go to the bottom (highest priority).
    public List<StagedMod> InstallArchive(string archivePath, ModSource source, string? url = null, string? version = null)
    {
        string work = Path.Combine(_instance.TempRoot, Guid.NewGuid().ToString("N"));
        try
        {
            ArchiveTools.Extract(archivePath, work);
            List<ModCandidate> candidates = _module.FindMods(work);
            if (candidates.Count == 0)
                throw new Exception("No " + _module.DisplayName + " mod was found in " + Path.GetFileName(archivePath) + ".");

            string fallbackName = Path.GetFileNameWithoutExtension(archivePath);
            List<StagedMod> staged = new();
            foreach (ModCandidate c in candidates)
            {
                string name = c.Name == "Mod" || c.Name.Length == 0 ? fallbackName : c.Name;
                staged.Add(StageOne(c, name, source, url, version, Path.GetFileName(archivePath)));
            }

            _list.Save();
            return staged;
        }
        finally
        {
            try { if (Directory.Exists(work)) SafeFileSystem.DeleteDirectory(work); } catch { }
        }
    }

    // Removes a mod from staging and the list. Deploy afterwards to take its
    // files out of the game.
    public void Remove(string key)
    {
        StagedMod? m = _list.Find(key);
        if (m == null) return;

        string dir = Path.Combine(_instance.StagingRoot, key);
        if (Directory.Exists(dir)) SafeFileSystem.DeleteDirectory(dir);
        _list.Mods.Remove(m);
        _list.Save();
        _log.Add("DELETE", "Removed " + m.Name + " from staging.");
    }

    private StagedMod StageOne(ModCandidate c, string name, ModSource source, string? url, string? version, string fileName)
    {
        StagedMod? existing =
            (c.Id != null ? _list.Mods.FirstOrDefault(m => String.Equals(m.ModId, c.Id, StringComparison.OrdinalIgnoreCase)) : null)
            ?? _list.Mods.FirstOrDefault(m => m.ModId == null && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        string key = existing?.Key ?? NewKey(name);
        string final = Path.Combine(_instance.StagingRoot, key);
        string fresh = final + ".new";
        string old = final + ".old";

        // Build the new copy beside the old one, then swap: a failure part-way
        // leaves the previously staged version intact.
        if (Directory.Exists(fresh)) SafeFileSystem.DeleteDirectory(fresh);
        SafeFileSystem.CopyDirectory(c.SourceDir, c.TargetPrefix.Length == 0 ? fresh : Path.Combine(fresh, c.TargetPrefix));

        if (Directory.Exists(old)) SafeFileSystem.DeleteDirectory(old);
        if (Directory.Exists(final)) Directory.Move(final, old);
        Directory.Move(fresh, final);
        if (Directory.Exists(old)) SafeFileSystem.DeleteDirectory(old);

        StagedMod m = existing ?? new StagedMod { Key = key, Enabled = true };
        m.Name = name;
        m.ModId = c.Id;
        m.Source = source;
        m.Url = url ?? m.Url;
        m.Version = version ?? m.Version;
        m.FileName = fileName;
        m.InstalledUtc = DateTime.UtcNow;
        if (existing == null) _list.Mods.Add(m);

        _log.Add("INSTALL", "[" + ModSourceTags.Tag(source) + "] " + name + (existing == null ? " staged" : " updated in staging") +
                            " (" + _instance.Name + ")");
        return m;
    }

    private string NewKey(string name)
    {
        string baseKey = ArchiveTools.SafeFolderName(name, "Mod");
        string key = baseKey;
        for (int n = 2;
             _list.Mods.Any(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
             Directory.Exists(Path.Combine(_instance.StagingRoot, key));
             n++)
            key = baseKey + " (" + n + ")";
        return key;
    }
}
