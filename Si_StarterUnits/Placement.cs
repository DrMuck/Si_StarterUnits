/*
 Si_StarterUnits 1.3.1 — placement validation for starter units.

 Every starter gets a position that is checked before Game.SpawnPrefab:
   1. Landing: Physics.BoxCast straight down with the unit's box (the prefab's solid colliders, ObjectInfo.PhysicalBounds
      as fallback; widened by clearance_margin_m) against GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC, triggers ignored.
      Same cast + mask the game uses to drop its own initial units (StrategyTeamSetup.SpawnAIUnits), teleported units
      (TeleportUI) and units that fell through the ground (Unit.UpdateFallThroughGround). The box ends in contact with
      the first static surface under it and overlaps nothing on those layers. A Physics.CheckBox at the cast origin
      guards against the one blind spot of a cast (colliders that already contain the start box are not reported).
      The cast comes from above, so a unit always lands ON the top surface (IndustrialQuarter's metal floor), never on
      the heightmap underneath it.
   2. Level: the landing surface may be at most max_step_from_base_m above/below the BASE GROUND = the surface the base
      actually stands on (ray down from the base, base structures and units ignored; 1.3.0 took the heightmap there,
      which is 13 m under an IndustrialQuarter platform and ~100 m under a Citadel plateau).
      Elevated surface (1.3.1): a non-terrain landing more than max_height_above_terrain_m above BOTH the heightmap at
      the spot and the base ground (roof, tank, catwalk, pillar or rock top) is accepted only when the ground-vehicle
      navmesh ("Small Vehicle") has a node there on its main component, i.e. it is drivable ground connected to the
      map. Only for units whose own graph cannot tell (alien Voxel Graph, which covers walls and roofs; flying units);
      vehicles and infantry are judged by their own graph in 4.
   3. Clearance: Physics.OverlapBox of the landed box against static world layers + units (Character, Vehicle,
      Dynamic) + Invisible Walls, triggers ignored; plus a 2D spacing check against the starters already placed in
      this pass (radius + radius + unit_gap_m).
   4. Navmesh (not for flying units): the nearest walkable node of the unit's own A* graph (Seeker.graphMask, as
      GameAI.GetPointValidForAI / Unit.GetFallThroughNavmeshLanding / MP_Strategy.GetStartPointOffNavmesh query it)
      within nav_max_horizontal_delta_m / nav_max_height_delta_m (+ voxel size for the alien point graph). With
      nav_island_check the node must lie in the REFERENCE component: the graph's main component (most walkable nodes)
      when a probe around the base on the base's level reaches it, else the component of the base vote. The reference
      is kept as a node and compared at query time (A* renumbers GraphNode.Area after graph updates). Roof, tank and
      pillar tops are separate small components (or have no node at all) and fail here.
 The search starts at the 1.2.0 ring slot and walks outwards in tiers around it (radius limits min_radius..max_radius
 from the base), first valid spot wins. No valid spot within max_candidates_per_unit: best candidate (least severe
 failure, then closest to the slot) with a warning, always at its landed pose. A unit is never dropped.
*/

using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using Newtonsoft.Json;
using Pathfinding;
using UnityEngine;

namespace Si_StarterUnits
{
    /// <summary>UserData/cfg/StarterUnits/placement.json. Missing file = these defaults (and the file is written once).</summary>
    public class PlacementConfig
    {
        [JsonProperty("validate_placement")] public bool Enabled = true;
        [JsonProperty("ring_radius_m")] public float RingRadius = 120f;
        [JsonProperty("min_radius_m")] public float MinRadius = 60f;
        [JsonProperty("max_radius_m")] public float MaxRadius = 250f;
        [JsonProperty("max_candidates_per_unit")] public int MaxCandidates = 64;
        [JsonProperty("clearance_margin_m")] public float ClearanceMargin = 1.0f;
        [JsonProperty("unit_gap_m")] public float UnitGap = 2.0f;
        [JsonProperty("nav_max_horizontal_delta_m")] public float NavMaxHorizontal = 3.0f;
        [JsonProperty("nav_max_height_delta_m")] public float NavMaxHeight = 3.0f;
        [JsonProperty("nav_island_check")] public bool NavIslandCheck = true;
        /// <summary>1.3.1: a non-terrain landing more than this above both the heightmap at the spot and the base
        /// ground is an "elevated surface"; for alien ground and flying units it must be on the main ground-vehicle
        /// navmesh (else roof / tank / pillar top). Vehicles and infantry: not used (their own navmesh decides).
        /// (1.3.0: hard limit above the heightmap for every unit.)</summary>
        [JsonProperty("max_height_above_terrain_m")] public float MaxAboveTerrain = 4f;
        /// <summary>Landing ground more than this above or below the base ground (the surface the base stands on;
        /// 1.3.0: the heightmap at the base) = other level.</summary>
        [JsonProperty("max_step_from_base_m")] public float MaxStepFromBase = 25f;
        /// <summary>TEST ONLY (1.3.1): live spawns use the 1.2.0 placement exactly (ring slot, heightmap + 1 m, no
        /// checks), which puts units under IndustrialQuarter's metal floor. Exists so the post-check's under-floor
        /// test can be shown to fire. Never enable on a played server. The audit ignores it.</summary>
        [JsonProperty("test_legacy_heightmap_placement")] public bool LegacyHeightmap = false;
        [JsonProperty("post_check_delay_s")] public float PostCheckDelay = 5f;
        [JsonProperty("queries_per_frame")] public int QueriesPerFrame = 200;
        /// <summary>Runs the layout audit (as "!starteraudit layouts") once per map per server run, after the
        /// post-check. For servers nobody types on; off by default (it costs CPU at round start).</summary>
        [JsonProperty("auto_audit_layouts")] public bool AutoAuditLayouts = false;
        /// <summary>Also log one line per unit placed as planned (landing surface, height above terrain).</summary>
        [JsonProperty("log_all_placements")] public bool LogAll = false;

        internal const string FileName = "placement.json";

        internal static PlacementConfig Load(string dir)
        {
            var cfg = new PlacementConfig();
            string path = System.IO.Path.Combine(dir, FileName);
            try
            {
                if (File.Exists(path))
                    cfg = JsonConvert.DeserializeObject<PlacementConfig>(File.ReadAllText(path)) ?? new PlacementConfig();
                else
                {
                    File.WriteAllText(path, JsonConvert.SerializeObject(cfg, Formatting.Indented));
                    MelonLogger.Msg("[STARTER] Wrote default " + path);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[STARTER] " + FileName + " unreadable (" + ex.Message + "), using defaults");
                cfg = new PlacementConfig();
            }
            cfg.Clamp();
            return cfg;
        }

        void Clamp()
        {
            RingRadius = Mathf.Clamp(Fin(RingRadius, 120f), 10f, 1000f);
            MinRadius = Mathf.Clamp(Fin(MinRadius, 60f), 0f, RingRadius);
            MaxRadius = Mathf.Clamp(Fin(MaxRadius, 250f), RingRadius, 2000f);
            MaxCandidates = Math.Max(1, Math.Min(MaxCandidates, 200));
            ClearanceMargin = Mathf.Clamp(Fin(ClearanceMargin, 1f), 0f, 10f);
            UnitGap = Mathf.Clamp(Fin(UnitGap, 2f), 0f, 50f);
            NavMaxHorizontal = Mathf.Clamp(Fin(NavMaxHorizontal, 3f), 0.5f, 50f);
            NavMaxHeight = Mathf.Clamp(Fin(NavMaxHeight, 3f), 0.5f, 50f);
            MaxAboveTerrain = Mathf.Clamp(Fin(MaxAboveTerrain, 4f), 0.5f, 1000f);
            MaxStepFromBase = Mathf.Clamp(Fin(MaxStepFromBase, 25f), 1f, 1000f);
            PostCheckDelay = Mathf.Clamp(Fin(PostCheckDelay, 5f), 0f, 120f);
            QueriesPerFrame = Math.Max(20, Math.Min(QueriesPerFrame, 100000));
        }

        static float Fin(float v, float d) => float.IsNaN(v) || float.IsInfinity(v) ? d : v;
    }

