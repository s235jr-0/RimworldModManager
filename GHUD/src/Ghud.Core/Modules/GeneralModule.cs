namespace Ghud.Modules;

public enum GeneralLayout
{
    // Each mod is its own folder inside the deploy folder (Mods/<mod>/...).
    FolderPerMod,

    // A mod's files merge straight into the deploy folder, later mods
    // overwriting earlier ones (gamedata-style overrides).
    MergeIntoDeployFolder,
}

// Settings for a game GHUD has no built-in module for. Saved with the
// instance, so any game can be added without code.
public sealed class GeneralGameSettings
{
    public string Name { get; set; } = "My game";

    // Deploy folder relative to the game folder ("Mods", "" = the game folder).
    public string DeploySubfolder { get; set; } = "Mods";

    public GeneralLayout Layout { get; set; } = GeneralLayout.FolderPerMod;

    // What marks a mod: a file or folder name ("SubModule.xml", "gamedata") or
    // a file pattern ("*.pak"). Empty = FolderPerMod uses every top-level
    // folder; MergeIntoDeployFolder uses the archive's contents.
    public string Marker { get; set; } = "";

    public string? NexusDomain { get; set; }
}

public sealed class GeneralModule : IGameModule
{
    private readonly GeneralGameSettings _s;

    public GeneralModule(GeneralGameSettings settings) => _s = settings;

    public string Id => "general";
    public string DisplayName => _s.Name;
    public string? NexusDomain => _s.NexusDomain;
    public string? SteamAppId => null;
    public LoadOrderKind LoadOrder =>
        _s.Layout == GeneralLayout.MergeIntoDeployFolder ? LoadOrderKind.FilePriority : LoadOrderKind.GameManaged;
    public string DefaultDeploySubfolder => _s.DeploySubfolder;

    public IEnumerable<string> GuessGameFolders() => Array.Empty<string>();

    public bool LooksLikeGameFolder(string folder) => Directory.Exists(folder);

    public List<ModCandidate> FindMods(string unpackedRoot)
    {
        if (_s.Layout == GeneralLayout.MergeIntoDeployFolder)
        {
            // With a marker ("gamedata"), the mod root is the folder that holds
            // it. Without one the archive is taken as-is: a lone top folder may
            // be a wrapper ("MyMod-1.0/") or real ("gamedata/"), and only a
            // marker can tell. "Mod" = name it after the archive.
            string? dir = _s.Marker.Length == 0 ? unpackedRoot : FindFolderWithMarker(unpackedRoot, 4);
            return dir == null
                ? new List<ModCandidate>()
                : new List<ModCandidate> { new(dir == unpackedRoot ? "Mod" : Path.GetFileName(dir), null, dir, "") };
        }

        // Folder per mod: without a marker, skip wrapper folders; with one,
        // search from the top (stripping could step inside the marked folder).
        string root = _s.Marker.Length == 0 ? StripWrappers(unpackedRoot) : unpackedRoot;

        List<ModCandidate> found = new();
        if (_s.Marker.Length == 0)
        {
            foreach (string sub in SubDirs(root))
                found.Add(new ModCandidate(Path.GetFileName(sub), null, sub, ArchiveTools.SafeFolderName(Path.GetFileName(sub), "Mod")));
            return found;
        }

        Queue<(string Dir, int Depth)> queue = new();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            (string dir, int depth) = queue.Dequeue();
            if (HasMarker(dir))
            {
                string name = dir == unpackedRoot ? "Mod" : Path.GetFileName(dir);
                found.Add(new ModCandidate(name, null, dir, ArchiveTools.SafeFolderName(name, "Mod")));
                continue;
            }
            if (depth < 6)
                foreach (string sub in SubDirs(dir)) queue.Enqueue((sub, depth + 1));
        }
        return found;
    }

    private string? FindFolderWithMarker(string root, int maxDepth)
    {
        Queue<(string Dir, int Depth)> queue = new();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            (string dir, int depth) = queue.Dequeue();
            if (HasMarker(dir)) return dir;
            if (depth < maxDepth)
                foreach (string sub in SubDirs(dir)) queue.Enqueue((sub, depth + 1));
        }
        return null;
    }

    // Marker = a file/folder name (any case) or a file pattern with *.
    private bool HasMarker(string dir)
    {
        if (_s.Marker.Contains('*'))
            return Directory.EnumerateFiles(dir, _s.Marker, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Any();

        return Directory.EnumerateFileSystemEntries(dir)
            .Any(e => Path.GetFileName(e).Equals(_s.Marker, StringComparison.OrdinalIgnoreCase));
    }

    // Archives often wrap everything in one folder ("MyMod-1.2/..."): skip
    // single-folder levels that hold no files.
    internal static string StripWrappers(string root)
    {
        string dir = root;
        for (int i = 0; i < 4; i++)
        {
            string[] dirs = SubDirs(dir).ToArray();
            if (dirs.Length != 1 || Directory.EnumerateFiles(dir).Any()) break;
            dir = dirs[0];
        }
        return dir;
    }

    private static IEnumerable<string> SubDirs(string dir) =>
        Directory.GetDirectories(dir).Where(d => !SafeFileSystem.IsLink(d));
}
