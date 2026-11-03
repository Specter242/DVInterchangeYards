using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

// Fields are filled by the JSON reader.
#pragma warning disable CS0649

namespace Interchanges
{
    /// <summary>
    /// One interchange's in-game layout, written by tools/export_layout.py and
    /// checked by tools/check_layout.py. Positions are absolute map
    /// coordinates: the [railway] root's own frame (x east, z north).
    /// </summary>
    internal sealed class Layout
    {
        [JsonProperty("site")] public string Site;
        [JsonProperty("format")] public int Format;
        // Its own name, code and colour on the maps and in the switch books, e.g. "West
        // Interchange", "WI" (owner, 2026-11-01). Site, track, switch and office names stay
        // as they were: saves and the contract mod know the interchanges by them.
        [JsonProperty("title")] public string Title;
        [JsonProperty("code")] public string Code;
        [JsonProperty("colour")] public int[] Colour;
        [JsonProperty("switches")] public List<SwitchSpec> Switches = new List<SwitchSpec>();
        [JsonProperty("tracks")] public List<TrackSpec> Tracks = new List<TrackSpec>();
        [JsonProperty("stockEdits")] public List<StockEdit> StockEdits = new List<StockEdit>();
        [JsonProperty("bufferStops")] public List<BufferStopSpec> BufferStops = new List<BufferStopSpec>();
        [JsonProperty("views")] public List<ViewSpec> Views = new List<ViewSpec>();
        [JsonProperty("earthworks")] public List<EarthworkSpec> Earthworks = new List<EarthworkSpec>();
        [JsonProperty("vegetation")] public List<VegetationSpec> Vegetation = new List<VegetationSpec>();
        [JsonProperty("tunnels")] public List<TunnelSpec> Tunnels = new List<TunnelSpec>();
        [JsonProperty("runs")] public List<RunSpec> Runs = new List<RunSpec>();
        [JsonProperty("offices")] public List<OfficeSpec> Offices = new List<OfficeSpec>();
        [JsonProperty("booklet")] public BookletSpec Booklet;
        [JsonProperty("routeMap")] public RouteMapSpec RouteMap;
        // Where its name goes on the maps: a side of the yard ("south"), or of its office's marker ("marker-west").
        [JsonProperty("mapLabel")] public string MapLabel = "south";

        public const int SupportedFormat = 1;

        public Color32 Color32 => Colour != null && Colour.Length == 3
            ? new Color32((byte)Colour[0], (byte)Colour[1], (byte)Colour[2], 255)
            : new Color32(52, 128, 132, 255);

        public static List<Layout> LoadAll(string folder)
        {
            var result = new List<Layout>();
            // In a fixed order, so the layouts' objects always come in the same order after the stock ones.
            foreach (var file in Directory.GetFiles(folder, "layout-*.json").OrderBy(Path.GetFileName, System.StringComparer.Ordinal))
            {
                var layout = JsonConvert.DeserializeObject<Layout>(File.ReadAllText(file));
                if (layout == null || layout.Format != SupportedFormat)
                {
                    Main.Warn($"{Path.GetFileName(file)}: format {layout?.Format} is not {SupportedFormat}; skipped");
                    continue;
                }
                result.Add(layout);
            }
            return result;
        }
    }

