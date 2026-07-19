# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**HotsStats** — a Windows desktop overlay for Blizzard's *Heroes of the Storm*. During the
loading screen and in-game it shows each player's MMR, games played, hero/map win rates, and a
profile link (mimicking the built-in Shift-Tab stats panel). This checkout is the
`hotsapi/HotsStats` fork of the original `poma/HotsStats`. The project is **archived**; the
current work is to revive it for the latest game version.

> Former dead dependency (now replaced): the original stats source (HotsLogs.com) was shut down
> in ~2022. `StatsFetcher/ProfileFetcher.cs` now fetches from heroesprofile.com's internal JSON
> endpoints instead. That replacement is itself unverified from a live response — see
> "Data Source" below for the important Cloudflare/403 caveat.

## Repo Layout

The solution `HotsStats.sln` is at the repo root (its header still reads "Visual Studio 14"
format, but all four projects are now SDK-style `.csproj`s). All commands below assume you are
at the repo root.

| Path                                          | What lives there                                                                 |
|-----------------------------------------------|----------------------------------------------------------------------------------|
| `StatsFetcher/`                               | Core library: file monitoring, battlelobby parsing, external stats fetch/parse   |
| `StatsDisplay/`                               | WPF app (`OutputType=WinExe`, assembly name `HotsStats`): UI, settings           |
| `Heroes.ReplayParser/` (git submodule)        | `Heroes.ReplayParser` + `MpqTool` projects: parse `.StormSave`/`.StormReplay`     |
| `HotsStats.sln`                               | Solution tying the four projects together                                        |
| `.appveyor.yml`                               | Legacy AppVeyor CI (VS2017 image, still references NuGet/Squirrel — not the current build; not used to build locally) |

**The `Heroes.ReplayParser` submodule is required and is not committed as source.** `.gitmodules`
points at `https://github.com/hotsapi/Heroes.ReplayParser.git`. You **must** initialize it before
the solution will build (and see "Known Limitations / TODO" — the SDK-style retarget commit is
not yet pushed there):

```shell
git submodule update --init --recursive
```

## Stack & Versions

- **Runtime:** SDK-style `.csproj`s throughout, NuGet via `PackageReference` (`packages.config`
  is gone). **.NET SDK 10 is required.**
- **Target frameworks:** `StatsDisplay.csproj` targets `net10.0-windows`
  (`<UseWPF>true</UseWPF>` + `<UseWindowsForms>true</UseWindowsForms>`, for the WPF UI and the
  `NotifyIcon` tray icon). `StatsFetcher.csproj`, `Heroes.ReplayParser.csproj`, and
  `MpqTool.csproj` all target `netstandard2.0`.
- **Language:** C#. **UI:** WPF with **CommunityToolkit.Mvvm** (replaced the deprecated
  MVVM Light).
- **Key NuGet packages:** CommunityToolkit.Mvvm 8.4.0, NLog 5.5.0, Newtonsoft.Json 13.0.4,
  DotNetZip 1.16.0 (via `MpqTool`, replaced SharpCompress), System.Configuration.ConfigurationManager
  10.0.0 (needed for `App.config` `userSettings` on modern .NET). HtmlAgilityPack and the whole
  Squirrel.Windows stack are gone.
- **Logging:** NLog; `StatsDisplay/NLog.config` writes rolling `log.txt` beside the exe.

## Build & Run

Canonical build is now the **dotnet CLI** (VS 2026 / MSBuild against the same `.sln` also works):

```shell
git submodule update --init --recursive     # still required; submodule is empty otherwise
dotnet restore HotsStats.sln
dotnet build HotsStats.sln -c Debug          # or -c Release
```

- Run the overlay: launch `StatsDisplay/bin/Debug/net10.0-windows/HotsStats.exe` (or
  `bin/Release/net10.0-windows/...`) on Windows with HotS set to **Windowed (Fullscreen)** so the
  overlay can draw on top.
- **Release no longer produces an installer.** Squirrel packaging/releasify was dropped; `Release`
  is a plain build output, same shape as `Debug`. Runtime auto-update is likewise disabled —
  `App.CheckForUpdates()` in `StatsDisplay/App.xaml.cs` is a no-op for this MVP.
- Debug builds set `App.Debug = true`, which dumps captured `.StormSave` /
  `replay.server.battlelobby` samples under a `saves/` folder.

## Tests

There is **no test project** and no test framework in the solution. Do not assume `dotnet test`
works. If adding tests, `BattleLobbyParser` (pure byte parsing) and the stats-parsing layer are
the natural first targets.

## Architecture & Data Flow

1. `StatsDisplay/App.xaml.cs` starts a `FileMonitor` and a global Shift-Tab hotkey, and calls
   `CheckForUpdates()` — currently a logged no-op; auto-update is disabled (see "Build & Run").
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

### Data Source

