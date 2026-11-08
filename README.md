# Si_StarterUnits

> Configurable per-faction **starter unit spawns** for [Silica](https://store.steampowered.com/app/504900/Silica/) servers.

When a round starts, Si_StarterUnits reads JSON config files from `UserData/cfg/StarterUnits/`, looks up each named unit's `ObjectInfo`, and spawns the configured count for each faction at that faction's HQ / Nest. Optional **4-way cross-spawn** support lets you place a Sol player's units in front of the Centauri HQ on 4-way rounds (or any other cross-team combination).

Use the [**interactive web tool**](https://drmuck.github.io/Si_StarterUnits/) to author the JSONs visually — pick from the full unit list per faction, set counts, configure cross-spawns, then export ready-to-deploy files.

## Features

- **Per-faction starter units** — Sol, Centauri, Alien, Wildlife each get their own JSON
- **4-way cross-spawns** — drop units from one faction at another faction's spawn (subscribes to Si_4way events)
- **2v1 lone-team multiplier** (1.2.0) — with Si_VersusVote 0.3.0+ the lone team of a 2v1 round gets its counts multiplied (see below)
- **Hot configurable** — edit the JSONs between rounds; no code change required
- **Hidden from the regular spawn flow** — runs after `MP_Strategy.OnMissionStateChanged` so it slots in after the game's own startup logic

### When it spawns

Postfix on `MP_Strategy.OnMissionStateChanged`: when the mission state becomes `STARTED` (round start, after the
commander phase) a coroutine waits 5 s and then spawns every faction's list around that team's first structure (HQ /
Nest), on a 120 m circle. The JSON files are read at that moment, so edits apply from the next round.

## Installation

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) on your Silica dedicated server
2. Install the [Silica Admin Mod](https://github.com/data-bomb/Silica) (`Si_AdminMod.dll`) — usual server base; this mod itself does not call it
3. Copy `Si_StarterUnits.dll` to your server's `Mods/` folder
4. (Optional) Copy [Si_4way](https://github.com/data-bomb/Silica) if you want cross-spawn support on 4-way rounds
5. (Optional) Place your starter config JSONs into `UserData/cfg/StarterUnits/` — see below

The config folder is auto-created on first run; an empty starter list = vanilla spawns. No commands needed at runtime.

## Configuration

### Per-faction JSON

The mod looks for these files in `UserData/cfg/StarterUnits/`:

| File | Spawns at | Required units |
|--|--|--|
| `sol.json` | Sol HQ | — |
| `centauri.json` | Centauri HQ | — |
| `alien.json` | Alien Nest | — |
| `wildlife.json` | Wildlife (1v1 or sandbox modes) | — |

A missing file means that faction gets vanilla spawns.

#### Format

```json
{
  "units": [
    { "name": "Platoon Hauler", "count": 3 },
    { "name": "Light Quad",     "count": 2 },
    { "name": "Heavy Quad",     "count": 1 },
    { "name": "Light Striker",  "count": 2 }
  ]
}
```

The `name` field is matched **case-insensitively** against `ObjectInfo.DisplayName` (the same name that appears on units in-game). Use the [interactive tool](https://drmuck.github.io/Si_StarterUnits/) to pick valid unit names per faction.

### 4-way cross-spawns

If you also run [Si_4way](https://github.com/data-bomb/Silica), drop a `4way_cross_spawns.json` into the same folder to add cross-team starter units that only fire on 4-way rounds:

```json
{
  "cross_spawns": [
    {
      "from_faction": "Sol",
      "at_faction":   "Centauri",
      "units": [
        { "name": "Light Quad", "count": 1 }
      ]
    }
  ]
}
```

Each entry spawns the listed `units` **on team `from_faction`** but **at `at_faction`'s spawn position** — useful for the 4-way mode where each base is shared by two factions and you want both factions represented at the start.

### 2v1 lone-team multiplier (Si_VersusVote)

Si_VersusVote 0.3.0 can play the 3-way button as 2v1 (Sol + Centauri allied against the Aliens). If it is loaded,
Si_StarterUnits looks up `Si_VersusVote.VersusApi` by reflection (no compile-time reference; `ApiVersion >= 2`
required, a missing or older Si_VersusVote changes nothing) and, for the team where `VersusApi.IsLoneTeam(team)` is
true, multiplies every count of that team's JSON by `VersusApi.LoneStarterUnitsMult` (an integer, Si_VersusVote config
`two_vs_one.lone_starter_units_mult`, default 2, `!versus set lone_units <n>`; clamped to 1..10 here as well).
Log: `[StarterUnits] 2v1 lone team Alien: counts x2 (Si_VersusVote)`.

- **Cap.** The JSON counts are always spawned, as before. The extra `(mult - 1) x count` units are only added while
  the team's vanilla unit cap for that unit type has room (`Team.GetUnitTypeCapEntryForUnit(info).Max`, 0 = no cap),
  counting the units already on the map plus every starter spawned before them. Skipped extras are logged
  (`[StarterUnits] 2v1 extras for Alien: N added, skipped at the unit cap: Squid x3`). The starters themselves still
  bypass the cap like before (they are spawned directly, not produced).
- **Timing.** The multiplier follows `VersusApi.TwoVsOneActive` + `LoneTeam`, which Si_VersusVote sets when the layout
  is decided (SetTeamVersusMode prefix), not the later Si_Alliance confirmation. The spawn happens at round start + 5 s,
  normally many seconds after the alliance was confirmed (~0.2 s after the vote), so in practice both agree. Trade-off:
  if a round ever started within ~5 s of the layout vote while the alliance request was still pending and Si_Alliance
  then refused it, the Aliens would keep their extra starters in what became a normal 3-way.
- 4-way cross-spawns are not multiplied (4-way and 2v1 never run together).

## Interactive configuration tool

A **browser-based tool** is hosted at:

➡️ **<https://drmuck.github.io/Si_StarterUnits/>**

The tool runs entirely in your browser (no server / no data uploaded anywhere) and lets you:

- Build per-faction starter lists from a pre-populated dropdown of every Silica unit (so you can't typo a name)
- Add / remove unit lines, set counts
- Configure 4-way cross-spawn rules with the same dropdowns
- **Export** — generates `sol.json`, `centauri.json`, `alien.json`, `wildlife.json`, and `4way_cross_spawns.json` ready to drop into `UserData/cfg/StarterUnits/`
- **Import** — paste in an existing config and continue editing

It's the recommended way to author the configs unless you're scripting them programmatically.

This is consistent with the rest of the DrMuck toolchain — see also:
- [Map Balance Tool](https://drmuck.github.io/Silica-MapBalanceTool/) for per-map spawn / resource layouts
- [Unit Balance Interactive](https://drmuck.github.io/Si_UnitBalance_Interactive/) for live unit stat overrides

## Building from source

```bash
cd Si_StarterUnits
dotnet build -c Release                           # test branch 1.0.x (default)
dotnet build -c Release -p:SilicaBranch=default   # 0.9.47 refs, output bin/Release/default/
```

Targets `netstandard2.1`. Reference DLLs come from `../../_silica_refs/<branch>/` (copied from that branch's server
install): `SilicaCore.dll`, `MelonLoader.dll`, `0Harmony.dll`, `Newtonsoft.Json.dll`, `UnityEngine.dll`,
`UnityEngine.CoreModule.dll`, `UnityEngine.TerrainModule.dll`. (Before 1.2.0 the project used Si_4way's 0.9.47 refs
and a NuGet Admin Mod reference that the code never used.)

Build output: `Si_StarterUnits/bin/Release/netstandard2.1/Si_StarterUnits.dll`.

## Changelog

- **1.2.0** (2026-11-08): 2v1 lone-team multiplier from Si_VersusVote (VersusApi v2), extras within the unit cap;
  built against 1.0.15 refs; not run live yet.
- **1.1.0**: config moved to `UserData/cfg/StarterUnits` (old `UserData/StarterUnits` moved once).
- **1.0.0**: per-faction starter units, 4-way cross-spawns.

## License

GPL-3.0 (matches Silica modding ecosystem).