    internal sealed class SwitchSpec
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("asset")] public string Asset;     // junc-left or junc-right
        [JsonProperty("center")] public float[] Center;
        [JsonProperty("joint")] public float[] Joint;
        [JsonProperty("heading")] public float Heading;  // degrees from +x toward +z: the through direction
        [JsonProperty("pitch")] public float Pitch;
        [JsonProperty("stand")] public string Stand;     // "outside": the stand goes on the non-diverging side (SwitchStands)

        public Vector3 JointV => Vec.Of(Joint);
        public Vector3 Forward => Vec.Heading(Heading, Pitch);
    }

    internal sealed class TrackSpec
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("length")] public float Length;
        [JsonProperty("points")] public List<PointSpec> Points = new List<PointSpec>();
    }

    internal sealed class PointSpec
    {
        [JsonProperty("position")] public float[] Position;
        [JsonProperty("handle1")] public float[] Handle1;
        [JsonProperty("handle2")] public float[] Handle2;
    }

    /// <summary>A tile of the switch books (tools/schematic.py): a stock sprite at a position, turned and mirrored.</summary>
    internal sealed class BookTile
    {
        [JsonProperty("sprite")] public string Sprite;
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("rot")] public float Rotation;
        [JsonProperty("sx")] public float ScaleX = 1f;
        [JsonProperty("sy")] public float ScaleY = 1f;
        [JsonProperty("text")] public string Text;     // a track or station badge's text
    }

    /// <summary>The interchange's page in the station booklet (item MapSchematic): a chapter and tab of its own.</summary>
    internal sealed class BookletSpec
    {
        [JsonProperty("station")] public string Station;     // the stock station whose first page it is cloned from, e.g. "IME"
        [JsonProperty("yardId")] public string YardId;
        [JsonProperty("yardType")] public string YardType;
        [JsonProperty("tiles")] public List<BookTile> Tiles = new List<BookTile>();          // in the page's SMap frame
        [JsonProperty("cells")] public Dictionary<string, int[]> Cells = new Dictionary<string, int[]>();  // switch id -> touch cell
    }

    /// <summary>The interchange on the route map (item RouteMap): its tie-in and a spur to its badge.</summary>
    internal sealed class RouteMapSpec
    {
        [JsonProperty("station")] public string Station;     // the badge whose colour its badge takes
        [JsonProperty("clear")] public List<int[]> Clear = new List<int[]>();       // stock track cells it replaces
        [JsonProperty("tiles")] public List<BookTile> Tiles = new List<BookTile>();  // in the page canvas's frame
        [JsonProperty("cells")] public Dictionary<string, int[]> Cells = new Dictionary<string, int[]>();
    }

    internal sealed class StockEdit
    {
        [JsonProperty("track")] public string Track;
        [JsonProperty("trimEndTo")] public float[] TrimEndTo;
        [JsonProperty("endHeading")] public float EndHeading;
        [JsonProperty("stub")] public string Stub;
    }

    internal sealed class BufferStopSpec
    {
        [JsonProperty("at")] public float[] At;
        [JsonProperty("heading")] public float Heading;
    }

    /// <summary>A camera position for the inspector's screenshots.</summary>
    internal sealed class ViewSpec
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("eye")] public float[] Eye;
        [JsonProperty("target")] public float[] Target;
        [JsonProperty("fov")] public float Fov;
        [JsonProperty("wait")] public float Wait;
        [JsonProperty("kiosk")] public string Kiosk;   // instead of eye and target: at this office's kiosk
    }

    /// <summary>The inspector's run-through test: a car pushed from a place on one track along the route to another.</summary>
    internal sealed class RunSpec
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("from")] public string From;
        [JsonProperty("fromSpan")] public float FromSpan;
        [JsonProperty("to")] public string To;
        [JsonProperty("after")] public float After;
    }

    /// <summary>
    /// The map frame: the railway scene's coordinates, the same frame the
    /// terrain tiles use. Its objects now hang under the world mover's
    /// origin-shift parent, which moves as the player travels.
    /// </summary>
    internal static class Frames
    {
        public static Vector3 World(Transform railwayRoot, Vector3 map) =>
            railwayRoot.parent != null ? railwayRoot.parent.TransformPoint(map) : map;

        public static Vector3 WorldDir(Transform railwayRoot, Vector3 dir) =>
            (railwayRoot.parent != null ? railwayRoot.parent.TransformDirection(dir) : dir).normalized;

        public static Vector3 Map(Transform railwayRoot, Vector3 world) =>
            railwayRoot.parent != null ? railwayRoot.parent.InverseTransformPoint(world) : world;
    }

    internal static class Vec
    {
        public static Vector3 Of(float[] a) => new Vector3(a[0], a[1], a[2]);

        /// <summary>A unit direction from a map heading (degrees from +x toward +z) and a pitch in degrees.</summary>
        public static Vector3 Heading(float headingDeg, float pitchDeg)
        {
            float h = headingDeg * Mathf.Deg2Rad, p = pitchDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(h) * Mathf.Cos(p), Mathf.Sin(p), Mathf.Sin(h) * Mathf.Cos(p)).normalized;
        }
    }
}
