using System.Globalization;
using System.IO.Compression;
using System.Text;
using RimModManager.Core.Sources;

namespace RimModManager.Core;

// Keeps dated backups for good: each RWBackup_yyyyMMdd folder can be zipped
// into RWArchive (next to the backups). Cleanup only ever deletes
// RWBackup_* folders and the download cache, so archived zips are never
// removed automatically.
public sealed class BackupArchive
{
    public const string ArchiveFolderName = "RWArchive";

    public sealed record BackupFolder(string Path, DateTime Date, long Bytes, int ModCount);

    private readonly ActivityLog _log;

    public BackupArchive(ActivityLog log) => _log = log;

    public static string ArchiveRootFor(string modsRoot) =>
        System.IO.Path.Combine(ModInstaller.BackupParentFor(modsRoot), ArchiveFolderName);

    // Every dated backup folder for this Mods folder, newest first.
    public static List<BackupFolder> ListBackups(string modsRoot)
    {
        List<BackupFolder> list = new();
        if (String.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot)) return list;

        string parent = ModInstaller.BackupParentFor(modsRoot);
        foreach (string dir in Directory.GetDirectories(parent, ModInstaller.BackupFolderPrefix + "*"))
        {
            if (SafeFileSystem.IsLink(dir)) continue;
            if (!DateTime.TryParseExact(
                    System.IO.Path.GetFileName(dir).Substring(ModInstaller.BackupFolderPrefix.Length),
                    "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                continue;

            int mods = Directory.GetDirectories(dir).Count(d => !SafeFileSystem.IsLink(d));
            list.Add(new BackupFolder(dir, date, SafeFileSystem.GetDirectorySize(dir), mods));
        }

        return list.OrderByDescending(b => b.Date).ToList();
    }

    // Zips one backup folder into archiveRoot, with a modlist.txt inside.
    // Written to a temporary file first, so a failure never leaves a broken
    // zip behind. Returns the zip's path.
    public string Archive(BackupFolder backup, string archiveRoot, bool deleteFolderAfter, Action<string>? status)
    {
        Directory.CreateDirectory(archiveRoot);
        string name = System.IO.Path.GetFileName(backup.Path);
        string zipPath = UniquePath(System.IO.Path.Combine(archiveRoot, name + ".zip"));
        string tempPath = zipPath + ".partial";

        try
        {
            using (FileStream fs = new(tempPath, FileMode.Create, FileAccess.Write))
            using (ZipArchive zip = new(fs, ZipArchiveMode.Create))
            {
                ZipArchiveEntry list = zip.CreateEntry("modlist.txt");
                using (StreamWriter w = new(list.Open(), new UTF8Encoding(false)))
                    w.Write(ModList(backup));

                AddFolder(zip, backup.Path, name, status);
            }

            File.Move(tempPath, zipPath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        long zipBytes = new FileInfo(zipPath).Length;
        _log.Add("BACKUP", "Archived " + name + " (" + backup.ModCount + " mod folder(s), " +
                           Cleanup.FormatBytes(backup.Bytes) + " -> " + Cleanup.FormatBytes(zipBytes) + ") -> " + zipPath);

        if (deleteFolderAfter)
        {
            SafeFileSystem.DeleteDirectory(backup.Path);
            _log.Add("DELETE", "Removed the backup folder after archiving it: " + backup.Path);
        }

        return zipPath;
    }

    // Folder name, package ID and name of every mod in the backup.
    public static string ModList(BackupFolder backup)
    {
        StringBuilder sb = new();
        sb.AppendLine("Backup of " + backup.Date.ToString("yyyy-MM-dd") + " (" + System.IO.Path.GetFileName(backup.Path) + ")");
        sb.AppendLine();

        foreach (string dir in Directory.GetDirectories(backup.Path).Where(d => !SafeFileSystem.IsLink(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string folder = System.IO.Path.GetFileName(dir);
            List<FoundMod> mods = ModFinder.FindMods(dir, maxDepth: 2);
            if (mods.Count == 0)
                sb.AppendLine(folder);
            foreach (FoundMod m in mods)
                sb.AppendLine(folder + " | " + m.PackageId + " | " + m.Name);
        }

        return sb.ToString();
    }

    // Links inside the folder are skipped, like everywhere else in the manager.
    private static void AddFolder(ZipArchive zip, string dir, string entryPrefix, Action<string>? status)
    {
        foreach (FileSystemInfo entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string entryName = entryPrefix + "/" + entry.Name;

            if (entry is DirectoryInfo)
            {
                zip.CreateEntry(entryName + "/");
                AddFolder(zip, entry.FullName, entryName, status);
            }
            else
            {
                status?.Invoke("Zipping " + entryName);
                zip.CreateEntryFromFile(entry.FullName, entryName, CompressionLevel.Optimal);
            }
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = System.IO.Path.GetDirectoryName(path)!;
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = System.IO.Path.Combine(dir, stem + " (" + i + ").zip");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
