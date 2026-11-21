using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DV.TerrainSystem;
using Newtonsoft.Json;
using UnityEngine;

namespace Interchanges
{
    /// <summary>A track bed to shape into the terrain: formation heights along a centreline.</summary>
    internal sealed class EarthworkSpec
    {
#pragma warning disable CS0649
        [JsonProperty("name")] public string Name;
        [JsonProperty("halfWidth")] public float HalfWidth;
        [JsonProperty("cut")] public float Cut;    // horizontal per vertical
        [JsonProperty("fill")] public float Fill;
        [JsonProperty("squareEnd")] public bool SquareEnd;   // ends flat at a portal, not rounded
        [JsonProperty("paint")] public bool Paint;           // painted with the station ground
        [JsonProperty("pad")] public bool Pad;               // a pad: sloped back to the ground on every side, its ends too
        [JsonProperty("points")] public List<float[]> Points = new List<float[]>();
#pragma warning restore CS0649
    }

    /// <summary>
    /// Shapes the terrain under the layouts' track beds. Terrain tiles stream
    /// in and out as the player travels, each a fresh TerrainData, so the
    /// edit is applied every time a tile is shown (TerrainGrid.TerrainDataLoaded
    /// fires as its data is attached, before the vegetation runs). Within the
    /// formation half-width the ground is set to the formation; beyond it, cut
    /// and fill slopes meet the original ground. Positions are in the map
    /// frame, which is the terrain grid's frame: tile (x, y) spans 512 m from
    /// (512x, 512y).
    ///
    /// The game draws a copy of each tile's data (MicroSplatVisibilityHack
    /// copies it in its own TerrainDataLoaded handler, with the tile's splat
    /// textures). The edit reaches that copy because this handler runs first:
    /// it subscribes when the mod loads, before any terrain exists, and the
    /// copy's handler subscribes as each terrain wakes.
    /// </summary>
    internal static class TerrainEdits
    {
        private sealed class Work
        {
            public Layout Layout;
            public EarthworkSpec Spec;
            public Vector3[] P;            // map frame: x, formation height, z
            public float MinX, MaxX, MinZ, MaxZ, Margin;
            public Vector2 EndAt, EndDir;  // a square end's plane: its last point and outward direction
            public Dictionary<long, List<int>> Grid;   // 16 m cells -> segment indices
        }

        private const float Cell = 16f;
        private static readonly List<Work> works = new List<Work>();
        public static int TilesEdited;
        public static int SamplesChanged;

        /// <summary>A new world: reset per-session counters while keeping the layout-static works and tile plans.</summary>
        public static void ResetStats()
        {
            TilesEdited = 0;
            SamplesChanged = 0;
            TexelsPainted = 0;
        }

        public static void Install()
        {
            foreach (var layout in Main.Layouts)
            {
                if (layout?.Earthworks == null)
                    continue;
                foreach (var e in layout.Earthworks)
                {
                    if (!IsValidEarthwork(e))
                    {
                        Main.Warn($"terrain: skipping malformed earthwork {e?.Name ?? "(unnamed)"} in {layout.Site}");
                        continue;
                    }
                    var w = Prepare(e);
                    if (w == null)
                        continue;
                    w.Layout = layout;
                    works.Add(w);
                }
            }
            if (works.Count > 0)
            {
                TerrainGrid.TerrainDataLoaded += OnLoaded;
                PlanAhead();
            }
            Main.Log(FormattableString.Invariant($"terrain: {works.Count} earthwork(s)"));
        }

        private static bool IsValidEarthwork(EarthworkSpec e) =>
            e?.Points != null && e.Points.Count >= 2 &&
            e.HalfWidth >= 0f && !float.IsInfinity(e.HalfWidth) &&
            e.Points.All(p => p != null && p.Length >= 3 && !float.IsNaN(p[0]) && !float.IsInfinity(p[0]) && !float.IsNaN(p[1]) && !float.IsInfinity(p[1]) && !float.IsNaN(p[2]) && !float.IsInfinity(p[2]));

