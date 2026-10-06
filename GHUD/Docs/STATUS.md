# G.H.U.D. (General Hotfix Universal Downloader) — status and plan

A modular mod manager: a rock-solid **core** that downloads, stages and deploys mods for
any game, plus **game modules** that add each game's specifics. "General" covers games
without a module (a niche or new game with a mods folder). GHUD is the parent app: the
RimWorld Mod Manager's features become its RimWorld module. .NET 10 + Avalonia 12,
Windows + Linux. Self-contained in this folder, ready to move to its own repository.

**Current version:** 0.1.0, core library only (no window yet).

## Decisions (2026-10-05)
- Mods are **staged** (each unpacked in its own folder) and **deployed** into the game with
  **hard links** (copies when the drive differs). Never a virtual file system.
- GHUD manages **load order itself** where the game needs it (file priority: the lower mod
  in the list wins), merging what Mod Organizer does for games like S.T.A.L.K.E.R.
  Anomaly. Where the game sorts mods itself (RimWorld), GHUD's order only matters for
  clashing files.
- First targets: the core + a **RimWorld** instance + **General**. Then Skyrim / Fallout 4
  (plugins, FOMOD), S.T.A.L.K.E.R. Anomaly. Mount & Blade only if wanted later.
- The RimWorld Mod Manager (v3) stays as it is.

## Decisions (round 2)
- **Profiles:** each instance can have several profiles (e.g. "Vanilla+", "Testing"). One
  shared staging folder per instance; each profile has its own on/off choices and order.
  Switching profile = redeploy.
- **Staging location:** automatic, on the game's drive (`<drive>\GHUD Staging\<instance>`),
  so hard links always work; shown and changeable in the instance settings.
- **Existing mods:** when a game already has mods (by hand, Vortex, MO2), GHUD lists them
  and offers to adopt the chosen ones into staging (tagged Manual); the rest are shown as
  "not managed" and never touched.
- **Download library:** every downloaded archive is kept, so mods can be reinstalled or
  rolled back; old versions can be cleaned up after N days.

## Decisions (round 3)
- **Window:** a home screen of game icons, like a desktop: click a game to open its own
  mod screen. A new icon appears as each game module is built; "+" adds a game (General
  or a detected install). Each module can add its own panels (load order, plugins, ...).
- **Downloads:** up to 3 at once (adjustable), resuming interrupted downloads where the
  site allows it.
- **Nexus links (`nxm://`):** GHUD owns them and installs into the matching game instance
  (the game is part of the link). The RimWorld manager stops registering for them.
- **Download library:** next to staging, `<drive>\GHUD Staging\<instance>\downloads`.
- **Own repository:** when GHUD is ready it moves to its own GitHub repository, separate
  from the RimWorld Mod Manager. DownloadKit lives here (`src\DownloadKit.Core`); the
  RimWorld manager keeps its own frozen copy. Everything GHUD needs is inside this folder.

## Layout
```
GHUD\
  src\DownloadKit.Core\  downloads, link sorting, GitHub / GitLab / Nexus (any game),
                         MEGA / MediaFire / Drive / Dropbox, archives, Downloads watcher,
                         link-safe file operations, logging, colour schemes. No UI.
  src\Ghud.Core\         instances, game modules, staging, deploying, load order
  tests\Ghud.Tests\      tests for both (dotnet test in this folder)
```

## Core (`src\Ghud.Core`)
- `GameModule.cs`: `IGameModule` (Nexus game name, Steam app ID, deploy folder, how to find
  mods in an archive, load-order kind). Modules: `RimWorldModule` (About/About.xml →
  `Mods\<folder>`), `GeneralModule` (settings: deploy folder, folder-per-mod or merge,
  marker file / folder / pattern such as `SubModule.xml`, `gamedata`, `*.pak`).
- `GameInstance.cs`: one installed game; data in `%LocalAppData%\GHUD\instances\<id>\`
  (Linux `~/.local/share/GHUD/...`): `instance.json`, `mods.json`, `deployment.json`,
  `staging\`, `originals\`, `changed\`.
- `Staging.cs`: archive → unpack → module finds mods → `staging\<key>\` laid out exactly as
  in the game. Updates (same game ID, else same name) replace in place, keeping position
  and on/off; the new copy is built beside the old one and swapped in.
- `Deployer.cs`: makes the deploy folder match the enabled mods in priority order; lists
  conflicts (which mods ship the same file, who wins). Game files a mod replaces go to
  `originals\` and come back when no mod provides them; files changed in the game folder
  since deploying (edits, game updates) go to `changed\` instead of being deleted; folders
  GHUD created are removed when empty; links in staging are never followed; the manifest
  is saved even after a failure part-way.
- 14 tests (`dotnet test` in this folder): module detection, staging updates, refusing
  archives without mods, instance store, priority / reorder / conflicts, originals kept
  and restored, fall back to the next mod then the original, hard links, files replaced
  in the game folder kept, RimWorld folder deploy leaves hand-installed mods alone, links
  in staging not followed.

## Licence
GPL-3.0-or-later, Copyright (C) 2026 s235JR: `LICENSE` (official text, unchanged),
notice in `README.md`, bundled libraries in `THIRD-PARTY-NOTICES.md` (all MIT, compatible
with the GPL). Version stamped into every built file through `Directory.Build.props`.

## Roadmap
1. ✅ DownloadKit library (downloads, sources, archives, logs, colour schemes).
2. ✅ Core: instances, modules, staging, deployer, load order by priority, conflicts.
3. Window: instance picker, mod list (drag to reorder, on/off, source tags, conflicts),
   install from links / files (DownloadKit), Deploy / Undeploy, Accounts, Log, Appearance.
   Reuses UI pieces from the RimWorld manager (colour schemes, dialogs, saved accounts)
   as a `DownloadKit.Ui` library here. Licence duties for the window: an About box with
   the copyright, "no warranty" and where the licence is (GPL-3.0 section 5d); add the
   UI libraries (Avalonia, Skia, HarfBuzz, CommunityToolkit.Mvvm, ...) to
   `THIRD-PARTY-NOTICES.md`; release builds ship `LICENSE` and the notices next to the
   `.exe` and say where the source is.
4. RimWorld instance extras: Steam Workshop through anonymous SteamCMD (move SteamCmd to
   DownloadKit), update checks per source, adopting mods already in the Mods folder.
5. Download library: keep every downloaded archive for reinstall / rollback.
6. Skyrim SE/AE + Fallout 4 module: `Data\` deploy, script extenders into the game folder,
   plugin list (`plugins.txt`) and its order, FOMOD installers.
7. S.T.A.L.K.E.R. Anomaly module (gamedata priority, ModDB through the browser step).

## Open questions
- Hard links need staging on the game's drive: pick the staging folder automatically per
  drive, or ask when creating an instance?
