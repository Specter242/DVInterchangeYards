using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Interchanges
{
    /// <summary>
    /// The interchanges' codes among a map's station names: on the handheld map
    /// (Map/MapPaper/Names) and on the offices' wall maps (MapPaperOffice/Names; owner,
    /// 2026-11-01: "just add the name abbreviations to the station maps"). Every one of them
    /// sets its 20 station names at the same places in its Names frame, so one placement,
    /// worked out in the handheld map's frame as MapMarkersController lays out its markers,
    /// serves them all. Each code is a clone of that sheet's own "MF", so it takes the sheet's
    /// font, size and lighting.
    /// </summary>
    internal static class MapNames
    {
        public const string Prefix = "[interchange name] ";
        public static readonly List<string> Notes = new List<string>();
        public static int WallMapsNamed { get; private set; }
        public static Transform Content;   // the content scene's root, where the offices stand

        public static void Reset()
        {
            Notes.Clear();
            WallMapsNamed = 0;
            Content = null;
        }

        /// <summary>The map frame (x, z) to a handheld map's Names frame.</summary>
        public static Func<Vector2, Vector3> Frame(MapMarkersController map, Transform names) =>
            m => names.InverseTransformPoint(map.transform.TransformPoint(MapMarks.Overlay.LocalOf(map, m, 0f)));

        /// <summary>Adds the codes to a Names container, unless they are there. Returns how many it added.</summary>
        public static int Add(Transform names, Func<Vector2, Vector3> toNames)
        {
            if (names == null || names.Cast<Transform>().Any(t => t.name.StartsWith(Prefix, StringComparison.Ordinal)))
                return 0;
            var template = names.Cast<Transform>().FirstOrDefault(t => t.name == "MF") ??
                           names.Cast<Transform>().FirstOrDefault(t => t.GetComponent<TMP_Text>() != null && t.name != "Legend");
            if (template == null)
                return 0;
            var added = 0;
            foreach (var d in MapMarks.Drawings())
            {
                var label = Object.Instantiate(template.gameObject, names, false);
                label.name = Prefix + d.Code;
                foreach (var l in label.GetComponents<DV.Localization.Localize>())
                    Object.DestroyImmediate(l);
                foreach (var l in label.GetComponents<I2.Loc.Localize>())
                    Object.DestroyImmediate(l);
                label.GetComponent<TMP_Text>().text = d.Code;
                var at = toNames(d.Label);
                label.transform.localPosition = new Vector3(at.x, at.y, template.localPosition.z);
                if (d.Marker.HasValue)
                {
                    // The marker's place and the side's direction in the Names frame; a marker is
                    // MarkerAcross wide there, and the name's near edge stands half that from its rim.
                    var o = toNames(d.Marker.Value);
                    var step = toNames(d.Marker.Value + MapMarks.Direction(d.Side) * 100f) - o;
                    var beside = label.AddComponent<NameBeside>();
                    beside.Marker = new Vector2(o.x, o.y);
                    beside.Direction = new Vector2(step.x, step.y).normalized;
                    beside.Reach = new Vector2(step.x, step.y).magnitude / 100f * MapMarks.MarkerAcross;
                    beside.Place();
                }
                added++;
            }
            return added;
        }

        /// <summary>
        /// Names the offices' wall maps under the content scene's root (the stock offices and the
        /// interchange offices' copies), placed by a handheld map: the one given, else the
        /// handheld map's prefab. Safe to call again: a wall map already named is left alone.
        /// </summary>
        public static void AddToWalls(Transform content, MapMarkersController map = null)
        {
            if (content == null || Builder.Built.Count == 0)
                return;
            if (LevelInfo.WorldSize.x <= 0f)
            {
                Notes.Add("wall maps: the world's size is not known yet; named later");
                return;
            }
            if (map == null)
            {
                var prefab = Resources.Load<GameObject>("Map");
                map = prefab != null ? prefab.GetComponentInChildren<MapMarkersController>(true) : null;
            }
            var handNames = map != null ? map.transform.Find("MapPaper/Names") : null;
            if (handNames == null)
            {
                Notes.Add("wall maps: no handheld map prefab to place the names by");
                return;
            }
            var frame = Frame(map, handNames);
            int walls = 0, named = 0, skipped = 0;
            foreach (var paper in content.GetComponentsInChildren<Transform>(true).Where(t => t.name == "MapPaperOffice"))
            {
                var names = paper.Find("Names");
                if (names == null)
                    continue;
                walls++;
                // The same names at the same places as the handheld map's, or it is some other sheet.
                var pairs = handNames.Cast<Transform>().Select(h => (H: h, W: names.Find(h.name))).Where(p => p.W != null && p.H.name != "Legend").ToList();
                if (pairs.Count < 10 || pairs.Any(p => (p.W.localPosition - p.H.localPosition).magnitude > 1e-4f))
                {
                    skipped++;
                    continue;
                }
                if (Add(names, frame) > 0)
                    named++;
            }
            WallMapsNamed += named;
            Notes.Add($"wall maps: {walls} found, {named} named now, {skipped} laid out otherwise and left alone");
        }
    }

    /// <summary>
    /// Sets a name beside its marker once its text is laid out: its text box centred on the line
    /// from the marker along the side, its near edge `Reach` from the marker's centre (all in
    /// its parent's frame, the map's Names). A wall map far from the player is inactive, so this
    /// waits for the name's first Start when its text could not be laid out at once.
    /// </summary>
    internal sealed class NameBeside : MonoBehaviour
    {
        public Vector2 Marker, Direction;
        public float Reach;
        public bool Placed;

        private void Start()
        {
            if (!Placed)
                Place();
        }

        public void Place()
        {
            var text = GetComponent<TMP_Text>();
            if (text == null || transform.parent == null)
                return;
            Bounds b;
            try
            {
                text.ForceMeshUpdate(true);
                b = text.textBounds;
            }
            catch (Exception)
            {
                return;   // not woken yet (a wall map far from the player): placed at its Start
            }
            if (b.size.x <= 0f)
                return;
            var parent = transform.parent;
            var corners = new[] { new Vector3(b.min.x, b.min.y, 0f), new Vector3(b.max.x, b.min.y, 0f), new Vector3(b.min.x, b.max.y, 0f), new Vector3(b.max.x, b.max.y, 0f) }
                .Select(c => (Vector2)parent.InverseTransformPoint(transform.TransformPoint(c))).ToList();
            var centre = new Vector2((corners.Min(c => c.x) + corners.Max(c => c.x)) / 2f, (corners.Min(c => c.y) + corners.Max(c => c.y)) / 2f);
            var half = corners.Max(c => Vector2.Dot(c - centre, Direction));
            var shift = Marker + Direction * (Reach + half) - centre;
            transform.localPosition += new Vector3(shift.x, shift.y, 0f);
            Placed = true;
        }
    }
}
