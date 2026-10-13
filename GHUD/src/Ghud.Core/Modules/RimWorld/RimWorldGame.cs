using System.Text;

namespace Ghud.Modules.RimWorld;

// A RimWorld instance's game-side view: its user-data folder, the active mod
// list built from a GHUD profile, and the session report / help bundle.
public sealed class RimWorldGame
{
    public const string UserDataSetting = "rimworld.userdata";
    public const string WriteModListSetting = "rimworld.writeModList";

    private readonly GameInstance _instance;
    private readonly ModSet _mods;

    public RimWorldGame(GameInstance instance, ModSet mods)
    {
        _instance = instance;
        _mods = mods;
    }

    public string UserData => _instance.Setting(UserDataSetting) ?? RimWorldMods.DefaultUserDataFolder();
    public string ModsConfig => RimWorldMods.ModsConfigPath(UserData);
    public string? Version => RimWorldMods.GameVersion(_instance.GameFolder);

    // Opt-in: Deploy also sets RimWorld's active mod list. Off by default so
    // a load order kept with another tool (RimSort, RimPy) is never re-sorted
    // unless the user asks for it.
    public bool WriteModListOnDeploy => _instance.Setting(WriteModListSetting) == "true";

    // Core and the expansions installed with the game (Data\<name>\About).
    public List<RimWorldModInfo> Official()
    {
        string data = Path.Combine(_instance.GameFolder, "Data");
        if (!Directory.Exists(data)) return new();
        return Directory.GetDirectories(data).Select(d => RimWorldMods.Read(d, Version))
                        .Where(m => m != null && m.IsOfficial).Select(m => m!).ToList();
    }

