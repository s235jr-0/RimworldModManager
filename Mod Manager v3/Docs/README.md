# RimWorld Mod Manager v3

Downloads, installs, updates and checks RimWorld mods without the Steam client (made for
the GOG version): Steam Workshop mods through anonymous SteamCMD, plus mods from GitHub,
GitLab / GitGud, Nexus Mods, LoversLab and file hosts. Runs on Windows and Linux.

## Building
- **Windows:** double-click `BUILD.bat`. It runs the tests, then creates
  `publish\win-x64\RimModManager.exe` (a single file, no .NET install needed to run it).
- **Linux:** `./build.sh` creates `publish/linux-x64/RimModManager`.
  SteamCMD needs 32-bit libraries: `sudo apt install lib32gcc-s1` (Debian/Ubuntu).

Building needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10` on Windows).

## Tabs
- **Install:** paste links, one per line, and press *Install All*:
  - Steam: Workshop links, IDs or collections. Current mods are skipped; downloads run in
    groups of 10.
  - GitHub / GitLab / GitGud repository links: the newest release (or tag, or latest
    commit) is downloaded. Direct file links from those sites work too.
  - Nexus Mods: needs your personal API key (Accounts). Free accounts finish the download
    with the *Mod Manager Download* button on the Nexus page; see *Handle Nexus links*.
  - LoversLab: only allows browser downloads. The page opens, you click download, and the
    manager installs the file when it lands in your Downloads folder.
  - MEGA, MediaFire, Google Drive, Dropbox and any direct `.zip` / `.7z` / `.rar` link.
  - *Install from file...* installs an archive you already have (tagged Manual).
  Archives may hold several mods; each is found by its `About/About.xml`. A mod that is
  already installed (same packageId) is updated in place, with a backup if ticked.
- **Accounts** (on the Install tab): optional GitHub / GitLab tokens, the Nexus API key and
  the Downloads folder. Saved encrypted for your user account (Windows: DPAPI; Linux: the
  system keyring through `secret-tool`, else a file only you can read). No passwords.
- **Installed / Updates:** *Scan* your Mods folder, *Check for Updates* (Steam, Git and
  Nexus mods), then *Update Selected* or *Update Outdated / Unknown*, or *Delete Selected*.
  A failed update is reverted to the previous version, shown in red, and retried next time.
  The **Source** column tags every mod: Steam, Nexus, Git, LoversLab or Manual. LoversLab
  and Manual mods can't be checked automatically.
- **RimWorld Session:** reads your load order (ModsConfig.xml), installed mods, the download
  cache and Player.log. *Export/Copy LLM Bundle* makes a diagnostic text for an AI helper,
  with your account name removed from paths.
- **Manager Log:** colored history of everything the manager did today.
- **Cleanup:** deletes old dated backups and/or cached downloads older than N days.
  *Keep backups permanently* zips chosen backups (with a `modlist.txt` inside) into
  `RWArchive` next to the backups; cleanup never deletes anything there.
- **Export:** your mod list as TXT, CSV or plain links, with each mod's source.
- **Appearance:** colour schemes. Pick *Light*, *Night* or *High contrast*, or press
  *Duplicate* to make your own: choose a colour on the left (window, mod-status and log
  colours), then use the wheel or type a hex / RGB value. Changes apply live and save
  automatically. *Export* / *Import* share schemes as small `.json` files.

SteamCMD always logs in anonymously; the manager never asks for a Steam account.

## Where things are stored
| | Windows | Linux |
|---|---|---|
| Settings, colour schemes (`themes.json`), mod sources (`sources.json`), saved accounts, logs | `%LocalAppData%\RimWorldModManager` | `~/.local/share/RimWorldModManager` |
| SteamCMD + download cache | `%LocalAppData%\WorkshopModManager` | `~/.local/share/WorkshopModManager` |
| RimWorld user data (read) | `%UserProfile%\AppData\LocalLow\Ludeon Studios\...` | `~/.config/unity3d/Ludeon Studios/...` |
| Backups | two folders above Mods, e.g. `C:\GOG Games\RWBackup_yyyyMMdd` | same rule |
| Kept (zipped) backups | `RWArchive` next to the backups | same rule |

Deleting or replacing a mod that is a link (junction/symlink) only removes the link, never
the files it points to.