    /// <summary>Size, graph and flying flag of one starter unit type, resolved once per pass.</summary>
    internal sealed class UnitSpec
    {
        public string Name = "";
        public ObjectInfo Info = null!;
        public GameObject Prefab = null!;
        public Vector3 Extents;       // half extents of the physical box, local
        public Vector3 Center;        // centre of the physical box, local to the pivot
        public string SizeSource = "";
        public bool Flying;
        public GraphMask Mask = GraphMask.everything;
        public string MaskSource = "all graphs";
        public float NavSlack;        // extra snap tolerance: node spacing of point graphs (alien VoxelGraph)
        public float Radius => Mathf.Max(Extents.x, Extents.z);
    }

    internal enum PlaceOutcome { AsPlanned, Relocated, Fallback, Unchecked }

    internal struct PlaceResult
    {
        public Vector3 Planned;
        public Vector3 Position;
        public Quaternion Rotation;
        public PlaceOutcome Outcome;
        public string SlotReason;      // why the planned slot was rejected ("" when it was fine)
        public string FallbackReason;  // why the fallback candidate is not valid either
        public string Landed;          // landing surface of the chosen candidate and its height above the terrain
        public int Candidates;
    }

    /// <summary>State of one placement pass (one team at one base).</summary>
    internal sealed class PassContext
    {
        public string Map = "";
        public string Label = "";
        public Vector3 BasePos;
        public float BaseGroundY;  // surface the base stands on (Placer.BaseGround; an HQ may be spawned raised)
        public string BaseGroundInfo = "";
        public Vector3 BaseForward = Vector3.forward;
        public bool IgnoreUnits;   // audit: judge the static world only
        public bool Audit;         // audit: also test the 1.2.0 slot and the chosen spot for "under a floor"
        public readonly List<(Vector3 pos, float r, string name)> Placed = new List<(Vector3, float, string)>();
        /// <summary>Reference node per graph mask (null = no island test); its Area is read at query time.</summary>
        public readonly Dictionary<int, (GraphNode? node, string how)> RefNodes = new Dictionary<int, (GraphNode?, string)>();
        public int AsPlanned, Relocated, Fallback, Unchecked, Spawned;
        public int LegacyUnder, ChosenUnder;
        public int Queries, QueriesSinceYield;
        public bool NavReady;
        public string NavState = "";
        public int Total => AsPlanned + Relocated + Fallback + Unchecked;

        public string Summary(string verb) =>
            $"[STARTER] {verb} {Map} {Label}: {Total} units, {AsPlanned} as planned, {Relocated} relocated, {Fallback} fallback" +
            (Unchecked > 0 ? $", {Unchecked} unchecked (validate_placement off / test placement)" : "") +
            (Audit ? $", under a floor: 1.2.0 slot {LegacyUnder}, chosen {ChosenUnder}" : "") +
            $" ({Queries} queries, base ground {BaseGroundInfo}{(NavReady ? "" : ", no navmesh test: " + NavState)})";
    }

    internal static class Placer
    {
        const float CastHeight = 150f;
        const float CastRaise = 250f;
        const float Lift = 0.25f;
        const float NavSearch = 15f;
        const float NavRefSearch = 80f;
        // Failure severities (lower = closer to valid). Candidates >= NoPose have no landed pose. 1.3.1: another level
        // on the reference component (can drive back) is less bad than an elevated top or an isolated navmesh piece.
        const int SevUnits = 3, SevStep = 4, SevRoof = 5, SevIsland = 6, SevNav = 6, SevStatic = 7, SevNoPose = 8, SevSpacing = 9;
        const float SurfaceRayUp = 500f;

        static readonly Collider[] s_Buf = new Collider[16];
        static readonly float[] s_Tiers = { 12f, 25f, 40f, 60f, 85f, 115f, 150f, 190f };
        static readonly float[] s_TierAngles = { 90f, -90f, 0f, 180f, 45f, -45f, 135f, -135f };
        static readonly Dictionary<ObjectInfo, GraphMask> s_LearnedMasks = new Dictionary<ObjectInfo, GraphMask>();
        static readonly HashSet<string> s_SpecLogged = new HashSet<string>();

        static bool s_MasksReady;
        static int s_CastMask, s_StaticMask, s_UnitMask, s_WallMask, s_SurfaceSkip, s_TerrainLayer = -1;

        internal static void EnsureMasks()
        {
            if (s_MasksReady) return;
            s_CastMask = GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC;
            int terrain = GamePhysics.TRACELAYERMASK_TERRAIN;
            int nonBlocking = LayerMask.GetMask("Water", "No Collide", "Gizmos", "UI");
            s_StaticMask = s_CastMask & ~terrain & ~nonBlocking;
            s_UnitMask = LayerMask.GetMask("Character", "Vehicle", "Dynamic");
            s_WallMask = LayerMask.GetMask("Invisible Walls");
            // not ground for the surface ray: AI steering volumes (palms on CrimsonPeak) and non-blocking layers
            s_SurfaceSkip = nonBlocking | LayerMask.GetMask("AIVehicleObstacle");
            s_TerrainLayer = GamePhysics.LAYER_TERRAIN;
            s_MasksReady = true;
        }

        // ---------------------------------------------------------------- surface (1.3.1)

        static readonly RaycastHit[] s_SurfHits = new RaycastHit[32];
        static readonly Dictionary<int, bool> s_IsGoCol = new Dictionary<int, bool>();

        static bool IsGameObjectCollider(Collider c)
        {
            int id = c.GetInstanceID();
            if (s_IsGoCol.TryGetValue(id, out bool v)) return v;
            try { v = c.GetComponentInParent<BaseGameObject>() != null; } catch { v = false; }
            if (s_IsGoCol.Count > 20000) s_IsGoCol.Clear();
            s_IsGoCol[id] = v;
            return v;
        }

        /// <summary>First world surface hit by a ray straight down from <paramref name="fromY"/> over
        /// <paramref name="dist"/> m (IGNOREDYNAMIC, triggers ignored), skipping colliders of BaseGameObjects (base
        /// structures, units) and non-ground layers. This is the surface a unit stands on there: the metal floor on
        /// IndustrialQuarter, not the heightmap under it.</summary>
        internal static bool SurfaceHit(float x, float z, float fromY, float dist, out RaycastHit best)
        {
            EnsureMasks();
            best = default;
            int n = Physics.RaycastNonAlloc(new Vector3(x, fromY, z), Vector3.down, s_SurfHits, dist, s_CastMask & ~s_SurfaceSkip, QueryTriggerInteraction.Ignore);
            float bd = float.MaxValue; bool any = false;
            for (int i = 0; i < n; i++)
            {
                var h = s_SurfHits[i];
                if (h.collider == null || h.distance >= bd) continue;
                if (h.collider.gameObject.layer != s_TerrainLayer && IsGameObjectCollider(h.collider)) continue;
                bd = h.distance; best = h; any = true;
            }
            return any;
        }

        /// <summary>Top surface at x/z (ray from 500 m above the heightmap / refY), else null.</summary>
        internal static float? SurfaceY(float x, float z, float refY)
        {
            float? th = TerrainHeight(new Vector3(x, 0f, z));
            float top = Mathf.Max(th ?? refY, refY) + SurfaceRayUp;
            return SurfaceHit(x, z, top, SurfaceRayUp * 2f + 100f, out RaycastHit h) ? h.point.y : (float?)null;
        }