`StatsFetcher/ProfileFetcher.cs` is the only external-data layer, and now talks to
**heroesprofile.com**'s internal JSON routes (the same ones its own front-end uses) rather than
HotsLogs.com (shut down ~2022) or HTML scraping — HtmlAgilityPack has been removed entirely:
- A CSRF-cookie session is bootstrapped once per process (`EnsureBootstrappedAsync`), and the
  `XSRF-TOKEN` cookie is resent as an `X-XSRF-TOKEN` header on every POST; a 419/401 triggers one
  re-bootstrap-and-retry.
- `POST /api/v1/battletag/search` resolves a lobby battletag to a `blizz_id`, matching by full
  battletag first and falling back to short-name + most-games-played within the correct region.
- `POST /api/v1/player` fills per-mode MMR/rank (`qm_mmr_data`, `ud_mmr_data`, `hl_mmr_data`,
  `tl_mmr_data`, `sl_mmr_data`; `ar_mmr_data` is fetched but unmapped — no `PlayerProfile.Ranks`
  slot for it yet).
- `POST /api/v1/player/heroes/all` and `.../maps/all` fill hero/map win rates.
- `FileProcessor.ExtractBasicData`/`ExtractFullData` are now thin (win-rate lookup only) or
  no-ops, since `ProfileFetcher` populates `PlayerProfile` directly.

**Important caveat — unverified against a live response.** heroesprofile.com sits behind
Cloudflare and **returns 403 to datacenter / non-browser-looking IPs**; a real smoke run from
this development environment hit a 403. The JSON field names above (and the `rank_tier` →
`PlayerProfile.League` mapping in `ProfileFetcher.MapLeague`) were inferred from the site's
client-side JS, not confirmed against a real payload. Every parse path is defensive (a
missing/renamed field is logged and skipped, never thrown) and `LogFirstResponse` logs the first
raw response per endpoint so a human running from a residential IP with browser-like headers can
confirm the real schema. **Do not trust the MMR/rank/win-rate output as correct until that
verification has happened.**

## Conventions & Gotchas

- **Code style (from README):** spaces for indentation, C-style (Allman) braces. Existing code
  swallows exceptions liberally (`catch { }`) and uses static globals (`App.Game`) — match the
  surrounding file rather than refactoring broadly while reviving the build.
- The solution only has `Debug|Any CPU` / `Release|Any CPU` rows now — the `x86` platform rows
  were dropped along with the classic-MSBuild project format.
- SDK-style projects: adding a `.cs` file under a project folder is picked up automatically
  (implicit globbing) — no `.csproj` edit needed. Adding a NuGet package means adding a
  `<PackageReference>` in the `.csproj`; there is no `packages.config` anymore.
- Auto-update is disabled outright (see "Build & Run"); `StatsDisplay/App.config`
  `UpdateRepository` (`https://github.com/hotsapi/HotsStats`) is unused while that's the case.
- `StatsDisplay.csproj` suppresses `CA1416` (platform-compatibility warnings on
  `NotifyIcon`/`Icon.ExtractAssociatedIcon`) — expected, since the app is `net10.0-windows`
  (Windows-only by design), not a cross-platform target.

## Known Limitations / TODO

- **Submodule retarget commit is local-only.** The `Heroes.ReplayParser` submodule's SDK-style /
  `netstandard2.0` retarget lives only on the local `local/net472` branch of that submodule's
  checkout (see `Heroes.ReplayParser/`); it has not been pushed to a fork on GitHub. A fresh clone
  of this repo running `git submodule update --init --recursive` will **not** get that commit and
  will fail to build until the commit is pushed somewhere reachable and `.gitmodules` (or the
  submodule's tracked commit) points at it.
- **BattleLobbyParser byte offsets are unverified.** They were made non-fatal (log-and-skip
  instead of throw) on layout drift, but have not been re-verified against a capture from the
  current game patch — region/battletag extraction may silently come back empty on a real lobby.
- **One-time settings reset.** The `net10.0-windows` migration changed the per-user config storage
  path (it's derived from assembly identity), so existing users will see their saved settings
  (battletag, window positions, etc.) reset once on first run after upgrading.
- **heroesprofile.com integration needs live validation.** See the "Data Source" caveat above —
  this has only been inferred from client-side JS and hit a 403 in this environment's smoke test.
- **Replay-parser bump for latest-patch replays is still pending live testing** (deferred fix from
  an earlier review round) — parsing newer `.StormReplay`/`.StormSave` files hasn't been confirmed
  end-to-end against a current-patch game client.

## Things Not To Do

- Don't try to build before `git submodule update --init --recursive`.
- Don't commit build output (`bin/`, `obj/`, `Releases/`, `packages/`) or the sample
  `replay.server.battlelobby` capture.
- Don't broadly rewrite the exception-swallowing / global-state style while only trying to fix the build.
- Don't trust heroesprofile.com JSON field names/scales as confirmed — they're best-effort until
  validated from a non-datacenter IP (see "Data Source").
