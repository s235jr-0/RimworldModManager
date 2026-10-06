namespace Ghud;

// Where a downloaded archive came from, kept with the mods staged from it.
public sealed record ArchiveOrigin(ModSource Source, string? Url = null, string? RemoteId = null,
                                   string? Version = null, long RemoteUpdated = 0)
{
    public static ArchiveOrigin Manual(string? url = null) => new(ModSource.Manual, url);
}

// Puts mods from a downloaded archive into the instance's staging folder:
// staging\<key>\<files exactly as they will appear in the deploy folder>.
// The archive itself is kept in the download library (downloads\) for
// reinstalling and rolling back. Nothing in the game folder changes here;
// that is the Deployer's job.
public sealed class Staging
{
    private readonly GameInstance _instance;
    private readonly IGameModule _module;
    private readonly ModSet _mods;
    private readonly ActivityLog _log;

    public Staging(GameInstance instance, IGameModule module, ModSet mods, ActivityLog log)
    {
        _instance = instance;
        _module = module;
        _mods = mods;
        _log = log;
    }

    // Unpacks the archive and stages every mod in it. A mod that is already
    // staged (same game ID, else same name) is replaced in place and keeps
    // its position and on/off state in every profile; new mods go to the
    // bottom (highest priority), enabled in the active profile.
    // updateKey: the mod this archive is a new (or older) version of, when the
    // caller knows it (Update, Roll back). Used when the archive holds one mod,
    // since names taken from file names ("Mod-1.2.zip") change between versions.
    public List<StagedMod> InstallArchive(string archivePath, ArchiveOrigin origin, Action<string>? status = null, string? updateKey = null)
    {
        string work = Path.Combine(_instance.TempRoot, Guid.NewGuid().ToString("N"));
        try
        {
            status?.Invoke("Unpacking " + Path.GetFileName(archivePath) + "...");
            ArchiveTools.Extract(archivePath, work);
            List<ModCandidate> candidates = _module.FindMods(work);
            if (candidates.Count == 0)
                throw new Exception("No " + _module.DisplayName + " mod was found in " + Path.GetFileName(archivePath) + ".");

            string libraryFile = KeepInLibrary(archivePath);
            string fallbackName = Path.GetFileNameWithoutExtension(archivePath);
            List<StagedMod> staged = new();
            foreach (ModCandidate c in candidates)
            {
                string name = c.Name == "Mod" || c.Name.Length == 0 ? fallbackName : c.Name;
                status?.Invoke("Staging " + name + "...");
                StagedMod? target = updateKey != null && candidates.Count == 1 ? _mods.Find(updateKey) : null;
                staged.Add(StageOne(c, target?.Name ?? name, origin, Path.GetFileName(libraryFile), target));
            }

            _mods.Save();
            return staged;
        }
        finally
        {
            try { if (Directory.Exists(work)) SafeFileSystem.DeleteDirectory(work); } catch { }
        }
    }

    // Re-stages an earlier archive of a mod from the library. Its origin
    // (link, update ID) stays; the version goes back to that archive's.
    public List<StagedMod> RollBack(StagedMod mod, ArchiveVersion version, Action<string>? status = null)
    {
        string archive = Path.Combine(_instance.DownloadsRoot, version.FileName);
        if (!File.Exists(archive))
            throw new Exception("That archive is no longer in the download library: " + version.FileName);

        List<StagedMod> staged = InstallArchive(archive,
            new ArchiveOrigin(mod.Source, mod.Url, mod.RemoteId, version.Version, version.RemoteUpdated), status, mod.Key);
        foreach (StagedMod m in staged.Where(m => m.Key == mod.Key))
        {
            m.Version = version.Version;           // exactly that archive's, even when unknown
            m.RemoteUpdated = version.RemoteUpdated;
        }
        _mods.Save();
        _log.Add("INSTALL", "Rolled back " + mod.Name + " to " + (version.Version ?? version.FileName) + " (" + _instance.Name + ")");
        return staged;
    }

