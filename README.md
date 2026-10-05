# RimWorld Tools & Modding

RimWorld tooling and small mods.

## What's where

```
Mod Manager v3/                     RimWorld Mod Manager: Windows + Linux, .NET 10 + Avalonia
├─ src/RimModManager.Core/          All logic, no UI
├─ src/RimModManager.App/           The window
├─ tests/                           Automated tests (run: dotnet test)
├─ BUILD.bat / build.sh             Tests + single-file build into publish/
└─ Docs/                            README (user guide) + STATUS

Shared/                             DownloadKit: download code shared by both apps
└─ src/DownloadKit.Core/            Links, GitHub/GitLab/Nexus, file hosts, archives, logging

Universal Downloader/               Sister project (planned): download manager + game profiles
└─ Docs/STATUS.md                   Plan and roadmap

Mods/                               Small mods, one folder each
├─ Rimatomics Diplomatic Credit/
│  ├─ BuildFiles/                   Source code, About.xml template, Harmony finder
│  ├─ BUILD_TO_READY_FOLDER.bat     Double-click to compile the mod
│  └─ Docs/                         README + STATUS
└─ Rimatomics Fuel Pool Bill Counter/
   ├─ BuildFiles/
   ├─ BUILD_TO_READY_FOLDER.bat
   ├─ CHECK_AFTER_LOAD.bat          Read-only log checker after loading the game
   └─ Docs/                         README + STATUS
```

Each project's **`Docs/STATUS.md`** shows what works, what's untested and what's next.

## RimWorld Mod Manager

Installs, updates and checks RimWorld mods without the Steam client (made for the GOG
version): Steam Workshop mods through anonymous SteamCMD, plus GitHub, GitLab / GitGud,
Nexus Mods, LoversLab, MEGA, MediaFire, Google Drive, Dropbox and direct links. Every mod
is tagged with where it came from. See [`Mod Manager v3/Docs/README.md`](Mod%20Manager%20v3/Docs/README.md).

## Conventions

- Mods use author `s235JR` and package IDs starting with `s235jr.`.
- Release zips contain one top-level folder.
- Build output (`.exe`, `.dll`, `READY_TO_DROP/`, `bin/`, `obj/`, `publish/`) is not kept
  in version control; the build scripts recreate it.

## Toolchain

- **Mod Manager:** .NET 10 SDK, Avalonia 12, xunit v3.
- **Mods:** the Windows built-in .NET Framework C# compiler
  (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`), which only understands
  about C# 5. The mods stay on .NET Framework because that's what RimWorld runs.
- RimWorld 1.6; the build scripts look in the default GOG folder `C:\GOG Games\RimWorld`.
- Harmony 2.x: Workshop mod 2009463077, found automatically by `Find_Harmony2.ps1`.
