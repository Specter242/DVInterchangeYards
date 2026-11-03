using System;
using System.Collections.Generic;
using System.Linq;
using DV.Teleporters;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Interchanges
{
    /// <summary>
    /// The interchanges on the game's maps (owner, 2026-11-01: "update the in game map to show
    /// the interchanges and allow fast travel").
    ///
    /// - Fast travel: each office carries a WorldFastTravelDestination, as stations carry
    ///   theirs. Every map then shows a station marker for it, and pressing the marker offers
    ///   the stock fast travel, priced and timed by distance as for any station. The player
    ///   lands on the pad before the office's porch, facing it; a locomotive fast-travels to
    ///   the track nearest that spot, a yard track.
    /// - Drawing: the maps show a fixed picture of the stock map (texture WorldMap). The yards are
    ///   painted into a copy of that picture in its own style (MapPicture), and each map gets the
    ///   interchanges' codes as clones of its own station names.
    /// </summary>
    internal static class MapMarks
    {
        public const string DestinationName = "[interchange fast travel]";
        public const string MarkerAnchorName = "map marker";
        public static string MarkerName(Layout layout) => layout.Title ?? layout.Site;

        // The office's porch faces its local -x; the floor and the pad are 1.9 m over its origin
        // (tools/export_layout.py, OFFICE_FLOOR), and the office is 14 m long in x.
        private static readonly Vector3 PorchStand = new Vector3(-9.5f, 1.95f, 0f);

        /// <summary>
        /// Adds the destination to an office copy, still inactive: its fields are set before it
        /// wakes. Returns its map marker's anchor, at the office until the caller places it
        /// where the layout says (OfficeSpec.MapMarker): the maps place a station's marker once,
        /// as it is added, so the anchor must stand in place before the office wakes.
        /// </summary>
        public static Transform AddFastTravel(Layout layout, OfficeSpec spec, GameObject office)
        {
            var go = new GameObject(DestinationName);
            go.transform.SetParent(office.transform, false);
            var anchor = new GameObject("player anchor").transform;
            anchor.SetParent(go.transform, false);
            anchor.localPosition = PorchStand;
            anchor.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
            var marker = new GameObject(MarkerAnchorName).transform;
            marker.SetParent(go.transform, false);
            var destination = go.AddComponent<WorldFastTravelDestination>();
            destination.markerName = MarkerName(layout);
            destination.markerType = FastTravelDestination.MarkerType.Station;
            destination.showOnMap = true;
            destination.playerTeleportAnchor = anchor;
            destination.mapMarkerAnchor = marker;
            destination.sideMarkers = Array.Empty<FastTravelDestination.SideMarkers>();
            return marker;
        }

        /// <summary>The destination of an interchange's office, if it was built this load.</summary>
        public static WorldFastTravelDestination Destination(string name) =>
            Offices.Of(name)?.GetComponentInChildren<WorldFastTravelDestination>(true);

        // ------------------------------------------------------------ drawing

        internal sealed class Drawing
        {
            public string Name;                         // the office's name
            public string Code;                         // the interchange's code, for the label
            public Vector2 Label;                       // map frame (x, z): the name's centre, or near it when beside the marker
            public Vector2? Marker;                     // map frame (x, z): the marker the name stands beside, if it does
            public string Side;                         // which side of the marker
            public readonly List<List<Vector2>> Lines = new List<List<Vector2>>();
        }

        private static List<Drawing> drawings;

        /// <summary>A new world: the next map draws from the tracks built for it.</summary>
        public static void Reset()
        {
            drawings = null;
            MapPicture.Reset();
            MapNames.Reset();
        }

        /// <summary>
        /// The yards' tracks in the map frame, every 10 m, without the tie-ins' through legs and
        /// stubs, which follow stock track the map already shows.
        /// </summary>
        public static List<Drawing> Drawings()
        {
            if (drawings != null)
                return drawings;
            var registry = DV.Utils.SingletonBehaviour<RailTrackRegistryBase>.Instance;
            if (registry == null || Builder.Built.Count == 0)
                return new List<Drawing>();
            var root = registry.TrackRootParent;
            var stockRoutes = new HashSet<RailTrack>(Builder.TieIns.SelectMany(t => new[] { t.Through, t.Stub }));
            var result = new List<Drawing>();
            foreach (var layout in Builder.Built.OrderBy(l => l.Site, StringComparer.Ordinal))
            {
                var office = layout.Offices.FirstOrDefault();
                var drawing = new Drawing { Name = office?.Name ?? layout.Site, Code = layout.Code ?? office?.Name ?? layout.Site };
                var prefix = layout.Site + "/";
                foreach (var track in Builder.OurTracks.Where(t => !stockRoutes.Contains(t) &&
                         (Builder.IdOf(t) ?? "").StartsWith(prefix, StringComparison.Ordinal)))
                {
                    var c = track.curve;
                    var line = new List<Vector2>();
                    for (var s = 0; s < c.pointCount - 1; s++)
                    {
                        var n = Math.Max(1, Mathf.CeilToInt((c[s + 1].position - c[s].position).magnitude / 10f));
                        for (var i = s == 0 ? 0 : 1; i <= n; i++)
                        {
                            var m = Frames.Map(root, CurveCut.At(c, s, i / (float)n));
                            line.Add(new Vector2(m.x, m.z));
                        }
                    }
                    if (line.Count > 1)
                        drawing.Lines.Add(line);
                }
                var all = drawing.Lines.SelectMany(l => l).ToList();
                if (all.Count == 0)
                    continue;
                // The name beside the yard's tracks, on the side the layout names (clear of its marker),
                // or beside the office's marker ("marker-west"): placed on each map by its text's size.
                float x0 = all.Min(p => p.x), x1 = all.Max(p => p.x), y0 = all.Min(p => p.y), y1 = all.Max(p => p.y);
                var off = LabelGap + LabelHeight / 2f;
                var side = layout.MapLabel ?? "south";
                if (side.StartsWith(BesideMarker, StringComparison.Ordinal) && office != null)
                {
                    var m = office.MapMarker != null && office.MapMarker.Length == 2
                        ? new Vector2(office.MapMarker[0], office.MapMarker[1])
                        : new Vector2(office.Position[0], office.Position[2]);
                    drawing.Marker = m;
                    drawing.Side = side.Substring(BesideMarker.Length);
                    drawing.Label = m + Direction(drawing.Side) * (MarkerAcross + LabelHeight);
                    result.Add(drawing);
                    continue;
                }
                switch (side)
                {
                    case "north": drawing.Label = new Vector2((x0 + x1) / 2f, y1 + off); break;
                    case "east": drawing.Label = new Vector2(x1 + off * 1.6f, (y0 + y1) / 2f); break;
                    case "west": drawing.Label = new Vector2(x0 - off * 1.6f, (y0 + y1) / 2f); break;
                    default: drawing.Label = new Vector2((x0 + x1) / 2f, y0 - off); break;
                }
                result.Add(drawing);
            }
            return drawings = result;
        }

        // The name's place beside the yard, in metres of the world: as far from the tracks as the
        // stock map's names stand from their stations.
        public const float LabelHeight = 280f, LabelGap = 90f;

        // Beside the marker: the name's near edge half a marker's width from the marker's rim
        // (owner, 2026-11-01: "move the West Interchange, WI, next to the dot").
        public const string BesideMarker = "marker-";
        public const float MarkerAcross = 200f;   // a station marker on the maps, metres (tools/stock_markers.py)

        internal static Vector2 Direction(string side)
        {
            switch (side)
            {
                case "north": return Vector2.up;
                case "south": return Vector2.down;
                case "east": return Vector2.right;
                default: return Vector2.left;
            }
        }

        [HarmonyPatch(typeof(MapMarkersController), "Awake")]
        private static class MapAwake
        {
            private static void Postfix(MapMarkersController __instance)
            {
                if (__instance.GetComponent<Overlay>() == null)
                    __instance.gameObject.AddComponent<Overlay>();
            }
        }

        /// <summary>
        /// One map's share of the drawing: the painted picture (MapPicture, shared by every map)
        /// and the interchanges' names, cloned from the map's own station names, so they take the
        /// same font, size, lighting and outline.
        /// </summary>
        internal sealed class Overlay : MonoBehaviour
        {
            public int Drawn { get; private set; }

            private void Start()
            {
                try
                {
                    MapPicture.Apply();
                    Name();
                    // The offices' wall maps, if they could not be named as their scene loaded.
                    if (MapNames.WallMapsNamed == 0)
                        MapNames.AddToWalls(MapNames.Content, GetComponent<MapMarkersController>());
                }
                catch (Exception e)
                {
                    Main.Error($"map drawing: {e}");
                }
            }

            /// <summary>The map frame (x, z) to this controller's frame, as MapMarkersController places markers.</summary>
            internal Vector3 Local(Vector2 m, float y) => LocalOf(GetComponent<MapMarkersController>(), m, y);

            /// <summary>The map frame (x, z) to a map's frame, as its MapMarkersController places markers.</summary>
            internal static Vector3 LocalOf(MapMarkersController controller, Vector2 m, float y)
            {
                var bounds = Traverse.Create(controller).Field("bounds").GetValue<BoxCollider>();
                var half = new Vector2(bounds.size.x, bounds.size.z) * 0.5f;
                var size = LevelInfo.WorldSize;
                var offset = LevelInfo.WorldOffset;
                return new Vector3(
                    Mathf.Lerp(-half.x, half.x, Mathf.InverseLerp(0f, size.x, m.x - offset.x)), y,
                    Mathf.Lerp(-half.y, half.y, Mathf.InverseLerp(0f, size.z, m.y - offset.z)));
            }

            /// <summary>A station marker's width on this map, in its frame: the marker's disc, as the map's prefab draws it.</summary>
            internal float MarkerDiameter()
            {
                var controller = GetComponent<MapMarkersController>();
                var prefab = Traverse.Create(controller).Field("stationMarkerPrefab").GetValue<MapMarker>();
                var disc = prefab == null ? null : prefab.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(f => f.name == "Visuals" && f.sharedMesh != null);
                if (disc != null)
                    return disc.sharedMesh.bounds.size.x * disc.transform.lossyScale.x;
                var bounds = Traverse.Create(controller).Field("bounds").GetValue<BoxCollider>();
                return MarkerAcross * bounds.size.x / LevelInfo.WorldSize.x;
            }

            /// <summary>A name's text box on this map, in its frame (x, z), if the name is there.</summary>
            internal Rect? NameBox(string code)
            {
                var label = transform.Find("MapPaper/Names/" + MapNames.Prefix + code)?.GetComponent<TMP_Text>();
                if (label == null)
                    return null;
                label.ForceMeshUpdate(true);
                var b = label.textBounds;
                var corners = new[] { new Vector3(b.min.x, b.min.y, 0f), new Vector3(b.max.x, b.min.y, 0f), new Vector3(b.min.x, b.max.y, 0f), new Vector3(b.max.x, b.max.y, 0f) }
                    .Select(c => transform.InverseTransformPoint(label.transform.TransformPoint(c))).ToList();
                return Rect.MinMaxRect(corners.Min(c => c.x), corners.Min(c => c.z), corners.Max(c => c.x), corners.Max(c => c.z));
            }

            /// <summary>Where a drawing lies on this map, in world space, for the inspector's pictures.</summary>
            internal Bounds WorldBounds(Drawing d)
            {
                var pts = d.Lines.SelectMany(l => l).Concat(new[] { d.Label }).Select(m => transform.TransformPoint(Local(m, 0f))).ToList();
                var b = new Bounds(pts[0], Vector3.zero);
                foreach (var p in pts)
                    b.Encapsulate(p);
                return b;
            }

            private void Name()
            {
                var names = transform.Find("MapPaper/Names");
                if (names != null)
                    Drawn += MapNames.Add(names, MapNames.Frame(GetComponent<MapMarkersController>(), names));
            }
        }
    }

    /// <summary>
    /// The interchanges painted into the map's own picture, in the stock map's style (measured
    /// from its WorldMap texture, 2048 px for the world's 16384 m, north up): each track a cream
    /// line 4 px wide inside a dark outline 4 px wide, the dark carried 3.5 px further south as a
    /// drop shadow. The dark is laid first, sparing the stock lines' cream so a tie-in merges as
    /// stock junctions do, then the cream. The painted copy is compressed like the original and
    /// put in place of it in every material that shows it, so every map shows the interchanges
    /// lit and blended as the rest of its picture; each new world starts from the original.
    /// </summary>
    internal static class MapPicture
    {
        private const string SourceName = "WorldMap";
        private static readonly Color Cream = new Color32(198, 190, 140, 255), Dark = new Color32(49, 44, 33, 255);
        private const float CreamHalf = 2.2f, Outline = 4f, ShadowSouth = 3.5f;   // pixels

        private static readonly Dictionary<Material, Texture> originals = new Dictionary<Material, Texture>();
        private static Texture2D painted;
        public static readonly List<string> Notes = new List<string>();

        public static bool Painted => painted != null;
        public static int Materials => originals.Count;

        /// <summary>A new world: every material gets its original picture back.</summary>
        public static void Reset()
        {
            foreach (var kv in originals.Where(kv => kv.Key != null))
                kv.Key.mainTexture = kv.Value;
            originals.Clear();
            if (painted != null)
                UnityEngine.Object.Destroy(painted);
            painted = null;
            Notes.Clear();
        }

        public static void Apply()
        {
            if (painted != null)
            {
                Cover();
                return;
            }
            var drawings = MapMarks.Drawings();
            var source = Resources.FindObjectsOfTypeAll<Material>().Select(MainTexture)
                .FirstOrDefault(t => t != null && t.name == SourceName);
            if (drawings.Count == 0 || source == null)
                return;
            var tex = Readable(source, SourceName + " (interchanges)", true);
            var w = tex.width;
            foreach (var d in drawings)
                Paint(tex, d, w / 16384f);
            tex.Apply(true);
            tex.Compress(false);
            tex.Apply(false, true);
            painted = tex;
            Cover();
        }

        /// <summary>Every material showing the original picture shows the painted one.</summary>
        private static void Cover()
        {
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                if (MainTexture(m) is Texture t && t != painted && t.name == SourceName)
                {
                    originals[m] = m.mainTexture;
                    m.mainTexture = painted;
                    Notes.Add($"map picture: material {m.name} shows the interchanges");
                }
        }

        /// <summary>A material's main texture, asking only materials that have that texture slot.</summary>
        private static Texture MainTexture(Material m) =>
            m != null && m.shader != null && m.GetTexturePropertyNames().Contains("_MainTex") ? m.GetTexture("_MainTex") : null;

        /// <summary>The painted picture's pixels around a drawing, for the inspector (the texture itself is GPU-only).</summary>
        public static Texture2D Crop(MapMarks.Drawing d, int margin)
        {
            if (painted == null)
                return null;
            var full = Readable(painted, SourceName + " (interchanges)", true);
            var scale = full.width / 16384f;
            var all = d.Lines.SelectMany(l => l).Select(p => p * scale).ToList();
            int x0 = Mathf.Max(0, Mathf.FloorToInt(all.Min(p => p.x)) - margin), y0 = Mathf.Max(0, Mathf.FloorToInt(all.Min(p => p.y)) - margin);
            int x1 = Mathf.Min(full.width - 1, Mathf.CeilToInt(all.Max(p => p.x)) + margin), y1 = Mathf.Min(full.height - 1, Mathf.CeilToInt(all.Max(p => p.y)) + margin);
            var crop = new Texture2D(x1 - x0 + 1, y1 - y0 + 1, TextureFormat.RGBA32, false);
            crop.SetPixels(full.GetPixels(x0, y0, crop.width, crop.height));
            crop.Apply();
            UnityEngine.Object.Destroy(full);
            return crop;
        }

        /// <summary>A readable copy of a texture, colours as stored (sRGB in, sRGB out).</summary>
        internal static Texture2D Readable(Texture source, string name, bool mips)
        {
            var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, rt);
            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(source.width, source.height, TextureFormat.RGBA32, mips, false)
            {
                name = name, wrapMode = source.wrapMode, filterMode = source.filterMode, anisoLevel = source.anisoLevel,
            };
            tex.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        private static void Paint(Texture2D tex, MapMarks.Drawing d, float scale)
        {
            // The lines in pixels: x east, y north (a texture's row 0 is its south edge).
            var lines = d.Lines.Select(l => l.Select(p => p * scale).ToList()).ToList();
            var all = lines.SelectMany(l => l).ToList();
            var margin = CreamHalf + Outline + ShadowSouth + 3f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(all.Min(p => p.x) - margin)), x1 = Mathf.Min(tex.width - 1, Mathf.CeilToInt(all.Max(p => p.x) + margin));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(all.Min(p => p.y) - margin)), y1 = Mathf.Min(tex.height - 1, Mathf.CeilToInt(all.Max(p => p.y) + margin));
            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            var px = tex.GetPixels(x0, y0, w, h);
            var segments = lines.SelectMany(l => l.Zip(l.Skip(1), (a, b) => (a, b))).ToList();
            float Dist(Vector2 q)
            {
                var best = float.MaxValue;
                foreach (var (a, b) in segments)
                {
                    var ab = b - a;
                    var t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / ab.sqrMagnitude) : 0f;
                    best = Mathf.Min(best, (a + ab * t - q).sqrMagnitude);
                }
                return Mathf.Sqrt(best);
            }
            var changed = 0;
            for (var j = 0; j < h; j++)
                for (var i = 0; i < w; i++)
                {
                    var q = new Vector2(x0 + i + 0.5f, y0 + j + 0.5f);
                    var d0 = Dist(q);
                    var dShadow = Dist(q + new Vector2(0f, ShadowSouth));   // the line moved south, seen from here
                    var dark = Mathf.Max(Mathf.Clamp01(CreamHalf + Outline + 0.5f - d0), Mathf.Clamp01(CreamHalf + Outline + 0.5f - dShadow));
                    var cream = Mathf.Clamp01(CreamHalf + 0.5f - d0);
                    if (dark <= 0f && cream <= 0f)
                        continue;
                    var c = px[j * w + i];
                    if (!IsCream(c))
                        c = Color.Lerp(c, Dark, dark);
                    c = Color.Lerp(c, Cream, cream);
                    px[j * w + i] = c;
                    changed++;
                }
            tex.SetPixels(x0, y0, w, h, px);
            Notes.Add($"map picture: {d.Name} painted, {changed} px in ({x0}, {y0})-({x1}, {y1})");
        }

        private static bool IsCream(Color c) => c.r > 0.66f && c.b > 0.43f && c.r - c.b > 0.12f;
    }
}
