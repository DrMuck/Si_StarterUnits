/*
 Si_StarterUnits — Configurable starter unit spawns per faction
 Reads JSON configs from UserData/cfg/StarterUnits/
 Supports 4-way cross-spawns via Si_4way events.
 1.2.0: 2v1 lone-team multiplier from Si_VersusVote (VersusApi v2, by reflection, see VersusVoteBridge.cs): the lone
        team's counts x LoneStarterUnitsMult; the extra units only while the team's unit cap has room.
 1.3.0: placement validation (Placement.cs): every starter is landed, ground/navmesh- and clearance-checked before it is
        spawned, relocated near its ring slot when the slot is bad; per-team placement summary, post-spawn self-check,
        admin audit command !starteraudit (AdminCommand.cs, only with the Admin Mod loaded).
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_StarterUnits.StarterUnits), "Starter Units", Si_StarterUnits.StarterUnits.Version, "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_StarterUnits
{
    public class StarterUnitsConfig
    {
        [JsonProperty("units")]
        public List<UnitEntry> Units = new List<UnitEntry>();
    }

    public class UnitEntry
    {
        [JsonProperty("name")]
        public string? Name;
        [JsonProperty("count")]
        public int Count = 1;
    }

    public class CrossSpawnConfig
    {
        [JsonProperty("cross_spawns")]
        public List<CrossSpawnEntry> CrossSpawns = new List<CrossSpawnEntry>();
    }

    public class CrossSpawnEntry
    {
        [JsonProperty("from_faction")]
        public string? FromFaction;  // units belong to this faction
        [JsonProperty("at_faction")]
        public string? AtFaction;    // spawn at this faction's base
        [JsonProperty("units")]
        public List<UnitEntry> Units = new List<UnitEntry>();
    }

    public class StarterUnits : MelonMod
    {
        public const string Version = "1.3.0";

        /// <summary>UserData/cfg/StarterUnits — the server's cfg/ folder. A pre-1.1
        /// UserData/StarterUnits/ folder is moved there once.</summary>
        internal static string ConfigDir => _configDir ?? (_configDir = ResolveConfigDir());
        private static string? _configDir;

        private static string ResolveConfigDir()
        {
            string userData = MelonEnvironment.UserDataDirectory;
            string dir = Path.Combine(userData, "cfg", "StarterUnits");
            string legacy = Path.Combine(userData, "StarterUnits");
            try
            {
                if (!Directory.Exists(dir) && Directory.Exists(legacy))
                {
                    Directory.CreateDirectory(Path.Combine(userData, "cfg"));
                    Directory.Move(legacy, dir);
                    MelonLogger.Msg($"[STARTER] Moved config folder {legacy} -> {dir}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[STARTER] Could not move {legacy} -> {dir}: {ex.Message}");
            }
            return dir;
        }
        private static bool _is4WayRound = false;
        private static bool _auditRunning = false;

        private static readonly string[] FactionFiles = { "sol.json", "centauri.json", "alien.json", "wildlife.json" };

        public override void OnInitializeMelon()
        {
            Directory.CreateDirectory(ConfigDir);
            PlacementConfig.Load(ConfigDir);   // writes placement.json with the defaults when it is missing
            MelonLogger.Msg("Si_StarterUnits v" + Version + " loaded. Configs: " + ConfigDir);
        }

        public override void OnLateInitializeMelon()
        {
            SubscribeTo4WayEvents();

            // Subscribe to game start
            try
            {
                var harmony = new HarmonyLib.Harmony("com.drmuck.starterunits");
                var mpStrategy = typeof(GameMode).Assembly.GetType("MP_Strategy");
                if (mpStrategy != null)
                {
                    var onMission = mpStrategy.GetMethod("OnMissionStateChanged",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (onMission != null)
                    {
                        harmony.Patch(onMission,
                            postfix: new HarmonyMethod(typeof(StarterUnits), nameof(Postfix_OnMissionStateChanged)));
                        MelonLogger.Msg("[STARTER] Patched OnMissionStateChanged");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[STARTER] Patch failed: {ex.Message}");
            }

            // Optional admin command; AdminCommand is the only class that references the Admin Mod.
            if (!RegisteredMelons.Any(m => m.Info.Name == "Admin Mod"))
                MelonLogger.Msg("[STARTER] Admin Mod not found: !starteraudit unavailable (placement validation runs anyway)");
            else
            {
                try { AdminCommand.Register(); }
                catch (Exception ex) { MelonLogger.Warning("[STARTER] !starteraudit registration failed: " + ex.Message); }
            }
        }

        public static void Postfix_OnMissionStateChanged(object __instance, MP_Strategy.EMissionState __0)
        {
            if (__0 == MP_Strategy.EMissionState.STARTED && Game.GetIsServer())
            {
                MelonCoroutines.Start(SpawnAllStarters(5f));
            }
        }

        // === 4-Way event subscription ===
        private static void SubscribeTo4WayEvents()
        {
            try
            {
                var si4wayType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "Si_4way.Si_4way");
                if (si4wayType == null) return;

                var startedEvent = si4wayType.GetEvent("On4WayRoundStarted", BindingFlags.Public | BindingFlags.Static);
                if (startedEvent != null)
                {
                    Action<Team, Team> handler = (a, w) => { _is4WayRound = true; };
                    startedEvent.AddEventHandler(null, handler);
                }

                var endedEvent = si4wayType.GetEvent("On4WayRoundEnded", BindingFlags.Public | BindingFlags.Static);
                if (endedEvent != null)
                {
                    Action handler = () => { _is4WayRound = false; };
                    endedEvent.AddEventHandler(null, handler);
                }

                MelonLogger.Msg("[STARTER] Subscribed to Si_4way events");
            }
            catch { }
        }

        // === Spawn logic ===

        /// <summary>One placement pass: a unit list for one team at one base structure.</summary>
        private sealed class SpawnJob
        {
            public string Label = "";
            public string LogLine = "";      // "{0}" = number spawned
            public Team Team = null!;
            public Vector3 BasePos;
            public Vector3 BaseForward;
            public List<(string name, ObjectInfo info)> Units = new List<(string, ObjectInfo)>();
        }

        private static Dictionary<string, ObjectInfo> BuildInfoIndex()
        {
            var infoByName = new Dictionary<string, ObjectInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in Resources.FindObjectsOfTypeAll<ObjectInfo>())
            {
                if (info == null || info.Prefab == null) continue;
                string dn = info.DisplayName;
                if (!string.IsNullOrEmpty(dn) && !infoByName.ContainsKey(dn))
                    infoByName[dn] = info;
            }
            return infoByName;
        }

        private static IEnumerator SpawnAllStarters(float delay)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            MelonLogger.Msg("[STARTER] Spawning starter units...");

            PlacementConfig cfg = new PlacementConfig();
            var jobs = new List<SpawnJob>();
            try
            {
                cfg = PlacementConfig.Load(ConfigDir);
                jobs = CollectSpawnJobs(BuildInfoIndex());
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[STARTER] Preparing the spawn failed: {ex.Message}");
            }

            string map = Placer.MapName();
            int totalSpawned = 0;
            var records = new List<Placer.SpawnRecord>();
            foreach (var job in jobs)
            {
                var ctx = new PassContext();
                IEnumerator pass = RunPass(job, cfg, map, spawn: true, ctx, records);
                while (true)
                {
                    bool more;
                    try { more = pass.MoveNext(); }
                    catch (Exception ex) { MelonLogger.Error($"[STARTER] {job.Label}: pass aborted: {ex.Message}"); break; }
                    if (!more) break;
                    yield return pass.Current;
                }
                totalSpawned += ctx.Spawned;
            }

            MelonLogger.Msg($"[STARTER] Complete: {totalSpawned} total units spawned");
            if (cfg.PostCheckDelay > 0f && records.Count > 0)
                MelonCoroutines.Start(Placer.PostCheck(records, cfg.PostCheckDelay, map, cfg));
            if (cfg.AutoAuditLayouts && _autoAuditedMaps.Add(map))
                MelonCoroutines.Start(DelayedAudit(cfg.PostCheckDelay + 3f));
        }

        private static readonly HashSet<string> _autoAuditedMaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static IEnumerator DelayedAudit(float delay)
        {
            yield return new WaitForSeconds(delay);
            StartAudit(true, _ => { });
        }

        /// <summary>The per-faction passes (sol.json ... wildlife.json, with the 2v1 multiplier) and, on 4-way rounds,
        /// the cross-spawn passes. Config errors are logged per file, as before.</summary>
        private static List<SpawnJob> CollectSpawnJobs(Dictionary<string, ObjectInfo> infoByName)
        {
            var jobs = new List<SpawnJob>();
            foreach (string file in FactionFiles)
            {
                string path = Path.Combine(ConfigDir, file);
                if (!File.Exists(path)) continue;

                try
                {
                    var config = JsonConvert.DeserializeObject<StarterUnitsConfig>(File.ReadAllText(path));
                    if (config?.Units == null || config.Units.Count == 0) continue;

                    string factionName = Path.GetFileNameWithoutExtension(file);
                    Team? team = FindTeamByFaction(factionName);
                    if (team == null) { MelonLogger.Warning($"[STARTER] Team not found for '{factionName}'"); continue; }

                    Structure? baseStruct = FindBaseStructure(team);
                    if (baseStruct == null)
                    {
                        // no structures at all = the team does not play this round (e.g. Sol in HvA): not a problem
                        int nStruct = 0; try { nStruct = team.Structures.Count; } catch { }
                        if (nStruct == 0) MelonLogger.Msg($"[STARTER] {factionName}: team not in this round, no starters");
                        else MelonLogger.Warning($"[STARTER] No base structure for '{factionName}' ({nStruct} structures)");
                        continue;
                    }

                    int mult = VersusVoteBridge.MultFor(team, out string loneName);
                    if (mult > 1)
                        MelonLogger.Msg($"[StarterUnits] 2v1 lone team {loneName}: counts x{mult} (Si_VersusVote)");

                    jobs.Add(new SpawnJob
                    {
                        Label = factionName,
                        LogLine = $"[STARTER] {factionName}: spawned {{0}} units" + (mult > 1 ? $" (2v1 lone team x{mult})" : ""),
                        Team = team,
                        BasePos = baseStruct.transform.position,
                        BaseForward = baseStruct.transform.forward,
                        Units = BuildSpawnList(config.Units, team, infoByName, mult),
                    });
                }
                catch (Exception ex)
                {
                    MelonLogger.Error($"[STARTER] Error loading {file}: {ex.Message}");
                }
            }

            // 4-Way cross-spawns
            if (_is4WayRound)
            {
                string crossPath = Path.Combine(ConfigDir, "4way_cross_spawns.json");
                if (File.Exists(crossPath))
                {
                    try
                    {
                        var crossConfig = JsonConvert.DeserializeObject<CrossSpawnConfig>(File.ReadAllText(crossPath));
                        if (crossConfig?.CrossSpawns != null)
                        {
                            foreach (var entry in crossConfig.CrossSpawns)
                            {
                                if (entry == null) continue;
                                Team? fromTeam = FindTeamByFaction(entry.FromFaction);
                                Team? atTeam = FindTeamByFaction(entry.AtFaction);
                                if (fromTeam == null || atTeam == null) continue;

                                Structure? atBase = FindBaseStructure(atTeam);
                                if (atBase == null) continue;

                                jobs.Add(new SpawnJob
                                {
                                    Label = $"cross {entry.FromFaction}@{entry.AtFaction}",
                                    LogLine = $"[STARTER] Cross-spawn: {entry.FromFaction} units at {entry.AtFaction} base: {{0}} units",
                                    Team = fromTeam,
                                    BasePos = atBase.transform.position,
                                    BaseForward = atBase.transform.forward,
                                    Units = BuildSpawnList(entry.Units, fromTeam, infoByName, 1),
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Error($"[STARTER] Cross-spawn error: {ex.Message}");
                    }
                }
            }
            return jobs;
        }

        /// <summary>
        /// Places (and with <paramref name="spawn"/> spawns) every unit of <paramref name="job"/> on the ring around the
        /// base: unit i of N at ring slot 360/N*i, validated and relocated by Placer.Place. Yields a frame whenever
        /// queries_per_frame physics/navmesh queries were used. A unit whose placement or spawn throws is logged and
        /// skipped; the loop goes on.
        /// </summary>
        private static IEnumerator RunPass(SpawnJob job, PlacementConfig cfg, string map, bool spawn, PassContext ctx,
            List<Placer.SpawnRecord>? records)
        {
            ctx.Map = map;
            ctx.Label = job.Label;
            ctx.BasePos = job.BasePos;
            ctx.BaseForward = job.BaseForward;
            ctx.BaseGroundY = Placer.TerrainHeight(job.BasePos) ?? job.BasePos.y;
            ctx.IgnoreUnits = !spawn;
            ctx.NavReady = Placer.NavReady(out ctx.NavState);
            string verb = spawn ? "placement" : "audit";
            var specs = new Dictionary<ObjectInfo, UnitSpec>();

            int total = job.Units.Count;
            int spawned = 0;
            for (int i = 0; i < total; i++)
            {
                var (name, info) = job.Units[i];
                try
                {
                    UnitSpec spec = Placer.GetSpec(name, info, specs);
                    PlaceResult r;
                    try { r = Placer.Place(spec, ctx, (360f / total) * i, cfg); }
                    catch (Exception ex)
                    {
                        // validation itself failed: keep the unit, 1.2.0 position
                        MelonLogger.Warning($"[STARTER] {verb} check failed for '{name}' #{i}: {ex.Message}; using the unchecked ring position");
                        r = LegacyPose(job, cfg, total, i);
                        ctx.Fallback++;
                    }
                    Placer.LogResult(ctx, verb, name, i, r, cfg.LogAll);

                    if (spawn)
                    {
                        if (job.Team == null) break;
                        GameObject go = Game.SpawnPrefab(info.Prefab, null, job.Team, r.Position, r.Rotation);
                        spawned++;
                        ctx.Spawned++;
                        Unit? unit = go != null ? go.GetComponent<Unit>() : null;
                        if (records != null && unit != null)
                            records.Add(new Placer.SpawnRecord { Unit = unit, Spec = spec, Label = job.Label, SpawnPos = r.Position });
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[STARTER] Spawn failed '{name}': {ex.Message}");
                }

                if (ctx.QueriesSinceYield >= cfg.QueriesPerFrame)
                {
                    ctx.QueriesSinceYield = 0;
                    yield return null;
                }
            }

            if (ctx.Fallback > 0) MelonLogger.Warning(ctx.Summary(verb));
            else MelonLogger.Msg(ctx.Summary(verb));
            if (spawn) MelonLogger.Msg(string.Format(job.LogLine, spawned));
        }

        private static PlaceResult LegacyPose(SpawnJob job, PlacementConfig cfg, int total, int i)
        {
            Vector3 dir = Quaternion.Euler(0f, (360f / total) * i, 0f) * job.BaseForward;
            Vector3 pos = job.BasePos + dir * cfg.RingRadius;
            pos.y = (Placer.TerrainHeight(pos) ?? pos.y) + 1f;
            return new PlaceResult
            {
                Planned = pos, Position = pos,
                Rotation = dir.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(dir, Vector3.up) : Quaternion.identity,
                Outcome = PlaceOutcome.Fallback, SlotReason = "check failed", FallbackReason = "check failed",
            };
        }

        /// <summary>Flattens the JSON list to individual spawns: the JSON counts first, then the 2v1 extras. The
        /// (mult - 1) x count extra units are added only while the team's vanilla unit cap for that unit type has room
        /// (UnitCapEntry.Max, 0 = no cap), counting the units already on the map and every starter before them.
        /// Skipped extras are logged. Unknown unit names are logged and skipped (as before).</summary>
        private static List<(string name, ObjectInfo info)> BuildSpawnList(List<UnitEntry> units, Team team,
            Dictionary<string, ObjectInfo> infoByName, int mult)
        {
            if (mult < 1) mult = 1;
            var spawnList = new List<(string name, ObjectInfo info)>();
            var extras = new List<(string name, ObjectInfo info)>();
            foreach (var entry in units)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Name)) continue;
                string name = entry.Name!;
                if (!infoByName.TryGetValue(name, out ObjectInfo unitInfo))
                {
                    MelonLogger.Warning($"[STARTER] Unit not found: '{name}'");
                    continue;
                }
                for (int i = 0; i < entry.Count; i++)
                    spawnList.Add((name, unitInfo));
                for (int i = 0; i < entry.Count * (mult - 1); i++)
                    extras.Add((name, unitInfo));
            }
            if (extras.Count > 0)
                AddExtrasWithinCap(team, spawnList, extras);
            return spawnList;
        }

        /// <summary>Appends the extras to spawnList while the team's unit cap per cap type has room (see BuildSpawnList).</summary>
        private static void AddExtrasWithinCap(Team team, List<(string name, ObjectInfo info)> spawnList, List<(string name, ObjectInfo info)> extras)
        {
            var projected = new Dictionary<UnitCapEntry, int>();
            int CapOf(ObjectInfo info, out UnitCapEntry? entry)
            {
                entry = null;
                try { entry = team.GetUnitTypeCapEntryForUnit(info); } catch { }
                if (entry == null) return 0;
                if (!projected.ContainsKey(entry)) projected[entry] = entry.Current;
                int max = 0; try { max = entry.Max; } catch { }
                return max;
            }
            try { team.UpdateUnitCapValues(); } catch { }
            foreach (var s in spawnList)
            {
                CapOf(s.info, out UnitCapEntry? e);
                if (e != null) projected[e] += Math.Max(0, s.info.UnitCapValue);
            }
            var skipped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var x in extras)
            {
                int max = CapOf(x.info, out UnitCapEntry? e);
                int cost = Math.Max(0, x.info.UnitCapValue);
                if (e != null && max > 0 && projected[e] + cost > max)
                {
                    skipped.TryGetValue(x.name, out int n);
                    skipped[x.name] = n + 1;
                    continue;
                }
                if (e != null) projected[e] += cost;
                spawnList.Add(x);
                added++;
            }
            string teamName = "";
            try { teamName = team.TeamShortName; } catch { }
            if (skipped.Count > 0)
                MelonLogger.Msg($"[StarterUnits] 2v1 extras for {teamName}: {added} added, skipped at the unit cap: " +
                                string.Join(", ", skipped.Select(kv => kv.Key + " x" + kv.Value)));
        }

        /// <summary>The team's HQ / Nest: the first structure whose ObjectInfo is Team.BaseStructure, else (as before
        /// 1.3.0) the first structure.</summary>
        private static Structure? FindBaseStructure(Team team)
        {
            Structure? first = null;
            try
            {
                ObjectInfo? baseInfo = null;
                try { baseInfo = team.BaseStructure; } catch { }
                foreach (var s in team.Structures)
                {
                    if (s == null) continue;
                    if (first == null) first = s;
                    if (baseInfo != null && s.ObjectInfo == baseInfo) return s;
                }
            }
            catch { }
            return first;
        }

        private static Team? FindTeamByFaction(string? faction)
        {
            if (string.IsNullOrEmpty(faction)) return null;
            faction = faction!.ToLowerInvariant();
            foreach (var t in Team.Teams)
            {
                if (t == null) continue;
                string name = (t.TeamName ?? "").ToLowerInvariant();
                if (faction == "sol" && name.Contains("sol")) return t;
                if (faction == "centauri" && name.Contains("centauri")) return t;
                if (faction == "alien" && name.Contains("alien") && !name.Contains("worm")) return t;
                if (faction == "wildlife" && (name.Contains("worm") || name.Contains("wildlife"))) return t;
            }
            return null;
        }

        // === Dry-run audit (!starteraudit) ===

        /// <summary>
        /// Evaluates the starter positions without spawning anything, against the static world only (units ignored).
        /// layouts = false: every faction config at its team's current base. layouts = true: every Si_MapBalance
        /// layout JSON in UserData/cfg/Spawns/&lt;map&gt;/ (first entry per faction, base forward +Z, base height =
        /// terrain). Detail lines + summaries go to the server log; <paramref name="reply"/> gets one line.
        /// </summary>
        internal static void StartAudit(bool layouts, Action<string> reply)
        {
            if (_auditRunning) { reply("an audit is already running"); return; }
            _auditRunning = true;
            MelonCoroutines.Start(AuditRoutine(layouts, reply));
        }

        private static IEnumerator AuditRoutine(bool layouts, Action<string> reply)
        {
            string map = Placer.MapName();
            PlacementConfig cfg = new PlacementConfig();
            var jobs = new List<SpawnJob>();
            string error = "";
            try
            {
                cfg = PlacementConfig.Load(ConfigDir);
                jobs = layouts ? CollectLayoutJobs(map, BuildInfoIndex(), out error) : CollectSpawnJobs(BuildInfoIndex());
            }
            catch (Exception ex) { error = ex.Message; }

            MelonLogger.Msg($"[STARTER] audit {map} ({(layouts ? "Si_MapBalance layouts" : "current bases")}): {jobs.Count} passes" + (error.Length > 0 ? " — " + error : ""));
            int units = 0, relocated = 0, fallback = 0;
            foreach (var job in jobs)
            {
                var ctx = new PassContext();
                IEnumerator pass = RunPass(job, cfg, map, spawn: false, ctx, null);
                while (true)
                {
                    bool more;
                    try { more = pass.MoveNext(); }
                    catch (Exception ex) { MelonLogger.Error($"[STARTER] audit {job.Label}: aborted: {ex.Message}"); break; }
                    if (!more) break;
                    yield return pass.Current;
                }
                units += ctx.Total; relocated += ctx.Relocated; fallback += ctx.Fallback;
                yield return null;
            }
            string line = $"audit {map}: {jobs.Count} passes, {units} units, {relocated} relocated, {fallback} fallback (details in the server log)";
            MelonLogger.Msg("[STARTER] " + line);
            _auditRunning = false;
            try { reply(error.Length > 0 && jobs.Count == 0 ? $"audit {map}: {error}" : line); } catch { }
        }

        /// <summary>
        /// Audit passes for every place a base can stand on <paramref name="map"/>, each faction with its own config:
        ///  1. the map's vanilla start points (MP_Strategy.TeamSetups[].BaseStructureStartPoints, position + forward;
        ///     Si_MapBalance may have moved one of them this round);
        ///  2. every Si_MapBalance layout in UserData/cfg/Spawns/&lt;map&gt;/, read as plain JSON (no reference to
        ///     Si_MapBalance): web-tool format ("Sol": [{"x":..,"z":..}], first entry, as Si_MapBalance uses it) and
        ///     legacy variants ("variant": {"Sol": {"x":..,"z":..}}); base forward +Z;
        ///  3. audit-only extra points in UserData/cfg/StarterUnits/audit/&lt;map&gt;/*.json, same format, every entry of
        ///     each faction array (for testing a base position no layout uses yet).
        /// Base height = terrain height.
        /// </summary>
        private static List<SpawnJob> CollectLayoutJobs(string map, Dictionary<string, ObjectInfo> infoByName, out string error)
        {
            error = "";
            var jobs = new List<SpawnJob>();

            var factionUnits = new Dictionary<string, List<(string, ObjectInfo)>>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in FactionFiles)
            {
                try
                {
                    string path = Path.Combine(ConfigDir, file);
                    if (!File.Exists(path)) continue;
                    var config = JsonConvert.DeserializeObject<StarterUnitsConfig>(File.ReadAllText(path));
                    if (config?.Units == null || config.Units.Count == 0) continue;
                    string faction = Path.GetFileNameWithoutExtension(file);
                    Team? team = FindTeamByFaction(faction);
                    if (team == null) continue;
                    var list = BuildSpawnList(config.Units, team, infoByName, 1);
                    if (list.Count > 0) factionUnits[faction] = list;
                }
                catch (Exception ex) { MelonLogger.Warning($"[STARTER] audit: {file}: {ex.Message}"); }
            }
            if (factionUnits.Count == 0) { error = "no faction config with units"; return jobs; }

            void Add(string label, string faction, Vector3 xz, Vector3 forward)
            {
                if (!factionUnits.TryGetValue(faction, out var units)) return;
                float? th = Placer.TerrainHeight(xz);
                if (!th.HasValue) { MelonLogger.Warning($"[STARTER] audit {label}: ({xz.x:F0},{xz.z:F0}) outside the terrain"); return; }
                jobs.Add(new SpawnJob
                {
                    Label = $"{label} {faction} @({xz.x:F0},{xz.z:F0})",
                    Team = null!,
                    BasePos = new Vector3(xz.x, th.Value, xz.z),
                    BaseForward = forward,
                    Units = units,
                });
            }

            // 1. vanilla start points
            try
            {
                if (GameMode.CurrentGameMode is MP_Strategy mp && mp.TeamSetups != null)
                {
                    foreach (var setup in mp.TeamSetups)
                    {
                        if (setup == null || setup.Team == null || setup.BaseStructureStartPoints == null) continue;
                        string? faction = TeamFaction(setup.Team);
                        if (faction == null) continue;
                        int k = 0;
                        foreach (var pt in setup.BaseStructureStartPoints)
                        {
                            if (pt == null) { k++; continue; }
                            Add($"startpoint {pt.name}#{k}", faction, pt.position, pt.forward);
                            k++;
                        }
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[STARTER] audit: start points: {ex.Message}"); }

            // 2. Si_MapBalance layouts (first entry per faction), 3. audit-only points (every entry)
            string layoutDir = Path.Combine(MelonEnvironment.UserDataDirectory, "cfg", "Spawns", map);
            string extraDir = Path.Combine(ConfigDir, "audit", map);
            foreach (var (dir, allEntries, tag) in new[] { (layoutDir, false, ""), (extraDir, true, "extra ") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var root = JObject.Parse(File.ReadAllText(file));
                        if (!(root["spawns"] is JObject spawns)) continue;
                        string layout = tag + Path.GetFileNameWithoutExtension(file);
                        foreach (var prop in spawns.Properties())
                        {
                            if (prop.Value is JArray arr)
                            {
                                string? faction = LayoutFaction(prop.Name);
                                if (faction == null) continue;
                                for (int i = 0; i < arr.Count && (allEntries || i == 0); i++)
                                    if (arr[i] is JObject e)
                                        Add(layout + (allEntries ? "#" + i : ""), faction, XZ(e), Vector3.forward);
                            }
                            else if (prop.Value is JObject variant)
                            {
                                foreach (var fp in variant.Properties())
                                {
                                    string? faction = LayoutFaction(fp.Name);
                                    if (faction != null && fp.Value is JObject e) Add(layout + "/" + prop.Name, faction, XZ(e), Vector3.forward);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { MelonLogger.Warning($"[STARTER] audit: {Path.GetFileName(file)}: {ex.Message}"); }
                }
            }
            if (jobs.Count == 0) error = "no start points, layouts or audit points for " + map;
            return jobs;
        }

        private static Vector3 XZ(JObject e) => new Vector3(e["x"]?.Value<float>() ?? 0f, 0f, e["z"]?.Value<float>() ?? 0f);

        private static string? TeamFaction(Team team)
        {
            string name = "";
            try { name = (team.TeamName ?? "").ToLowerInvariant(); } catch { }
            if (name.Contains("sol")) return "sol";
            if (name.Contains("centauri")) return "centauri";
            if (name.Contains("worm") || name.Contains("wildlife")) return "wildlife";
            if (name.Contains("alien")) return "alien";
            return null;
        }
        private static string? LayoutFaction(string key)
        {
            string k = key.ToLowerInvariant();
            if (k == "sol") return "sol";
            if (k == "cent" || k == "centauri") return "centauri";
            if (k == "alien" || k == "aliens") return "alien";
            if (k == "wildlife") return "wildlife";
            return null;
        }
    }
}