        /// <summary>Ground the base stands on: first world surface below the base pivot (from 10 m above it, base
        /// structures ignored; human HQs are spawned some metres above the floor). Never the bare heightmap when a
        /// surface exists; no hit at all: the pivot height.</summary>
        internal static float BaseGround(Vector3 basePos, out string info)
        {
            float? th = TerrainHeight(basePos);
            if (SurfaceHit(basePos.x, basePos.z, basePos.y + 10f, 120f, out RaycastHit h))
            {
                bool terr = h.collider.gameObject.layer == s_TerrainLayer;
                info = $"{h.point.y:F0} ({(terr ? "terrain" : Describe(h.collider))}, {h.point.y - (th ?? h.point.y):F1} m above the heightmap)";
                return h.point.y;
            }
            info = $"{basePos.y:F0} (no surface under the base, pivot height)";
            return basePos.y;
        }

        /// <summary>"" or why a unit whose feet are at <paramref name="feetY"/> and whose top is
        /// <paramref name="height"/> m above them is UNDER a walkable static surface at x/z: the first surface from
        /// above is not terrain, faces up (normal.y > 0.7) and lies more than 0.5 m above the unit's top. The
        /// IndustrialQuarter fault of 1.2.0: heightmap + 1 m under a 13 m metal floor.</summary>
        internal static string UnderFloor(float x, float feetY, float z, float height)
        {
            float? th = TerrainHeight(new Vector3(x, 0f, z));
            float top = Mathf.Max(th ?? feetY, feetY) + SurfaceRayUp;
            if (!SurfaceHit(x, z, top, SurfaceRayUp + 50f + (top - feetY), out RaycastHit h)) return "";
            if (h.collider.gameObject.layer == s_TerrainLayer || h.normal.y < 0.7f) return "";
            float above = h.point.y - feetY;
            if (above <= height + 0.5f) return "";
            string feet = th.HasValue && Mathf.Abs(feetY - th.Value) < 3f ? ", feet on the heightmap" : th.HasValue ? $", feet {feetY - th.Value:F1} m above the heightmap" : "";
            return $"under a floor: {Describe(h.collider)} {above:F1} m above its feet{feet}";
        }

        // ---------------------------------------------------------------- helpers

        internal static string MapName()
        {
            try { string m = NetworkGameServer.GetServerMap(); if (!string.IsNullOrEmpty(m)) return m; } catch { }
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
            return "?";
        }

        internal static float? TerrainHeight(Vector3 pos)
        {
            foreach (var terrain in Terrain.activeTerrains)
            {
                if (terrain == null) continue;
                var tp = terrain.transform.position;
                var td = terrain.terrainData;
                if (td == null) continue;
                if (pos.x >= tp.x && pos.x <= tp.x + td.size.x && pos.z >= tp.z && pos.z <= tp.z + td.size.z)
                    return terrain.SampleHeight(pos) + tp.y;
            }
            return null;
        }

        internal static string V(Vector3 v) => $"({v.x:F0},{v.y:F0},{v.z:F0})";

