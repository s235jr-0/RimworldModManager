# Shared (DownloadKit)

Code used by both the RimWorld Mod Manager and the Universal Downloader.

- `src/DownloadKit.Core`: link sorting, HTTP downloads, GitHub / GitLab / Nexus (any
  game), MEGA / MediaFire / Google Drive / Dropbox, archives (zip / 7z / rar / tar,
  zip-slip safe), Downloads-folder watcher, link-safe file operations, logging, colour
  schemes. No UI and nothing game-specific.

Apps set `ClientInfo.Name` / `ClientInfo.Version` at startup (sent as the User-Agent and
to the Nexus API). The RimWorld manager's tests (`Mod Manager v3`: `dotnet test`) cover
this code for now.
