using System.Globalization;
using System.Text;

namespace RimModManager.Core;

// Dated backups and the SteamCMD download cache. Installed mods in
// RimWorld's Mods folder are never touched here.
public sealed class Cleanup
{
    public sealed record Target(string Kind, string Path, DateTime Date, long Bytes);   // Kind: "backup" / "cache"

    public sealed class Plan
    {
        public int Days { get; init; }
        public List<Target> ToDelete { get; } = new();
        public int BackupCount { get; set; }
        public long BackupBytes { get; set; }
        public int CacheCount { get; set; }
        public long CacheBytes { get; set; }
        public string BackupLocation { get; set; } = "";
        public string CacheLocation { get; set; } = "";
    }

    private readonly SteamCmd _steam;
    private readonly StateStore _state;
    private readonly ActivityLog _log;

    public Cleanup(SteamCmd steam, StateStore state, ActivityLog log)
    {
        _steam = steam;
        _state = state;
        _log = log;
    }

    // Measures everything and lists what is older than `days` for the
    // selected kinds. Deletes nothing.
    public Plan BuildPlan(string? modsRoot, int days, bool backups, bool cache, Action<string>? status)
    {
        Plan plan = new() { Days = days, CacheLocation = _steam.ContentRoot(ModInstaller.RimWorldAppId) };
        DateTime cutoff = DateTime.Today.AddDays(-days);

        if (!String.IsNullOrWhiteSpace(modsRoot) && Directory.Exists(modsRoot))
        {
            string parent = ModInstaller.BackupParentFor(modsRoot);
            plan.BackupLocation = Path.Combine(parent, ModInstaller.BackupFolderPrefix + "yyyyMMdd");

            status?.Invoke("Measuring backups...");
            foreach (string dir in Directory.GetDirectories(parent, ModInstaller.BackupFolderPrefix + "*"))
            {
                if (!DateTime.TryParseExact(
                        Path.GetFileName(dir).Substring(ModInstaller.BackupFolderPrefix.Length),
                        "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
                    continue;

                long bytes = SafeFileSystem.GetDirectorySize(dir);
                plan.BackupCount++;
                plan.BackupBytes += bytes;

                if (backups && date < cutoff)
                    plan.ToDelete.Add(new Target("backup", dir, date, bytes));
            }
        }

        status?.Invoke("Measuring download cache...");
        foreach (SteamCmd.CachedItem item in _steam.GetCachedItems(ModInstaller.RimWorldAppId))
        {
            long bytes = SafeFileSystem.GetDirectorySize(item.Path);
            plan.CacheCount++;
            plan.CacheBytes += bytes;

            if (cache && item.LastUsed < cutoff)
                plan.ToDelete.Add(new Target("cache", item.Path, item.LastUsed, bytes));
        }

        return plan;
    }

    public string Execute(Plan plan, Action<string>? status)
    {
        int removed = 0;
        long freed = 0;
        int errors = 0;

        foreach (Target t in plan.ToDelete)
        {
            status?.Invoke("Cleaning up " + (removed + errors + 1) + "/" + plan.ToDelete.Count + "...");

            try
            {
                SafeFileSystem.DeleteDirectory(t.Path);
                removed++;
                freed += t.Bytes;
                _log.Add("DELETE", "Cleanup removed old " + t.Kind + " (" + t.Date.ToString("yyyy-MM-dd") + ", " + FormatBytes(t.Bytes) + ") -> " + t.Path);
            }
            catch (Exception ex)
            {
                errors++;
                _log.Add("ERROR", "Cleanup could not remove " + t.Path + ": " + ex.Message);
            }
        }

        string summary = "Cleanup removed " + removed + " item(s), freeing " + FormatBytes(freed) + ".";
        if (errors > 0)
            summary += " " + errors + " item(s) could not be removed (see Manager Log).";

        _log.Add("SUCCESS", summary);
        return summary;
    }

    // Used after installs/updates when automatic cleanup is on.
    public void RunAutomatic(string modsRoot, Action<string>? status)
    {
        StateData s = _state.Data;
        if (!s.CleanupBackups && !s.CleanupCache) return;

        try
        {
            Plan plan = BuildPlan(modsRoot, s.CleanupOlderThanDays, s.CleanupBackups, s.CleanupCache, status);
            if (plan.ToDelete.Count > 0)
                Execute(plan, status);
        }
        catch (Exception ex)
        {
            // Never let cleanup turn a finished install into an error.
            _log.Add("ERROR", "Automatic cleanup failed: " + ex.Message);
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.00") + " GB";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
        return (bytes / 1024.0).ToString("0") + " KB";
    }

    public static string Describe(Plan plan)
    {
        long backupOld = plan.ToDelete.Where(t => t.Kind == "backup").Sum(t => t.Bytes);
        int backupOldCount = plan.ToDelete.Count(t => t.Kind == "backup");
        long cacheOld = plan.ToDelete.Where(t => t.Kind == "cache").Sum(t => t.Bytes);
        int cacheOldCount = plan.ToDelete.Count(t => t.Kind == "cache");

        StringBuilder sb = new();
        sb.AppendLine("DATED BACKUPS");
        if (String.IsNullOrEmpty(plan.BackupLocation))
        {
            sb.AppendLine("  Choose your Mods folder on the Installed / Updates tab to find backups.");
        }
        else
        {
            sb.AppendLine("  Location:  " + plan.BackupLocation);
            sb.AppendLine("  Total:     " + plan.BackupCount + " day folder(s), " + FormatBytes(plan.BackupBytes));
            sb.AppendLine("  Selected:  " + backupOldCount + " older than " + plan.Days + " days, " + FormatBytes(backupOld));
        }

        sb.AppendLine();
        sb.AppendLine("DOWNLOAD CACHE");
        sb.AppendLine("  Location:  " + plan.CacheLocation);
        sb.AppendLine("  Total:     " + plan.CacheCount + " mod(s), " + FormatBytes(plan.CacheBytes));
        sb.AppendLine("  Selected:  " + cacheOldCount + " not used in " + plan.Days + " days, " + FormatBytes(cacheOld));

        return sb.ToString();
    }
}
