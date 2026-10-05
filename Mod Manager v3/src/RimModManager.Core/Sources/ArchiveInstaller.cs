using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace RimModManager.Core.Sources;

public sealed record FoundMod(string Dir, string PackageId, string Name);

public sealed class ArchiveInstallResult
{
    public string Name { get; init; } = "";
    public string PackageId { get; init; } = "";
    public string Folder { get; set; } = "";
    public bool Succeeded { get; set; }
    public string Message { get; set; } = "";
}

public static class ArchiveTools
{
    private static readonly string[] Extensions = { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

    public static bool IsArchive(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // Unpacks zip / 7z / rar / tar(.gz) into destDir. Entries that would land
    // outside destDir ("zip-slip", e.g. "../../x") are refused.
    public static void Extract(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destDir)) + Path.DirectorySeparatorChar;

        using IArchive archive = ArchiveFactory.Open(archivePath);

        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (entry.IsDirectory || String.IsNullOrEmpty(entry.Key)) continue;

            string target = Path.GetFullPath(Path.Combine(destDir, entry.Key.Replace('\\', '/')));
            if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("The archive contains an unsafe path and was not unpacked: " + entry.Key);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using Stream input = entry.OpenEntryStream();
            using FileStream output = File.Create(target);
            input.CopyTo(output);
        }
    }

    // Every mod folder (one with About/About.xml) inside root, however deeply
    // the archive nested it. A mod's own subfolders are not searched.
    public static List<FoundMod> FindMods(string root, int maxDepth = 6)
    {
        List<FoundMod> found = new();
        Queue<(string Dir, int Depth)> queue = new();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            (string dir, int depth) = queue.Dequeue();

            if (ModMetadata.IsModFolder(dir))
            {
                RimWorldModRecord meta = ModMetadata.Read(dir);
                found.Add(new FoundMod(dir, meta.PackageId, meta.Name));
                continue;
            }

            if (depth >= maxDepth) continue;
            foreach (string sub in Directory.GetDirectories(dir))
                queue.Enqueue((sub, depth + 1));
        }

        return found;
    }

    // A folder name for a new mod: its name without characters Windows or
    // Linux can't use, kept short.
    public static string SafeFolderName(string name, string fallback)
    {
        string s = Regex.Replace(name ?? "", @"[<>:""/\\|?*\x00-\x1F]", "").Trim().TrimEnd('.');
        if (s.Length > 60) s = s.Substring(0, 60).Trim();
        return s.Length == 0 ? fallback : s;
    }
}

// Installs the mod(s) inside a downloaded archive (any non-Steam source) into
// RimWorld's Mods folder, with the same staging / backup / revert protection
// as Steam installs, and records where each one came from.
public sealed class ArchiveInstaller
{
    private readonly StateStore _state;
    private readonly SourceRegistry _sources;
    private readonly ActivityLog _log;
    private readonly string _workRoot;

    public ArchiveInstaller(StateStore state, SourceRegistry sources, ActivityLog log, string workRoot)
    {
        _state = state;
        _sources = sources;
        _log = log;
        _workRoot = workRoot;
    }

    // `origin` describes where the archive came from (source, link, version);
    // its Folder / PackageId are filled in per installed mod.
    public List<ArchiveInstallResult> Install(
        string archivePath, string modsRoot, bool backup, SourceRecord origin, Action<string> status)
    {
        string tag = ModSourceTags.Tag(origin.Source);
        string work = Path.Combine(_workRoot, "extract_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        List<ArchiveInstallResult> results = new();

        try
        {
            status("Unpacking " + Path.GetFileName(archivePath) + "...");
            ArchiveTools.Extract(archivePath, work);

            List<FoundMod> mods = ArchiveTools.FindMods(work);
            if (mods.Count == 0)
                throw new Exception("No RimWorld mod (a folder with About/About.xml) was found in " + Path.GetFileName(archivePath) + ".");

            foreach (FoundMod mod in mods)
                results.Add(InstallOne(mod, modsRoot, backup, origin, tag, status));
        }
        finally
        {
            try { SafeFileSystem.DeleteDirectory(work); } catch { }
        }

        return results;
    }

    private ArchiveInstallResult InstallOne(
        FoundMod mod, string modsRoot, bool backup, SourceRecord origin, string tag, Action<string> status)
    {
        ArchiveInstallResult result = new() { Name = mod.Name, PackageId = mod.PackageId };

        // Same packageId already installed -> update that folder in place.
        string? existing = FindInstalledByPackageId(modsRoot, mod.PackageId);
        bool hadPrevious = existing != null;
        string destination = existing ?? UniqueFolder(modsRoot, ArchiveTools.SafeFolderName(mod.Name, ArchiveTools.SafeFolderName(mod.PackageId, "Mod")));
        result.Folder = Path.GetFileName(destination);

        ModSource previousSource = hadPrevious
            ? _sources.SourceOf(result.Folder, mod.PackageId, ModMetadata.DiscoverWorkshopId(destination, result.Folder))
            : origin.Source;

        string? backupPath = null;

        try
        {
            if (backup && hadPrevious)
            {
                status("[" + tag + "] backing up " + mod.Name);
                backupPath = ModInstaller.CreateDatedBackup(modsRoot, destination, mod.PackageId, result.Folder);
                _log.Add("BACKUP", "[" + tag + "] " + mod.Name + " -> " + backupPath);
            }

            status("[" + tag + "] installing " + mod.Name);
            ModInstaller.ReplaceModFromSource(mod.Dir, destination);

            SourceRecord record = origin.Clone();
            record.Folder = result.Folder;
            record.PackageId = mod.PackageId;
            record.InstalledUtc = DateTime.UtcNow;
            _sources.Set(record);

            // A Steam mod replaced by another source is no longer tracked as
            // Steam (its folder may still be named after the Workshop ID).
            if (hadPrevious && previousSource == ModSource.Steam && origin.Source != ModSource.Steam)
            {
                string workshopId = ModMetadata.DiscoverWorkshopId(destination, result.Folder);
                if (!String.IsNullOrWhiteSpace(workshopId))
                {
                    _state.Remove(workshopId);
                    _state.Save();
                }
                result.Message = "replaced the Steam version";
            }

            result.Succeeded = true;
            _log.Add("INSTALL", "[" + tag + "] " + mod.Name + (hadPrevious ? " (updated)" : "") + " -> " + destination);
        }
        catch (Exception ex)
        {
            string outcome = ModInstaller.RevertAfterFailedInstall(destination, hadPrevious, backupPath);
            result.Message = "Install failed: " + ex.Message + " -> " + outcome;
            _log.Add("ERROR", "[" + tag + "] " + mod.Name + " " + result.Message);
        }

        return result;
    }

    private static string? FindInstalledByPackageId(string modsRoot, string packageId)
    {
        if (String.IsNullOrWhiteSpace(packageId) || !Directory.Exists(modsRoot)) return null;

        foreach (string dir in ModMetadata.ModFolders(modsRoot))
        {
            if (String.Equals(ModMetadata.ReadCached(dir).PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                return dir;
        }

        return null;
    }

    private static string UniqueFolder(string modsRoot, string name)
    {
        string candidate = Path.Combine(modsRoot, name);
        for (int i = 2; Directory.Exists(candidate); i++)
            candidate = Path.Combine(modsRoot, name + " (" + i + ")");
        return candidate;
    }
}
