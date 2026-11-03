using System;
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

        public static void Install()
        {
            foreach (var layout in Main.Layouts)
                foreach (var e in layout.Earthworks)
                {
                    var w = Prepare(e);
                    w.Layout = layout;
                    works.Add(w);
                }
            if (works.Count > 0)
                TerrainGrid.TerrainDataLoaded += OnLoaded;
            Main.Log($"terrain: {works.Count} earthwork(s)");
        }

        private static Work Prepare(EarthworkSpec e)
        {
            var w = new Work { Spec = e, P = e.Points.Select(Vec.Of).ToArray() };
            w.MinX = w.P.Min(p => p.x); w.MaxX = w.P.Max(p => p.x);
            w.MinZ = w.P.Min(p => p.z); w.MaxZ = w.P.Max(p => p.z);
            w.Margin = e.HalfWidth + 40f * Math.Max(e.Cut, e.Fill);   // reaches 40 m of cut or fill
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
                        var key = ((long)cx << 32) ^ (uint)cz;
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
            try
            {
                Apply(data, coord);
            }
            catch (Exception e)
            {
                Main.Error($"terrain edit on tile {coord}: {e}");
            }
        }

        private static void Apply(TerrainData data, Vector2Int coord)
        {
            var size = data.size;
            var res = data.heightmapResolution;
            var step = size.x / (res - 1);
            float ox = coord.x * size.x, oz = coord.y * size.z;
            var changed = 0;
            foreach (var w in works)
            {
                // Only the beds of layouts that were built.
                if (!Builder.Built.Contains(w.Layout))
                    continue;
                float x0 = Math.Max(ox, w.MinX - w.Margin), x1 = Math.Min(ox + size.x, w.MaxX + w.Margin);
                float z0 = Math.Max(oz, w.MinZ - w.Margin), z1 = Math.Min(oz + size.z, w.MaxZ + w.Margin);
                if (x0 > x1 || z0 > z1)
                    continue;
                int j0 = Mathf.Clamp((int)Math.Floor((x0 - ox) / step), 0, res - 1), j1 = Mathf.Clamp((int)Math.Ceiling((x1 - ox) / step), 0, res - 1);
                int i0 = Mathf.Clamp((int)Math.Floor((z0 - oz) / step), 0, res - 1), i1 = Mathf.Clamp((int)Math.Ceiling((z1 - oz) / step), 0, res - 1);
                int width = j1 - j0 + 1, height = i1 - i0 + 1;
                var heights = data.GetHeights(j0, i0, width, height);
                var any = false;
                for (var i = 0; i < height; i++)
                    for (var j = 0; j < width; j++)
                    {
                        float x = ox + (j0 + j) * step, z = oz + (i0 + i) * step;
                        if (!Nearest(w, x, z, out var d, out var formation))
                            continue;
                        var orig = heights[i, j] * size.y;
                        float target;
                        var beyond = d - w.Spec.HalfWidth;
                        if (beyond <= 0) target = formation;
                        else if (orig > formation) target = Math.Min(orig, formation + beyond / w.Spec.Cut);
                        else target = Math.Max(orig, formation - beyond / w.Spec.Fill);
                        if (Math.Abs(target - orig) > 0.005f)
                        {
                            heights[i, j] = target / size.y;
                            any = true;
                            changed++;
                        }
                    }
                if (any)
                    data.SetHeightsDelayLOD(j0, i0, heights);
            }
            if (changed > 0)
            {
                data.SyncHeightmap();
                TilesEdited++;
                SamplesChanged += changed;
            }
            Paint(data, coord);
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
        private static void Paint(TerrainData data, Vector2Int coord)
        {
            var layers = data.alphamapLayers;
            if (layers <= StationLayer)
                return;
            if (StationLayerName == null)
                StationLayerName = data.terrainLayers != null && data.terrainLayers.Length > StationLayer && data.terrainLayers[StationLayer] != null
                    ? data.terrainLayers[StationLayer].name : "?";
            var size = data.size;
            int res = data.alphamapResolution;
            var texel = size.x / res;
            float ox = coord.x * size.x, oz = coord.y * size.z;
            var reach = PaintOuter;
            float[,,] maps = null;
            var any = false;
            foreach (var w in works)
            {
                if (!w.Spec.Paint || !Builder.Built.Contains(w.Layout))
                    continue;
                if (w.MaxX + w.Spec.HalfWidth + reach < ox || w.MinX - w.Spec.HalfWidth - reach > ox + size.x ||
                    w.MaxZ + w.Spec.HalfWidth + reach < oz || w.MinZ - w.Spec.HalfWidth - reach > oz + size.z)
                    continue;
                if (maps == null)
                    maps = data.GetAlphamaps(0, 0, res, res);
                for (var i = 0; i < res; i++)
                    for (var j = 0; j < res; j++)
                    {
                        float x = ox + (j + 0.5f) * texel, z = oz + (i + 0.5f) * texel;
                        if (!Nearest(w, x, z, out var d, out _))
                            continue;
                        var beyond = d - w.Spec.HalfWidth;
                        var weight = beyond <= PaintInner ? 1f : Mathf.Clamp01(1f - (beyond - PaintInner) / (PaintOuter - PaintInner));
                        if (weight <= maps[i, j, StationLayer] + 1e-3f)
                            continue;
                        // Raise the station layer to the weight; scale the others to keep the sum.
                        var rest = 1f - maps[i, j, StationLayer];
                        var keep = rest > 1e-5f ? (1f - weight) / rest : 0f;
                        for (var k = 0; k < layers; k++)
                            maps[i, j, k] = k == StationLayer ? weight : maps[i, j, k] * keep;
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
            var key = ((long)(int)Math.Floor(x / Cell) << 32) ^ (uint)(int)Math.Floor(z / Cell);
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
