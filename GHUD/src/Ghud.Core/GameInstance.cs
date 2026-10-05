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
}

// One installed game managed by GHUD. Its own folder (DataFolder) holds:
//   instance.json  these settings
//   mods.json      the mod list, in priority order
//   deployment.json which files GHUD placed in the game, and for which mod
//   staging\       each mod unpacked in its own folder (unless StagingFolder is set)
//   originals\     game files a mod replaced, put back when the mod goes
//   changed\       deployed files that were edited in the game folder, kept on removal
public sealed class GameInstance
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ModuleId { get; set; } = "";
    public string GameFolder { get; set; } = "";
    public string DeploySubfolder { get; set; } = "";

    // Hard links only work within one drive: put staging on the game's drive
    // when the game isn't on the same drive as GHUD's data folder.
    public string? StagingFolder { get; set; }

    public GeneralGameSettings? General { get; set; }

    [JsonIgnore] public string DataFolder { get; set; } = "";

    [JsonIgnore] public string DeployFolder => Path.Combine(GameFolder, DeploySubfolder);
    [JsonIgnore] public string StagingRoot => String.IsNullOrWhiteSpace(StagingFolder) ? Path.Combine(DataFolder, "staging") : StagingFolder;
    [JsonIgnore] public string ModListFile => Path.Combine(DataFolder, "mods.json");
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
        Save(i);
        return i;
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