        static float Dist2D(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        internal static string Describe(Collider c)
        {
            if (c == null) return "?";
            string layer = "";
            try { layer = LayerMask.LayerToName(c.gameObject.layer); } catch { }
            if (c.gameObject.layer == s_TerrainLayer) return "terrain";
            string owner = "";
            try
            {
                var bgo = c.GetComponentInParent<BaseGameObject>();
                if (bgo != null && bgo.ObjectInfo != null) owner = bgo.ObjectInfo.DisplayName;
            }
            catch { }
            return owner.Length > 0 ? $"'{c.name}' of {owner} [{layer}]" : $"'{c.name}' [{layer}]";
        }

        internal static bool NavReady(out string state)
        {
            try
            {
                if (AstarPath.active == null) { state = "no AstarPath"; return false; }
                var rs = GameAI.ReloadState;
                state = rs.ToString();
                return rs == ENavmeshReloadState.Ready;
            }
            catch (Exception ex) { state = ex.GetType().Name; return false; }
        }

        static NNInfo Nearest(Vector3 p, GraphMask mask, float maxDist)
        {
            var c = new NearestNodeConstraint { graphMask = mask, area = -1, tags = -1, maxDistance = maxDist };
            return AstarPath.active.GetNearest(p, c);
        }

        internal static bool TryGetUnitMask(Unit u, out GraphMask mask)
        {
            mask = GraphMask.everything;
            try
            {
                var agent = u.AIAgent;
                if (agent == null) return false;
                var pf = agent.AgentPathfinding;
                if (pf == null) return false;
                var seeker = pf.PathfindingSeeker;
                if (seeker == null) return false;
                mask = seeker.graphMask;
                return true;
            }
            catch { return false; }
        }

        internal static void LearnMask(Unit u)
        {
            try
            {
                if (u == null || u.ObjectInfo == null) return;
                if (TryGetUnitMask(u, out var m) && !m.containsAllGraphs) s_LearnedMasks[u.ObjectInfo] = m;
            }
            catch { }
        }

        /// <summary>Snap slack for graphs made of point nodes: the alien NavMeshGenerator.VoxelGraph.VoxelGraph
        /// returns the nearest node centre (NNInfo.position = node position), which lies up to about one voxel from
        /// the surface. Slack = VoxelSize + |SurfaceOffset| of the voxel graphs in the mask (read by reflection, so the
        /// build does not depend on that type); 0 for navmesh graphs, which return the closest point on the surface.</summary>
        static float GraphSlack(GraphMask mask)
        {
            float slack = 0f;
            try
            {
                var graphs = AstarPath.active != null ? AstarPath.active.data.graphs : null;
                if (graphs == null) return 0f;
                for (int i = 0; i < graphs.Length && i < 32; i++)
                {
                    var g = graphs[i];
                    if (g == null || ((mask.value >> i) & 1) == 0) continue;
                    if (g.GetType().Name != "VoxelGraph") continue;
                    object? cf = g.GetType().GetField("Config")?.GetValue(g);
                    if (cf == null) continue;
                    float vs = Convert.ToSingle(cf.GetType().GetField("VoxelSize")?.GetValue(cf) ?? 0f);
                    float so = Convert.ToSingle(cf.GetType().GetField("SurfaceOffset")?.GetValue(cf) ?? 0f);
                    if (vs > 0f && vs < 50f) slack = Mathf.Max(slack, vs + Mathf.Abs(so));
                }
            }
            catch { }
            return slack;
        }

        static string GraphName(GraphMask mask)
        {
            try
            {
                var named = GameAI.NamedGraphMasks;
                if (named != null)
                    foreach (var kv in named)
                        if (kv.Value.value == mask.value) return kv.Key;
            }
            catch { }
            return mask.containsAllGraphs ? "all graphs" : "mask " + mask.value;
        }

        // ---------------------------------------------------------------- main component (1.3.1)

        static AstarPath? s_MainAstar;
        static readonly Dictionary<int, (GraphNode? node, string info)> s_MainRef = new Dictionary<int, (GraphNode?, string)>();

        static bool Alive(GraphNode? n)
        {
            if (n == null) return false;
            try { return !n.Destroyed && n.Walkable; } catch { return false; }
        }

        internal static uint? AreaOf(GraphNode? n)
        {
            if (!Alive(n)) return null;
            try { return n!.Area; } catch { return null; }
        }

        /// <summary>Main connected component of the graphs in the mask (the GraphNode.Area with the most walkable
        /// nodes), returned as one REFERENCE NODE of it, as Si_MapBalance's SiteTest does: A* renumbers areas after
        /// graph updates (CrystalChasm: one component seen as 13955, 13957, 13959), so callers compare a node's Area
        /// with the reference node's Area read at the same moment. Cached per AstarPath instance and mask; recomputed
        /// when the reference node was destroyed (tile rebuilt by a navmesh cut).</summary>
        internal static GraphNode? MainRef(GraphMask mask, out string info)
        {
            if (!ReferenceEquals(s_MainAstar, AstarPath.active)) { s_MainRef.Clear(); s_MainAstar = AstarPath.active; }
            if (s_MainRef.TryGetValue(mask.value, out var cached) && (cached.node == null || Alive(cached.node)))
            { info = cached.info; return cached.node; }
            var counts = new Dictionary<uint, int>();
            var sample = new Dictionary<uint, GraphNode>();
            int total = 0;
            try
            {
                var graphs = AstarPath.active.data.graphs;
                for (int i = 0; i < graphs.Length && i < 32; i++)
                {
                    var g = graphs[i];
                    if (g == null || ((mask.value >> i) & 1) == 0) continue;
                    g.GetNodes(n =>
                    {
                        if (n == null || !n.Walkable) return;
                        uint a = n.Area;
                        counts.TryGetValue(a, out int c);
                        counts[a] = c + 1;
                        if (!sample.ContainsKey(a)) sample[a] = n;
                        total++;
                    });
                }
            }
            catch (Exception ex) { info = "node walk failed: " + ex.Message; s_MainRef[mask.value] = (null, info); return null; }
            uint bestA = 0; int bestN = 0;
            foreach (var kv in counts) if (kv.Value > bestN) { bestN = kv.Value; bestA = kv.Key; }
            GraphNode? main = bestN > 0 ? sample[bestA] : null;
            info = $"{total} walkable nodes, {counts.Count} components, main {bestN}";
            s_MainRef[mask.value] = (main, info);
            return main;
        }

        static bool s_VehMaskResolved;
        static GraphMask s_VehMask;
        static bool s_HasVehMask;

        /// <summary>The ground-vehicle graph ("Small Vehicle"), the oracle for "is this elevated surface drivable
        /// ground" for units whose own graph covers walls and roofs (alien Voxel Graph) or that fly.</summary>
        static bool VehicleMask(out GraphMask m)
        {
            if (!s_VehMaskResolved)
            {
                try
                {
                    var named = GameAI.NamedGraphMasks;
                    if (named != null && named.TryGetValue("Small Vehicle", out var v)) { s_VehMask = v; s_HasVehMask = true; s_VehMaskResolved = true; }
                }
                catch { }
            }
            m = s_VehMask;
            return s_HasVehMask;
        }

        // ---------------------------------------------------------------- unit spec

        internal static UnitSpec GetSpec(string name, ObjectInfo info, Dictionary<ObjectInfo, UnitSpec> cache)
        {
            if (cache.TryGetValue(info, out var spec)) return spec;
            spec = new UnitSpec { Name = name, Info = info, Prefab = info.Prefab };

            // Size: the prefab's solid colliders (what actually collides), else ObjectInfo.PhysicalBounds (the box the
            // game itself casts with), else PhysicalRadius, else per class.
            Bounds b = default;
            try { b = info.PhysicalBounds; } catch { }
            Vector3 e = b.extents;
            string pbTxt = $"PhysicalBounds {e.x * 2f:F1}x{e.y * 2f:F1}x{e.z * 2f:F1} c{b.center.y:F1}";
            if (TryPrefabColliderBox(spec.Prefab, out Bounds cb, out int nCol) && Ok(cb.extents.x) && Ok(cb.extents.y) && Ok(cb.extents.z))
            {
                spec.Extents = cb.extents;
                spec.Center = cb.center;
                spec.SizeSource = $"{nCol} prefab colliders, c{cb.center.y:F1}; {pbTxt}";
            }
            else if (Ok(e.x) && Ok(e.y) && Ok(e.z))
            {
                spec.Extents = e;
                spec.Center = b.center;
                spec.SizeSource = pbTxt;
            }
            else
            {
                float r = 0f;
                try { r = info.PhysicalRadius; } catch { }
                if (Ok(r) && r > 0.3f)
                {
                    spec.Extents = new Vector3(r, Mathf.Min(r, 6f), r);
                    spec.SizeSource = "PhysicalRadius";
                }
                else
                {
                    float f = ClassFallback(info);
                    spec.Extents = new Vector3(f, Mathf.Min(f, 3f), f);
                    spec.SizeSource = "class fallback";
                }
                spec.Center = new Vector3(0f, spec.Extents.y, 0f);
            }

            // Flying: the prefab's Unit.IsFlyingType (VehicleAir, flying CreatureDecapod) or UnitType Aircraft.
            try { var u = spec.Prefab != null ? spec.Prefab.GetComponent<Unit>() : null; if (u != null && u.IsFlyingType) spec.Flying = true; } catch { }
            try { if (info.UnitType == UnitType.Aircraft) spec.Flying = true; } catch { }

            // Graph: learned from a live unit of this type, else a live unit now, else the prefab's Seeker, else by class.
            if (s_LearnedMasks.TryGetValue(info, out var learned)) { spec.Mask = learned; spec.MaskSource = "live unit"; }
            else
            {
                bool found = false;
                try
                {
                    foreach (var u in Unit.Units)
                    {
                        if (u == null || u.ObjectInfo != info) continue;
                        if (TryGetUnitMask(u, out var m) && !m.containsAllGraphs)
                        {
                            spec.Mask = m; spec.MaskSource = "live unit"; s_LearnedMasks[info] = m; found = true;
                            break;
                        }
                    }
                }
                catch { }
                if (!found)
                {
                    try
                    {
                        var seeker = spec.Prefab != null ? spec.Prefab.GetComponentInChildren<Seeker>(true) : null;
                        if (seeker != null && !seeker.graphMask.containsAllGraphs)
                        { spec.Mask = seeker.graphMask; spec.MaskSource = "prefab Seeker"; found = true; }
                    }
                    catch { }
                }
                if (!found)
                {
                    try
                    {
                        string g = info.UnitType == UnitType.Infantry ? "Soldier" : "Small Vehicle";
                        var named = GameAI.NamedGraphMasks;
                        if (named != null && named.TryGetValue(g, out var m)) { spec.Mask = m; spec.MaskSource = "class fallback"; }
                    }
                    catch { }
                }
            }

            if (!spec.Flying) spec.NavSlack = GraphSlack(spec.Mask);
            cache[info] = spec;
            if (s_SpecLogged.Add(name.ToLowerInvariant() + "|" + spec.MaskSource))
            {
                var ex = spec.Extents;
                MelonLogger.Msg($"[STARTER] unit '{name}': box {ex.x * 2f:F1}x{ex.y * 2f:F1}x{ex.z * 2f:F1} m ({spec.SizeSource})" +
                                (spec.Flying ? ", flying (no navmesh test)" : $", graph '{GraphName(spec.Mask)}' ({spec.MaskSource}" + (spec.NavSlack > 0f ? $", node spacing slack {spec.NavSlack:F1} m" : "") + ")"));
            }
            return spec;
        }

        static bool Ok(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && v >= 0.1f && v <= 60f;

        static int s_SolidDeny = -1;

        /// <summary>Layers whose non-trigger colliders do not take part in movement collisions (hit zones, damage
        /// bounds, avoidance volumes...). Taken from the exclusions of GamePhysics.TRACELAYERMASK_MOVEMENT plus the
        /// avoidance layers.</summary>
        static bool IsSolidLayer(int layer)
        {
            if (s_SolidDeny == -1)
                s_SolidDeny = LayerMask.GetMask("Ignore Raycast", "Ignore Raycast Avoidance", "Single Object Raycast", "Trigger",
                    "AIObstacle", "Zone", "CharacterHitZone", "Debris", "CollideWithStatic", "Damage Bounds", "Water", "SelfOnly",
                    "SelfOnly Avoidance", "Vehicle Avoidance", "Invisible Walls Avoidance", "No Collide", "UI", "Gizmos");
            return ((1 << layer) & s_SolidDeny) == 0;
        }

        /// <summary>Axis-aligned box (in the prefab root's local space) around the prefab's solid colliders, computed
        /// from the collider shapes (Collider.bounds is not valid on a prefab asset).</summary>
        static bool TryPrefabColliderBox(GameObject prefab, out Bounds box, out int count)
        {
            box = default; count = 0;
            if (prefab == null) return false;
            try
            {
                Transform root = prefab.transform;
                bool any = false;
                foreach (var c in prefab.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null || c.isTrigger || !IsSolidLayer(c.gameObject.layer)) continue;
                    Vector3 lc, le;
                    if (c is BoxCollider bc) { lc = bc.center; le = bc.size * 0.5f; }
                    else if (c is SphereCollider sc) { lc = sc.center; le = Vector3.one * sc.radius; }
                    else if (c is CapsuleCollider cc)
                    {
                        lc = cc.center; le = Vector3.one * cc.radius;
                        float h = Mathf.Max(cc.height * 0.5f, cc.radius);
                        if (cc.direction == 0) le.x = h; else if (cc.direction == 1) le.y = h; else le.z = h;
                    }
                    else if (c is MeshCollider mc && mc.sharedMesh != null) { lc = mc.sharedMesh.bounds.center; le = mc.sharedMesh.bounds.extents; }
                    else continue;
                    Transform t = c.transform;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 corner = lc + new Vector3((i & 1) == 0 ? -le.x : le.x, (i & 2) == 0 ? -le.y : le.y, (i & 4) == 0 ? -le.z : le.z);
                        Vector3 p = root.InverseTransformPoint(t.TransformPoint(corner));
                        if (!any) { box = new Bounds(p, Vector3.zero); any = true; }
                        else box.Encapsulate(p);
                    }
                    count++;
                }
                return any;
            }
            catch { return false; }
        }