        private static Work Prepare(EarthworkSpec e)
        {
            if (!IsValidEarthwork(e))
                return null;
            var w = new Work { Spec = e, P = e.Points.Select(Vec.Of).ToArray() };
            if (w.P.Length < 2)
                return null;
            w.MinX = w.P.Min(p => p.x); w.MaxX = w.P.Max(p => p.x);
            w.MinZ = w.P.Min(p => p.z); w.MaxZ = w.P.Max(p => p.z);
            var cut = e.Cut > 1e-4f && !float.IsInfinity(e.Cut) ? e.Cut : 0f;
            var fill = e.Fill > 1e-4f && !float.IsInfinity(e.Fill) ? e.Fill : 0f;
            w.Margin = e.HalfWidth + 40f * Math.Max(cut, fill);   // reaches 40 m of cut or fill
            if (w.P.Length >= 2)
            {
                var a = w.P[w.P.Length - 2]; var b = w.P[w.P.Length - 1];
                w.EndAt = new Vector2(b.x, b.z);
                w.EndDir = new Vector2(b.x - a.x, b.z - a.z).normalized;
            }
            w.Grid = new Dictionary<long, List<int>>();
            for (var i = 0; i < w.P.Length - 1; i++)
            {
                var a = w.P[i]; var b = w.P[i + 1];
                int x0 = (int)Math.Floor((Math.Min(a.x, b.x) - w.Margin) / Cell), x1 = (int)Math.Floor((Math.Max(a.x, b.x) + w.Margin) / Cell);
                int z0 = (int)Math.Floor((Math.Min(a.z, b.z) - w.Margin) / Cell), z1 = (int)Math.Floor((Math.Max(a.z, b.z) + w.Margin) / Cell);
                for (var cx = x0; cx <= x1; cx++)
                    for (var cz = z0; cz <= z1; cz++)
                    {
                        var key = InterchangeMath.CellKey(cx, cz);
                        if (!w.Grid.TryGetValue(key, out var list)) w.Grid[key] = list = new List<int>();
                        list.Add(i);
                    }
            }
            return w;
        }

        private static void OnLoaded(TerrainData data, Vector2Int coord)
        {
            if (data == null || Builder.Built.Count == 0)
                return;
            ReportPlanning();
            try
            {
                Apply(data, coord);
            }
            catch (Exception e)
            {
                Main.Error(FormattableString.Invariant($"terrain edit on tile {coord}: {e}"));
            }
        }

        // ------------------------------------------------------------ plans

        /// <summary>
        /// One bed's share of one tile, worked out once: the heightmap region it may touch, the
        /// samples there within its reach with their distance from its centerline and the
        /// formation height, or the splat texels it paints with their weight. None of that
        /// depends on the tile's own heights, so it is planned off the game's thread as the mod
        /// loads, and a tile's load only applies it. Finding each sample's nearest bed while the
        /// game waited took 0.3 to 1.5 s per tile near a yard, each time the tile streamed in
        /// (a drive across the map in the game, 2026-11-19; Sayori, 2026-11-19:
        /// "freezes for like 2 seconds").
        /// </summary>
        private sealed class Share
        {
            public Work Work;
            public int J0, I0, Width;
            public int[] Samples;          // heights: row * Width + column in the region
            public float[] Dist, Formation;
            public int[] Texels;           // paint: row * alphamap resolution + column
            public float[] Weight;
        }

        private sealed class TilePlan
        {
            public float SizeX, SizeZ;
            public int Res, AlphaRes;
            public List<Share> Heights = new List<Share>();
            public List<Share> Paint = new List<Share>();

            public bool HeightsFit(TerrainData data) => data.size.x == SizeX && data.size.z == SizeZ && data.heightmapResolution == Res;
        }

