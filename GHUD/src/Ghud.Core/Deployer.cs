using System.Text.Json;

namespace Ghud;

// One file GHUD put into the game's deploy folder.
public sealed class DeployedFile
{
    public string Mod { get; set; } = "";       // StagedMod.Key
    public bool Linked { get; set; }             // hard link (true) or copy
    public long Size { get; set; }
    public long WriteTicksUtc { get; set; }
    public bool ReplacedOriginal { get; set; }   // a game file was moved to originals\
}

public sealed class DeploymentManifest
{
    // Path relative to the deploy folder -> what is there.
    public Dictionary<string, DeployedFile> Files { get; set; } = new();

    // Folders GHUD created (relative), removed again when they end up empty.
    public List<string> CreatedFolders { get; set; } = new();
}

// A file shipped by more than one enabled mod; the last one in Mods wins.
public sealed record Conflict(string Path, List<string> Mods)
{
    public string Winner => Mods[^1];
}

public sealed record DeployResult(int Placed, int Removed, int Unchanged, int Linked, int Copied,
                                  int OriginalsKept, int EditedKept, List<Conflict> Conflicts);

// Makes the deploy folder match the enabled mods, in priority order:
//   - each file comes from the LAST enabled mod that has it (lower = wins)
//   - hard link when possible, copy otherwise
//   - a game file that a mod replaces is moved to originals\ and put back
//     when no mod provides it any more
//   - files GHUD placed earlier but no longer wants are removed; one that
//     was edited in the game folder is moved to changed\ instead of deleted
//   - links (junctions / symlinks) in staging are never followed
// The manifest is saved even if something fails half-way, so the next run
// knows exactly what is in the game folder.
public sealed class Deployer
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly GameInstance _instance;
    private readonly ActivityLog _log;

    public Deployer(GameInstance instance, ActivityLog log)
    {
        _instance = instance;
        _log = log;
    }

    public DeploymentManifest LoadManifest()
    {
        if (!File.Exists(_instance.ManifestFile)) return new DeploymentManifest();
        DeploymentManifest m = JsonSerializer.Deserialize<DeploymentManifest>(File.ReadAllText(_instance.ManifestFile), InstanceStore.Json)
                               ?? new DeploymentManifest();
        m.Files = new Dictionary<string, DeployedFile>(m.Files, PathComparer);
        return m;
    }

    // Which file comes from which mod, and every conflict. Reads staging only.
    public (Dictionary<string, (StagedMod Mod, string Source)> Winners, List<Conflict> Conflicts) Plan(IReadOnlyList<StagedMod> mods)
    {
        Dictionary<string, (StagedMod Mod, string Source)> winners = new(PathComparer);
        Dictionary<string, List<string>> providers = new(PathComparer);

        foreach (StagedMod mod in mods.Where(m => m.Enabled))
        {
            string root = Path.Combine(_instance.StagingRoot, mod.Key);
            if (!Directory.Exists(root)) continue;

            foreach (string rel in Files(root, ""))
            {
                winners[rel] = (mod, Path.Combine(root, rel));
                if (!providers.TryGetValue(rel, out List<string>? list))
                    providers[rel] = list = new List<string>();
                list.Add(mod.Key);
            }
        }

        List<Conflict> conflicts = providers.Where(p => p.Value.Count > 1)
            .Select(p => new Conflict(p.Key, p.Value))
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (winners, conflicts);
    }

    public DeployResult Deploy(IReadOnlyList<StagedMod> mods, Action<string>? status = null)
    {
        string deployRoot = Path.GetFullPath(_instance.DeployFolder);
        Directory.CreateDirectory(deployRoot);

        (Dictionary<string, (StagedMod Mod, string Source)> wanted, List<Conflict> conflicts) = Plan(mods);
        DeploymentManifest manifest = LoadManifest();
        int placed = 0, removed = 0, unchanged = 0, linked = 0, copied = 0, originals = 0, edited = 0;

        try
        {
            // 1. Take out what is no longer wanted, or now comes from another mod.
            foreach (string rel in manifest.Files.Keys.ToList())
            {
                DeployedFile was = manifest.Files[rel];
                if (wanted.TryGetValue(rel, out var w) && w.Mod.Key == was.Mod && SameAsSource(Target(deployRoot, rel), w.Source))
                    continue;

                status?.Invoke("Removing " + rel);
                if (RemovePlaced(deployRoot, rel, was)) edited++;
                if (!wanted.ContainsKey(rel) && was.ReplacedOriginal) RestoreOriginal(deployRoot, rel);
                if (!wanted.ContainsKey(rel) || !was.ReplacedOriginal) manifest.Files.Remove(rel);
                else manifest.Files[rel] = new DeployedFile { Mod = "", ReplacedOriginal = true };
                removed++;
            }

            // 2. Place every wanted file that isn't there yet.
            foreach ((string rel, (StagedMod mod, string source)) in wanted)
            {
                string target = Target(deployRoot, rel);
                if (manifest.Files.TryGetValue(rel, out DeployedFile? cur) && cur.Mod == mod.Key && SameAsSource(target, source))
                {
                    unchanged++;
                    continue;
                }

                status?.Invoke("Deploying " + rel);
                bool replacedOriginal = cur?.ReplacedOriginal ?? false;
                if (cur == null && File.Exists(target))
                {
                    // A game file (or something placed by hand): keep it safe.
                    MoveOriginalAside(deployRoot, rel);
                    replacedOriginal = true;
                    originals++;
                }

                EnsureFolder(deployRoot, Path.GetDirectoryName(rel) ?? "", manifest);
                if (File.Exists(target)) File.Delete(target);

                bool link = HardLinks.TryCreate(target, source);
                if (!link)
                {
                    File.Copy(source, target);
                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
                }
                if (link) linked++; else copied++;

                FileInfo fi = new(target);
                manifest.Files[rel] = new DeployedFile
                {
                    Mod = mod.Key,
                    Linked = link,
                    Size = fi.Length,
                    WriteTicksUtc = fi.LastWriteTimeUtc.Ticks,
                    ReplacedOriginal = replacedOriginal,
                };
                placed++;
            }

            // 3. Remove folders GHUD created that are now empty (deepest first).
            foreach (string dir in manifest.CreatedFolders.OrderByDescending(d => d.Length).ToList())
            {
                string full = Target(deployRoot, dir);
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                {
                    Directory.Delete(full);
                    manifest.CreatedFolders.Remove(dir);
                }
                else if (!Directory.Exists(full))
                {
                    manifest.CreatedFolders.Remove(dir);
                }
            }
        }
        finally
        {
            JsonFile.WriteAtomic(_instance.ManifestFile, JsonSerializer.Serialize(manifest, InstanceStore.Json));
        }

        DeployResult result = new(placed, removed, unchanged, linked, copied, originals, edited, conflicts);
        _log.Add("SUCCESS", "Deployed " + _instance.Name + ": " + placed + " placed (" + linked + " linked, " + copied + " copied), " +
                            removed + " removed, " + unchanged + " unchanged, " + conflicts.Count + " conflict(s)." +
                            (originals > 0 ? " " + originals + " game file(s) kept in originals." : "") +
                            (edited > 0 ? " " + edited + " edited file(s) kept in changed." : ""));
        return result;
    }

    // Takes every GHUD file out of the game and puts the originals back.
    public DeployResult Undeploy(Action<string>? status = null) => Deploy(Array.Empty<StagedMod>(), status);

    // ------------------------------------------------------------------

    // Deletes a file GHUD placed. If it changed since (size or time differ),
    // it is moved to changed\ instead. Returns true when it was kept that way.
    private bool RemovePlaced(string deployRoot, string rel, DeployedFile was)
    {
        string target = Target(deployRoot, rel);
        if (was.Mod.Length == 0 || !File.Exists(target)) return false;

        FileInfo fi = new(target);
        // Changed since deploying (edited, or replaced by a game update): keep it.
        bool editedByUser = fi.Length != was.Size || fi.LastWriteTimeUtc.Ticks != was.WriteTicksUtc;
        if (editedByUser)
        {
            string keep = Path.Combine(_instance.ChangedRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"), rel);
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
            File.Move(target, keep);
            _log.Add("BACKUP", "Kept an edited file instead of deleting it: " + rel + " -> " + keep);
            return true;
        }

        File.Delete(target);
        return false;
    }

    private void MoveOriginalAside(string deployRoot, string rel)
    {
        string keep = Path.Combine(_instance.OriginalsRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
        if (File.Exists(keep)) File.Delete(Target(deployRoot, rel));   // the oldest copy is the real original
        else File.Move(Target(deployRoot, rel), keep);
        _log.Add("BACKUP", "Game file replaced by a mod, original kept: " + rel);
    }

    private void RestoreOriginal(string deployRoot, string rel)
    {
        string keep = Path.Combine(_instance.OriginalsRoot, rel);
        if (!File.Exists(keep)) return;
        string target = Target(deployRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(keep, target, overwrite: true);
        _log.Add("BACKUP", "Original game file restored: " + rel);
    }

    private static void EnsureFolder(string deployRoot, string relDir, DeploymentManifest manifest)
    {
        if (relDir.Length == 0) return;
        string parent = Path.GetDirectoryName(relDir) ?? "";
        EnsureFolder(deployRoot, parent, manifest);

        string full = Target(deployRoot, relDir);
        if (Directory.Exists(full)) return;
        Directory.CreateDirectory(full);
        if (!manifest.CreatedFolders.Contains(relDir, PathComparer)) manifest.CreatedFolders.Add(relDir);
    }

    // Is the deployed file still exactly the staged one? (A hard link always
    // is; a copy is compared by size and time.)
    private static bool SameAsSource(string target, string source)
    {
        if (!File.Exists(target) || !File.Exists(source)) return false;
        FileInfo t = new(target), s = new(source);
        return t.Length == s.Length && t.LastWriteTimeUtc == s.LastWriteTimeUtc;
    }

    // A path inside the deploy folder; anything that would escape it is refused.
    private static string Target(string deployRoot, string rel)
    {
        string full = Path.GetFullPath(Path.Combine(deployRoot, rel));
        string root = Path.TrimEndingDirectorySeparator(deployRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing a path outside the deploy folder: " + rel);
        return full;
    }

    // Every file under dir as a relative path; links are skipped.
    private static IEnumerable<string> Files(string root, string rel)
    {
        string dir = rel.Length == 0 ? root : Path.Combine(root, rel);
        foreach (FileSystemInfo e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string childRel = rel.Length == 0 ? e.Name : Path.Combine(rel, e.Name);
            if (e is DirectoryInfo)
                foreach (string f in Files(root, childRel)) yield return f;
            else
                yield return childRel;
        }
    }
}
