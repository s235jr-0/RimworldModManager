using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ghud.Modules.RimWorld;

// What a RimWorld mod says about itself in About/About.xml.
public sealed class RimWorldModInfo
{
    public string Dir { get; init; } = "";
    public string Name { get; init; } = "";
    public string PackageId { get; init; } = "";      // lower-case
    public string WorkshopId { get; init; } = "";
    public List<string> Dependencies { get; init; } = new();
    public List<string> LoadAfter { get; init; } = new();
    public List<string> LoadBefore { get; init; } = new();
    public List<string> IncompatibleWith { get; init; } = new();
    public List<string> SupportedVersions { get; init; } = new();
    public bool IsOfficial => RimWorldMods.IsOfficial(PackageId);
}

public static class RimWorldMods
{
    // Core first, then the expansions in release order.
    public static readonly string[] OfficialOrder =
    {
        "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology",
        "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey",
    };

    public static bool IsOfficial(string? packageId) =>
        packageId != null && OfficialOrder.Contains(packageId.ToLowerInvariant());

    // Windows: %UserProfile%\AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios
    // Linux:   ~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios
    public static string DefaultUserDataFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            return Path.Combine(home, "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "RimWorld");
        string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        return Path.Combine(config, "unity3d", "Ludeon Studios", "RimWorld by Ludeon Studios");
    }

    public static string ModsConfigPath(string userData) => Path.Combine(userData, "Config", "ModsConfig.xml");

    public static string FindPlayerLog(string userData)
    {
        string direct = Path.Combine(userData, "Player.log");
        if (File.Exists(direct)) return direct;
        string previous = Path.Combine(userData, "Player-prev.log");
        return File.Exists(previous) ? previous : "";
    }

    // "1.6" from the game's Version.txt ("1.6.4518 rev..."); null if unknown.
    public static string? GameVersion(string gameFolder)
    {
        try
        {
            string f = Path.Combine(gameFolder, "Version.txt");
            if (!File.Exists(f)) return null;
            Match m = Regex.Match(File.ReadAllText(f), @"(\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    // About/About.xml, matched case-insensitively (Linux file systems aren't).
    public static string? FindAboutXml(string modDir)
    {
        if (!Directory.Exists(modDir)) return null;
        string? aboutDir = Directory.GetDirectories(modDir)
            .FirstOrDefault(d => Path.GetFileName(d).Equals("About", StringComparison.OrdinalIgnoreCase));
        return aboutDir == null
            ? null
            : Directory.GetFiles(aboutDir).FirstOrDefault(f => Path.GetFileName(f).Equals("About.xml", StringComparison.OrdinalIgnoreCase));
    }

    // Reads About.xml. `gameVersion` ("1.6") also picks the *ByVersion lists
    // for that version. Null when the folder isn't a mod.
    public static RimWorldModInfo? Read(string modDir, string? gameVersion = null)
    {
        string? about = FindAboutXml(modDir);
        if (about == null) return null;

        XElement? root;
        try { root = XDocument.Load(about).Root; }
        catch { return new RimWorldModInfo { Dir = modDir, Name = Path.GetFileName(modDir) }; }

        string packageId = Value(root, "packageId").ToLowerInvariant();
        string name = Value(root, "name");

        List<string> deps = Child(root, "modDependencies")?.Elements().Select(li => Value(li, "packageId").ToLowerInvariant())
                                .Where(s => s.Length > 0).ToList() ?? new();
        List<string> after = Ids(Child(root, "loadAfter")).Concat(Ids(Child(root, "forceLoadAfter"))).ToList();
        List<string> before = Ids(Child(root, "loadBefore")).Concat(Ids(Child(root, "forceLoadBefore"))).ToList();
        List<string> incompatible = Ids(Child(root, "incompatibleWith"));

        if (gameVersion != null)
        {
            XElement? v(string list) => Child(root, list)?.Elements()
                .FirstOrDefault(e => e.Name.LocalName.TrimStart('v').Equals(gameVersion, StringComparison.OrdinalIgnoreCase));
            deps.AddRange(v("modDependenciesByVersion")?.Elements().Select(li => Value(li, "packageId").ToLowerInvariant()).Where(s => s.Length > 0)
                          ?? Enumerable.Empty<string>());
            after.AddRange(Ids(v("loadAfterByVersion")));
            before.AddRange(Ids(v("loadBeforeByVersion")));
            incompatible.AddRange(Ids(v("incompatibleWithByVersion")));
        }

        return new RimWorldModInfo
        {
            Dir = modDir,
            Name = name.Length > 0 ? name : Path.GetFileName(modDir),
            PackageId = packageId,
            WorkshopId = WorkshopIdOf(modDir, root),
            Dependencies = deps.Distinct().ToList(),
            LoadAfter = after.Distinct().ToList(),
            LoadBefore = before.Distinct().ToList(),
            IncompatibleWith = incompatible.Distinct().ToList(),
            SupportedVersions = Child(root, "supportedVersions")?.Elements().Select(e => e.Value.Trim()).ToList() ?? new(),
        };
    }

    // The mod's own Workshop ID: a numeric folder name, About/PublishedFileId.txt,
    // or its OWN top-level <steamWorkshopUrl> (never a dependency's URL).
    public static string WorkshopIdOf(string modDir, XElement? aboutRoot = null)
    {
        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(modDir));
        if (Regex.IsMatch(folder, @"^\d{6,}$")) return folder;

        string? aboutDir = Path.GetDirectoryName(FindAboutXml(modDir) ?? "");
        foreach (string? dir in new[] { aboutDir, modDir })
        {
            if (String.IsNullOrEmpty(dir)) continue;
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
        }

        if (aboutRoot == null && FindAboutXml(modDir) is { } aboutFile)
        {
            try { aboutRoot = XDocument.Load(aboutFile).Root; } catch { }
        }
        Match url = Regex.Match(Value(aboutRoot, "steamWorkshopUrl"), @"(\d{6,})");
        return url.Success ? url.Groups[1].Value : "";
    }

    // Shared or posted text must not carry the account name:
    //   Windows  C:\Users\<name>  (Player.log writes both \ and /)
    //   Linux    /home/<name>     macOS  /Users/<name>
    public static string RedactUserPaths(string text)
    {
        if (String.IsNullOrEmpty(text)) return text;
        text = Regex.Replace(text, @"(?i)\b([A-Z]:[\\/]+Users[\\/]+)[^\\/\r\n""'<>|:*?]+", "$1<user>");
        text = Regex.Replace(text, @"(?<![\w.~])(/home/|/Users/)[^/\r\n""'<>|:*?\s]+", "$1<user>");
        return text;
    }

    private static XElement? Child(XElement? e, string name) =>
        e?.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string Value(XElement? e, string name) => Child(e, name)?.Value.Trim() ?? "";

    private static List<string> Ids(XElement? list) =>
        list?.Elements().Select(li => li.Value.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList() ?? new();
}