        static float ClassFallback(ObjectInfo info)
        {
            UnitType t = UnitType.None;
            try { t = info.UnitType; } catch { }
            switch (t)
            {
                case UnitType.Infantry: return 0.75f;
                case UnitType.Cavalry: return 2.5f;
                case UnitType.Tank: return 4f;
                case UnitType.Siege: return 5f;
                case UnitType.Aircraft: return 6f;
                case UnitType.Transport: return 5f;
                case UnitType.Harvester: return 8f;
                default: return 4f;
            }
        }

        // ---------------------------------------------------------------- placement

        /// <summary>Places unit <paramref name="u"/> for ring slot angle <paramref name="slotAngle"/> (degrees from
        /// the base's forward). Never throws for game-side failures: the caller still wraps it.</summary>
        internal static PlaceResult Place(UnitSpec u, PassContext ctx, float slotAngle, PlacementConfig cfg)
        {
            Vector3 fwd = ctx.BaseForward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            Vector3 slotDir = Quaternion.Euler(0f, slotAngle, 0f) * fwd;
            Vector3 slot = ctx.BasePos + slotDir * cfg.RingRadius;
            EnsureMasks();
            float? slotTerrain = TerrainHeight(slot);
            // 1.3.1: the slot's height is the top surface there (metal floor, rock plateau), never the heightmap under it
            slot.y = SurfaceY(slot.x, slot.z, Mathf.Max(ctx.BaseGroundY, slotTerrain ?? ctx.BaseGroundY)) ?? Mathf.Max(slotTerrain ?? ctx.BaseGroundY, ctx.BaseGroundY);

            // ring slot on the top surface + 1 m, facing outwards (= 1.2.0 wherever the heightmap is the surface)
            var res = new PlaceResult
            {
                Planned = slot,
                Position = slot + Vector3.up * 1f,
                Rotation = Quaternion.LookRotation(slotDir, Vector3.up),
                SlotReason = "",
                FallbackReason = "",
            };
            if (ctx.Audit)
            {
                // the 1.2.0 position (heightmap + 1 m): under a floor?
                if (slotTerrain.HasValue && UnderFloor(slot.x, slotTerrain.Value + 1f, slot.z, 2f * u.Extents.y).Length > 0) ctx.LegacyUnder++;
                ctx.Queries++; ctx.QueriesSinceYield++;
            }
            if (cfg.LegacyHeightmap || !cfg.Enabled)
            {
                if (cfg.LegacyHeightmap)
                {
                    // TEST ONLY: exactly 1.2.0 (heightmap + 1 m), to reproduce the under-floor fault for the post-check
                    res.Position = new Vector3(slot.x, (slotTerrain ?? slot.y) + 1f, slot.z);
                    res.Planned = res.Position;
                }
                res.Outcome = PlaceOutcome.Unchecked;
                ctx.Unchecked++;
                ctx.Placed.Add((res.Position, u.Radius, u.Name));
                return res;
            }

            bool nav = ctx.NavReady && !u.Flying;
            GraphNode? refNode = null; string refHow = "";
            if (nav && cfg.NavIslandCheck) (refNode, refHow) = RefNode(ctx, u, fwd, cfg);

            int bestSev = int.MaxValue; float bestD = float.MaxValue;
            Vector3 bestPos = res.Position; Quaternion bestRot = res.Rotation; string bestReason = "", bestLanded = "";
            int evaluated = 0;

            foreach (Vector3 cand in Candidates(ctx.BasePos, slot, slotDir, cfg))
            {
                if (evaluated >= cfg.MaxCandidates) break;
                evaluated++;
                Vector3 radial = cand - ctx.BasePos; radial.y = 0f;
                Quaternion rot = Quaternion.LookRotation(radial.sqrMagnitude > 1e-4f ? radial.normalized : slotDir, Vector3.up);

                int sev = Evaluate(u, ctx, cand, rot, nav, refNode, refHow, cfg, out Vector3 pivot, out string reason, out string landed);
                if (evaluated == 1) res.SlotReason = reason;
                if (sev == 0)
                {
                    res.Position = pivot; res.Rotation = rot; res.Candidates = evaluated; res.Landed = landed;
                    res.Outcome = evaluated == 1 ? PlaceOutcome.AsPlanned : PlaceOutcome.Relocated;
                    if (evaluated == 1) ctx.AsPlanned++; else ctx.Relocated++;
                    ctx.Placed.Add((pivot, u.Radius, u.Name));
                    AuditChosen(ctx, u, res);
                    return res;
                }
                float d = Dist2D(cand, slot);
                if (sev < SevNoPose && (sev < bestSev || (sev == bestSev && d < bestD)))
                {
                    bestSev = sev; bestD = d; bestPos = pivot; bestRot = rot; bestReason = reason; bestLanded = landed;
                }
            }

            res.Candidates = evaluated;
            res.Outcome = PlaceOutcome.Fallback;
            ctx.Fallback++;
            if (bestSev < SevNoPose) { res.Position = bestPos; res.Rotation = bestRot; res.FallbackReason = bestReason; res.Landed = bestLanded; }
            else res.FallbackReason = "no candidate landed, ring slot on the top surface + 1 m";   // res.Position = slot (surface) + 1 m
            ctx.Placed.Add((res.Position, u.Radius, u.Name));
            AuditChosen(ctx, u, res);
            return res;
        }

