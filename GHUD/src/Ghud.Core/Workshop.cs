using DownloadKit.Steam;

namespace Ghud;

// Steam Workshop mods through anonymous SteamCMD (never a Steam account),
// for any game module that has a Steam app ID.
public sealed class WorkshopInstaller
{
    // Mods per SteamCMD run: each run pays startup + login once; 10 cuts that
    // overhead while keeping a failed group small.
    public const int BatchSize = 10;

    public sealed record Result(List<StagedMod> Installed, List<string> Skipped, Dictionary<string, string> Failed);

    private readonly GameInstance _instance;
    private readonly IGameModule _module;
    private readonly ModSet _mods;
    private readonly Staging _staging;
    private readonly SteamCmd _steam;
    private readonly ActivityLog _log;

    public WorkshopInstaller(GameInstance instance, IGameModule module, ModSet mods, Staging staging, SteamCmd steam, ActivityLog log)
    {
        _instance = instance;
        _module = module;
        _mods = mods;
        _staging = staging;
        _steam = steam;
        _log = log;
    }

    private string AppId => _module.SteamAppId ?? throw new Exception(_module.DisplayName + " has no Steam Workshop support in GHUD.");

    private StagedMod? Staged(string workshopId) =>
        _mods.Catalog.FirstOrDefault(m => m.Source == ModSource.Steam && m.RemoteId == workshopId);

    // IDs, Workshop links or collections. Mods already staged and current are
    // skipped unless `force`.
    public Result Install(IEnumerable<string> rootIds, bool force, Action<string> status)
    {
        string appId = AppId;
        List<StagedMod> installed = new();
        List<string> skipped = new();
        Dictionary<string, string> failed = new();

        List<WorkshopDetails> items = SteamApi.ResolveItems(rootIds, status);
        List<WorkshopDetails> wanted = new();
        foreach (WorkshopDetails d in items)
        {
            if (d.AppId != appId) { failed[d.Id] = (d.Title.Length > 0 ? d.Title : d.Id) + " is for another game (Steam app " + d.AppId + ")."; continue; }

            StagedMod? s = Staged(d.Id);
            if (!force && s != null && s.RemoteUpdated >= d.TimeUpdated && d.TimeUpdated > 0 && Directory.Exists(Path.Combine(_instance.StagingRoot, s.Key)))
            {
                skipped.Add(s.Name);
                continue;
            }
            wanted.Add(d);
        }

        for (int i = 0; i < wanted.Count; i += BatchSize)
        {
            List<WorkshopDetails> group = wanted.Skip(i).Take(BatchSize).ToList();
            status("Steam Workshop: downloading " + (i + 1) + "-" + (i + group.Count) + " of " + wanted.Count + "...");
            Dictionary<string, string> groupFailures;
            try { groupFailures = _steam.Download(group, status); }
            catch (Exception ex) { groupFailures = group.ToDictionary(d => d.Id, _ => ex.Message); }

            foreach (WorkshopDetails d in group)
            {
                if (groupFailures.TryGetValue(d.Id, out string? why)) { failed[d.Id] = (d.Title.Length > 0 ? d.Title : d.Id) + ": " + why; continue; }
                try
                {
                    StagedMod m = _staging.InstallFolder(_steam.CachePath(appId, d.Id),
                        new ArchiveOrigin(ModSource.Steam, WorkshopIds.Url(d.Id), d.Id, UnixTime.Format(d.TimeUpdated), d.TimeUpdated),
                        Staged(d.Id)?.Key, d.Id, status);
                    installed.Add(m);
                }
                catch (Exception ex) { failed[d.Id] = (d.Title.Length > 0 ? d.Title : d.Id) + ": " + ex.Message; }
            }
        }

        foreach (string f in failed.Values) _log.Add("ERROR", "[Steam] " + f);
        return new Result(installed, skipped, failed);
    }

    // One request to Steam for all of them (in chunks), not one per mod.
    public Dictionary<string, UpdateResult> CheckUpdates(IEnumerable<StagedMod> steamMods, Action<string> status)
    {
        List<StagedMod> mods = steamMods.Where(m => m.Source == ModSource.Steam && !String.IsNullOrEmpty(m.RemoteId)).ToList();
        Dictionary<string, UpdateResult> result = new();
        if (mods.Count == 0) return result;

        Dictionary<string, WorkshopDetails> details = SteamApi.GetDetailsWithRetry(mods.Select(m => m.RemoteId!), "update check", 3, status);
        foreach (StagedMod m in mods)
        {
            if (!details.TryGetValue(m.RemoteId!, out WorkshopDetails? d) || d.Result != 1)
                result[m.Key] = new UpdateResult(UpdateState.Missing, "");
            else if (m.RemoteUpdated <= 0)
                result[m.Key] = new UpdateResult(UpdateState.Unknown, UnixTime.Format(d.TimeUpdated));
            else
                result[m.Key] = new UpdateResult(d.TimeUpdated > m.RemoteUpdated ? UpdateState.UpdateAvailable : UpdateState.Current,
                                                 UnixTime.Format(d.TimeUpdated));
        }
        return result;
    }

    // SteamCMD's cached copies not used for `days`. Staged mods keep their
    // own copy, so this only costs a full download next update.
    public (int Count, long Bytes) CleanCache(int days, bool dryRun)
    {
        DateTime cutoff = DateTime.Now.AddDays(-days);
        int count = 0;
        long bytes = 0;
        foreach (SteamCmd.CachedItem item in _steam.GetCachedItems(AppId).Where(c => c.LastUsed < cutoff))
        {
            count++;
            bytes += SafeFileSystem.GetDirectorySize(item.Path);
            if (!dryRun) SafeFileSystem.DeleteDirectory(item.Path);
        }
        if (!dryRun && count > 0)
            _log.Add("DELETE", "SteamCMD cache cleanup: " + count + " item(s), " + Sizes.Format(bytes) + " freed.");
        return (count, bytes);
    }

    public (int Count, long Bytes) CacheSize()
    {
        List<SteamCmd.CachedItem> items = _steam.GetCachedItems(AppId);
        return (items.Count, items.Sum(i => SafeFileSystem.GetDirectorySize(i.Path)));
    }
}
