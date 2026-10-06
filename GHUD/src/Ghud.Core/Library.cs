using System.Text;

namespace Ghud;

// The instance's download library (downloads\) and exports of its mod list.
public static class Library
{
    public sealed record CleanupResult(int Deleted, long Bytes, List<string> Files);

    // Deletes archives older than `days`, except each mod's current archive
    // and every archive of a mod marked "keep all versions". Rollback entries
    // whose archive is gone are dropped. Left-over partial downloads go too.
    public static CleanupResult Cleanup(GameInstance instance, ModSet mods, int days, bool dryRun, ActivityLog? log = null)
    {
        List<string> deleted = new();
        long bytes = 0;
        if (!Directory.Exists(instance.DownloadsRoot)) return new CleanupResult(0, 0, deleted);

        HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase);
        foreach (StagedMod m in mods.Catalog)
        {
            if (m.FileName != null) keep.Add(m.FileName);
            if (m.KeepAllVersions) foreach (ArchiveVersion h in m.History) keep.Add(h.FileName);
        }

        DateTime cutoff = DateTime.Now.AddDays(-days);
        foreach (FileInfo f in new DirectoryInfo(instance.DownloadsRoot).EnumerateFiles())
        {
            if (keep.Contains(f.Name) || f.LastWriteTime >= cutoff) continue;
            deleted.Add(f.Name);
            bytes += f.Length;
            if (!dryRun) f.Delete();
        }

        if (!dryRun)
        {
            string partial = Path.Combine(instance.DownloadsRoot, ".partial");
            if (Directory.Exists(partial)) SafeFileSystem.DeleteDirectory(partial);

            foreach (StagedMod m in mods.Catalog)
                m.History.RemoveAll(h => !File.Exists(Path.Combine(instance.DownloadsRoot, h.FileName)));
            mods.Save();
            if (deleted.Count > 0)
                log?.Add("DELETE", "Library cleanup (" + instance.Name + "): " + deleted.Count + " old archive(s), " + Sizes.Format(bytes) + " freed.");
        }

        return new CleanupResult(deleted.Count, bytes, deleted);
    }

    public static long Size(GameInstance instance) =>
        Directory.Exists(instance.DownloadsRoot) ? SafeFileSystem.GetDirectorySize(instance.DownloadsRoot) : 0;

    // ---------------- Exports (active profile) ----------------

    public static string ExportText(GameInstance instance, ModSet mods)
    {
        StringBuilder sb = new();
        sb.AppendLine(instance.Name + " - profile \"" + mods.ActiveProfile + "\" (" + DateTime.Now.ToString("yyyy-MM-dd") + ")");
        sb.AppendLine();
        int n = 1;
        foreach ((StagedMod m, ProfileEntry e) in mods.Current())
            sb.AppendLine((n++).ToString().PadLeft(3) + ". " + (e.Enabled ? "[x] " : "[ ] ") + m.Name +
                          "  [" + ModSourceTags.Tag(m.Source) + "]" +
                          (String.IsNullOrEmpty(m.Version) ? "" : "  " + m.Version) +
                          (String.IsNullOrEmpty(m.Url) ? "" : "  " + m.Url));
        return sb.ToString();
    }

    public static string ExportCsv(ModSet mods)
    {
        StringBuilder sb = new();
        sb.AppendLine("Position,Enabled,Name,Source,Version,Link,Archive");
        int n = 1;
        foreach ((StagedMod m, ProfileEntry e) in mods.Current())
            sb.AppendLine(String.Join(",", (n++).ToString(), e.Enabled ? "yes" : "no", Csv(m.Name),
                ModSourceTags.Tag(m.Source), Csv(m.Version), Csv(m.Url), Csv(m.FileName)));
        return sb.ToString();
    }

    // Links only, enabled mods in order: paste them into GHUD elsewhere to
    // install the same set.
    public static string ExportLinks(ModSet mods) =>
        String.Join(Environment.NewLine, mods.Current().Where(p => p.Entry.Enabled && !String.IsNullOrEmpty(p.Mod.Url))
                                                       .Select(p => p.Mod.Url).Distinct()) + Environment.NewLine;

    // Quotes a field; values starting with = + - @ are made inert so a
    // spreadsheet never runs them as formulas.
    private static string Csv(string? v)
    {
        v ??= "";
        if (v.Length > 0 && "=+-@".Contains(v[0])) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
