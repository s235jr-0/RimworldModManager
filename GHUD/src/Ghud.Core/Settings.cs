using System.Text.Json;

namespace Ghud;

// App-wide settings (settings.json in GHUD's data folder).
public sealed class GhudSettings
{
    // Watched for downloads finished in the browser (LoversLab).
    public string? DownloadsFolder { get; set; }

    private string _file = "";

    public static GhudSettings Load(string file)
    {
        GhudSettings s = new();
        try
        {
            if (File.Exists(file))
                s = JsonSerializer.Deserialize<GhudSettings>(File.ReadAllText(file), InstanceStore.Json) ?? new();
        }
        catch { /* a damaged file starts fresh */ }
        s._file = file;
        if (String.IsNullOrWhiteSpace(s.DownloadsFolder)) s.DownloadsFolder = GhudPaths.DefaultDownloadsFolder();
        return s;
    }

    public void Save() => JsonFile.WriteAtomic(_file, JsonSerializer.Serialize(this, InstanceStore.Json));
}
