# G.H.U.D. (General Hotfix Universal Downloader) — status and plan

A modular mod manager: a rock-solid **core** that downloads, stages and deploys mods for
any game, plus **game modules** that add each game's specifics. "General" covers games
without a module (a niche or new game with a mods folder). GHUD is the parent app: the
RimWorld Mod Manager's features become its RimWorld module. .NET 10 + Avalonia 12,
Windows + Linux. Self-contained in this folder, ready to move to its own repository.

**Current version:** 0.3.0: the core matches the RimWorld Mod Manager's general features
(updates, rollback, library cleanup, export, Nexus links, LoversLab pickup, colour schemes,
adopting existing mods). Next: the RimWorld module.

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
  src\Ghud.Core\         instances, game modules, profiles, staging, download library,
                         link downloads, deploying, load order, conflicts
  src\Ghud.App\          the window (Avalonia 12); builds GHUD.exe
  tests\Ghud.Tests\      tests (dotnet test in this folder)
  licenses\             full texts of bundled non-MIT licences (ship with every build)
```

## Core (`src\Ghud.Core`)
- `GameModule.cs`: `IGameModule` (Nexus game name, Steam app ID, deploy folder, how to find
  mods in an archive, load-order kind). Modules: `RimWorldModule` (About/About.xml →
  `Mods\<folder>`), `GeneralModule` (settings: deploy folder, folder-per-mod or merge,
  marker file / folder / pattern such as `SubModule.xml`, `gamedata`, `*.pak`).
- `GameInstance.cs`: one installed game; data in `%LocalAppData%\GHUD\instances\<id>\`
  (Linux `~/.local/share/GHUD/...`): `instance.json`, `mods.json`, `profiles.json`,
  `deployment.json`, `originals\`, `changed\`. Staged mods (`staging\`) and the download
  library (`downloads\`) live in the storage folder: the data folder, or
  `<drive>\GHUD Staging\<id>` when the game is on another drive (hard links need one
  drive). Removing a game can keep or delete its staged mods.
- `ModSet.cs`: the staged mods (shared) plus profiles, each with its own order and on/off;
  new mods join every profile, enabled only in the active one.
- `LinkDownloader.cs`: GitHub, GitLab / GitGud, Git file links, Nexus (Premium downloads
  directly; free accounts get a browser step), `nxm://` links, MEGA, MediaFire, Drive,
  Dropbox, direct files; LoversLab is a browser step. Archives go to the library.
