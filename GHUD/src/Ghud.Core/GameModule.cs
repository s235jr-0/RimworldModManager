namespace Ghud;

// How a game decides which mod wins when two of them ship the same file.
public enum LoadOrderKind
{
    // The game sorts mods itself (RimWorld: ModsConfig.xml). GHUD's order
    // only matters if two mods install the very same file.
    GameManaged,

    // File priority: mods lower in GHUD's list overwrite the files of mods
    // above them (S.T.A.L.K.E.R. Anomaly gamedata, loose-file games).
    FilePriority,
}

// One mod found inside an unpacked archive.
//   SourceDir:    the folder holding the mod's files.
//   TargetPrefix: where SourceDir's contents go, relative to the instance's
//                 deploy folder ("" = straight into it, "MyMod" = a subfolder).
//   Id:           a stable identity (RimWorld packageId) used to recognise
//                 updates of the same mod; null when the game has none.
public sealed record ModCandidate(string Name, string? Id, string SourceDir, string TargetPrefix);

// Everything GHUD needs to know about one game. Built-in modules live in
// Modules\; "General" is configured by the user instead of coded.
public interface IGameModule
{
    // Short, stable key: "rimworld", "general", ...
    string Id { get; }

    string DisplayName { get; }

    // Nexus Mods game name in URLs (null = not on Nexus).
    string? NexusDomain { get; }

    // Steam app ID for anonymous SteamCMD Workshop downloads (null = none).
    string? SteamAppId { get; }

    LoadOrderKind LoadOrder { get; }

    // Deploy folder relative to the game folder ("Mods", "Data", "" = the game folder).
    string DefaultDeploySubfolder { get; }

    // Likely install folders on this computer (Steam / GOG defaults); may be empty.
    IEnumerable<string> GuessGameFolders();

    // True when the folder looks like this game's install folder.
    bool LooksLikeGameFolder(string folder);

    // The mods inside an unpacked archive. Empty = this archive has no mod
    // this game recognises.
    List<ModCandidate> FindMods(string unpackedRoot);
}
