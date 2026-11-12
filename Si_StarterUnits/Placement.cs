/*
 Si_StarterUnits 1.3.0 — placement validation for starter units.

 Every starter gets a position that is checked before Game.SpawnPrefab:
   1. Landing: Physics.BoxCast straight down with the unit's box (the prefab's solid colliders, ObjectInfo.PhysicalBounds
      as fallback; widened by clearance_margin_m) against GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC, triggers ignored.
      Same cast + mask the game uses to drop its own initial units (StrategyTeamSetup.SpawnAIUnits), teleported units
      (TeleportUI) and units that fell through the ground (Unit.UpdateFallThroughGround). The box ends in contact with
      the first static surface under it and overlaps nothing on those layers. A Physics.CheckBox at the cast origin
      guards against the one blind spot of a cast (colliders that already contain the start box are not reported).
   2. Height: the landing surface may be at most max_height_above_terrain_m above the terrain at that spot (no roofs,
      tanks, rock or cliff tops) and at most max_step_from_base_m above/below the terrain at the base.
   3. Clearance: Physics.OverlapBox of the landed box against static world layers + units (Character, Vehicle,
      Dynamic) + Invisible Walls, triggers ignored; plus a 2D spacing check against the starters already placed in
      this pass (radius + radius + unit_gap_m).
   4. Navmesh (not for flying units): the nearest walkable node of the unit's own A* graph (Seeker.graphMask, as
      GameAI.GetPointValidForAI / Unit.GetFallThroughNavmeshLanding / MP_Strategy.GetStartPointOffNavmesh query it)
      within nav_max_horizontal_delta_m / nav_max_height_delta_m (+ voxel size for the alien point graph). With
      nav_island_check the node must lie in the base's connected component (GraphNode.Area): no isolated ledges.
 The search starts at the 1.2.0 ring slot and walks outwards in tiers around it (radius limits min_radius..max_radius
 from the base), first valid spot wins. No valid spot within max_candidates_per_unit: best candidate (least severe
 failure, then closest to the slot) with a warning. A unit is never dropped.
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
        /// <summary>Landing surface more than this above the terrain at that spot = rooftop / top of a rock or cliff mesh.</summary>
        [JsonProperty("max_height_above_terrain_m")] public float MaxAboveTerrain = 4f;
        /// <summary>Landing ground more than this above or below the terrain at the base structure = other level.</summary>
        [JsonProperty("max_step_from_base_m")] public float MaxStepFromBase = 25f;
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
        public float BaseGroundY;  // terrain height at the base (an HQ may be spawned raised)
        public Vector3 BaseForward = Vector3.forward;
        public bool IgnoreUnits;   // audit: judge the static world only
        public readonly List<(Vector3 pos, float r, string name)> Placed = new List<(Vector3, float, string)>();
        public readonly Dictionary<int, uint?> RefAreas = new Dictionary<int, uint?>();
        public int AsPlanned, Relocated, Fallback, Unchecked, Spawned;
        public int Queries, QueriesSinceYield;
        public bool NavReady;
        public string NavState = "";
        public int Total => AsPlanned + Relocated + Fallback + Unchecked;

        public string Summary(string verb) =>
            $"[STARTER] {verb} {Map} {Label}: {Total} units, {AsPlanned} as planned, {Relocated} relocated, {Fallback} fallback" +
            (Unchecked > 0 ? $", {Unchecked} unchecked (validate_placement off)" : "") +
            $" ({Queries} queries{(NavReady ? "" : ", no navmesh test: " + NavState)})";
    }

    internal static class Placer
    {
        const float CastHeight = 150f;
        const float CastRaise = 250f;
        const float Lift = 0.25f;
        const float NavSearch = 15f;
        const float NavRefSearch = 80f;
        // Failure severities (lower = closer to valid). Candidates >= NoPose have no landed pose.
        const int SevUnits = 3, SevIsland = 4, SevNav = 5, SevRoof = 6, SevStep = 6, SevStatic = 7, SevNoPose = 8, SevSpacing = 9;

        static readonly Collider[] s_Buf = new Collider[16];
        static readonly float[] s_Tiers = { 12f, 25f, 40f, 60f, 85f, 115f, 150f, 190f };
        static readonly float[] s_TierAngles = { 90f, -90f, 0f, 180f, 45f, -45f, 135f, -135f };
        static readonly Dictionary<ObjectInfo, GraphMask> s_LearnedMasks = new Dictionary<ObjectInfo, GraphMask>();
        static readonly HashSet<string> s_SpecLogged = new HashSet<string>();

        static bool s_MasksReady;
        static int s_CastMask, s_StaticMask, s_UnitMask, s_WallMask, s_TerrainLayer = -1;

        static void EnsureMasks()
        {
            if (s_MasksReady) return;
            s_CastMask = GamePhysics.TRACELAYERMASK_IGNOREDYNAMIC;
            int terrain = GamePhysics.TRACELAYERMASK_TERRAIN;
            int nonBlocking = LayerMask.GetMask("Water", "No Collide", "Gizmos", "UI");
            s_StaticMask = s_CastMask & ~terrain & ~nonBlocking;
            s_UnitMask = LayerMask.GetMask("Character", "Vehicle", "Dynamic");
            s_WallMask = LayerMask.GetMask("Invisible Walls");
            s_TerrainLayer = GamePhysics.LAYER_TERRAIN;
            s_MasksReady = true;
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
            slot.y = TerrainHeight(slot) ?? ctx.BasePos.y;

            // 1.2.0 placement: ring slot, terrain + 1 m, facing outwards
            var res = new PlaceResult
            {
                Planned = slot,
                Position = slot + Vector3.up * 1f,
                Rotation = Quaternion.LookRotation(slotDir, Vector3.up),
                SlotReason = "",
                FallbackReason = "",
            };
            if (!cfg.Enabled)
            {
                res.Outcome = PlaceOutcome.Unchecked;
                ctx.Unchecked++;
                ctx.Placed.Add((res.Position, u.Radius, u.Name));
                return res;
            }

            EnsureMasks();
            bool nav = ctx.NavReady && !u.Flying;
            uint? refArea = nav && cfg.NavIslandCheck ? RefArea(ctx, u, fwd, cfg) : null;

            int bestSev = int.MaxValue; float bestD = float.MaxValue;
            Vector3 bestPos = res.Position; Quaternion bestRot = res.Rotation; string bestReason = "", bestLanded = "";
            int evaluated = 0;

            foreach (Vector3 cand in Candidates(ctx.BasePos, slot, slotDir, cfg))
            {
                if (evaluated >= cfg.MaxCandidates) break;
                evaluated++;
                Vector3 radial = cand - ctx.BasePos; radial.y = 0f;
                Quaternion rot = Quaternion.LookRotation(radial.sqrMagnitude > 1e-4f ? radial.normalized : slotDir, Vector3.up);

                int sev = Evaluate(u, ctx, cand, rot, nav, refArea, cfg, out Vector3 pivot, out string reason, out string landed);
                if (evaluated == 1) res.SlotReason = reason;
                if (sev == 0)
                {
                    res.Position = pivot; res.Rotation = rot; res.Candidates = evaluated; res.Landed = landed;
                    res.Outcome = evaluated == 1 ? PlaceOutcome.AsPlanned : PlaceOutcome.Relocated;
                    if (evaluated == 1) ctx.AsPlanned++; else ctx.Relocated++;
                    ctx.Placed.Add((pivot, u.Radius, u.Name));
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
            else res.FallbackReason = "no candidate landed, 1.2.0 ring position (terrain + 1 m)";
            ctx.Placed.Add((res.Position, u.Radius, u.Name));
            return res;
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
        static int Evaluate(UnitSpec u, PassContext ctx, Vector3 cand, Quaternion rot, bool nav, uint? refArea,
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
            float groundRef = terrainAt ?? ctx.BasePos.y;
            Vector3 half = new Vector3(u.Extents.x + cfg.ClearanceMargin, u.Extents.y, u.Extents.z + cfg.ClearanceMargin);
            Vector3 origin = new Vector3(cand.x, Mathf.Max(groundRef, ctx.BasePos.y) + CastHeight + half.y, cand.z);

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

            float castDist = origin.y - (groundRef - 30f);
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
            landed = $"landed on {landedOn}, ground {groundY - groundRef:F1} m above terrain";

            // height: not on a roof / rock / cliff top, not on another level than the base
            if (hasTerrain && groundY - groundRef > cfg.MaxAboveTerrain)
            {
                reason = $"on top of {landedOn}, {groundY - groundRef:F1} m above terrain";
                return SevRoof;
            }
            float step = groundY - ctx.BaseGroundY;
            if (Mathf.Abs(step) > cfg.MaxStepFromBase)
            {
                reason = $"ground {(step > 0 ? "+" : "")}{step:F0} m vs the base ground (landed on {landedOn})";
                return SevStep;
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
                    reason = $"no navmesh within {NavSearch:F0} m (landed on {landedOn})";
                    return SevNav;
                }
                float dy = Mathf.Abs(nn.position.y - groundY);
                float dxz = Dist2D(nn.position, groundPt);
                float tolY = cfg.NavMaxHeight + 0.4f * u.Radius + u.NavSlack;   // a wide box rests on the highest bump under it
                if (dxz > cfg.NavMaxHorizontal + u.NavSlack || dy > tolY)
                {
                    reason = $"off navmesh: nearest node {dxz:F1} m away, {dy:F1} m height difference (landed on {landedOn})";
                    return SevNav;
                }
                if (refArea.HasValue)
                {
                    uint area = refArea.Value;
                    try { area = nn.node.Area; } catch { }
                    if (area != refArea.Value)
                    {
                        reason = $"isolated navmesh area {area} (base area {refArea.Value}, landed on {landedOn})";
                        return SevIsland;
                    }
                }
            }
            return 0;
        }

        /// <summary>Connected navmesh component (GraphNode.Area) of the base for the unit's graph: weighted vote of
        /// the nearest nodes to the base and to 4 points just outside it (weight 3 each) and to 8 ring points (weight 1).
        /// Null (no island test) with fewer than 3 votes.</summary>
        static uint? RefArea(PassContext ctx, UnitSpec u, Vector3 fwd, PlacementConfig cfg)
        {
            int key = u.Mask.value;
            if (ctx.RefAreas.TryGetValue(key, out var cached)) return cached;
            var votes = new Dictionary<uint, int>();
            void Vote(Vector3 p, int w)
            {
                try
                {
                    ctx.Queries++; ctx.QueriesSinceYield++;
                    NNInfo nn = Nearest(p, u.Mask, NavRefSearch);
                    if (nn.node == null) return;
                    uint a = nn.node.Area;
                    votes.TryGetValue(a, out int c);
                    votes[a] = c + w;
                }
                catch { }
            }
            // next to the base structure (just outside its navmesh cut), weight 3 each: the base's own component wins
            Vote(ctx.BasePos, 3);
            for (int k = 0; k < 4; k++)
            {
                Vector3 p = ctx.BasePos + (Quaternion.Euler(0f, 45f + 90f * k, 0f) * fwd) * Mathf.Max(30f, cfg.MinRadius * 0.6f);
                p.y = TerrainHeight(p) ?? ctx.BasePos.y;
                Vote(p, 3);
            }
            // ring probes, weight 1 each
            for (int k = 0; k < 8; k++)
            {
                Vector3 p = ctx.BasePos + (Quaternion.Euler(0f, 45f * k, 0f) * fwd) * cfg.RingRadius;
                p.y = TerrainHeight(p) ?? ctx.BasePos.y;
                Vote(p, 1);
            }
            uint? best = null; int bestVotes = 0, total = 0;
            foreach (var kv in votes) { total += kv.Value; if (kv.Value > bestVotes) { bestVotes = kv.Value; best = kv.Key; } }
            if (total < 3) best = null;
            ctx.RefAreas[key] = best;
            return best;
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
                        if (cfg.LogAll) MelonLogger.Msg($"[STARTER] post-check detail {map} {r.Label} '{r.Spec.Name}': spawned {V(r.SpawnPos)} now {V(u.transform.position)} " +
                                                        $"(moved {Dist2D(r.SpawnPos, u.transform.position):F1} m, dy {u.transform.position.y - r.SpawnPos.y:F1} m, {u.transform.position.y - (TerrainHeight(u.transform.position) ?? 0f):F1} m above terrain){(issue.Length > 0 ? " -> " + issue : "")}");
                        if (issue.Length == 0) continue;
                        // A unit that walked or flew away from its spawn point is not stuck there: an alien climbing a
                        // pillar 5 s after spawning is the AI moving it, not a placement fault. Below-terrain always counts.
                        float moved = Dist2D(r.SpawnPos, u.transform.position);
                        if (moved > MovedLimit && issue.IndexOf("below terrain", StringComparison.Ordinal) < 0)
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

        /// <summary>"" when fine, else what is wrong: below terrain (vanilla fall-through criterion), embedded in a
        /// static collider (box shrunk to 80 % so resting contact does not count), off the navmesh.</summary>
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
