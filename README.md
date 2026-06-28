# Si_StarterUnits

> Configurable per-faction **starter unit spawns** for [Silica](https://store.steampowered.com/app/504900/Silica/) servers.

When a round starts, Si_StarterUnits reads JSON config files from `UserData/StarterUnits/`, looks up each named unit's `ObjectInfo`, and spawns the configured count for each faction at that faction's HQ / Nest. Optional **4-way cross-spawn** support lets you place a Sol player's units in front of the Centauri HQ on 4-way rounds (or any other cross-team combination).

Use the [**interactive web tool**](https://drmuck.github.io/Si_StarterUnits/) to author the JSONs visually — pick from the full unit list per faction, set counts, configure cross-spawns, then export ready-to-deploy files.

## Features

- **Per-faction starter units** — Sol, Centauri, Alien, Wildlife each get their own JSON
- **4-way cross-spawns** — drop units from one faction at another faction's spawn (subscribes to Si_4way events)
- **Hot configurable** — edit the JSONs between rounds; no code change required
- **Unit cap-aware** — spawned units use the normal team unit allocator
- **Hidden from the regular spawn flow** — runs after `MP_Strategy.OnMissionStateChanged` so it slots in after the game's own startup logic

## Installation

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) on your Silica dedicated server
2. Install the [Silica Admin Mod](https://github.com/data-bomb/Silica) (`Si_AdminMod.dll`) — runtime dependency
3. Copy `Si_StarterUnits.dll` to your server's `Mods/` folder
4. (Optional) Copy [Si_4way](https://github.com/data-bomb/Silica) if you want cross-spawn support on 4-way rounds
5. (Optional) Place your starter config JSONs into `UserData/StarterUnits/` — see below

The config folder is auto-created on first run; an empty starter list = vanilla spawns. No commands needed at runtime.

## Configuration

### Per-faction JSON

The mod looks for these files in `UserData/StarterUnits/`:

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

## Interactive configuration tool

A **browser-based tool** is hosted at:

➡️ **<https://drmuck.github.io/Si_StarterUnits/>**

The tool runs entirely in your browser (no server / no data uploaded anywhere) and lets you:

- Build per-faction starter lists from a pre-populated dropdown of every Silica unit (so you can't typo a name)
- Add / remove unit lines, set counts
- Configure 4-way cross-spawn rules with the same dropdowns
- **Export** — generates `sol.json`, `centauri.json`, `alien.json`, `wildlife.json`, and `4way_cross_spawns.json` ready to drop into `UserData/StarterUnits/`
- **Import** — paste in an existing config and continue editing

It's the recommended way to author the configs unless you're scripting them programmatically.

This is consistent with the rest of the DrMuck toolchain — see also:
- [Map Balance Tool](https://drmuck.github.io/Silica-MapBalanceTool/) for per-map spawn / resource layouts
- [Unit Balance Interactive](https://drmuck.github.io/Si_UnitBalance_Interactive/) for live unit stat overrides

## Building from source

```bash
cd Si_StarterUnits
dotnet build -c Release
```

Targets `netstandard2.1`. Reference DLLs in `include/netstandard2.1/`:
- `SilicaCore.dll`, `MelonLoader.dll`, `0Harmony.dll`, `Newtonsoft.Json.dll`
- `UnityEngine.dll`, `UnityEngine.CoreModule.dll`

Build output: `Si_StarterUnits/bin/Release/netstandard2.1/Si_StarterUnits.dll`.

## License

GPL-3.0 (matches Silica modding ecosystem).
