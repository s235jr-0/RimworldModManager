# RimWorld Mod Manager v3 — status

**Current version:** v3.3.0
- v3.0.0: rewrite of the Windows-only v2.3.0 (WinForms, now retired) in .NET 10 +
  Avalonia 12, to run on Windows and Linux. Same tabs, same behavior; SteamCMD anonymous-only.
- v3.1.0: **colour schemes** (Appearance tab), described below.
- v3.2.0: **mods from other sites** and **source tags**, described below.
- v3.3.0: new cubist app icon (replaces the RimWorld storyteller art); Cleanup tab can
  **zip backups to keep them permanently** (`Core/BackupArchive.cs` → `RWArchive`, with a
  `modlist.txt`; links skipped; written to a `.partial` file first). 3 tests.

## Other sources (v3.2.0)
- Core in `Core/Sources/`:
  - `ModSources.cs`: `ModSource` (Steam / Nexus / Git / LoversLab / Manual) and
    `SourceRegistry` → `sources.json` (folder, packageId, source, URL, version, dates).
  - `Downloads.cs`: `LinkClassifier` sorts each pasted line; `HttpDownloader` refuses web
    pages; `FileHosts` handles MEGA, MediaFire, Google Drive and Dropbox.
  - `GitSources.cs`: GitHub (release asset → release source → latest commit) and GitLab /
    GitGud (newer of latest release and latest tag). Anonymous; tokens optional.
  - `NexusSource.cs`: Nexus API v1 with the user's API key. Free accounts can't get
    download links from the API without a click, so the Files page opens and the
    *Mod Manager Download* button sends an `nxm://` link back to the manager.
  - `ArchiveInstaller.cs`: extracts zip / 7z / rar / tar (SharpCompress, zip-slip checked),
    finds every mod by `About/About.xml`, updates the folder with the same packageId in
    place (backup + revert on failure), records the source.
  - `ExternalSources.cs`: install / check / update for every source. LoversLab answers
    403 to anything but a browser, so it is a browser step: the page opens and
    `DownloadWatcher` picks the archive up from the Downloads folder.
- App:
  - `Services/SecretStore.cs`: tokens and the API key, encrypted (Windows DPAPI; Linux
    `secret-tool`, else a 600-permission file). No usernames or passwords are stored:
    Nexus has no password login for other programs, and LoversLab doesn't allow
    non-browser downloads anyway.
  - `Services/NexusLinks.cs`: single-instance pipe (a second start hands its `nxm://` link
    to the running window) and opt-in registration as the `nxm` handler (Windows registry
    under HKCU; Linux `.desktop` file + `xdg-mime`). Opt-in because Mod Organizer / Vortex
    may own it.
  - Source tag column on Installed and Session tabs; source in exports and the LLM bundle.
  - `Program.cs` writes unhandled errors to the Manager Log.
- Tested on Windows (2026-09-25 / 10-05): 95 automated tests (link sorting, GitHub /
  GitLab / Nexus parsing, 7z and rar fixtures, zip-slip, update in place, Steam → other
  source). Live: Harmony from GitHub through the real window into a temporary Mods
  folder (installed, then updated in place with backup, tagged Git, check says Current);
  GitGud RJW resolves to its newest tag; LoversLab link opens the page; a web-page link
  is refused.
