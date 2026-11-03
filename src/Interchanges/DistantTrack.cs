using System;
using System.Collections.Generic;
using System.Linq;
using DV.Utils;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Interchanges
{
    /// <summary>
    /// The yards in the distant view (owner, 2026-11-03: "how do we add our track to the distant
    /// low lod view?" ... "do it"). Beyond the streamed terrain tiles the game draws the ground with
    /// one material (DistantTerrain) over world-sized maps at 8 m a pixel: a height map, a normal
    /// map, and a ground-texture weight map, <c>_Control</c> (DistantTerrain_SplatControl_LFS: a
    /// 2048 × 2048 array of four RGBA slices, sixteen layer weights). Stock track shows there only
    /// as that map's weights.
    ///
    /// So once the distant terrain is loaded, every built layout's beds (its earthworks: tracks,
    /// ladders and pads, ending at the tunnel portals) are painted into a copy of that map: each
    /// pixel blended, by the share of it the beds cover, toward the weights of stock yard ballast,
    /// measured from the same map where three or more stock yard tracks cross a pixel. The copy,
    /// uncompressed (RGBA32 with its mips, about 85 MB of video memory against 22 MB, as the game
    /// can't compress an array at runtime), is given to the material. The heights are left: the
    /// earthworks move the ground 4 m at most, under one of the height map's 8 m pixels.
    ///
    /// Reading the map back: the asset is BC7, which the game's CPU read can't decode (run 38:
    /// black). Under Proton the game loads it as DXT5, which it can, and a GPU blit of the slice
    /// reads the same (run 40). The blit goes first, as it reads either. A read that finds no
    /// stock yard in the map leaves the distant view as it is.
    /// </summary>
    internal static class DistantTrack
    {
        private const float MapSize = 16384f;
        private const int Samples = 4;        // subsamples a pixel side, for the beds' cover
        private const int YardTracks = 3;     // stock yard tracks crossing a pixel for it to count as yard ballast
        private const int YardPixels = 50;    // fewest such pixels for a measure

        /// <summary>The ways to read a slice back, tried in turn until one finds the stock yards.</summary>
        private static readonly (string Name, Func<Texture2DArray, int, Color32[]> Read)[] Readers =
        {
            ("GPU blit", BlitSlice),
            ("CPU", (source, slice) => source.GetPixels32(slice, 0)),
        };

        public static readonly List<string> Notes = new List<string>();
        private static Material material;
        private static Texture2DArray painted, stock;
        private static bool gaveUp;   // the map couldn't be read: no second try this game

        /// <summary>Whether the distant terrain now draws the painted map.</summary>
        public static bool Painted => material != null && painted != null && material.GetTexture("_Control") == painted;

        /// <summary>
        /// Paints the built layouts into the distant map once the distant terrain is loaded: tried
        /// after the build and, failing that, when loading finishes (<paramref name="last"/>).
        /// </summary>
        public static void TryPaint(bool last)
        {
            try
            {
                Paint(last);
            }
            catch (Exception e)
            {
                gaveUp |= last;
                Note($"distant view: not painted ({e.GetType().Name}: {e.Message}); it shows the stock ground", error: true);
            }
        }

        /// <summary>For the inspection: the distant terrain draws the stock map (true) or the painted one.</summary>
        public static void ShowStock(bool show)
        {
            if (material != null && stock != null && painted != null)
                material.SetTexture("_Control", show ? (Texture)stock : painted);
        }

        private static void Note(string text, bool error = false)
        {
            Notes.Add(text);
            if (error)
                Main.Error(text);
            else
                Main.Log(text);
        }

        private static void Paint(bool last)
        {
            if (Builder.Built.Count == 0 || Painted || gaveUp)
                return;
            var mat = FindMaterial();
            if (mat == null)
            {
                if (last)
                    Note("distant view: no distant terrain found; it shows the stock ground");
                return;
            }
            if (!(mat.GetTexture("_Control") is Texture2DArray source))
            {
                gaveUp = true;
                Note("distant view: the distant terrain has no ground map array; it shows the stock ground");
                return;
            }
            int w = source.width, h = source.height, depth = source.depth;
            var started = Time.realtimeSinceStartup;
            Color32[][] slices = null;
            float[] yard = null;
            var dense = 0;
            foreach (var (name, read) in Readers)
            {
                try
                {
                    var tried = new Color32[depth][];
                    for (var i = 0; i < depth; i++)
                        tried[i] = read(source, i);
                    if (tried.Any(s => s == null || s.Length != w * h))
                    {
                        Note($"distant view: the {name} read of {source.name} came back short");
                        continue;
                    }
                    yard = StockYard(tried, w, h, out dense);
                    if (yard == null)
                    {
                        Note($"distant view: the {name} read of {source.name} ({source.graphicsFormat}) shows no stock yards ({dense} yard pixels)");
                        continue;
                    }
                    slices = tried;
                    Note($"distant view: {source.name} ({source.graphicsFormat}) read by {name}; stock yard ballast over {dense} pixels weighs " +
                         string.Join(" ", yard.Select(v => v.ToString("F0"))));
                    break;
                }
                catch (Exception e)
                {
                    Note($"distant view: the {name} read of {source.name} failed: {e.GetType().Name} {e.Message}");
                }
            }
            if (slices == null)
            {
                if (last)
                {
                    gaveUp = true;
                    Note($"distant view: {source.name} couldn't be read; it shows the stock ground");
                }
                return;
            }
            var cover = Cover(w, h);
            foreach (var pixel in cover)
            {
                var a = pixel.Value;
                for (var i = 0; i < depth; i++)
                {
                    var c = slices[i][pixel.Key];
                    slices[i][pixel.Key] = new Color32(Mix(c.r, yard[4 * i], a), Mix(c.g, yard[4 * i + 1], a), Mix(c.b, yard[4 * i + 2], a), Mix(c.a, yard[4 * i + 3], a));
                }
            }
            var copy = new Texture2DArray(w, h, depth, TextureFormat.RGBA32, true, true)
            {
                name = "Interchanges distant ground", filterMode = source.filterMode, wrapMode = source.wrapMode, anisoLevel = source.anisoLevel,
            };
            for (var i = 0; i < depth; i++)
                copy.SetPixels32(slices[i], i, 0);
            copy.Apply(true, true);
            if (painted != null)
                Object.Destroy(painted);
            painted = copy;
            stock = source;
            material = mat;
            mat.SetTexture("_Control", copy);
            Note($"distant view: {cover.Count} pixels painted ({cover.Count(p => p.Value > 0.99f)} wholly) on a {w} x {h} x {depth} copy " +
                 $"in {Time.realtimeSinceStartup - started:F1} s");
        }

        /// <summary>
        /// The weights of stock yard ballast (0–255 for each of the slices' channels): their mean over
        /// the pixels that three or more stock yard tracks cross; null if too few such pixels, or if
        /// they carry no clear weight (a read that came back blank).
        /// </summary>
        private static float[] StockYard(Color32[][] slices, int w, int h, out int dense)
        {
            dense = 0;
            var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
            if (registry == null)
                return null;
            var root = registry.TrackRootParent;
            var size = MapSize / w;
            var crossings = new Dictionary<int, int>();
            foreach (var track in registry.AllTracks.Where(t => t != null && t.name.StartsWith("[Y]", StringComparison.Ordinal) && !Builder.IsOurs(t)))
            {
                var curve = track.curve;
                if (curve == null || curve.pointCount < 2)
                    continue;
                var cells = new HashSet<int>();
                for (var s = 0; s + 1 < curve.pointCount; s++)
                {
                    var steps = Mathf.Clamp(Mathf.CeilToInt((curve[s + 1].position - curve[s].position).magnitude / 2f), 2, 400);
                    for (var k = 0; k <= steps; k++)
                    {
                        var m = Frames.Map(root, CurveCut.At(curve, s, k / (float)steps));
                        int column = Mathf.FloorToInt(m.x / size), row = Mathf.FloorToInt(m.z / size);
                        if (column >= 0 && column < w && row >= 0 && row < h)
                            cells.Add(row * w + column);
                    }
                }
                foreach (var cell in cells)
                    crossings[cell] = crossings.TryGetValue(cell, out var n) ? n + 1 : 1;
            }
            var yardCells = crossings.Where(p => p.Value >= YardTracks).Select(p => p.Key).ToList();
            dense = yardCells.Count;
            if (dense < YardPixels)
                return null;
            var weights = new float[slices.Length * 4];
            foreach (var cell in yardCells)
                for (var i = 0; i < slices.Length; i++)
                {
                    var c = slices[i][cell];
                    weights[4 * i] += c.r;
                    weights[4 * i + 1] += c.g;
                    weights[4 * i + 2] += c.b;
                    weights[4 * i + 3] += c.a;
                }
            for (var k = 0; k < weights.Length; k++)
                weights[k] /= dense;
            return weights.Max() >= 64f ? weights : null;
        }

        /// <summary>A slice's top mip, drawn straight from the array into a linear render target of its size (each pixel sampled at its centre), and read back.</summary>
        private static Color32[] BlitSlice(Texture2DArray source, int slice)
        {
            var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var read = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            var active = RenderTexture.active;
            try
            {
                Graphics.Blit(source, target, slice, 0);
                RenderTexture.active = target;
                read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0, false);
                read.Apply(false);
                return read.GetPixels32();
            }
            finally
            {
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
                Object.Destroy(read);
            }
        }

        /// <summary>The distant terrain's material: on its meshes, the one with the ground map.</summary>
        private static Material FindMaterial()
        {
            var type = AccessTools.TypeByName("DV.WorldTools.DistantTerrain");
            if (type == null || !(Object.FindObjectOfType(type) is Component distant))
                return null;
            return distant.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.sharedMaterial)
                .FirstOrDefault(m => m != null && m.HasProperty("_Control") && m.HasProperty("_WorldNormalmap"));
        }

        /// <summary>The share of each map pixel (row × width + column) the built layouts' beds cover, in the map frame (8 m a pixel at 2048).</summary>
        private static Dictionary<int, float> Cover(int w, int h)
        {
            var size = MapSize / w;
            var masks = new Dictionary<int, int>();
            foreach (var layout in Builder.Built)
                foreach (var bed in layout.Earthworks)
                {
                    var hw = bed.HalfWidth;
                    for (var k = 0; k + 1 < bed.Points.Count; k++)
                    {
                        var a = new Vector2(bed.Points[k][0], bed.Points[k][2]);
                        var b = new Vector2(bed.Points[k + 1][0], bed.Points[k + 1][2]);
                        int c0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - hw) / size)), c1 = Mathf.Min(w - 1, Mathf.FloorToInt((Mathf.Max(a.x, b.x) + hw) / size));
                        int r0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, b.y) - hw) / size)), r1 = Mathf.Min(h - 1, Mathf.FloorToInt((Mathf.Max(a.y, b.y) + hw) / size));
                        for (var r = r0; r <= r1; r++)
                            for (var c = c0; c <= c1; c++)
                            {
                                var bits = 0;
                                for (var si = 0; si < Samples; si++)
                                    for (var sj = 0; sj < Samples; sj++)
                                    {
                                        var s = new Vector2((c + (sj + 0.5f) / Samples) * size, (r + (si + 0.5f) / Samples) * size);
                                        if (Distance(s, a, b) <= hw)
                                            bits |= 1 << (si * Samples + sj);
                                    }
                                if (bits == 0)
                                    continue;
                                var key = r * w + c;
                                masks[key] = (masks.TryGetValue(key, out var m) ? m : 0) | bits;
                            }
                    }
                }
            return masks.ToDictionary(p => p.Key, p => Count(p.Value) / (float)(Samples * Samples));
        }

        private static float Distance(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            var t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }

        private static int Count(int bits)
        {
            var n = 0;
            for (; bits != 0; bits &= bits - 1)
                n++;
            return n;
        }

        private static byte Mix(byte from, float to, float share) => (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(from, to, share)), 0, 255);
    }
}
