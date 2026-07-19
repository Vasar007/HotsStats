# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**HotsStats** — a Windows desktop overlay for Blizzard's *Heroes of the Storm*. During the
loading screen and in-game it shows each player's MMR, games played, hero/map win rates, and a
profile link (mimicking the built-in Shift-Tab stats panel). This checkout is the
`hotsapi/HotsStats` fork of the original `poma/HotsStats`. The project is **archived**; the
current work is to revive it for the latest game version.

> Known-dead dependency: the stats source (HotsLogs.com) was shut down in ~2022. Both the JSON
> API and the scraped HTML profile pages in `StatsFetcher/ProfileFetcher.cs` /
> `StatsFetcher/FileProcessor.cs` are gone. A replacement source (e.g. a crawler/parser against
> https://www.heroesprofile.com/) is expected. See "Data Source" below.

## Repo Layout

The solution `HotsStats.sln` (Visual Studio 14 / 2015 format) is at the repo root. All commands
below assume you are at the repo root.

| Path                                          | What lives there                                                                 |
|-----------------------------------------------|----------------------------------------------------------------------------------|
| `StatsFetcher/`                               | Core library: file monitoring, battlelobby parsing, external stats fetch/parse   |
| `StatsDisplay/`                               | WPF app (`OutputType=WinExe`, assembly name `HotsStats`): UI, settings, updater  |
| `Heroes.ReplayParser/` (git submodule)        | `Heroes.ReplayParser` + `MpqTool` projects: parse `.StormSave`/`.StormReplay`     |
| `HotsStats.sln`                               | Solution tying the four projects together                                        |
| `.appveyor.yml`                               | Legacy AppVeyor CI (VS2017 image, Squirrel release + GitHub deploy)              |

**The `Heroes.ReplayParser` submodule is required and is not committed as source.** `.gitmodules`
uses a relative URL (`../../poma/Heroes.ReplayParser.git`). You **must** initialize it before the
solution will build:

```shell
git submodule update --init --recursive
```

## Stack & Versions

- **Runtime:** .NET Framework (classic, non-SDK `.csproj`, `packages.config` NuGet, MSBuild
  `ToolsVersion=14.0`).
- **Target framework (both first-party projects):** `v4.5` (`net45`). `StatsFetcher.csproj` and
  `StatsDisplay.csproj` both set `<TargetFrameworkVersion>v4.5</TargetFrameworkVersion>`.
  `App.config` pins `.NETFramework,Version=v4.5`; the Squirrel releasify step passes
  `--framework-version=net462`, so end users need .NET 4.6.2 at runtime.
- **Language:** C#. **UI:** WPF with **MVVM Light** (`MvvmLightLibs` 5.2).
- **Key NuGet packages:** HtmlAgilityPack 1.4.9, Newtonsoft.Json 8.0.2, NLog 4.2.3,
  Squirrel.Windows 1.7.8 (+ Mono.Cecil, DeltaCompressionDotNet, Splat, CommonServiceLocator),
  NuGet.CommandLine 4.3.0.
- **Logging:** NLog; `StatsDisplay/NLog.config` writes rolling `log.txt` beside the exe.

## Build & Run

This is a classic .NET Framework solution — use **MSBuild / Visual Studio**, not `dotnet build`.

```shell
git submodule update --init --recursive     # required; submodule is empty otherwise
nuget restore HotsStats.sln
msbuild HotsStats.sln /p:Configuration=Debug        # or Release
```

- Run the overlay: launch `StatsDisplay/bin/<Config>/HotsStats.exe` on Windows with HotS set to
  **Windowed (Fullscreen)** so the overlay can draw on top.
- **Release also builds an installer.** The `AfterBuild` target in `StatsDisplay.csproj`
  (Release only) runs `nuget pack HotsStats.nuspec` + Squirrel `releasify`, producing
  `Releases/HotsStatsSetup.exe`. It errors intentionally if `NuGet.CommandLine` or
  `Squirrel.Windows` are not restored.
- Debug builds set `App.Debug = true`, which disables auto-update and dumps captured
  `.StormSave` / `replay.server.battlelobby` samples under a `saves/` folder.

## Tests

There is **no test project** and no test framework in the solution. Do not assume `dotnet test`
works. If adding tests, `BattleLobbyParser` (pure byte parsing) and the stats-parsing layer are
the natural first targets.

## Architecture & Data Flow

1. `StatsDisplay/App.xaml.cs` starts a `FileMonitor` and a global Shift-Tab hotkey, and kicks off
   Squirrel update checks.
2. `StatsFetcher/FileMonitor.cs` raises three events by watching game files:
   - **BattleLobbyCreated** — `%TEMP%\Heroes of the Storm\TempWriteReplayP1\replay.server.battlelobby` (polled).
   - **RejoinFileCreated** — `*.StormSave` under `Documents\Heroes of the Storm\Accounts`.
   - **ReplayFileCreated** — `*.StormReplay` under the same folder.
3. `StatsFetcher/FileProcessor.cs` orchestrates each stage into a `Game` of ten
   `PlayerProfile`s and calls the external stats layer.
4. `StatsFetcher/BattleLobbyParser.cs` heuristically byte-scans the **undocumented**
   `replay.server.battlelobby` for BattleTags and region — expect this to be fragile across game
   patches.
5. `Heroes.ReplayParser` (submodule) parses `.StormSave`/`.StormReplay` MPQ files for map,
   heroes, hero levels, and post-match `ScoreResult`.
6. WPF windows under `StatsDisplay/Stats/` bind to `Game`/`PlayerProfile`
   (`INotifyPropertyChanged`, coarse "notify everything" triggers).

### Data Source (currently broken)

`StatsFetcher/ProfileFetcher.cs` is the only external-data layer:
- MMR/ranks: `GET https://www.hotslogs.com/API/Players/{region}/{battletag}` (JSON).
- Full profile: `GET http://www.hotslogs.com/Player/Profile?PlayerID=...`, then **HTML scraping**
  with HtmlAgilityPack via `FileProcessor.ExtractBasicData` / `ExtractFullData` (fragile XPath
  against element IDs like `mapStatistics`, `heroStatistics`).

**HotsLogs is offline**, so all of the above fails at runtime. When replacing the source
(e.g. heroesprofile.com), keep the seams:
- Swap the fetch in `ProfileFetcher`.
- Swap the parse in `FileProcessor.Extract*Data`.
- Both read/write the existing `PlayerProfile` model, so UI and replay parsing stay untouched.
- Map any new source's leagues/regions onto `StatsFetcher/Region.cs` and
  `PlayerProfile.League` / `GameMode` (from the submodule; includes `StormLeague`).

## Conventions & Gotchas

- **Code style (from README):** spaces for indentation, C-style (Allman) braces. Existing code
  swallows exceptions liberally (`catch { }`) and uses static globals (`App.Game`) — match the
  surrounding file rather than refactoring broadly while reviving the build.
- The solution has `Debug|x86` / `Release|x86` platform rows in addition to `Any CPU`; the
  submodule's `Heroes.ReplayParser` builds `x86` in those configs.
- Non-SDK projects: adding a source file means editing the `.csproj` `<Compile Include=...>`
  list, and adding a NuGet package means editing both `packages.config` and the `<Reference>`
  `HintPath` in the `.csproj`.
- Auto-update targets `https://github.com/poma/HotsStats` (see `StatsDisplay/App.config`
  `UpdateRepository`); this is stale for the `hotsapi` fork.

## Things Not To Do

- Don't run `dotnet build` / `dotnet test` and assume success — this is classic MSBuild + packages.config.
- Don't try to build before `git submodule update --init --recursive`.
- Don't commit build output (`bin/`, `obj/`, `Releases/`, `packages/`) or the sample
  `replay.server.battlelobby` capture.
- Don't broadly rewrite the exception-swallowing / global-state style while only trying to fix the build.