- `Staging.cs`: archive → unpack → module finds mods → `staging\<key>\` laid out exactly as
  in the game. Updates (same game ID, else same name) replace in place, keeping position
  and on/off; the new copy is built beside the old one and swapped in.
- `Deployer.cs`: makes the deploy folder match the enabled mods in priority order; lists
  conflicts (which mods ship the same file, who wins). Game files a mod replaces go to
  `originals\` and come back when no mod provides them; files changed in the game folder
  since deploying (edits, game updates) go to `changed\` instead of being deleted; folders
  GHUD created are removed when empty; links in staging are never followed; the manifest
  is saved even after a failure part-way.
- 84 tests (`dotnet test` in this folder): DownloadKit (links, Git / Nexus parsing,
  archives, link-safe files, colour schemes, VDF, watcher), module detection, staging
  updates, refusing archives without mods, instance store, profiles (joining, switching,
  names, removal), download library, storage drive, removing a game while keeping its
  mods, priority / reorder / conflicts, originals kept and restored, fall back to the next
  mod then the original, hard links, files replaced in the game folder kept, RimWorld
  deploy leaves hand-installed mods alone, links in staging not followed.

## Window (`src\Ghud.App`, v0.2.0)
- Home screen: one tile per game (coloured initials, no game artwork), "+ Add game",
  Accounts, Log folder, About (GPL notice, section 5d). Colour schemes from DownloadKit
  apply (no Appearance editor yet).
- Add game: RimWorld (finds the usual install folders) or General (mods folder, layout,
  marker, Nexus name).
- Game screen: profile picker (New / Rename / Delete), mod list in priority order (on/off,
  source tag, version, conflicts "wins N / loses N"), Top / Up / Down / Bottom / Remove,
  details with each conflicting file, Install links (paste) / Install from file, Deploy /
  Undeploy with a "press Deploy" reminder, open game / staging / download folders,
  Remove game.
- Accounts: GitHub / GitLab tokens, Nexus API key + Check key, saved encrypted.
- Tested through the real window (2026-10-06, Windows): added a General game (merge,
  marker `gamedata`) on a test folder, installed two archives through the Open dialog,
  conflicts shown, Deploy placed the winner as hard links and kept the game's file,
  Undeploy restored the folder exactly, Remove game deleted everything.

## Added in 0.3.0 (parity with the RimWorld Mod Manager)
| RimWorld Mod Manager | GHUD |
|---|---|
| Check for updates / Update selected / outdated | Same, per source (GitHub, GitLab, Nexus); Update column |
| Backup + revert on a failed update | Staging swaps only when complete; every archive stays in the library; **Roll back** to an earlier one |
| Cleanup (backups, cache after N days) | **Clean up library** after N days (preview first); never a mod's current archive or a mod marked **Keep all versions** |
| Keep backups permanently | **Keep all versions** per mod |
| Export TXT / CSV / links | Same for the active profile, plus copy links |
| nxm:// links, single window | Same; GHUD owns them (opt-in) and routes each link to the game with that Nexus name |
| LoversLab pick-up from Downloads | Same; waiting panel; Downloads folder under Accounts (`settings.json`) |
| Appearance tab | Appearance page (same editor and `themes.json` format) |
| — | **Adopt existing mods** (folder-per-mod games): offered on a game's first visit; moved into staging and deployed straight back |
| — | **Remove game** asks: leave the mods in the game (hard-linked files survive) or take them out; game files mods had replaced are never deleted (moved to `kept originals`) |

Tested through the window (2026-10-06): adoption on a fake RimWorld folder (offered, moved,
deployed back), install-links failure reported cleanly, Check updates, Appearance page,
Remove game. Not tested live: downloads from GitHub (its servers were unreachable from
this computer that day; the same code worked the day before), Nexus, LoversLab, MEGA.

## Licence
GPL-3.0-or-later, Copyright (C) 2026 s235JR: `LICENSE` (official text, unchanged),
notice in `README.md` and the About box, bundled components in `THIRD-PARTY-NOTICES.md`
(MIT, BSD-3-Clause, "Old MIT", SIL OFL 1.1 for the Inter font: all GPL-compatible), full
non-MIT texts in `licenses\`. Builds copy `LICENSE`, the notices and `licenses\` next to
`GHUD.exe`. Version and copyright stamped into every built file (`Directory.Build.props`).

## Roadmap
1. ✅ DownloadKit library (downloads, sources, archives, logs, colour schemes).
2. ✅ Core: instances, modules, staging, deployer, load order by priority, conflicts.
3. ✅ Window (v0.2.0): home tiles, add game, profiles, mod list, install, deploy, accounts,
   About + licence files.
4. ✅ Core parity with the RimWorld Mod Manager (0.3.0): see the table above.
5. **Next: the RimWorld module** (the RimWorld Mod Manager's RimWorld-only features):
   Steam Workshop through anonymous SteamCMD (IDs, links, collections; grouped downloads;
   update checks), the session reader (ModsConfig.xml load order, Player.log, LLM bundle),
   Workshop-ID detection of existing mods.
6. Before a first release: download queue (3 at once, resume, progress per item),
   drag-and-drop ordering, Linux test, release build script (single-file GHUD.exe +
   LICENSE + notices + licenses), README user guide, then its own GitHub repository.
7. Skyrim SE/AE + Fallout 4 module: `Data\` deploy, script extenders into the game folder,
   plugin list (`plugins.txt`) and its order, FOMOD installers.
8. S.T.A.L.K.E.R. Anomaly module (gamedata priority, ModDB through the browser step).

## Open questions
