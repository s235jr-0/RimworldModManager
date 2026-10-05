# Universal Downloader — status and plan

Working title. Sister project of the RimWorld Mod Manager: a general download manager,
with optional **game profiles** layered on top that add mod installing for any game.
Same stack (.NET 10 + Avalonia 12, Windows + Linux) and the same shared code
(`..\..\Shared\`, the DownloadKit library).

**Current state:** planning. Step 1 (the shared library) is done; no app yet.

## Layers
1. **Download manager (any file):** paste links, one per line → a download queue with
   progress → saved to a chosen folder, optionally unpacked → a history list showing each
   item's source tag, version, date and size → *Check for updates* for links that have
   versions (GitHub / GitLab releases and tags, Nexus files) → download the update.
   Link types: everything the RimWorld manager handles (GitHub, GitLab / GitGud, Nexus,
   MEGA, MediaFire, Google Drive, Dropbox, direct files, browser-only sites through the
   Downloads-folder watcher).
2. **Game profiles (optional):** a profile describes one game:
   - name, mods folder, backup folder
   - how a mod is recognised: a marker file pattern (`About/About.xml` for RimWorld,
     `*.esp` / `*.esm` for Bethesda games, `modinfo.json`, ...) or "every top-level folder"
   - Nexus game domain (`rimworld`, `skyrimspecialedition`, ...)
   - Steam app ID, if the game's Workshop allows anonymous SteamCMD downloads (many
     games don't; RimWorld does)
   Installing = download → unpack → find mods → copy into the mods folder with the same
   staging / backup / revert-on-failure protection as the RimWorld manager.

## Roadmap
1. ✅ **Shared library** `Shared\src\DownloadKit.Core`: link sorting, HTTP downloads,
   GitHub / GitLab / Nexus (any game), MEGA / MediaFire / Drive / Dropbox, archives
   (zip-slip safe), Downloads-folder watcher, link-safe file operations, logging, colour
   schemes, secret-store interface, `ClientInfo` (app name/version sent to websites).
   The RimWorld manager runs on it; its 98 tests pass unchanged.
2. **Shared UI library** `DownloadKit.Ui`: colour schemes + Appearance editor, dialogs,
   encrypted secret store, single-instance pipe, `nxm://` handler.
3. **App skeleton:** tabs Downloads (queue), History, Accounts, Log, Appearance.
4. **History + update checks**, saved to the app's own data folder.
5. **Game profiles + generic installer** (move the staging / backup / revert code out of
   the RimWorld installer into DownloadKit).
6. **Steam Workshop per profile** (SteamCMD, anonymous only).

## Open questions
- Final name.
- Default download folder; one folder per source / per profile?
- Resume of interrupted downloads, and how many downloads at once.
- `nxm://` links can only open one program: if both apps are installed, which one owns
  them? (Option: the Universal Downloader owns them and hands RimWorld links over.)