    // A staged mod's About.xml (staging\<key>\<folder>\About\About.xml).
    public RimWorldModInfo? Info(StagedMod mod)
    {
        string root = Path.Combine(_instance.StagingRoot, mod.Key);
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root).Select(d => RimWorldMods.Read(d, Version)).FirstOrDefault(m => m != null);
    }

    // Mod folders in the Mods folder GHUD doesn't manage, by package ID.
    private Dictionary<string, RimWorldModInfo> Unmanaged(HashSet<string> managedFolders)
    {
        Dictionary<string, RimWorldModInfo> map = new();
        if (!Directory.Exists(_instance.DeployFolder)) return map;
        foreach (string dir in Directory.GetDirectories(_instance.DeployFolder))
        {
            if (SafeFileSystem.IsLink(dir) || managedFolders.Contains(Path.GetFileName(dir))) continue;
            RimWorldModInfo? m = RimWorldMods.Read(dir, Version);
            if (m != null && m.PackageId.Length > 0) map.TryAdd(m.PackageId, m);
        }
        return map;
    }

    // The active list for the current profile, sorted:
    //   - Core always; expansions that are active now stay active
    //   - the profile's switched-on mods; its switched-off mods are removed
    //   - mods GHUD doesn't manage stay as they are (active or not)
    // The current ModsConfig.xml order is the preference wherever no rule decides.
    public SortResult BuildModList()
    {
        List<string> current = LoadOrder.ReadActive(ModsConfig);
        Dictionary<string, int> currentPos = current.Select((id, i) => (id, i)).GroupBy(x => x.id).ToDictionary(g => g.Key, g => g.First().i);

        Dictionary<string, RimWorldModInfo> official = Official().ToDictionary(m => m.PackageId);
        HashSet<string> managedFolders = new(StringComparer.OrdinalIgnoreCase);
        List<RimWorldModInfo> enabled = new();
        HashSet<string> disabled = new();
        int profilePos = 0;
        Dictionary<string, int> profileOrder = new();

        foreach ((StagedMod mod, ProfileEntry entry) in _mods.Current())
        {
            RimWorldModInfo? info = Info(mod);
            if (info == null) continue;
            managedFolders.Add(Path.GetFileName(info.Dir));
            if (info.PackageId.Length == 0) continue;
            if (entry.Enabled) { enabled.Add(info); profileOrder[info.PackageId] = profilePos++; }
            else disabled.Add(info.PackageId);
        }

        Dictionary<string, RimWorldModInfo> unmanaged = Unmanaged(managedFolders);
        List<RimWorldModInfo> active = new();
        if (official.TryGetValue("ludeon.rimworld", out RimWorldModInfo? core)) active.Add(core);
        foreach (RimWorldModInfo o in official.Values.Where(o => o.PackageId != "ludeon.rimworld" && currentPos.ContainsKey(o.PackageId)))
            active.Add(o);
        active.AddRange(enabled);
        foreach (string id in current)
            if (unmanaged.TryGetValue(id, out RimWorldModInfo? u) && !disabled.Contains(id) && active.All(a => a.PackageId != id))
                active.Add(u);

        // Preference: where it is in ModsConfig.xml now; new mods after, in profile order.
        List<RimWorldModInfo> preferred = active
            .GroupBy(m => m.PackageId).Select(g => g.First())
            .OrderBy(m => currentPos.TryGetValue(m.PackageId, out int p) ? p : 10_000 + profileOrder.GetValueOrDefault(m.PackageId))
            .ToList();
        return LoadOrder.Sort(preferred);
    }

    public SortResult ApplyModList(ActivityLog log)
    {
        SortResult r = BuildModList();
        LoadOrder.WriteActive(ModsConfig, r.Order.Select(m => m.PackageId));
        log.Add("SUCCESS", "RimWorld mod list set (" + _instance.Name + ", profile \"" + _mods.ActiveProfile + "\"): " + r.Order.Count + " active mod(s)." +
                           (r.Notes.Count > 0 ? " Notes: " + String.Join(" ", r.Notes) : ""));
        return r;
    }

    // Mods adopted before Workshop detection existed are tagged Manual. Tags
    // the Workshop ones as Steam (only GHUD's records change). Returns how many.
    public int DetectWorkshopOrigins(IGameModule module)
    {
        int found = 0;
        foreach (StagedMod m in _mods.Catalog.Where(m => m.Source == ModSource.Manual && String.IsNullOrEmpty(m.RemoteId)))
        {
            string root = Path.Combine(_instance.StagingRoot, m.Key);
            if (!Directory.Exists(root)) continue;
            foreach (string dir in Directory.GetDirectories(root))
            {
                if (module.OriginOf(dir) is not { } origin) continue;
                m.Source = origin.Source;
                m.RemoteId = origin.RemoteId;
                m.Url ??= origin.Url;
                found++;
                break;
            }
        }
        if (found > 0) _mods.Save();
        return found;
    }

    // ---------------- Session report / help bundle ----------------

    public sealed record SessionReport(string Summary, string Bundle);

    public SessionReport ReadSession()
    {
        string config = ModsConfig;
        List<string> active = LoadOrder.ReadActive(config);
        Dictionary<string, RimWorldModInfo> installed = new();
        foreach (RimWorldModInfo o in Official()) installed.TryAdd(o.PackageId, o);
        if (Directory.Exists(_instance.DeployFolder))
            foreach (string dir in Directory.GetDirectories(_instance.DeployFolder))
                if (RimWorldMods.Read(dir, Version) is { PackageId.Length: > 0 } m) installed.TryAdd(m.PackageId, m);

        Dictionary<string, StagedMod> managed = new();
        foreach (StagedMod s in _mods.Catalog)
            if (Info(s) is { PackageId.Length: > 0 } i) managed.TryAdd(i.PackageId, s);

        string Source(string id) => RimWorldMods.IsOfficial(id) ? "Game" : managed.TryGetValue(id, out StagedMod? s) ? ModSourceTags.Tag(s.Source) : "not managed";

        string log = RimWorldMods.FindPlayerLog(UserData);
        string logText = "";
        if (log.Length > 0)
        {
            try
            {
                using FileStream fs = new(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using StreamReader sr = new(fs, Encoding.UTF8, true);
                logText = sr.ReadToEnd();
            }
            catch (Exception ex) { logText = "[Could not read Player.log: " + ex.Message + "]"; }
        }
        int errors = logText.Split('\n').Count(l => l.Contains("Exception") || l.StartsWith("Error") || l.Contains("[Error]"));

        StringBuilder sum = new();
        sum.AppendLine("Active mods (RimWorld's load order): " + active.Count);
        int n = 1;
        foreach (string id in active)
        {
            string name = installed.TryGetValue(id, out RimWorldModInfo? m) ? m.Name : "(not installed)";
            sum.AppendLine((n++).ToString().PadLeft(4) + ". " + name + "  [" + Source(id) + "]  " + id);
        }
        List<string> missing = active.Where(id => !installed.ContainsKey(id)).ToList();
        sum.AppendLine();
        sum.AppendLine(missing.Count == 0 ? "Every active mod is installed." : "Active but not installed: " + String.Join(", ", missing));
        sum.AppendLine("Installed but not active: " + installed.Keys.Count(id => !active.Contains(id)));
        sum.AppendLine("Player.log: " + (log.Length == 0 ? "not found" : log + "  (" + errors + " error line(s))"));

        StringBuilder b = new();
        b.AppendLine("RIMWORLD MOD DIAGNOSTIC BUNDLE (G.H.U.D.)");
        b.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "   Game version: " + (Version ?? "unknown"));
        b.AppendLine();
        b.AppendLine("PURPOSE: Analyze this RimWorld mod session. Identify errors, missing dependencies, incompatibilities and " +
                     "load-order problems. Recommend an optimized ACTIVE mod order; separate mods to keep disabled, mods to remove, " +
                     "and conflicts that need a patch. Prefer the mods' own metadata over guesses.");
        b.AppendLine();
        b.AppendLine("ACTIVE LOAD ORDER");
        n = 1;
        foreach (string id in active)
            b.AppendLine((n++).ToString().PadLeft(4) + ". " + (installed.TryGetValue(id, out RimWorldModInfo? m) ? m.Name : "(not installed)") +
                         " | packageId=" + id + " | source=" + Source(id));
        b.AppendLine();
        b.AppendLine("INSTALLED MODS + METADATA");
        foreach (RimWorldModInfo m in installed.Values.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            b.AppendLine("MOD: " + m.Name + " | packageId=" + m.PackageId + " | workshop=" + (m.WorkshopId.Length > 0 ? m.WorkshopId : "-") +
                         " | source=" + Source(m.PackageId) + " | active=" + (active.Contains(m.PackageId) ? "yes" : "no"));
            b.AppendLine("  dependencies: " + Join(m.Dependencies) + "   loadAfter: " + Join(m.LoadAfter) + "   loadBefore: " + Join(m.LoadBefore));
            b.AppendLine("  incompatibleWith: " + Join(m.IncompatibleWith) + "   supportedVersions: " + Join(m.SupportedVersions));
        }
        b.AppendLine();
        b.AppendLine("RIMWORLD PLAYER.LOG");
        b.AppendLine(logText.Length == 0 ? "[Player.log not found or empty]" : logText);

        return new SessionReport(RimWorldMods.RedactUserPaths(sum.ToString()), RimWorldMods.RedactUserPaths(b.ToString()));
    }

    private static string Join(List<string> l) => l.Count == 0 ? "-" : String.Join(", ", l);
}