- **Not yet tested:** Nexus (needs the user's API key and *Handle Nexus links*), MEGA,
  MediaFire, Google Drive, Dropbox, the LoversLab Downloads-folder pickup, and all of it
  on Linux.

## Colour schemes (v3.1.0)
- 19 colour roles in three groups (window, mod status, Manager Log), defined in
  `Core/ColorSchemes.cs` (`ColorRoles`), plus built-in *Light* / *Night* / *High contrast*.
- `ColorSchemeStore` → `themes.json`: selected scheme + custom schemes. Built-ins are
  read-only. Duplicate / rename / delete / import / export. Unknown roles are dropped and
  missing roles filled from Light, so older/newer files keep working.
- Status text colour comes from `ColorRoles.ForStatus` (success / working / attention /
  error); log line colour from `ColorRoles.ForLogCategory`. Both are applied through
  `Tag`-based styles in `App.axaml`, so they switch live.
- `App/Services/ThemeManager.cs`: Fluent keeps ~700 brushes with frozen colour copies, so
  at startup each brush is matched to the palette colour it came from. On every change,
  replacements go into one app-level resource dictionary, swapped as a single change. That
  keeps it live and fast while dragging the wheel. The Windows title bar goes dark for dark
  schemes (DWM attribute, Windows only).
- Editor: colour ring + brightness slider (Avalonia ColorPicker primitives) and always-visible
  Hex / R / G / B boxes, all in sync. Saves are debounced (600 ms).
- Tested on Windows through the real UI (2026-09-23/24):
  - switching all presets live; status colours after a Steam check; readable text and
    ticks on light accents
  - Duplicate, Rename (its dialog), Export (Windows Save dialog), Import (Windows Open
    dialog; a clashing name becomes "(2)"), Delete (Yes deletes, No keeps; deleting the
    selected scheme falls back to Light)
  - typing hex colours: the window updates live and changes persist across a restart
  - press-and-drag on the colour ring and the brightness slider changes the colour live
    and saves it
  - 24 unit tests for the store and mappings

## Layout
```
Mod Manager v3\
  src\RimModManager.Core\    all logic, no UI (Windows + Linux)
  src\RimModManager.App\     Avalonia window; ViewModels\MainViewModel.<Tab>.cs per tab
  tests\RimModManager.Tests\ xunit v3 tests for the core
  BUILD.bat / build.sh       tests + single-file build into publish\
```
Commands (from this folder): `dotnet test`, `dotnet run --project src/RimModManager.App`.

## What changed from v2 (under the hood)
| v2 (Windows only) | v3 |
|---|---|
| WinForms, C# 5, `csc.exe` | Avalonia 12 (Fluent theme), .NET 10, C# 14 |
| robocopy copies / backups | `SafeFileSystem.CopyDirectory` (skips links inside) |
| Win32 `SafeDelete` | `SafeFileSystem.DeleteDirectory`, same link-safe rules on both OSes |
| `steamcmd.exe` only | Also `steamcmd_linux.tar.gz` → `steamcmd.sh`; `force_install_dir` pins the cache |
| Optional Steam login | **Removed**: SteamCMD is always anonymous, by design |
| JavaScriptSerializer / WebClient | System.Text.Json / HttpClient (same `state.json` format) |
| Redaction of `C:\Users\<name>` | Also `/home/<name>` and `/Users/<name>` |
| `About\About.xml` exact match | Matched case-insensitively on Linux |

Small UI differences: no Steam username box (anonymous only); one shared
"Backup before replacement" checkbox (also shown on the Install tab).

Settings, logs and the SteamCMD cache are **shared with v2** (same folders), so switching
between them loses nothing.

## Verified (2026-09-23, Windows)
- 56 automated tests pass (links/junctions, 384-char paths, locked files, reverts,
  restore from backup, state.json compatibility, cleanup, manifests, redaction, CSV).
- Live SteamCMD: grouped download into the existing v2 cache, versions verified.
- End-to-end install into a temporary Mods folder: fresh install, rerun skips, forced
  failure is retried, flagged, and keeps the previous version.
- The app itself (driven through UI Automation, read-only actions): Scan (185 mods), Check
  Steam, Read Current Session (191 records, DLCs recognized), Cleanup sizes (950 MB
  backups, 1.55 GB cache), Manager Log colors.
- Release builds: Windows single-file exe (100 MB) runs; Linux single-file binary (95 MB)
  builds.

## Not yet verified
- **Anything on Linux** (not tested on a Linux machine yet): SteamCMD download + extraction, paths, symlink handling. The core tests are
  ready to run there: `dotnet test`.
- Update / Delete / Install through the new window against the real Mods folder
  (the same core code was tested end-to-end, but not via these buttons).

## Known notes
- First Steam API request takes 5–9 s on this connection (same in v2); later ones ~1 s.
- On Linux, SteamCMD needs 32-bit libraries (Debian/Ubuntu: `sudo apt install lib32gcc-s1`).

## Next
1. To test: Nexus key + *Handle Nexus links* with a free account, a LoversLab
   download, MEGA / MediaFire links.
2. Later: tell scenarios apart from mods; compare mod versions across kept backups.
3. Test on Linux (install WSL, or on a Linux machine): `./build.sh`, then the app.
