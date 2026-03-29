/*
 Si_StarterUnits — Configurable starter unit spawns per faction
 Reads JSON configs from UserData/StarterUnits/
 Supports 4-way cross-spawns via Si_4way events.
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

[assembly: MelonInfo(typeof(Si_StarterUnits.StarterUnits), "Starter Units", "1.0.0", "DrMuck")]
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
        public string Name;
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
        public string FromFaction;  // units belong to this faction
        [JsonProperty("at_faction")]
        public string AtFaction;    // spawn at this faction's base
        [JsonProperty("units")]
        public List<UnitEntry> Units = new List<UnitEntry>();
    }

    public class StarterUnits : MelonMod
    {
        private static string ConfigDir => Path.Combine(MelonEnvironment.UserDataDirectory, "StarterUnits");
        private static bool _is4WayRound = false;
        private static bool _spawnScheduled = false;

        public override void OnInitializeMelon()
        {
            Directory.CreateDirectory(ConfigDir);
            MelonLogger.Msg("Si_StarterUnits v1.0.0 loaded. Configs: " + ConfigDir);
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
        private static IEnumerator SpawnAllStarters(float delay)
        {
            if (delay > 0f)
                yield return new WaitForSeconds(delay);

            MelonLogger.Msg("[STARTER] Spawning starter units...");

            // Cache ObjectInfo
            var allInfos = Resources.FindObjectsOfTypeAll<ObjectInfo>();
            var infoByName = new Dictionary<string, ObjectInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var info in allInfos)
            {
                if (info == null || info.Prefab == null) continue;
                string dn = info.DisplayName;
                if (!string.IsNullOrEmpty(dn) && !infoByName.ContainsKey(dn))
                    infoByName[dn] = info;
            }

            int totalSpawned = 0;

            // Spawn per-faction starters
            string[] factionFiles = { "sol.json", "centauri.json", "alien.json", "wildlife.json" };
            foreach (string file in factionFiles)
            {
                string path = Path.Combine(ConfigDir, file);
                if (!File.Exists(path)) continue;

                try
                {
                    var config = JsonConvert.DeserializeObject<StarterUnitsConfig>(File.ReadAllText(path));
                    if (config?.Units == null || config.Units.Count == 0) continue;

                    string factionName = Path.GetFileNameWithoutExtension(file);
                    Team team = FindTeamByFaction(factionName);
                    if (team == null) { MelonLogger.Warning($"[STARTER] Team not found for '{factionName}'"); continue; }

                    Structure baseStruct = team.Structures.Count > 0 ? team.Structures[0] : null;
                    if (baseStruct == null) { MelonLogger.Warning($"[STARTER] No base structure for '{factionName}'"); continue; }

                    int spawned = SpawnUnitsAroundStructure(config.Units, team, baseStruct, infoByName);
                    totalSpawned += spawned;
                    MelonLogger.Msg($"[STARTER] {factionName}: spawned {spawned} units");
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
                                Team fromTeam = FindTeamByFaction(entry.FromFaction);
                                Team atTeam = FindTeamByFaction(entry.AtFaction);
                                if (fromTeam == null || atTeam == null) continue;

                                Structure atBase = atTeam.Structures.Count > 0 ? atTeam.Structures[0] : null;
                                if (atBase == null) continue;

                                int spawned = SpawnUnitsAroundStructure(entry.Units, fromTeam, atBase, infoByName);
                                totalSpawned += spawned;
                                MelonLogger.Msg($"[STARTER] Cross-spawn: {entry.FromFaction} units at {entry.AtFaction} base: {spawned} units");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Error($"[STARTER] Cross-spawn error: {ex.Message}");
                    }
                }
            }

            MelonLogger.Msg($"[STARTER] Complete: {totalSpawned} total units spawned");
            yield break;
        }

        private static int SpawnUnitsAroundStructure(List<UnitEntry> units, Team team,
            Structure baseStruct, Dictionary<string, ObjectInfo> infoByName)
        {
            Vector3 hqPos = baseStruct.transform.position;
            Vector3 hqForward = baseStruct.transform.forward;
            float radius = 120f;

            // Flatten to individual spawns
            var spawnList = new List<(string name, ObjectInfo info)>();
            foreach (var entry in units)
            {
                if (!infoByName.TryGetValue(entry.Name, out ObjectInfo unitInfo))
                {
                    MelonLogger.Warning($"[STARTER] Unit not found: '{entry.Name}'");
                    continue;
                }
                for (int i = 0; i < entry.Count; i++)
                    spawnList.Add((entry.Name, unitInfo));
            }

            int total = spawnList.Count;
            int spawned = 0;

            for (int i = 0; i < total; i++)
            {
                try
                {
                    float angleDeg = (360f / total) * i;
                    Vector3 dir = Quaternion.Euler(0f, angleDeg, 0f) * hqForward;
                    Vector3 pos = hqPos + dir * radius;

                    // Sample terrain height
                    float bestY = pos.y;
                    foreach (var terrain in Terrain.activeTerrains)
                    {
                        if (terrain == null) continue;
                        var tp = terrain.transform.position;
                        var td = terrain.terrainData;
                        if (td == null) continue;
                        if (pos.x >= tp.x && pos.x <= tp.x + td.size.x &&
                            pos.z >= tp.z && pos.z <= tp.z + td.size.z)
                        {
                            bestY = terrain.SampleHeight(pos) + tp.y;
                            break;
                        }
                    }
                    pos.y = bestY + 1f;

                    Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
                    Game.SpawnPrefab(spawnList[i].info.Prefab, null, team, pos, rot);
                    spawned++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[STARTER] Spawn failed '{spawnList[i].name}': {ex.Message}");
                }
            }

            return spawned;
        }

        private static Team FindTeamByFaction(string faction)
        {
            if (string.IsNullOrEmpty(faction)) return null;
            faction = faction.ToLowerInvariant();
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
    }
}