        /// <summary>Audit self-test: the chosen position must not be under a floor (expected 0).</summary>
        static void AuditChosen(PassContext ctx, UnitSpec u, PlaceResult r)
        {
            if (!ctx.Audit) return;
            ctx.Queries++; ctx.QueriesSinceYield++;
            float feet = r.Position.y + (r.Rotation * u.Center).y - u.Extents.y;
            string under = UnderFloor(r.Position.x, feet, r.Position.z, 2f * u.Extents.y);
            if (under.Length == 0) return;
            ctx.ChosenUnder++;
            MelonLogger.Warning($"[STARTER] audit {ctx.Map} {ctx.Label} '{u.Name}' at {V(r.Position)}: chosen spot {under}");
        }

        /// <summary>The slot itself, then tiers of 8 points around it (tangential first, then outwards, inwards,
        /// diagonals), kept within min_radius..max_radius of the base.</summary>
        static IEnumerable<Vector3> Candidates(Vector3 basePos, Vector3 slot, Vector3 slotDir, PlacementConfig cfg)
        {
            yield return slot;
            foreach (float d in s_Tiers)
            {
                foreach (float a in s_TierAngles)
                {
                    Vector3 p = slot + (Quaternion.Euler(0f, a, 0f) * slotDir) * d;
                    float r = Dist2D(p, basePos);
                    if (r < cfg.MinRadius || r > cfg.MaxRadius) continue;
                    yield return p;
                }
            }
        }

        /// <returns>0 = valid, else a severity (see Sev*); <paramref name="pivot"/> is the landed pose when severity
        /// is below SevNoPose.</returns>
        static int Evaluate(UnitSpec u, PassContext ctx, Vector3 cand, Quaternion rot, bool nav, GraphNode? refNode, string refHow,
            PlacementConfig cfg, out Vector3 pivot, out string reason, out string landed)
        {
            pivot = cand;
            reason = "";
            landed = "";

            // starters already placed in this pass (no physics query)
            foreach (var p in ctx.Placed)
            {
                float d = Dist2D(p.pos, cand);
                if (d < u.Radius + p.r + cfg.UnitGap)
                {
                    reason = $"too close to starter '{p.name}' ({d:F0} m)";
                    return SevSpacing;
                }
            }

            float? terrainAt = TerrainHeight(cand);
            bool hasTerrain = terrainAt.HasValue;
            float groundRef = terrainAt ?? ctx.BaseGroundY;
            Vector3 half = new Vector3(u.Extents.x + cfg.ClearanceMargin, u.Extents.y, u.Extents.z + cfg.ClearanceMargin);
            // from well above the highest of heightmap, base pivot and base ground: the box lands on the top surface
            Vector3 origin = new Vector3(cand.x, Mathf.Max(groundRef, Mathf.Max(ctx.BasePos.y, ctx.BaseGroundY)) + CastHeight + half.y, cand.z);

            // a cast does not report colliders that already contain its start box
            ctx.Queries++; ctx.QueriesSinceYield++;
            if (Physics.CheckBox(origin, half, rot, s_CastMask, QueryTriggerInteraction.Ignore))
            {
                origin.y += CastRaise;
                ctx.Queries++; ctx.QueriesSinceYield++;
                if (Physics.CheckBox(origin, half, rot, s_CastMask, QueryTriggerInteraction.Ignore))
                {
                    reason = $"cast start blocked {CastHeight + CastRaise:F0} m above ground";
                    return SevNoPose;
                }
            }

            float castDist = origin.y - (Mathf.Min(groundRef, ctx.BaseGroundY) - 30f);
            ctx.Queries++; ctx.QueriesSinceYield++;
            if (!Physics.BoxCast(origin, half, Vector3.down, out RaycastHit hit, rot, castDist, s_CastMask, QueryTriggerInteraction.Ignore))
            {
                reason = "no ground under the spot";
                return SevNoPose;
            }

            Vector3 boxCenter = origin + Vector3.down * hit.distance;
            float groundY = boxCenter.y - half.y;
            bool onTerrain = hit.collider != null && hit.collider.gameObject.layer == s_TerrainLayer;
            string landedOn = onTerrain ? "terrain" : Describe(hit.collider!);
            // pivot such that the unit's physical box sits where the cast box came to rest, lifted a little
            pivot = boxCenter - rot * u.Center + Vector3.up * Lift;
            float aboveTerrain = hasTerrain ? groundY - groundRef : 0f;
            float step = groundY - ctx.BaseGroundY;
            string where = $"landed on {landedOn}, {aboveTerrain:F1} m above terrain, {(step >= 0 ? "+" : "")}{step:F1} m vs base ground";
            landed = where;

            // level: not on another level than the base (base ground = the surface the base stands on)
            if (Mathf.Abs(step) > cfg.MaxStepFromBase)
            {
                reason = $"other level: {where}";
                return SevStep;
            }

            // elevated surface (roof / tank / catwalk / pillar or rock top) for units whose own graph cannot tell:
            // accepted only on the main ground-vehicle navmesh (drivable ground connected to the map)
            if ((u.Flying || u.NavSlack > 0f) && !onTerrain && aboveTerrain > cfg.MaxAboveTerrain && step > cfg.MaxAboveTerrain
                && ctx.NavReady && VehicleMask(out GraphMask vm))
            {
                ctx.Queries++; ctx.QueriesSinceYield++;
                string why = "";
                NNInfo vn = Nearest(new Vector3(cand.x, groundY, cand.z), vm, NavSearch);
                if (vn.node == null) why = "no ground-vehicle navmesh";
                else
                {
                    float vdy = Mathf.Abs(vn.position.y - groundY), vdxz = Dist2D(vn.position, cand);
                    uint? mainA = AreaOf(MainRef(vm, out _));
                    uint? a = AreaOf(vn.node);
                    if (vdxz > cfg.NavMaxHorizontal + 0.4f * u.Radius || vdy > cfg.NavMaxHeight + 0.4f * u.Radius)
                        why = $"ground-vehicle navmesh {vdxz:F1} m / {vdy:F1} m away";
                    else if (mainA.HasValue && a != mainA) why = $"ground-vehicle navmesh piece {a} is not the main component {mainA}";
                }
                if (why.Length > 0)
                {
                    reason = $"elevated top ({why}): {where}";
                    return SevRoof;
                }
            }

            // clearance: static world the cast could not see, units, invisible walls
            int mask = s_StaticMask | s_WallMask | (ctx.IgnoreUnits ? 0 : s_UnitMask);
            ctx.Queries++; ctx.QueriesSinceYield++;
            int n = Physics.OverlapBoxNonAlloc(boxCenter + Vector3.up * 0.15f, half, s_Buf, rot, mask, QueryTriggerInteraction.Ignore);
            if (n > 0)
            {
                bool onlyUnits = true;
                Collider? first = null;
                for (int i = 0; i < n; i++)
                {
                    var c = s_Buf[i];
                    if (c == null) continue;
                    bool isUnit = ((1 << c.gameObject.layer) & s_UnitMask) != 0;
                    if (first == null || (!isUnit && onlyUnits)) first = c;
                    if (!isUnit) onlyUnits = false;
                }
                if (first != null)
                {
                    reason = "overlaps " + Describe(first) + (n > 1 ? $" (+{n - 1} more)" : "");
                    return onlyUnits ? SevUnits : SevStatic;
                }
            }

            if (nav)
            {
                Vector3 groundPt = new Vector3(cand.x, groundY, cand.z);
                ctx.Queries++; ctx.QueriesSinceYield++;
                NNInfo nn = Nearest(groundPt, u.Mask, NavSearch);
                if (nn.node == null)
                {
                    reason = $"no navmesh within {NavSearch:F0} m: {where}";
                    return SevNav;
                }
                float dy = Mathf.Abs(nn.position.y - groundY);
                float dxz = Dist2D(nn.position, groundPt);
                float tolY = cfg.NavMaxHeight + 0.4f * u.Radius + u.NavSlack;   // a wide box rests on the highest bump under it
                if (dxz > cfg.NavMaxHorizontal + u.NavSlack || dy > tolY)
                {
                    reason = $"off navmesh: nearest node {dxz:F1} m away, {dy:F1} m height difference: {where}";
                    return SevNav;
                }
                uint? refArea = AreaOf(refNode);   // read now: areas are renumbered after graph updates
                if (refArea.HasValue)
                {
                    uint? area = AreaOf(nn.node);
                    if (area.HasValue && area.Value != refArea.Value)
                    {
                        reason = $"isolated navmesh piece (area {area.Value}, reference {refArea.Value} = {refHow}): {where}";
                        return SevIsland;
                    }
                }
            }
            return 0;
        }

