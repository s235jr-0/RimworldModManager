using System.Text.Json;
using System.Text.Json.Serialization;
using Ghud.Modules;

namespace Ghud;

public static class GhudPaths
{
    // Windows: %LocalAppData%\GHUD   Linux: ~/.local/share/GHUD
    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "GHUD");

    public static string InstancesRoot => Path.Combine(DataRoot, "instances");

    public static string LogsDir => Path.Combine(DataRoot, "logs");

    // Colour schemes (shared format with the RimWorld Mod Manager).
    public static string ThemesFile => Path.Combine(DataRoot, "themes.json");

    // Where an instance keeps its staged mods and downloaded archives. Hard
    // links only work within one drive, so on Windows a game on another drive
    // gets "<drive>\GHUD Staging\<instance>"; otherwise the instance's own
    // data folder is used.
    public static string StorageFor(string gameFolder, string dataFolder, string instanceId)
    {
        if (!OperatingSystem.IsWindows()) return dataFolder;

        string gameRoot = Path.GetPathRoot(Path.GetFullPath(gameFolder)) ?? "";
        string dataRoot = Path.GetPathRoot(Path.GetFullPath(dataFolder)) ?? "";
        return gameRoot.Length == 0 || gameRoot.Equals(dataRoot, StringComparison.OrdinalIgnoreCase)
            ? dataFolder
            : Path.Combine(gameRoot, "GHUD Staging", instanceId);
    }
}

// One installed game managed by GHUD. Its own folder (DataFolder) holds:
//   instance.json   these settings
//   mods.json       the staged mods (shared by all profiles)
//   profiles.json   each profile's order and on/off choices, and the active one
//   deployment.json which files GHUD placed in the game, and for which mod
//   originals\      game files a mod replaced, put back when the mod goes
//   changed\        deployed files that were edited in the game folder, kept on removal
// Staged mods and downloaded archives live under StorageRoot (on the game's
// drive): staging\<mod>\ and downloads\.
public sealed class GameInstance
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string GameFolder { get; set; } = "";
    public string DeploySubfolder { get; set; } = "";

    // Staging + download library location (GhudPaths.StorageFor); null =
    // the data folder.
    public string? StorageFolder { get; set; }

    public GeneralGameSettings? General { get; set; }

    [JsonIgnore] public string DataFolder { get; set; } = "";

    [JsonIgnore] public string DeployFolder => Path.Combine(GameFolder, DeploySubfolder);
    [JsonIgnore] public string StorageRoot => String.IsNullOrWhiteSpace(StorageFolder) ? DataFolder : StorageFolder;
    [JsonIgnore] public string StagingRoot => Path.Combine(StorageRoot, "staging");
    [JsonIgnore] public string DownloadsRoot => Path.Combine(StorageRoot, "downloads");
    [JsonIgnore] public string ModListFile => Path.Combine(DataFolder, "mods.json");
    [JsonIgnore] public string ProfilesFile => Path.Combine(DataFolder, "profiles.json");
    [JsonIgnore] public string ManifestFile => Path.Combine(DataFolder, "deployment.json");
    [JsonIgnore] public string OriginalsRoot => Path.Combine(DataFolder, "originals");
    [JsonIgnore] public string ChangedRoot => Path.Combine(DataFolder, "changed");
    [JsonIgnore] public string TempRoot => Path.Combine(DataFolder, "tmp");

    public IGameModule CreateModule() => ModuleId switch
    {
        "rimworld" => new RimWorldModule(),
        "general" => new GeneralModule(General ?? new GeneralGameSettings()),
        _ => throw new Exception("Unknown game module: " + ModuleId),
    };
}

public sealed class InstanceStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;

    public InstanceStore(string root) => _root = root;

    public List<GameInstance> All()
    {
        List<GameInstance> list = new();
        if (!Directory.Exists(_root)) return list;

        foreach (string dir in Directory.GetDirectories(_root))
        {
            string file = Path.Combine(dir, "instance.json");
            if (!File.Exists(file)) continue;
            try
            {
                GameInstance? i = JsonSerializer.Deserialize<GameInstance>(File.ReadAllText(file), Json);
                if (i == null) continue;
                i.DataFolder = dir;
                list.Add(i);
            }
            catch { /* a damaged instance file shouldn't hide the others */ }
        }

        return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public GameInstance Create(string name, IGameModule module, string gameFolder, GeneralGameSettings? general = null)
    {
        string baseId = ArchiveTools.SafeFolderName(name, module.Id).Replace(' ', '-').ToLowerInvariant();
        string id = baseId;
        for (int n = 2; Directory.Exists(Path.Combine(_root, id)); n++) id = baseId + "-" + n;

        GameInstance i = new()
        {
            Id = id,
            Name = name,
            ModuleId = module.Id,
            GameFolder = gameFolder,
            DeploySubfolder = module.DefaultDeploySubfolder,
            General = general,
            DataFolder = Path.Combine(_root, id),
        };
        string storage = GhudPaths.StorageFor(gameFolder, i.DataFolder, id);
        if (storage != i.DataFolder) i.StorageFolder = storage;
        Save(i);
        return i;
    }

    // Forgets an instance: its settings, profiles and records. Staged mods and
    // the download library are only deleted when deleteStorage is set. The
    // game folder is never touched here (undeploy first).
    public void Delete(GameInstance i, bool deleteStorage)
    {
        bool storageInside = i.StorageRoot == i.DataFolder;
        if (deleteStorage && !storageInside && Directory.Exists(i.StorageRoot))
            SafeFileSystem.DeleteDirectory(i.StorageRoot);
        if (!Directory.Exists(i.DataFolder)) return;

        if (deleteStorage || !storageInside)
        {
            SafeFileSystem.DeleteDirectory(i.DataFolder);
            return;
        }

        // Keep staging\ and downloads\ (they live in the data folder); remove
        // everything else, including instance.json, so the game is forgotten.
        foreach (FileSystemInfo e in new DirectoryInfo(i.DataFolder).EnumerateFileSystemInfos())
        {
            if (e.Name is "staging" or "downloads") continue;
            if (e is DirectoryInfo) SafeFileSystem.DeleteDirectory(e.FullName);
            else e.Delete();
        }
    }

    public void Save(GameInstance i)
    {
        Directory.CreateDirectory(i.DataFolder);
        JsonFile.WriteAtomic(Path.Combine(i.DataFolder, "instance.json"), JsonSerializer.Serialize(i, Json));
    }
}

internal static class JsonFile
{
    // Write to a temporary file, then swap it in: a crash never leaves half a file.
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
