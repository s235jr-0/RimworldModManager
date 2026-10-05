using System.Text.Json;

namespace RimModManager.Core;

// state.json: saved update timestamps, failed-update flags and settings.
// Reads and writes the same JSON as v2.
public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _filePath;

    // Save runs from both the UI and background work; serialising while
    // another thread edits a dictionary would throw.
    private readonly object _sync = new();

    public StateData Data { get; private set; } = new();

    public StateStore(string filePath, string? legacyFilePath = null)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        Load(legacyFilePath);
    }

    private void Load(string? legacyFilePath)
    {
        try
        {
            string source = _filePath;

            // One-time migration of v1.7's adopted update timestamps.
            if (!File.Exists(source) && legacyFilePath != null && File.Exists(legacyFilePath))
                source = legacyFilePath;

            if (!File.Exists(source)) return;

            StateData? d = JsonSerializer.Deserialize<StateData>(File.ReadAllText(source), JsonOptions);
            if (d?.InstalledSteamTimes == null) return;

            d.PersistentModsFolder ??= "";
            d.FailedUpdates ??= new Dictionary<string, string>();
            if (d.CleanupOlderThanDays < 1) d.CleanupOlderThanDays = 14;

            Data = d;
        }
        catch
        {
            Data = new StateData();
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            // Write a temp file, then swap it in, so a crash mid-save can't
            // leave a half-written state.json (which Load would discard,
            // losing every saved update timestamp).
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Data, JsonOptions));
            File.Move(temp, _filePath, overwrite: true);
        }
    }

    public long Get(string workshopId)
    {
        lock (_sync)
            return Data.InstalledSteamTimes.TryGetValue(workshopId, out long value) ? value : 0;
    }

    public void Set(string workshopId, long steamTime)
    {
        lock (_sync)
            Data.InstalledSteamTimes[workshopId] = steamTime;
    }

    public void Remove(string workshopId)
    {
        if (String.IsNullOrWhiteSpace(workshopId)) return;

        lock (_sync)
        {
            Data.InstalledSteamTimes.Remove(workshopId);
            Data.FailedUpdates.Remove(workshopId);
        }
    }

    public void MarkFailed(string workshopId, string reason)
    {
        lock (_sync)
            Data.FailedUpdates[workshopId] = reason ?? "";
    }

    public void ClearFailed(string workshopId)
    {
        lock (_sync)
            Data.FailedUpdates.Remove(workshopId);
    }

    // null = the last update did not fail.
    public string? GetFailed(string workshopId)
    {
        lock (_sync)
            return Data.FailedUpdates.TryGetValue(workshopId, out string? reason) ? reason : null;
    }
}