        /// <summary>Reference component for the island test, as a NODE (its Area is read at query time, see MainRef):
        /// 13 probes around the base (the base, 4 points just outside it, 8 on the ring; each at the top surface
        /// there) snap to the unit's graph; probes more than max_step_from_base_m from the base ground are ignored
        /// (another level). If any probe lands on the graph's MAIN component, the reference is the main component:
        /// a base on a platform or roof piece whose own navmesh is cut off still sends its units to the ground they
        /// can drive out on. Otherwise the weighted vote of 1.3.0 (base + near probes weight 3, ring weight 1), e.g. a
        /// base on its own island. Null (no island test) with fewer than 3 votes. Recomputed when the node died.</summary>
        static (GraphNode? node, string how) RefNode(PassContext ctx, UnitSpec u, Vector3 fwd, PlacementConfig cfg)
        {
            int key = u.Mask.value;
            if (ctx.RefNodes.TryGetValue(key, out var cached) && (cached.node == null || Alive(cached.node))) return cached;

            GraphNode? main = MainRef(u.Mask, out string mainInfo);
            uint? mainArea = AreaOf(main);
            var votes = new Dictionary<uint, (int w, GraphNode n)>();
            int probes = 0, onMain = 0, otherLevel = 0;
            void Vote(Vector3 p, int w)
            {
                try
                {
                    ctx.Queries++; ctx.QueriesSinceYield++;
                    probes++;
                    NNInfo nn = Nearest(p, u.Mask, NavRefSearch);
                    if (nn.node == null) return;
                    if (Mathf.Abs(nn.position.y - ctx.BaseGroundY) > cfg.MaxStepFromBase) { otherLevel++; return; }
                    uint? a = AreaOf(nn.node);
                    if (!a.HasValue) return;
                    if (a == mainArea) onMain++;
                    votes.TryGetValue(a.Value, out var c);
                    votes[a.Value] = (c.w + w, nn.node);
                }
                catch { }
            }
            Vector3 At(Vector3 p)
            {
                ctx.Queries++; ctx.QueriesSinceYield++;
                p.y = SurfaceY(p.x, p.z, ctx.BaseGroundY) ?? (TerrainHeight(p) ?? ctx.BaseGroundY);
                return p;
            }
            // next to the base structure (just outside its navmesh cut), weight 3 each
            Vote(new Vector3(ctx.BasePos.x, ctx.BaseGroundY, ctx.BasePos.z), 3);
            for (int k = 0; k < 4; k++)
                Vote(At(ctx.BasePos + (Quaternion.Euler(0f, 45f + 90f * k, 0f) * fwd) * Mathf.Max(30f, cfg.MinRadius * 0.6f)), 3);
            // ring probes, weight 1 each
            for (int k = 0; k < 8; k++)
                Vote(At(ctx.BasePos + (Quaternion.Euler(0f, 45f * k, 0f) * fwd) * cfg.RingRadius), 1);

            (GraphNode? node, string how) result;
            string tail = $"{probes} probes, {onMain} on main, {otherLevel} on another level; graph '{GraphName(u.Mask)}': {mainInfo}";
            if (onMain > 0 && main != null)
                result = (main, "main component");
            else
            {
                GraphNode? best = null; int bestW = 0, total = 0; uint bestA = 0;
                foreach (var kv in votes) { total += kv.Value.w; if (kv.Value.w > bestW) { bestW = kv.Value.w; best = kv.Value.n; bestA = kv.Key; } }
                result = total >= 3 && best != null ? (best, $"base component {bestA} (no probe on main)") : ((GraphNode?)null, "none (too few probes on the navmesh)");
            }
            ctx.RefNodes[key] = result;
            MelonLogger.Msg($"[STARTER] {(ctx.Audit ? "audit" : "placement")} {ctx.Map} {ctx.Label}: island reference for '{u.Name}' = {result.how} ({tail})");
            return result;
        }

        /// <summary>One log line per relocated / fallback unit.</summary>
        internal static void LogResult(PassContext ctx, string verb, string unit, int index, PlaceResult r, bool logAll)
        {
            if (r.Outcome == PlaceOutcome.Relocated)
                MelonLogger.Msg($"[STARTER] {verb} relocated {ctx.Map} {ctx.Label} '{unit}' #{index}: {V(r.Planned)} -> {V(r.Position)} " +
                                $"({Dist2D(r.Planned, r.Position):F0} m, candidate {r.Candidates}, {r.Landed}); planned spot: {r.SlotReason}");
            else if (r.Outcome == PlaceOutcome.Fallback)
                MelonLogger.Warning($"[STARTER] {verb} FALLBACK {ctx.Map} {ctx.Label} '{unit}' #{index}: no valid spot in {r.Candidates} candidates, " +
                                    $"using {V(r.Position)} ({r.FallbackReason}{(string.IsNullOrEmpty(r.Landed) ? "" : "; " + r.Landed)}); planned {V(r.Planned)}: {r.SlotReason}");
            else if (logAll && r.Outcome == PlaceOutcome.AsPlanned)
                MelonLogger.Msg($"[STARTER] {verb} as planned {ctx.Map} {ctx.Label} '{unit}' #{index}: {V(r.Position)} ({r.Landed})");
        }

        // ---------------------------------------------------------------- post-spawn self-check

        internal sealed class SpawnRecord
        {
            public Unit Unit = null!;
            public UnitSpec Spec = null!;
            public string Label = "";
            public Vector3 SpawnPos;
        }

        const float MovedLimit = 5f;

