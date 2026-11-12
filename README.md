# Si_StarterUnits

> Configurable per-faction **starter unit spawns** for [Silica](https://store.steampowered.com/app/504900/Silica/) servers.

When a round starts, Si_StarterUnits reads JSON config files from `UserData/cfg/StarterUnits/`, looks up each named unit's `ObjectInfo`, and spawns the configured count for each faction at that faction's HQ / Nest. Optional **4-way cross-spawn** support lets you place a Sol player's units in front of the Centauri HQ on 4-way rounds (or any other cross-team combination).

Use the [**interactive web tool**](https://drmuck.github.io/Si_StarterUnits/) to author the JSONs visually — pick from the full unit list per faction, set counts, configure cross-spawns, then export ready-to-deploy files.

## Features

- **Per-faction starter units** — Sol, Centauri, Alien, Wildlife each get their own JSON
- **4-way cross-spawns** — drop units from one faction at another faction's spawn (subscribes to Si_4way events)
- **2v1 lone-team multiplier** (1.2.0) — with Si_VersusVote 0.3.0+ the lone team of a 2v1 round gets its counts multiplied (see below)
- **Placement validation** (1.3.0, reworked in 1.3.1) — every starter is checked for solid ground, navmesh reachability and free space before it is spawned and moved to the nearest valid spot otherwise; elevated playable ground (IndustrialQuarter metal floors, Citadel plateaus) counts as ground; summary + post-spawn self-check in the server log; `!starteraudit` dry run (see below)
- **Hot configurable** — edit the JSONs between rounds; no code change required
- **Hidden from the regular spawn flow** — runs after `MP_Strategy.OnMissionStateChanged` so it slots in after the game's own startup logic

### When it spawns

Postfix on `MP_Strategy.OnMissionStateChanged`: when the mission state becomes `STARTED` (round start, after the
commander phase) a coroutine waits 5 s and then spawns every faction's list around that team's HQ / Nest (the
structure whose ObjectInfo is `Team.BaseStructure`, else the first structure), unit *i* of *N* planned on a 120 m
circle at angle 360/N·i. The JSON files are read at that moment, so edits apply from the next round.

### Placement validation (1.3.0, 1.3.1)

Before 1.3.0 every unit was put at its ring slot at heightmap height + 1 m with no check at all, so a base next to a
cliff, rock or building (Citadel, IndustrialQuarter, NarakaCity) put starters inside or on top of the geometry. On
IndustrialQuarter the steel-plated "metal floor" platforms (`Industrial_Floor_06`, 13 m above the heightmap) are the
real ground; heightmap + 1 m is under that floor. 1.3.0 treated the floor as a roof (4 m height rule) and relocated
or fell back for no reason; 1.3.1 treats it as ground. Each unit is placed like this:

1. **Landing** — a `Physics.BoxCast` straight down with the unit's collision box against
   `GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC`, triggers ignored: the same cast and mask the game uses to drop its own
   initial units (`StrategyTeamSetup.SpawnAIUnits`), teleports (`TeleportUI`) and units that fell through the ground
   (`Unit.UpdateFallThroughGround`). The box (widened by `clearance_margin_m`) comes to rest on the first solid
   surface. A `Physics.CheckBox` at the cast start rules out the cast's blind spot (colliders that already contain it).
   The cast comes from above, so a unit always stands ON the top surface (metal floor, plateau), never on the
   heightmap underneath it.
   The box comes from the prefab's solid colliders (non-trigger, movement layers); `ObjectInfo.PhysicalBounds` is
   only the fallback because it is far too large for some creatures (Shocker 4.6 x 19.8 x 19.8 m vs a 4 m collider).
2. **Level** (1.3.1) — rejected when the landing surface is more than `max_step_from_base_m` (25 m) above or below
   the **base ground** (another level of the map). Base ground = the surface the base actually stands on: a ray down
   from 10 m above the base pivot, base structures and units ignored (1.3.0 used the heightmap there, 13 m under an
   IndustrialQuarter platform and up to ~100 m under a Citadel plateau). There is **no heightmap-based roof rule**
   any more for vehicles and infantry: roofs, oil tanks and pillar tops are separate small pieces of their navmesh
   or have none, and fail in 4.
   **Elevated surface** (alien ground units and flying units only): a non-terrain landing more than
   `max_height_above_terrain_m` (4 m) above both the heightmap at that spot and the base ground (roof, tank,
   catwalk, pillar or rock top) is accepted only if the ground-vehicle navmesh (`Small Vehicle`) has a node there on
   its main component, i.e. it is drivable ground connected to the map. Reason: the alien `Voxel Graph` covers walls
   and roofs (alien ground units climb), so the aliens' own navmesh cannot tell a floor from a tank top, and flying
   units have no navmesh test. A platform the Nest itself stands on is never "elevated" (it is the base ground).
3. **Clearance** — `Physics.OverlapBox` of the landed box against static world layers, units (Character, Vehicle,
   Dynamic) and invisible walls, triggers ignored; and at least `unit_gap_m` between this unit and every starter
   already placed in this pass.
4. **Navmesh** (not for flying units) — the nearest walkable node of the unit's own A* graph (the prefab's
   `Seeker.graphMask`, or that of a live unit of the same type) must be within `nav_max_horizontal_delta_m` /
   `nav_max_height_delta_m` of the landing point, as `GameAI.GetPointValidForAI` / `MP_Strategy.GetStartPointOffNavmesh`
   query it. For the alien voxel graph, whose nodes are points, the voxel size is added as slack. With
   `nav_island_check` the node must be in the **reference component**: 13 probes around the base (base, 4 just
   outside it, 8 on the ring, each at the top surface there; probes on another level than the base ground ignored)
   snap to the unit's graph; if any of them reaches the graph's **main component** (the `GraphNode.Area` with the
   most walkable nodes), that is the reference, so a base on a platform whose own navmesh piece is cut off sends its
   units to the ground they can drive out on (1.3.0 voted for the platform piece and then rejected all real ground
   as "isolated"). Otherwise the 1.3.0 weighted vote decides (a base on its own island). The reference is kept as a
   **node** and its `Area` is read at query time, because A* renumbers `GraphNode.Area` after graph updates (seen on
   CrystalChasm: one component as 13955, 13957, 13959 within one scan); the main component is recomputed (one node
   walk) when its reference node was destroyed by a tile rebuild.

The first candidate is the 1.2.0 ring slot, so layouts that were fine stay exactly as they were. If it fails, up to
`max_candidates_per_unit` spots around it are tried (rings of 8 at 12 … 190 m from the slot, tangential first, kept
within `min_radius_m` … `max_radius_m` of the base); the first valid one wins. If none is valid the least bad
candidate is used (closest to valid, then closest to the slot) with a warning, always at its landed pose; a unit is
never dropped. Severity order since 1.3.1 (least bad first): only other units in the way, another level, elevated
top, isolated navmesh piece / off the navmesh, inside static geometry. If no candidate landed at all: the ring slot
on the top surface + 1 m (1.3.0: heightmap + 1 m). Cost: about
4–5 physics/navmesh queries per candidate, 4–5 per unit on a clean map, worst case ~320 per unit; the pass yields a
frame every `queries_per_frame` queries.

### Log lines (grep `[STARTER]`)

```
[STARTER] unit 'Shocker': box 4.0x4.0x4.0 m (1 prefab colliders, ...; PhysicalBounds 4.6x19.8x19.8 ...), graph 'Voxel Graph' (live unit, node spacing slack ...)
[STARTER] placement relocated <map> <team> '<unit>' #i: (x,y,z) -> (x,y,z) (d m, candidate n, landed on <surface>, h m above terrain, +s m vs base ground); planned spot: <why>
    <why> = other level: ... | elevated top (no ground-vehicle navmesh | ground-vehicle navmesh piece a is not the main component m): ...
          | isolated navmesh piece (area a, reference r = main component | base component b): ... | off navmesh: ... | overlaps '<collider>' ...
[STARTER] placement <map> <team>: island reference for '<unit>' = main component (13 probes, 5 on main, 0 on another level; graph 'Small Vehicle': N walkable nodes, C components, main M)
[STARTER] placement FALLBACK <map> <team> '<unit>' #i: no valid spot in N candidates, using (x,y,z) (<why>); planned (x,y,z): <why>     (warning)
[STARTER] placement <map> <team>: 10 units, 3 as planned, 7 relocated, 0 fallback (136 queries, base ground 283 ('Industrial_Floor_06' ... [Static], 13.0 m above the heightmap))
[STARTER] audit <map> <label>: ..., under a floor: 1.2.0 slot N, chosen 0 (...)        (audit only)
[STARTER] post-check IndustrialQuarter: all 18 units clear
[STARTER] post-check <map> <team> '<unit>' at (x,y,z): spawned under a floor: ... | under a floor: '<collider>' [layer] h m above its feet, feet on the heightmap | embedded in '<collider>' [layer] (penetration 1.2 m) | inside '<collider>' | below terrain by X m | off navmesh (...) (moved d m since spawn at (x,y,z))   (warning)
[STARTER] post-check note <map> <team> '<unit>' at (x,y,z): <finding> (moved 17 m since spawn: not a placement fault)
[STARTER] sol: team not in this round, no starters
```

The **post-check** runs `post_check_delay_s` (5 s) after the spawn for every unit this mod spawned: below the terrain
(the game's own fall-through criterion), **under a floor** (1.3.1) = the first world surface straight above the
unit's x/z (ray from 500 m above, base structures and units ignored) is not terrain, faces up and lies more than
0.5 m above the unit's top, i.e. the unit stands under a walkable floor (the 1.2.0 fault on IndustrialQuarter: metal
floor above, heightmap under the feet), **embedded** = one of the unit's live solid colliders interpenetrates a static
collider by more than 0.5 m (`Physics.ComputePenetration`; standing on a surface or touching a wall does not count),
**inside** = completely enclosed in a mesh (a mesh collider is only a surface, so a unit fully inside a rock touches
no triangle: rays from the unit with `Physics.queriesHitBackfaces` hit back faces up and sideways), or off the
navmesh. A unit that has moved more than 5 m from its spawn point in those seconds is not stuck there (aliens climb
pillars and walls right away); its finding is logged as a `post-check note` and not counted, except below-terrain.
The under-a-floor test is also run on the **spawn point** itself (`spawned under a floor: ...`); that one always
counts, because it is a placement fault whatever the unit did since (a flying Squid that hovers under a building
5 s later is not).
With `log_all_placements` every unit gets a `post-check detail` line (spawn point, position now, movement, height).

### `!starteraudit` (admin, needs the Admin Mod)

Dry run, nothing is spawned, units are ignored (static world only). Detail lines and summaries go to the server log
with the `audit` verb, the caller gets a one-line summary in chat. The audit always runs the validation (1.3.1: also
with `validate_placement: false` or the test switch). Each pass summary also counts how many 1.2.0 ring slots
(heightmap + 1 m) would be under a floor and how many chosen spots are (expected 0; each one is a warning line).

- `!starteraudit` — every faction config at its team's current base.
- `!starteraudit layouts` — every place a base can stand on the current map: the map's vanilla start points
  (`MP_Strategy.TeamSetups[].BaseStructureStartPoints`), every Si_MapBalance layout in
  `UserData/cfg/Spawns/<map>/` (read as plain JSON, first entry per faction like Si_MapBalance; base forward +Z, or
  the entry's optional `"rot"` in degrees (Si_MapBalance 3.6.0) as `Euler(0, rot, 0) * +Z`; base height = the top
  surface at x/z like Si_MapBalance's `SampleSurfaceHeight`, 1.3.0: the heightmap), and
  audit-only test positions in `UserData/cfg/StarterUnits/audit/<map>/*.json` (same format, every entry). Si_MapBalance
  does not have to be loaded.
- `auto_audit_layouts: true` in `placement.json` runs the layout audit once per map per server run after the
  post-check, for servers nobody types on.

### placement.json

`UserData/cfg/StarterUnits/placement.json`, written with these defaults when missing, read at every round start
(a file written by 1.3.0 is read as it is; the missing 1.3.1 key takes its default):

| Key | Default | Meaning |
|--|--|--|
| `validate_placement` | `true` | `false` = ring slot on the top surface + 1 m, no checks (1.3.0: heightmap + 1 m = 1.2.0); summary and post-check still run |
| `ring_radius_m` | 120 | planned ring radius |
| `min_radius_m` / `max_radius_m` | 60 / 250 | search limits, distance from the base |
| `max_candidates_per_unit` | 64 | search budget per unit |
| `clearance_margin_m` | 1.0 | added to each horizontal side of the unit box |
| `unit_gap_m` | 2.0 | space between starters |
| `nav_max_horizontal_delta_m` / `nav_max_height_delta_m` | 3 / 3 | navmesh snap tolerance (height + 0.4 x unit radius) |
| `nav_island_check` | `true` | same navmesh component as the reference (main component if reachable next to the base) |
| `max_height_above_terrain_m` | 4 | **meaning changed in 1.3.1**: elevated-surface threshold (above both heightmap and base ground) for alien ground and flying units, which must then be on the main ground-vehicle navmesh; not used for vehicles/infantry. 1.3.0: hard limit above the heightmap for every unit. Existing files keep working unchanged |
| `max_step_from_base_m` | 25 | height difference to the base ground (1.3.1: the surface the base stands on; 1.3.0: the heightmap there) |
| `test_legacy_heightmap_placement` | `false` | **test only** (1.3.1): live spawns use the 1.2.0 placement exactly (heightmap + 1 m, no checks) to reproduce the under-floor fault for the post-check; warning every round; the audit ignores it. Never on a played server |
| `post_check_delay_s` | 5 | 0 = no post-check |
| `queries_per_frame` | 200 | yield a frame after this many queries |
| `auto_audit_layouts` | `false` | layout audit once per map per run |
| `log_all_placements` | `false` | also one line per unit placed as planned |

## Installation

1. Install [MelonLoader](https://github.com/LavaGang/MelonLoader) on your Silica dedicated server
2. Install the [Silica Admin Mod](https://github.com/data-bomb/Silica) (`Si_AdminMod.dll`) — usual server base; this mod itself does not call it
3. Copy `Si_StarterUnits.dll` to your server's `Mods/` folder
4. (Optional) Copy [Si_4way](https://github.com/data-bomb/Silica) if you want cross-spawn support on 4-way rounds
5. (Optional) Place your starter config JSONs into `UserData/cfg/StarterUnits/` — see below

The config folder is auto-created on first run (with a default `placement.json`); an empty starter list = vanilla
spawns. No commands needed at runtime.

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
`UnityEngine.CoreModule.dll`, `UnityEngine.TerrainModule.dll`, and since 1.3.0 `UnityEngine.PhysicsModule.dll`,
`AstarPathfindingProject.dll` (+ its `PackageTools.dll`, `Drawing.dll` base types) and `Si_AdminMod.dll` (only used by
`AdminCommand.cs`, which runs only when the Admin Mod melon is loaded; the mod works without it). (Before 1.2.0 the
project used Si_4way's 0.9.47 refs and a NuGet Admin Mod reference that the code never used.)

Build output: `Si_StarterUnits/bin/Release/netstandard2.1/Si_StarterUnits.dll`.

## Changelog

- **1.3.1** (2026-11-12): elevated playable ground is ground. Base ground = the surface the base stands on (not the
  heightmap); no heightmap roof rule for vehicles/infantry (their navmesh main component decides); elevated-surface
  rule for alien ground and flying units via the ground-vehicle navmesh; island reference = main component when
  reachable next to the base, tracked by a node (A* renumbers areas); no code path uses the heightmap as the
  landing height (slot, no-landing fallback, check-failed pose); post-check "under a floor"; audit: base on the top
  surface, layout `"rot"` honoured, under-floor counts, always validates; `test_legacy_heightmap_placement` switch.
- **1.3.0** (2026-11-12): placement validation (landing cast, height, clearance, navmesh + island check, local
  search, fallback with warning), per-team placement summary, post-spawn self-check, `!starteraudit [layouts]`,
  `placement.json`; base = the team's `BaseStructure`; "team not in this round" is an info line now.
- **1.2.0** (2026-11-08): 2v1 lone-team multiplier from Si_VersusVote (VersusApi v2), extras within the unit cap;
  built against 1.0.15 refs; not run live yet.
- **1.1.0**: config moved to `UserData/cfg/StarterUnits` (old `UserData/StarterUnits` moved once).
- **1.0.0**: per-faction starter units, 4-way cross-spawns.

## License

GPL-3.0 (matches Silica modding ecosystem).
