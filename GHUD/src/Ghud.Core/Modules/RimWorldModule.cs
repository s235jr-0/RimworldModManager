using System.Xml.Linq;

namespace Ghud.Modules;

// RimWorld: every mod is a folder with About/About.xml, deployed into the
// game's Mods folder. RimWorld sorts mods itself (ModsConfig.xml).
public sealed class RimWorldModule : IGameModule
{
    public string Id => "rimworld";
    public string DisplayName => "RimWorld";
    public string? NexusDomain => "rimworld";
    public string? SteamAppId => "294100";
    public LoadOrderKind LoadOrder => LoadOrderKind.GameManaged;
    public string DefaultDeploySubfolder => "Mods";

    public IEnumerable<string> GuessGameFolders()
    {
        List<string> list = new();
        if (OperatingSystem.IsWindows())
        {
            list.Add(@"C:\GOG Games\RimWorld");
            list.Add(@"C:\Program Files (x86)\GOG Galaxy\Games\RimWorld");
            list.Add(@"C:\Program Files (x86)\Steam\steamapps\common\RimWorld");
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            list.Add(Path.Combine(home, "GOG Games", "RimWorld"));
            list.Add(Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "RimWorld"));
        }
        return list.Where(LooksLikeGameFolder);
    }

    public bool LooksLikeGameFolder(string folder) =>
        Directory.Exists(Path.Combine(folder, "Data", "Core")) &&
        (File.Exists(Path.Combine(folder, "RimWorldWin64.exe")) || File.Exists(Path.Combine(folder, "RimWorldLinux")) ||
         Directory.Exists(Path.Combine(folder, "RimWorldWin64_Data")) || Directory.Exists(Path.Combine(folder, "RimWorldLinux_Data")));

    // Every folder with About/About.xml, however deeply the archive nested it.
    // A mod's own subfolders are not searched.
    public List<ModCandidate> FindMods(string unpackedRoot)
    {
        List<ModCandidate> found = new();
        Queue<(string Dir, int Depth)> queue = new();
        queue.Enqueue((unpackedRoot, 0));

        while (queue.Count > 0)
        {
            (string dir, int depth) = queue.Dequeue();

            string? about = FindAboutXml(dir);
            if (about != null && dir != unpackedRoot)
            {
                (string name, string packageId) = ReadAbout(about);
                string folder = Path.GetFileName(dir);
                found.Add(new ModCandidate(name.Length > 0 ? name : folder,
                                           packageId.Length > 0 ? packageId.ToLowerInvariant() : null,
                                           dir, ArchiveTools.SafeFolderName(folder, "Mod")));
                continue;
            }

            if (about != null)
            {
                // The archive's root itself is the mod: name the folder after it.
                (string name, string packageId) = ReadAbout(about);
                found.Add(new ModCandidate(name.Length > 0 ? name : "Mod",
                                           packageId.Length > 0 ? packageId.ToLowerInvariant() : null,
                                           dir, ArchiveTools.SafeFolderName(name, "Mod")));
                continue;
            }

            if (depth >= 6) continue;
            foreach (string sub in Directory.GetDirectories(dir))
                if (!SafeFileSystem.IsLink(sub))
                    queue.Enqueue((sub, depth + 1));
        }

        return found;
    }

    // About/About.xml, matched case-insensitively (Linux file systems aren't).
    private static string? FindAboutXml(string modDir)
    {
        string? aboutDir = Directory.GetDirectories(modDir)
            .FirstOrDefault(d => Path.GetFileName(d).Equals("About", StringComparison.OrdinalIgnoreCase));
        return aboutDir == null
            ? null
            : Directory.GetFiles(aboutDir).FirstOrDefault(f => Path.GetFileName(f).Equals("About.xml", StringComparison.OrdinalIgnoreCase));
    }

    private static (string Name, string PackageId) ReadAbout(string aboutXml)
    {
        try
        {
            XElement? root = XDocument.Load(aboutXml).Root;
            return (Child(root, "name"), Child(root, "packageId"));
        }
        catch
        {
            return ("", "");
        }
    }

    private static string Child(XElement? root, string name) =>
        root?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "";
}