    // Removes a mod from staging and every profile. Deploy afterwards to take
    // its files out of the game. Its archive stays in the download library.
    public void Remove(string key)
    {
        StagedMod? m = _mods.Find(key);
        if (m == null) return;

        string dir = Path.Combine(_instance.StagingRoot, key);
        if (Directory.Exists(dir)) SafeFileSystem.DeleteDirectory(dir);
        _mods.Remove(key);
        _mods.Save();
        _log.Add("DELETE", "Removed " + m.Name + " from " + _instance.Name + ".");
    }

    // Copies the archive into downloads\ unless it is already there. A name
    // already used by a different file gets " (2)" etc.
    private string KeepInLibrary(string archivePath)
    {
        string library = Path.GetFullPath(_instance.DownloadsRoot);
        string full = Path.GetFullPath(archivePath);
        if (String.Equals(Path.GetDirectoryName(full), library, StringComparison.OrdinalIgnoreCase))
            return full;

        Directory.CreateDirectory(library);
        string target = Path.Combine(library, Path.GetFileName(full));
        string stem = Path.GetFileNameWithoutExtension(full), ext = Path.GetExtension(full);
        for (int n = 2; File.Exists(target); n++)
        {
            if (SameContent(target, full)) return target;   // the same download again
            target = Path.Combine(library, stem + " (" + n + ")" + ext);
        }
        File.Copy(full, target);
        return target;
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using FileStream fa = File.OpenRead(a), fb = File.OpenRead(b);
        byte[] ba = new byte[81920], bb = new byte[81920];
        while (true)
        {
            int ra = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            int rb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (ra != rb || !ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, rb))) return false;
            if (ra == 0) return true;
        }
    }

    private StagedMod StageOne(ModCandidate c, string name, ArchiveOrigin origin, string libraryFile, StagedMod? target = null)
    {
        StagedMod? existing = target ??
            (c.Id != null ? _mods.Catalog.FirstOrDefault(m => String.Equals(m.ModId, c.Id, StringComparison.OrdinalIgnoreCase)) : null)
            ?? _mods.Catalog.FirstOrDefault(m => m.ModId == null && m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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

        StagedMod m = existing ?? new StagedMod { Key = key };

        // The archive it had until now becomes history (for rollback).
        if (existing?.FileName != null && existing.FileName != libraryFile)
        {
            m.History.RemoveAll(h => h.FileName == existing.FileName || h.FileName == libraryFile);
            m.History.Insert(0, new ArchiveVersion
            {
                FileName = existing.FileName, Version = existing.Version,
                RemoteUpdated = existing.RemoteUpdated, InstalledUtc = existing.InstalledUtc,
            });
            if (m.History.Count > 20) m.History.RemoveRange(20, m.History.Count - 20);
        }
        m.History.RemoveAll(h => h.FileName == libraryFile);

        m.Name = name;
        m.ModId = c.Id;
        m.Source = origin.Source;
        m.Url = origin.Url ?? m.Url;
        m.RemoteId = origin.RemoteId ?? m.RemoteId;
        m.Version = origin.Version ?? m.Version;
        m.RemoteUpdated = origin.RemoteUpdated != 0 ? origin.RemoteUpdated : m.RemoteUpdated;
        m.FileName = libraryFile;
        m.InstalledUtc = DateTime.UtcNow;
        if (existing == null) _mods.Add(m);

        _log.Add("INSTALL", "[" + ModSourceTags.Tag(origin.Source) + "] " + name + (existing == null ? " staged" : " updated") +
                            " (" + _instance.Name + ")");
        return m;
    }

    private string NewKey(string name)
    {
        string baseKey = ArchiveTools.SafeFolderName(name, "Mod");
        string key = baseKey;
        for (int n = 2;
             _mods.Catalog.Any(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ||
             Directory.Exists(Path.Combine(_instance.StagingRoot, key));
             n++)
            key = baseKey + " (" + n + ")";
        return key;
    }
}
