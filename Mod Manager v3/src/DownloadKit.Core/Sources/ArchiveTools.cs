using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace DownloadKit.Sources;

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

    // A folder name for a new mod: its name without characters Windows or
    // Linux can't use, kept short.
    public static string SafeFolderName(string name, string fallback)
    {
        string s = Regex.Replace(name ?? "", @"[<>:""/\\|?*\x00-\x1F]", "").Trim().TrimEnd('.');
        if (s.Length > 60) s = s.Substring(0, 60).Trim();
        return s.Length == 0 ? fallback : s;
    }
}