        internal static System.Collections.IEnumerator PostCheck(List<SpawnRecord> records, float delay, string map, PlacementConfig cfg)
        {
            yield return new WaitForSeconds(delay);
            int alive = 0, gone = 0, flagged = 0, movedIgnored = 0;
            try
            {
                EnsureMasks();
                bool nav = NavReady(out _);
                foreach (var r in records)
                {
                    try
                    {
                        Unit u = r.Unit;
                        if (u == null) { gone++; continue; }
                        alive++;
                        LearnMask(u);
                        string issue = CheckUnit(u, r.Spec, nav, cfg);
                        // 1.3.1: the spawn point itself under a floor is a placement fault whatever the unit did since
                        string spawnUnder = UnderFloor(r.SpawnPos.x, r.SpawnPos.y + r.Spec.Center.y - r.Spec.Extents.y, r.SpawnPos.z, 2f * r.Spec.Extents.y);
                        if (spawnUnder.Length > 0) issue = "spawned " + spawnUnder + (issue.Length > 0 ? "; now: " + issue : "");
                        if (cfg.LogAll) MelonLogger.Msg($"[STARTER] post-check detail {map} {r.Label} '{r.Spec.Name}': spawned {V(r.SpawnPos)} now {V(u.transform.position)} " +
                                                        $"(moved {Dist2D(r.SpawnPos, u.transform.position):F1} m, dy {u.transform.position.y - r.SpawnPos.y:F1} m, {u.transform.position.y - (TerrainHeight(u.transform.position) ?? 0f):F1} m above terrain){(issue.Length > 0 ? " -> " + issue : "")}");
                        if (issue.Length == 0) continue;
                        // A unit that walked or flew away from its spawn point is not stuck there: an alien climbing a
                        // pillar 5 s after spawning is the AI moving it, not a placement fault. Below-terrain always counts.
                        float moved = Dist2D(r.SpawnPos, u.transform.position);
                        if (moved > MovedLimit && spawnUnder.Length == 0 && issue.IndexOf("below terrain", StringComparison.Ordinal) < 0)
                        {
                            movedIgnored++;
                            MelonLogger.Msg($"[STARTER] post-check note {map} {r.Label} '{r.Spec.Name}' at {V(u.transform.position)}: {issue} (moved {moved:F0} m since spawn: not a placement fault)");
                            continue;
                        }
                        flagged++;
                        MelonLogger.Warning($"[STARTER] post-check {map} {r.Label} '{r.Spec.Name}' at {V(u.transform.position)}: {issue} (moved {moved:F1} m since spawn at {V(r.SpawnPos)})");
                    }
                    catch (Exception ex) { MelonLogger.Warning("[STARTER] post-check error: " + ex.Message); }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[STARTER] post-check failed: " + ex.Message); }
            string goneTxt = (gone > 0 ? $", {gone} already gone" : "") + (movedIgnored > 0 ? $", {movedIgnored} moved away from their spawn with contact (not counted)" : "");
            if (flagged == 0) MelonLogger.Msg($"[STARTER] post-check {map}: all {alive} units clear{goneTxt}");
            else MelonLogger.Warning($"[STARTER] post-check {map}: {flagged} of {alive} units flagged{goneTxt}");
        }

        /// <summary>"" when fine, else what is wrong: below terrain (vanilla fall-through criterion), under a floor
        /// (a walkable static surface above the unit at its x/z, see UnderFloor), embedded in / inside a static
        /// collider, off the navmesh.</summary>
        static string CheckUnit(Unit u, UnitSpec spec, bool nav, PlacementConfig cfg)
        {
            var issues = new List<string>();
            Transform t = u.transform;
            Vector3 pos = t.position;
            Quaternion rot = t.rotation;

            float radius = spec.Radius;
            try { radius = u.PhysicalRadius; } catch { }
            float? th = TerrainHeight(pos);
            if (th.HasValue && th.Value - pos.y > radius + 5f)
                issues.Add($"below terrain by {th.Value - pos.y:F1} m");

            // 1.3.1: under a walkable static surface (IndustrialQuarter metal floor above, heightmap under the feet)
            Vector3 c;
            try { c = u.WorldPhysicalCenter; } catch { c = pos + rot * spec.Center; }
            string under = UnderFloor(c.x, c.y - spec.Extents.y, c.z, 2f * spec.Extents.y);
            if (under.Length > 0) issues.Add(under);

            string emb = Embedded(u);
            if (emb.Length > 0) issues.Add(emb);

            bool flying = spec.Flying;
            try { flying |= u.IsFlying; } catch { }
            if (nav && !flying)
            {
                GraphMask mask = spec.Mask;
                if (TryGetUnitMask(u, out var live)) mask = live;
                NNInfo nn = Nearest(pos, mask, NavSearch);
                if (nn.node == null) issues.Add($"no navmesh within {NavSearch:F0} m");
                else
                {
                    float dxz = Dist2D(nn.position, pos), dy = Mathf.Abs(nn.position.y - pos.y);
                    if (dxz > cfg.NavMaxHorizontal + spec.NavSlack + 2f || dy > cfg.NavMaxHeight + spec.Extents.y + spec.NavSlack + 2f)
                        issues.Add($"off navmesh ({dxz:F1} m away, {dy:F1} m height difference)");
                }
            }
            return string.Join("; ", issues);
        }

        static readonly List<Collider> s_UnitCols = new List<Collider>(32);
        static readonly Collider[] s_Buf2 = new Collider[16];
        const float EmbedDepth = 0.5f;

        /// <summary>"" or "embedded in X (penetration d m)": the unit's own solid colliders (live, max 24) are tested
        /// against static world colliders, and an overlap only counts when Physics.ComputePenetration says the two
        /// shapes interpenetrate by more than 0.5 m (resting contact and the bounding-box slack do not count).</summary>
        static string Embedded(Unit u)
        {
            Transform t = u.transform;
            s_UnitCols.Clear();
            u.GetComponentsInChildren(false, s_UnitCols);
            float worst = 0f; Collider? worstCol = null;
            int tested = 0;
            foreach (var uc in s_UnitCols)
            {
                if (uc == null || !uc.enabled || uc.isTrigger || !IsSolidLayer(uc.gameObject.layer)) continue;
                if (++tested > 24) break;
                Bounds wb = uc.bounds;
                int n = Physics.OverlapBoxNonAlloc(wb.center, wb.extents, s_Buf2, Quaternion.identity, s_StaticMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    var c = s_Buf2[i];
                    if (c == null || c.transform.IsChildOf(t)) continue;
                    try
                    {
                        Transform ct = c.transform, ut = uc.transform;
                        if (Physics.ComputePenetration(uc, ut.position, ut.rotation, c, ct.position, ct.rotation, out _, out float d) && d > worst)
                        { worst = d; worstCol = c; }
                    }
                    catch { }
                }
            }
            s_UnitCols.Clear();
            if (worstCol != null && worst > EmbedDepth)
                return $"embedded in {Describe(worstCol)} (penetration {worst:F1} m)";
            return Enclosed(u);
        }

        static readonly Vector3[] s_Horiz = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };

        /// <summary>"" or "inside X": a non-convex mesh collider is a surface, so a unit completely inside a rock,
        /// cliff or building mesh touches no triangle and neither OverlapBox nor ComputePenetration sees it. From the
        /// unit's physical centre, rays with Physics.queriesHitBackfaces on: inside a closed mesh the first hit is a
        /// back face (the face normal points along the ray). Inside = the ray up AND at least 2 of 4 horizontal rays
        /// hit a back face first (a ceiling or overhang above an open spot is a front face seen from below).</summary>
        static string Enclosed(Unit u)
        {
            Vector3 c;
            try { c = u.WorldPhysicalCenter; } catch { c = u.transform.position + Vector3.up; }
            bool old = Physics.queriesHitBackfaces;
            try
            {
                Physics.queriesHitBackfaces = true;
                Transform self = u.transform;
                if (!BackFace(self, c, Vector3.up, 200f, out Collider? upCol)) return "";
                int n = 0;
                foreach (var d in s_Horiz) if (BackFace(self, c, d, 60f, out _)) n++;
                return n >= 2 ? $"inside {Describe(upCol!)} (back faces up and {n}/4 sideways)" : "";
            }
            finally { Physics.queriesHitBackfaces = old; }
        }

        static readonly RaycastHit[] s_Hits = new RaycastHit[16];

        /// <summary>Nearest static hit along the ray that is not part of the unit itself; true when it is a back face.</summary>
        static bool BackFace(Transform self, Vector3 from, Vector3 dir, float dist, out Collider? col)
        {
            col = null;
            int n = Physics.RaycastNonAlloc(from, dir, s_Hits, dist, s_StaticMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; Vector3 normal = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                var h = s_Hits[i];
                if (h.collider == null || h.collider.transform.IsChildOf(self) || h.distance >= best) continue;
                best = h.distance; normal = h.normal; col = h.collider;
            }
            return col != null && Vector3.Dot(normal, dir) > 0.05f;
        }
    }
}