        // The stock tiles: 512 m a side, 513 height samples and 64 splat texels across.
        private const float TileSize = 512f;
        private const int TileRes = 513, TileAlphaRes = 64;

        private static readonly ConcurrentDictionary<long, TilePlan> plans = new ConcurrentDictionary<long, TilePlan>();
        private static int planning, plannedAhead, planningReady;
        private static long planningTicks, planningElapsedMs;
        private static readonly System.Diagnostics.Stopwatch planningClock = new System.Diagnostics.Stopwatch();
        private static string planFailure;
        private static bool planReported;

        private static long Key(Vector2Int coord) => InterchangeMath.CellKey(coord.x, coord.y);

        /// <summary>Plans every tile a bed reaches, on the thread pool, before the world loads.</summary>
        private static void PlanAhead()
        {
            var tiles = new HashSet<Vector2Int>();
            foreach (var w in works)
                for (var tx = (int)Math.Floor((w.MinX - w.Margin) / TileSize); tx <= (int)Math.Floor((w.MaxX + w.Margin) / TileSize); tx++)
                    for (var tz = (int)Math.Floor((w.MinZ - w.Margin) / TileSize); tz <= (int)Math.Floor((w.MaxZ + w.Margin) / TileSize); tz++)
                        tiles.Add(new Vector2Int(tx, tz));
            planning = tiles.Count;
            if (tiles.Count == 0)
            {
                System.Threading.Volatile.Write(ref planningReady, 1);
                return;
            }
            planningClock.Start();
            foreach (var tile in tiles)
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        plans.TryAdd(Key(tile), Plan(tile, TileSize, TileSize, TileRes, TileAlphaRes));
                        System.Threading.Interlocked.Increment(ref plannedAhead);
                    }
                    catch (Exception e)
                    {
                        System.Threading.Interlocked.CompareExchange(ref planFailure, FormattableString.Invariant($"tile {tile}: {e}"), null);
                    }
                    finally
                    {
                        System.Threading.Interlocked.Add(ref planningTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                        if (System.Threading.Interlocked.Decrement(ref planning) == 0)
                        {
                            planningClock.Stop();
                            System.Threading.Volatile.Write(ref planningElapsedMs, planningClock.ElapsedMilliseconds);
                            System.Threading.Volatile.Write(ref planningReady, 1);
                        }
                    }
                });
        }

        /// <summary>Says once, on the game's thread, how the planning ahead went.</summary>
        private static void ReportPlanning()
        {
            if (planReported || System.Threading.Volatile.Read(ref planningReady) == 0)
                return;
            planReported = true;
            var cpu = System.Threading.Interlocked.Read(ref planningTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var planned = System.Threading.Volatile.Read(ref plannedAhead);
            var elapsed = System.Threading.Volatile.Read(ref planningElapsedMs);
            Main.Log(FormattableString.Invariant($"terrain: {planned} tile(s) planned off the game's thread in {elapsed} ms ({cpu:F0} ms of work)"));
            var failure = System.Threading.Volatile.Read(ref planFailure);
            if (failure != null)
                Main.Error($"terrain: planning ahead failed for {failure}; that tile is planned as it loads");
        }

        /// <summary>A tile's plan for its size and resolutions: every bed's share, built or not.</summary>
        private static TilePlan Plan(Vector2Int coord, float sizeX, float sizeZ, int res, int alphaRes)
        {
            var plan = new TilePlan { SizeX = sizeX, SizeZ = sizeZ, Res = res };
            if (res > 1 && sizeX > 0f && sizeZ > 0f)
            {
                var stepX = sizeX / (res - 1);
                var stepZ = sizeZ / (res - 1);
                float ox = coord.x * sizeX, oz = coord.y * sizeZ;
                var samples = new List<int>();
                var dists = new List<float>();
                var formations = new List<float>();
                foreach (var w in works)
                {
                    float x0 = Math.Max(ox, w.MinX - w.Margin), x1 = Math.Min(ox + sizeX, w.MaxX + w.Margin);
                    float z0 = Math.Max(oz, w.MinZ - w.Margin), z1 = Math.Min(oz + sizeZ, w.MaxZ + w.Margin);
                    if (x0 > x1 || z0 > z1)
                        continue;
                    int j0 = Mathf.Clamp((int)Math.Floor((x0 - ox) / stepX), 0, res - 1), j1 = Mathf.Clamp((int)Math.Ceiling((x1 - ox) / stepX), 0, res - 1);
                    int i0 = Mathf.Clamp((int)Math.Floor((z0 - oz) / stepZ), 0, res - 1), i1 = Mathf.Clamp((int)Math.Ceiling((z1 - oz) / stepZ), 0, res - 1);
                    int width = j1 - j0 + 1, height = i1 - i0 + 1;
                    samples.Clear();
                    dists.Clear();
                    formations.Clear();
                    for (var i = 0; i < height; i++)
                        for (var j = 0; j < width; j++)
                        {
                            float x = ox + (j0 + j) * stepX, z = oz + (i0 + i) * stepZ;
                            if (!Nearest(w, x, z, out var d, out var formation))
                                continue;
                            samples.Add(i * width + j);
                            dists.Add(d);
                            formations.Add(formation);
                        }
                    if (samples.Count > 0)
                        plan.Heights.Add(new Share { Work = w, J0 = j0, I0 = i0, Width = width, Samples = samples.ToArray(), Dist = dists.ToArray(), Formation = formations.ToArray() });
                }
            }
            PlanPaint(plan, coord, alphaRes);
            return plan;
        }

        /// <summary>The splat texels each painted bed reaches on a tile, with the station ground's weight there.</summary>
        private static void PlanPaint(TilePlan plan, Vector2Int coord, int alphaRes)
        {
            var paint = new List<Share>();
            if (alphaRes <= 0 || plan.SizeX <= 0f || plan.SizeZ <= 0f)
            {
                plan.Paint = paint;
                plan.AlphaRes = alphaRes;
                return;
            }
            var texelX = plan.SizeX / alphaRes;
            var texelZ = plan.SizeZ / alphaRes;
            float ox = coord.x * plan.SizeX, oz = coord.y * plan.SizeZ;
            var reach = PaintOuter;
            var texels = new List<int>();
            var weights = new List<float>();
            foreach (var w in works)
            {
                if (!w.Spec.Paint)
                    continue;
                if (w.MaxX + w.Spec.HalfWidth + reach < ox || w.MinX - w.Spec.HalfWidth - reach > ox + plan.SizeX ||
                    w.MaxZ + w.Spec.HalfWidth + reach < oz || w.MinZ - w.Spec.HalfWidth - reach > oz + plan.SizeZ)
                    continue;
                texels.Clear();
                weights.Clear();
                for (var i = 0; i < alphaRes; i++)
                    for (var j = 0; j < alphaRes; j++)
                    {
                        float x = ox + (j + 0.5f) * texelX, z = oz + (i + 0.5f) * texelZ;
                        if (!Nearest(w, x, z, out var d, out _))
                            continue;
                        var weight = InterchangeMath.TerrainPaintWeight(d, w.Spec.HalfWidth, PaintInner, PaintOuter);
                        // A texel's own weight is never below 0, so one this light is never raised.
                        if (weight <= 1e-3f)
                            continue;
                        texels.Add(i * alphaRes + j);
                        weights.Add(weight);
                    }
                if (texels.Count > 0)
                    paint.Add(new Share { Work = w, Texels = texels.ToArray(), Weight = weights.ToArray() });
            }
            plan.Paint = paint;
            plan.AlphaRes = alphaRes;
        }

        /// <summary>The tile's plan, made now (and kept for its next load) if none fits it yet.</summary>
        private static TilePlan PlanFor(TerrainData data, Vector2Int coord)
        {
            if (!plans.TryGetValue(Key(coord), out var plan) || !plan.HeightsFit(data))
            {
                var started = Perf.Start();
                plan = Plan(coord, data.size.x, data.size.z, data.heightmapResolution, data.alphamapResolution);
                plans[Key(coord)] = plan;
                Perf.Stop("terrain plan made as the tile loaded", started, FormattableString.Invariant($"tile {coord.x},{coord.y}"));
            }
            else if (plan.AlphaRes != data.alphamapResolution)
            {
                var started = Perf.Start();
                PlanPaint(plan, coord, data.alphamapResolution);
                Perf.Stop("terrain paint plan made as the tile loaded", started, FormattableString.Invariant($"tile {coord.x},{coord.y}"));
            }
            return plan;
        }

        // ------------------------------------------------------------ applying a plan

        /// <summary>
        /// Shapes a tile by its plan: its heights read once over the region the built beds reach,
        /// each bed applied in turn over what the beds before it left (as each bed once read the
        /// tile after the last one's edit), and written back once.
        /// </summary>
        private static void Apply(TerrainData data, Vector2Int coord)
        {
            var plan = PlanFor(data, coord);
            var size = data.size;
            var changed = 0;
            var started = Perf.Start();
            var shares = size.y > 0f ? plan.Heights.Where(s => Builder.Built.Contains(s.Work.Layout)).ToList() : new List<Share>();
            if (shares.Count > 0)
            {
                int uj0 = int.MaxValue, ui0 = int.MaxValue, uj1 = int.MinValue, ui1 = int.MinValue;
                foreach (var s in shares)
                {
                    var rows = (s.Samples[s.Samples.Length - 1] / s.Width) + 1;
                    uj0 = Math.Min(uj0, s.J0);
                    ui0 = Math.Min(ui0, s.I0);
                    uj1 = Math.Max(uj1, s.J0 + s.Width - 1);
                    ui1 = Math.Max(ui1, s.I0 + rows - 1);
                }
                var heights = data.GetHeights(uj0, ui0, uj1 - uj0 + 1, ui1 - ui0 + 1);
                foreach (var s in shares)
                {
                    var spec = s.Work.Spec;
                    for (var k = 0; k < s.Samples.Length; k++)
                    {
                        int i = s.I0 - ui0 + s.Samples[k] / s.Width, j = s.J0 - uj0 + s.Samples[k] % s.Width;
                        var orig = heights[i, j] * size.y;
                        var formation = s.Formation[k];
                        var target = InterchangeMath.TerrainTargetHeight(orig, formation, s.Dist[k], spec.HalfWidth, spec.Cut, spec.Fill);
                        if (Math.Abs(target - orig) > 0.005f)
                        {
                            heights[i, j] = target / size.y;
                            changed++;
                        }
                    }
                }
                if (changed > 0)
                    data.SetHeightsDelayLOD(uj0, ui0, heights);
            }
            Perf.Stop("terrain heights", started, FormattableString.Invariant($"tile {coord.x},{coord.y}, {changed} samples changed"));
            if (changed > 0)
            {
                started = Perf.Start();
                data.SyncHeightmap();
                Perf.Stop("terrain heightmap sync", started, FormattableString.Invariant($"tile {coord.x},{coord.y}"));
                TilesEdited++;
                SamplesChanged += changed;
            }
            started = Perf.Start();
            Paint(data, plan);
            Perf.Stop("terrain paint", started, FormattableString.Invariant($"tile {coord.x},{coord.y}"));
        }

        // The terrain layer under every stock station yard: the station ground (MF, FF, GF, SM,
        // IME and HB, measured from the tiles' splat maps by tools/station_layer.py).
        public const int StationLayer = 7;
        private const float PaintInner = 4f, PaintOuter = 12f;   // full, then fading, beyond the half-width
        public static int TexelsPainted;
        public static string StationLayerName;

        /// <summary>
        /// Paints the station beds with the station ground, as stock yards are painted: full
        /// within 4 m beyond the bed's half-width, fading out by 12 m. The splat maps are coarse
        /// (64 texels a tile, 8 m each), so this covers a yard, not a single track's width.
        /// </summary>
        private static void Paint(TerrainData data, TilePlan plan)
        {
            var layers = data.alphamapLayers;
            if (layers <= StationLayer)
                return;
            if (StationLayerName == null)
                StationLayerName = data.terrainLayers != null && data.terrainLayers.Length > StationLayer && data.terrainLayers[StationLayer] != null
                    ? data.terrainLayers[StationLayer].name : "?";
            int res = data.alphamapResolution;
            if (res <= 0)
                return;
            float[,,] maps = null;
            var any = false;
            foreach (var s in plan.Paint)
            {
                if (!Builder.Built.Contains(s.Work.Layout))
                    continue;
                if (maps == null)
                    maps = data.GetAlphamaps(0, 0, res, res);
                for (var k = 0; k < s.Texels.Length; k++)
                {
                    int i = s.Texels[k] / res, j = s.Texels[k] % res;
                    var weight = s.Weight[k];
                    if (weight <= maps[i, j, StationLayer] + 1e-3f)
                        continue;
                    // Raise the station layer to the weight; scale the others to keep the sum.
                    var rest = 1f - maps[i, j, StationLayer];
                    var keep = rest > 1e-5f ? (1f - weight) / rest : 0f;
                    for (var l = 0; l < layers; l++)
                        maps[i, j, l] = l == StationLayer ? weight : maps[i, j, l] * keep;
                    any = true;
                    TexelsPainted++;
                }
            }
            if (any)
                data.SetAlphamaps(0, 0, maps);
        }

        /// <summary>The designed formation height under a map position, if it lies on a bed (within its half-width).</summary>
        public static bool Formation(Vector3 map, out float formation)
        {
            formation = 0;
            var best = float.MaxValue;
            foreach (var w in works.Where(w => Builder.Built.Contains(w.Layout)))
                if (Nearest(w, map.x, map.z, out var d, out var f) && d <= w.Spec.HalfWidth && d < best)
                {
                    best = d;
                    formation = f;
                }
            return best < float.MaxValue;
        }

        /// <summary>Horizontal distance to the nearest centreline segment, and the formation height there.</summary>
        private static bool Nearest(Work w, float x, float z, out float dist, out float formation)
        {
            dist = float.MaxValue;
            formation = 0;
            // Past a square end (a portal) nothing is shaped: the hill stays over the tunnel.
            if (w.Spec.SquareEnd && Vector2.Dot(new Vector2(x, z) - w.EndAt, w.EndDir) > 0f)
                return false;
            var key = InterchangeMath.CellKey((int)Math.Floor(x / Cell), (int)Math.Floor(z / Cell));
            if (!w.Grid.TryGetValue(key, out var segs))
                return false;
            foreach (var i in segs)
            {
                var a = w.P[i]; var b = w.P[i + 1];
                float dx = b.x - a.x, dz = b.z - a.z;
                var len2 = dx * dx + dz * dz;
                var t = len2 > 1e-9f ? Mathf.Clamp01(((x - a.x) * dx + (z - a.z) * dz) / len2) : 0f;
                float px = a.x + dx * t, pz = a.z + dz * t;
                var d = Mathf.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                // Beyond the ends of a bed (a joint with another bed or stock track) only a round
                // end within the half-width is shaped. A pad stands alone, so its ends slope too.
                if (!w.Spec.Pad && (i == 0 && t <= 0f || i == w.P.Length - 2 && t >= 1f) && d > w.Spec.HalfWidth)
                    continue;
                if (d < dist)
                {
                    dist = d;
                    formation = a.y + (b.y - a.y) * t;
                }
            }
            return dist < float.MaxValue && dist <= w.Margin;
        }
    }
}
