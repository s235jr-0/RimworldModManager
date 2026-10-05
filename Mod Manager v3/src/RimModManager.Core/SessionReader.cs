using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace RimModManager.Core;

public sealed record SessionResult(List<RimWorldModRecord> Mods, string Summary, string Bundle);

// The RimWorld Session tab: combines the download cache, the Mods folder,
// ModsConfig.xml (active load order) and Player.log into one inventory and an
// LLM-ready diagnostic bundle.
public static class SessionReader
{
    public static SessionResult Read(
        string modsRoot, string dataRoot, SteamCmd steam, Sources.SourceRegistry? sources, Action<string>? status)
    {
        status?.Invoke("Reading RimWorld session...");

        string[] installedDirs = ModMetadata.ModFolders(modsRoot);
        ConcurrentBag<RimWorldModRecord> installedBag = new();
        int installedDone = 0;

        Parallel.ForEach(
            installedDirs,
            new ParallelOptions { MaxDegreeOfParallelism = ModMetadata.Parallelism },
            dir =>
            {
                RimWorldModRecord r = ModMetadata.ReadCached(dir);
                r.Source = ModSourceTags.Tag(sources?.SourceOf(r.FolderName, r.PackageId, r.WorkshopId)
                    ?? (String.IsNullOrWhiteSpace(r.WorkshopId) ? ModSource.Manual : ModSource.Steam));
                installedBag.Add(r);

                int done = Interlocked.Increment(ref installedDone);
                if (done == installedDirs.Length || done % 20 == 0)
                    status?.Invoke("Reading installed mod metadata " + done + "/" + installedDirs.Length + "...");
            });

        string modsConfig = Path.Combine(dataRoot, "Config", "ModsConfig.xml");
        Dictionary<string, int> active = ModMetadata.ReadActiveLoadOrder(modsConfig);
        List<RimWorldModRecord> downloaded = ReadDownloadedCacheMods(steam, status);

        static string KeyFor(RimWorldModRecord r) =>
            !String.IsNullOrWhiteSpace(r.PackageId) ? "pkg:" + r.PackageId :
            !String.IsNullOrWhiteSpace(r.WorkshopId) ? "ws:" + r.WorkshopId :
            "folder:" + r.FolderName;

        Dictionary<string, RimWorldModRecord> byKey = new(StringComparer.OrdinalIgnoreCase);

        foreach (RimWorldModRecord r in downloaded)
            byKey[KeyFor(r)] = r;

        foreach (RimWorldModRecord r in installedBag)
        {
            string key = KeyFor(r);

            if (byKey.TryGetValue(key, out RimWorldModRecord? existing))
            {
                existing.Installed = true;
                existing.Source = r.Source;
                existing.FolderPath = r.FolderPath;
                existing.FolderName = r.FolderName;
                existing.Name = r.Name;
                existing.PackageId = r.PackageId;
                if (!String.IsNullOrWhiteSpace(r.WorkshopId)) existing.WorkshopId = r.WorkshopId;
                existing.Dependencies = r.Dependencies;
                existing.LoadAfter = r.LoadAfter;
                existing.LoadBefore = r.LoadBefore;
                existing.IncompatibleWith = r.IncompatibleWith;
                existing.SupportedVersions = r.SupportedVersions;
            }
            else
            {
                byKey[key] = r;
            }
        }

        // Active package IDs that are not physically installed are still
        // important diagnostic records.
        foreach (KeyValuePair<string, int> kv in active)
        {
            RimWorldModRecord? match = byKey.Values.FirstOrDefault(
                r => String.Equals(r.PackageId, kv.Key, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                match = new RimWorldModRecord
                {
                    Name = kv.Key,
                    PackageId = kv.Key,
                    GameContent = ModMetadata.IsGameContentPackage(kv.Key),
                };
                byKey["active:" + kv.Key] = match;
            }

            match.Active = true;
            match.LoadOrder = kv.Value;
        }

        foreach (RimWorldModRecord r in byKey.Values)
        {
            if (!String.IsNullOrWhiteSpace(r.PackageId) && active.TryGetValue(r.PackageId, out int pos))
            {
                r.Active = true;
                r.LoadOrder = pos;
            }
        }

        List<RimWorldModRecord> mods = byKey.Values
            .OrderBy(r => r.Active ? 0 : 1)
            .ThenBy(r => r.Active ? r.LoadOrder : Int32.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string playerLog = FindPlayerLog(dataRoot);
        string logText = "";
        if (File.Exists(playerLog))
        {
            try { logText = ReadTextShared(playerLog); }
            catch (Exception ex) { logText = "[Could not read Player.log: " + ex.Message + "]"; }
        }

        return new SessionResult(
            mods,
            BuildSummary(mods),
            BuildBundle(mods, modsRoot, dataRoot, modsConfig, playerLog, logText));
    }

    private static List<RimWorldModRecord> ReadDownloadedCacheMods(SteamCmd steam, Action<string>? status)
    {
        string workshopRoot = steam.ContentRoot(ModInstaller.RimWorldAppId);
        if (!Directory.Exists(workshopRoot)) return new List<RimWorldModRecord>();

        string[] dirs = Directory.GetDirectories(workshopRoot);
        ConcurrentBag<RimWorldModRecord> result = new();
        int done = 0;

        Parallel.ForEach(
            dirs,
            new ParallelOptions { MaxDegreeOfParallelism = ModMetadata.Parallelism },
            dir =>
            {
                RimWorldModRecord r = ModMetadata.ReadCached(dir);
                r.Installed = false;
                r.Downloaded = true;
                r.Source = "Steam";
                if (String.IsNullOrWhiteSpace(r.WorkshopId))
                    r.WorkshopId = Path.GetFileName(dir);
                result.Add(r);

                int n = Interlocked.Increment(ref done);
                if (n == dirs.Length || n % 25 == 0)
                    status?.Invoke("Reading downloaded cache " + n + "/" + dirs.Length + "...");
            });

        return result.ToList();
    }

    public static string FindPlayerLog(string dataRoot)
    {
        string direct = Path.Combine(dataRoot, "Player.log");
        if (File.Exists(direct)) return direct;

        string previous = Path.Combine(dataRoot, "Player-prev.log");
        return File.Exists(previous) ? previous : "";
    }

    // Opens Player.log in a way that works while RimWorld is still writing it.
    private static string ReadTextShared(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader sr = new(fs, Encoding.UTF8, true);
        return sr.ReadToEnd();
    }

    public static string BuildSummary(List<RimWorldModRecord> mods)
    {
        List<RimWorldModRecord> activeMissing = mods.Where(r => r.Active && !r.Installed && !r.GameContent).ToList();

        StringBuilder sb = new();
        sb.AppendLine("SESSION INVENTORY");
        sb.AppendLine("=================");
        sb.AppendLine("Downloaded cache entries: " + mods.Count(r => r.Downloaded));
        sb.AppendLine("Installed mod folders:     " + mods.Count(r => r.Installed));
        sb.AppendLine("Active / loaded mods:      " + mods.Count(r => r.Active));
        sb.AppendLine("Ludeon game/DLC entries:   " + mods.Count(r => r.Active && r.GameContent));
        sb.AppendLine();

        if (activeMissing.Count > 0)
        {
            sb.AppendLine("UNMATCHED ACTIVE PACKAGE IDs");
            foreach (RimWorldModRecord r in activeMissing)
                sb.AppendLine("  - " + r.PackageId);
            sb.AppendLine();
        }

        sb.AppendLine("Installed but inactive: " + mods.Count(r => r.Installed && !r.Active));
        return sb.ToString();
    }

    private static string JoinOrDash(IEnumerable<string> values)
    {
        List<string> list = values.Where(s => !String.IsNullOrWhiteSpace(s)).ToList();
        return list.Count == 0 ? "-" : String.Join(", ", list);
    }

    private static string YesNo(bool b) => b ? "yes" : "no";

    public static string BuildBundle(
        List<RimWorldModRecord> mods, string modsRoot, string dataRoot, string modsConfig, string playerLog, string playerLogText)
    {
        StringBuilder sb = new();

        sb.AppendLine("RIMWORLD MOD DIAGNOSTIC BUNDLE");
        sb.AppendLine("==============================");
        sb.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("Mods folder: " + modsRoot);
        sb.AppendLine("RimWorld data: " + dataRoot);
        sb.AppendLine();

        sb.AppendLine("PURPOSE FOR THE LLM");
        sb.AppendLine("-------------------");
        sb.AppendLine(
            "Analyze this RimWorld mod session. Identify errors, missing " +
            "dependencies, incompatibilities, and load-order problems. " +
            "Recommend an optimized ACTIVE mod order. Clearly separate " +
            "mods that should remain installed-but-disabled, mods that " +
            "should be removed, and conflicts that require a compatibility " +
            "patch or workaround. Prefer explicit mod metadata/load-order " +
            "rules over guesses.");
        sb.AppendLine();

        sb.AppendLine("STATE LEGEND");
        sb.AppendLine("------------");
        sb.AppendLine("Downloaded = present in this manager's SteamCMD cache.");
        sb.AppendLine("Installed = physically present in RimWorld's Mods folder.");
        sb.AppendLine("Active = present in ModsConfig.xml and therefore part of the configured RimWorld load order.");
        sb.AppendLine();

        sb.AppendLine("ACTIVE LOAD ORDER");
        sb.AppendLine("-----------------");
        foreach (RimWorldModRecord r in mods.Where(m => m.Active).OrderBy(m => m.LoadOrder))
        {
            sb.AppendLine(
                (r.LoadOrder + 1).ToString().PadLeft(4) + ". " + r.Name +
                " | packageId=" + r.PackageId +
                " | workshop=" + r.WorkshopId +
                " | source=" + (r.Source.Length > 0 ? r.Source : "-") +
                " | installed=" + YesNo(r.Installed) +
                " | gameContent=" + YesNo(r.GameContent) +
                " | downloaded=" + YesNo(r.Downloaded));
        }

        sb.AppendLine();
        sb.AppendLine("FULL MOD INVENTORY + METADATA");
        sb.AppendLine("-----------------------------");
        foreach (RimWorldModRecord r in mods.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine("MOD: " + r.Name);
            sb.AppendLine("  packageId: " + r.PackageId);
            sb.AppendLine("  workshopId: " + r.WorkshopId);
            sb.AppendLine("  source: " + (r.Source.Length > 0 ? r.Source : "-"));
            sb.AppendLine("  state: downloaded=" + YesNo(r.Downloaded) + ", installed=" + YesNo(r.Installed) +
                          ", active=" + YesNo(r.Active) + ", gameContent=" + YesNo(r.GameContent));
            if (r.Active)
                sb.AppendLine("  loadOrder: " + (r.LoadOrder + 1));
            sb.AppendLine("  dependencies: " + JoinOrDash(r.Dependencies));
            sb.AppendLine("  loadAfter: " + JoinOrDash(r.LoadAfter));
            sb.AppendLine("  loadBefore: " + JoinOrDash(r.LoadBefore));
            sb.AppendLine("  incompatibleWith: " + JoinOrDash(r.IncompatibleWith));
            sb.AppendLine("  supportedVersions: " + JoinOrDash(r.SupportedVersions));
            sb.AppendLine();
        }

        sb.AppendLine("RAW MODSCONFIG.XML");
        sb.AppendLine("------------------");
        if (File.Exists(modsConfig))
        {
            try { sb.AppendLine(File.ReadAllText(modsConfig, Encoding.UTF8)); }
            catch { sb.AppendLine("[Could not read ModsConfig.xml]"); }
        }
        else
        {
            sb.AppendLine("[ModsConfig.xml not found]");
        }

        sb.AppendLine();
        sb.AppendLine("RIMWORLD PLAYER.LOG");
        sb.AppendLine("-------------------");
        sb.AppendLine("Path: " + playerLog);
        sb.AppendLine(String.IsNullOrWhiteSpace(playerLogText) ? "[Player.log not found or empty]" : playerLogText);

        return RedactUserPaths(sb.ToString());
    }

    // The bundle is meant to be pasted into an LLM or shared online, so strip
    // the account name out of home-folder paths:
    //   Windows  C:\Users\<name>  (Player.log writes both \ and /)
    //   Linux    /home/<name>
    //   macOS    /Users/<name>
    public static string RedactUserPaths(string text)
    {
        if (String.IsNullOrEmpty(text)) return text;

        text = Regex.Replace(text, @"(?i)\b([A-Z]:[\\/]+Users[\\/]+)[^\\/\r\n""'<>|:*?]+", "$1<user>");
        text = Regex.Replace(text, @"(?<![\w.~])(/home/|/Users/)[^/\r\n""'<>|:*?\s]+", "$1<user>");
        return text;
    }
}
