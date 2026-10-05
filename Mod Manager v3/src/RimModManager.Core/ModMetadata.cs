using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RimModManager.Core;

// Reading mod folders: About.xml identity, Workshop IDs, load order.
public static class ModMetadata
{
    // Folders the manager itself creates next to mods; never treated as mods.
    public const string ManagerFolderPrefix = "_WMM_";

    public static int Parallelism => Math.Max(2, Math.Min(8, Environment.ProcessorCount));

    // About/About.xml, matched case-insensitively: Linux file systems are
    // case-sensitive and some mods ship "about/About.xml".
    public static string? FindAboutXml(string modDir)
    {
        string exact = Path.Combine(modDir, "About", "About.xml");
        if (File.Exists(exact)) return exact;
        if (OperatingSystem.IsWindows()) return null;   // already case-insensitive

        try
        {
            string? aboutDir = Directory.EnumerateDirectories(modDir)
                .FirstOrDefault(d => String.Equals(Path.GetFileName(d), "About", StringComparison.OrdinalIgnoreCase));
            if (aboutDir == null) return null;

            return Directory.EnumerateFiles(aboutDir)
                .FirstOrDefault(f => String.Equals(Path.GetFileName(f), "About.xml", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    public static bool IsModFolder(string dir) => FindAboutXml(dir) != null;

    // Candidate mod folders directly inside a Mods folder.
    public static string[] ModFolders(string root) =>
        Directory.GetDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith(ManagerFolderPrefix, StringComparison.OrdinalIgnoreCase) &&
                        IsModFolder(d))
            .ToArray();

    // The mod's OWN top-level element, never one nested under dependencies.
    public static string DirectRootValue(XElement? root, string name)
    {
        if (root == null || String.IsNullOrWhiteSpace(name)) return "";

        XElement? node = root.Elements()
            .FirstOrDefault(e => String.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));

        return node == null ? "" : (node.Value ?? "").Trim();
    }

    private static List<string> XmlList(XElement root, string name)
    {
        XElement? parent = root.Descendants(name).FirstOrDefault();
        if (parent == null) return new List<string>();

        return parent.Elements()
            .Select(e => (e.Value ?? "").Trim())
            .Where(s => !String.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static readonly ConcurrentDictionary<string, RimWorldModRecord> MetadataCache =
        new(StringComparer.OrdinalIgnoreCase);

    // Cached for the life of the app; the key includes About.xml's timestamp,
    // so an updated mod is parsed again.
    public static RimWorldModRecord ReadCached(string dir)
    {
        string full = Path.GetFullPath(dir);
        DateTime stamp = DateTime.MinValue;

        try
        {
            string? about = FindAboutXml(full);
            stamp = about != null ? File.GetLastWriteTimeUtc(about) : Directory.GetLastWriteTimeUtc(full);
        }
        catch { }

        string key = full + "|" + stamp.Ticks;

        if (MetadataCache.TryGetValue(key, out RimWorldModRecord? cached))
            return cached.Clone();

        RimWorldModRecord fresh = Read(full);
        MetadataCache[key] = fresh.Clone();
        return fresh;
    }

    public static RimWorldModRecord Read(string dir)
    {
        RimWorldModRecord r = new()
        {
            FolderPath = dir,
            FolderName = Path.GetFileName(dir),
        };

        string? about = FindAboutXml(dir);
        r.Installed = about != null;

        try
        {
            XElement? root = about != null ? XDocument.Load(about).Root : null;

            if (root != null)
            {
                r.Name = DirectRootValue(root, "name");
                r.PackageId = DirectRootValue(root, "packageId");

                r.Dependencies = root.Descendants("modDependencies")
                    .Descendants("packageId")
                    .Select(e => (e.Value ?? "").Trim())
                    .Where(s => !String.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                r.LoadAfter = XmlList(root, "loadAfter");
                r.LoadBefore = XmlList(root, "loadBefore");
                r.IncompatibleWith = XmlList(root, "incompatibleWith");
                r.SupportedVersions = XmlList(root, "supportedVersions");

                Match m = Regex.Match(DirectRootValue(root, "steamWorkshopUrl"), @"(\d{6,})");
                if (m.Success)
                    r.WorkshopId = m.Groups[1].Value;
            }
        }
        catch { }

        if (String.IsNullOrWhiteSpace(r.WorkshopId))
            r.WorkshopId = DiscoverWorkshopId(dir, r.FolderName);

        if (String.IsNullOrWhiteSpace(r.Name))
            r.Name = r.FolderName;

        return r;
    }

    public static string DiscoverWorkshopId(string dir, string name)
    {
        // A valid RimWorld mod must have an immediate About/About.xml. This
        // also prevents numeric backup/staging folders being mistaken for
        // installed Workshop mods.
        string? about = FindAboutXml(dir);
        if (about == null) return "";

        if (Regex.IsMatch(name, @"^\d+$"))
            return name;

        foreach (string file in new[] { "PublishedFileId.txt", "publishedfileid.txt", "workshop_id.txt" })
        {
            try
            {
                string f = Path.Combine(dir, file);
                if (!File.Exists(f)) continue;
                Match m = Regex.Match(File.ReadAllText(f), @"\d{6,}");
                if (m.Success) return m.Value;
            }
            catch { }
        }

        // IMPORTANT: only the mod's OWN top-level <steamWorkshopUrl>.
        // Dependency URLs deeper in About.xml must never identify the mod.
        try
        {
            Match m = Regex.Match(DirectRootValue(XDocument.Load(about).Root, "steamWorkshopUrl"), @"(\d{6,})");
            if (m.Success) return m.Groups[1].Value;
        }
        catch { }

        return "";
    }

    // Workshop ID -> installed folder, built in one parallel pass.
    public static Dictionary<string, string> BuildInstalledWorkshopMap(string root)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return map;

        Parallel.ForEach(
            Directory.GetDirectories(root),
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
            dir =>
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(ManagerFolderPrefix, StringComparison.OrdinalIgnoreCase)) return;

                try
                {
                    string id = DiscoverWorkshopId(dir, name);
                    if (String.IsNullOrWhiteSpace(id)) return;

                    lock (map)
                        map.TryAdd(id, dir);
                }
                catch { }
            });

        return map;
    }

    public const string FailedLastUpdateStatus = "FAILED last update (see Manager Log)";

    // Installed / Updates tab scan. `sources` tags mods installed from
    // non-Steam sources; without it every mod is Steam (Workshop ID) or Manual.
    public static List<ModEntry> ScanMods(string root, StateStore state, Sources.SourceRegistry? sources, Action<string>? status)
    {
        string[] dirs = ModFolders(root);
        ConcurrentBag<ModEntry> found = new();
        int done = 0;

        Parallel.ForEach(
            dirs,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism },
            dir =>
            {
                string name = Path.GetFileName(dir);
                string id = DiscoverWorkshopId(dir, name);
                RimWorldModRecord meta = ReadCached(dir);
                Sources.SourceRecord? record = sources?.Find(name, meta.PackageId);

                Sources.ModSource source = record?.Source ??
                    (String.IsNullOrWhiteSpace(id) ? Sources.ModSource.Manual : Sources.ModSource.Steam);
                bool steam = source == Sources.ModSource.Steam;

                found.Add(new ModEntry
                {
                    FolderName = name,
                    FolderPath = dir,
                    WorkshopId = steam ? id : "",
                    IsLocalOnly = !steam,
                    Source = source,
                    SourceUrl = record?.Url ?? "",
                    // Steam titles come from Steam when checked; others from About.xml.
                    Title = steam ? "" : meta.Name,
                    Status = steam
                        ? (state.GetFailed(id) != null ? FailedLastUpdateStatus : "Not checked")
                        : record == null ? "Local / non-Steam" : "Not checked",
                });

                int n = Interlocked.Increment(ref done);
                if (n == dirs.Length || n % 25 == 0)
                    status?.Invoke("Scanning mod folders " + n + "/" + dirs.Length + "...");
            });

        return found.OrderBy(e => e.FolderName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static Dictionary<string, int> ReadActiveLoadOrder(string modsConfigPath)
    {
        Dictionary<string, int> order = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(modsConfigPath)) return order;

        try
        {
            XElement? active = XDocument.Load(modsConfigPath).Descendants("activeMods").FirstOrDefault();
            if (active == null) return order;

            int i = 0;
            foreach (XElement li in active.Elements("li"))
            {
                string packageId = (li.Value ?? "").Trim();
                if (String.IsNullOrWhiteSpace(packageId)) continue;

                order.TryAdd(packageId, i);
                i++;
            }
        }
        catch { }

        return order;
    }

    public static bool IsGameContentPackage(string? packageId)
    {
        if (String.IsNullOrWhiteSpace(packageId)) return false;

        return packageId.Trim().ToLowerInvariant() is
            "ludeon.rimworld" or
            "ludeon.rimworld.royalty" or
            "ludeon.rimworld.ideology" or
            "ludeon.rimworld.biotech" or
            "ludeon.rimworld.anomaly" or
            "ludeon.rimworld.odyssey";
    }

    // Workshop IDs from pasted URLs / IDs / text files.
    public static List<string> ParseIds(string text)
    {
        HashSet<string> ids = new();

        foreach (Match m in Regex.Matches(text, @"(?i)[?&]id=(\d+)"))
            ids.Add(m.Groups[1].Value);

        foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            string s = line.Trim();
            if (Regex.IsMatch(s, @"^\d+$")) ids.Add(s);

            Match m = Regex.Match(s, @"(?i)filedetails/(\d+)");
            if (m.Success) ids.Add(m.Groups[1].Value);
        }

        return ids.ToList();
    }
}
