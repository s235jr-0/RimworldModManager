# G.H.U.D. — General Hotfix Universal Downloader

A modular mod manager for any game: a solid core that downloads, stages and deploys mods
(hard links, load order by priority, conflicts, safe removal), plus game modules for
each game's specifics. Windows and Linux, .NET 10.

**Status:** early development (v0.2.0). Works today: add games (RimWorld, or any game
through "General"), profiles, installing from links or archives, priority order with
conflicts, Deploy / Undeploy. Plan, decisions and roadmap: [`Docs/STATUS.md`](Docs/STATUS.md).

## Building and running
Needs the .NET 10 SDK. From this folder:
- `dotnet test` runs the tests
- `dotnet run --project src/Ghud.App` starts G.H.U.D.

## License

Copyright (C) 2026 s235JR

This program is free software: you can redistribute it and/or modify it under the terms
of the GNU General Public License as published by the Free Software Foundation, either
version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU General Public License for more details.

You should have received a copy of the GNU General Public License along with this
program (`LICENSE`). If not, see <https://www.gnu.org/licenses/>.

Third-party components and their licences: [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).
