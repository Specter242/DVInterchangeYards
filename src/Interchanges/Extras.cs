using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeTechnologies;
using Newtonsoft.Json;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Interchanges
{
#pragma warning disable CS0649
    internal sealed class VegetationSpec
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("nodes")] public List<float[]> Nodes = new List<float[]>();
        [JsonProperty("widths")] public List<float> Widths = new List<float>();
        [JsonProperty("grassWidth")] public float GrassWidth;    // the formation, cleared of grass
    }

    internal sealed class TunnelSpec
    {
        [JsonProperty("track")] public string Track;
        [JsonProperty("portal")] public float[] Portal;       // rail at the portal, map frame
        [JsonProperty("direction")] public float[] Direction; // into the tunnel
        [JsonProperty("length")] public float Length;
        [JsonProperty("path")] public List<float[]> Path = new List<float[]>();
        [JsonProperty("face")] public List<float[]> Face = new List<float[]>();  // the cutting's section at the portal: right of the track, over the rail
    }
#pragma warning restore CS0649

    /// <summary>
    /// Vegetation masks along the new track (as the stock track has, so no
    /// tree or grass grows through the bed), and the off-map tunnels: a stock
    /// portal anchor (terrain hole and collision pass-through), a portal face
    /// and a lined tube.
    /// </summary>
    internal static class Extras
    {
        public static readonly List<VegetationMaskLine> Masks = new List<VegetationMaskLine>();
        public static readonly List<string> Notes = new List<string>();

        private static GameObject maskRoot;

        public static void Clear()
        {
            Masks.Clear();
            Notes.Clear();
            maskRoot = null;
            lining.Clear();
            cached = null;
        }

        public static void Build(Layout layout, Transform root)
        {
            foreach (var v in layout.Vegetation)
                Mask(v, root);
            foreach (var t in layout.Tunnels)
                Tunnel(t, root);
        }

        /// <summary>
        /// Vegetation Studio keeps its vegetation, and the masks it samples, in its own frame,
        /// which does not move when the game shifts the world's origin; a mask registers its
        /// line once, when it starts (or is updated), from its transform's world positions.
        /// So, as with the stock track masks (RailwayVegetationRemover), the masks hang off a
        /// root object that the origin shift leaves alone, with nodes in the system's frame,
        /// and are never re-registered later. Like the stock masks they keep the grass.
        /// </summary>
        private static void Mask(VegetationSpec v, Transform root)
        {
            if (v.Nodes.Count < 2)
                return;
            if (maskRoot == null)
            {
                maskRoot = new GameObject("[interchanges vegetation masks]");
                Notes.Add("stock railway vegetation remover " +
                          (GameObject.Find("[railway vegetation remover]") != null ? "had already run when the interchanges were built" : "had not yet run when the interchanges were built"));
            }
            var offset = AwesomeTechnologies.VegetationStudio.VegetationStudioManager.GetFloatingOriginOffset();
            var go = new GameObject($"[vegmask] {v.Name}");
            go.transform.SetParent(maskRoot.transform, false);
            var mask = go.AddComponent<VegetationMaskLine>();
            mask.RemoveGrass = false;
            mask.RemovePlants = mask.RemoveTrees = mask.RemoveObjects = mask.RemoveLargeObjects = true;
            mask.AdditionalGrassPerimiter = 2f;
            mask.AdditionalPlantPerimiter = 4f;
            mask.AdditionalTreePerimiter = 5f;
            mask.AdditionalObjectPerimiter = 5f;
            mask.AdditionalLargeObjectPerimiter = 6f;
            mask.LineWidth = v.Widths.Count > 0 ? v.Widths.Min() : 10f;
            var positions = v.Nodes.Select(n => Frames.World(root, Vec.Of(n)) - offset).ToArray();
            var widths = v.Nodes.Select((_, i) => i < v.Widths.Count ? v.Widths[i] : mask.LineWidth).ToArray();
            mask.AddNodesToEnd(positions, widths, positions.Select(_ => true).ToArray());
            Masks.Add(mask);

            // The formation itself is cleared of grass too, so the ballast and sleepers show
            // (stock beds get this from their gravel ground, which grass avoids).
            if (v.GrassWidth > 0)
            {
                var grass = new GameObject($"[vegmask] {v.Name} grass");
                grass.transform.SetParent(maskRoot.transform, false);
                var g = grass.AddComponent<VegetationMaskLine>();
                g.RemoveGrass = true;
                g.RemovePlants = g.RemoveTrees = g.RemoveObjects = g.RemoveLargeObjects = false;
                g.LineWidth = v.GrassWidth;
                g.AddNodesToEnd(positions);
                Masks.Add(g);
            }
        }

        private static void Tunnel(TunnelSpec t, Transform root)
        {
            var portal = Frames.World(root, Vec.Of(t.Portal));
            var dir = Frames.WorldDir(root, Vec.Of(t.Direction));
            var flat = new Vector3(dir.x, 0, dir.z).normalized;

            // The stock portal anchor: a terrain hole 3.2 m above its root and a pass-through box.
            var template = root.parent != null
                ? root.parent.Cast<Transform>().FirstOrDefault(c => c.name == "tunnel_collision_ignore")
                : null;
            if (template != null)
            {
                var anchor = Object.Instantiate(template.gameObject, root.parent);
                anchor.name = $"tunnel_collision_ignore ({t.Track})";
                // The hole is a sphere of 4.5 m radius, 3.2 m over the anchor. Set 2.25 m in, it centres
                // on where the cutting's terrain ends behind the face (export_layout.PORTAL_CUT_PAST),
                // opening it within the arch, and cuts nothing in front of the face. Like every stock
                // anchor (tools/stock_portals.py), it faces out of the tunnel: the hole manager opens
                // only the nearest hole that faces the camera.
                anchor.transform.SetPositionAndRotation(portal + flat * AnchorInset + Vector3.up * 0.15f, Quaternion.LookRotation(-flat, Vector3.up));
                Notes.Add($"tunnel {t.Track}: portal anchor placed");
            }
            else
            {
                Notes.Add($"tunnel {t.Track}: no stock portal anchor found; no terrain hole");
            }

            var material = FindMaterial();
            var lining = new GameObject($"[tunnel] {t.Track}");
            lining.transform.SetParent(root, false);
            lining.transform.position = portal;
            var path = t.Path.Select(p => lining.transform.InverseTransformPoint(Frames.World(root, Vec.Of(p)))).ToList();
            AddMesh(lining, "tube", Tube(path), material);
            var face = new GameObject("portal");
            face.transform.SetParent(lining.transform, false);
            face.transform.SetPositionAndRotation(portal, Quaternion.LookRotation(flat, Vector3.up));
            var outline = t.Face.Count >= 3
                ? t.Face.Select(f => new Vector2(f[0], f[1])).ToList()
                : new List<Vector2> { new Vector2(-5.5f, Floor), new Vector2(-5.5f, 8.9f), new Vector2(5.5f, 8.9f), new Vector2(5.5f, Floor) };
            AddMesh(face, "face", PortalFace(outline), material);
            // The floor just outside the portal, over what the terrain hole cuts from the ground.
            var apron = new GameObject("apron");
            apron.transform.SetParent(face.transform, false);
            AddMesh(apron, "apron", Apron(), material);
            // The far end: dark, so no daylight shows through the hill.
            var end = new GameObject("end");
            end.transform.SetParent(lining.transform, false);
            var last = path[path.Count - 1];
            var lastDir = last - path[path.Count - 2];
            lastDir.y = 0;
            end.transform.localPosition = last;
            end.transform.localRotation = Quaternion.LookRotation(lastDir.normalized, Vector3.up);
            AddMesh(end, "end", EndCap(), Dark);
            Notes.Add($"tunnel {t.Track}: {path.Count} lining rings, material {material.name}");
        }

        private const float AnchorInset = 2.25f;

        private static Material dark;

        private static Material Dark => dark != null ? dark
            : dark = new Material(Shader.Find("Standard")) { name = OwnPrefix + "tunnel dark", color = Color.black };

        private static Material cached;
        private static readonly List<MeshRenderer> lining = new List<MeshRenderer>();
        internal const string OwnPrefix = "interchanges ";

        /// <summary>A stock tiling concrete if one is loaded, else plain concrete grey.</summary>
        private static Material FindMaterial()
        {
            if (cached != null)
                return cached;
            cached = StockMaterial() ?? new Material(Shader.Find("Standard")) { name = OwnPrefix + "concrete", color = new Color(0.45f, 0.44f, 0.42f) };
            return cached;
        }

        private static Material StockMaterial()
        {
            var all = Resources.FindObjectsOfTypeAll<Material>().Where(m => m != null && !m.name.StartsWith(OwnPrefix, StringComparison.Ordinal)).ToList();
            bool Named(Material m, string part) => m.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
            bool Standard(Material m) => m.shader != null && m.shader.name.StartsWith("Standard", StringComparison.Ordinal);
            // Plain stone or concrete: not a painted, coloured, glass or decal variant.
            string[] skip = { "blue", "red", "green", "yellow", "orange", "white", "black", "paint", "decal", "glass", "stripe" };
            bool Plain(Material m) => !skip.Any(w => Named(m, w));
            // A concrete that tiles, named outright: whichever material loaded first used to win,
            // and the stock tunnel's own (tunnel_ent) is a texture atlas laid out for its portal
            // model, so on these walls it repeated whole pictures of a portal (owner, 2026-11-03:
            // "looks like our tunnel textures may be messed up"). Never a tunnel material.
            foreach (var name in new[] { "MB_ConcreteRough", "MB_Concrete", "MB_ConcreteVertical" })
            {
                var named = all.FirstOrDefault(m => m.name == name && Standard(m));
                if (named != null)
                    return named;
            }
            return all.FirstOrDefault(m => Named(m, "concrete") && !Named(m, "tunnel") && Standard(m) && Plain(m));
        }

        /// <summary>The loaded stock materials that could line a tunnel, for the inspector's log.</summary>
        public static IEnumerable<string> MaterialCandidates() =>
            Resources.FindObjectsOfTypeAll<Material>()
                .Where(m => m != null && !m.name.StartsWith(OwnPrefix, StringComparison.Ordinal) &&
                            new[] { "tunnel", "concrete", "stone", "brick" }.Any(w => m.name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(m => $"{m.name} ({m.shader?.name})").Distinct().OrderBy(n => n).Take(30);

        /// <summary>
        /// The railway loads before most scenery, so the lining may have had to start grey; once
        /// the world has loaded, a stock concrete takes its place if one is there.
        /// </summary>
        public static void RefreshMaterial()
        {
            if (lining.Count == 0 || cached == null || !cached.name.StartsWith(OwnPrefix, StringComparison.Ordinal))
                return;
            var stock = StockMaterial();
            if (stock == null)
            {
                Notes.Add("tunnel lining: no stock concrete loaded; it stays plain concrete grey");
                return;
            }
            cached = stock;
            foreach (var r in lining.Where(r => r != null))
                r.sharedMaterial = stock;
            Notes.Add($"tunnel lining: now {stock.name} ({stock.shader.name})");
        }

        private static void AddMesh(GameObject go, string name, Mesh mesh, Material material)
        {
            mesh.name = name;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            if (material == cached)
                lining.Add(renderer);
        }

        // The arch: walls 2.9 m from the centre, springing at 4.4 m above the rail, a round top.
        private const float HalfWidth = 2.9f, Spring = 4.4f, Floor = -0.65f;

        /// <summary>The arch with its floor: a closed outline, round from the floor's left end.</summary>
        private static List<Vector2> ClosedOutline()
        {
            var o = ArchOutline();
            o.Add(new Vector2(-HalfWidth, Floor));
            return o;
        }

        private static List<Vector2> ArchOutline(int arcSteps = 14)
        {
            var o = new List<Vector2> { new Vector2(-HalfWidth, Floor), new Vector2(-HalfWidth, Spring) };
            for (var i = 1; i < arcSteps; i++)
            {
                var a = Mathf.PI - Mathf.PI * i / arcSteps;
                o.Add(new Vector2(Mathf.Cos(a) * HalfWidth, Spring + Mathf.Sin(a) * HalfWidth));
            }
            o.Add(new Vector2(HalfWidth, Spring));
            o.Add(new Vector2(HalfWidth, Floor));
            return o;
        }

        /// <summary>The lining: the arch swept along the path, faces turned inward.</summary>
        private static Mesh Tube(List<Vector3> path)
        {
            var outline = ClosedOutline();
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float along = 0;
            for (var k = 0; k < path.Count; k++)
            {
                var fwd = (path[Math.Min(k + 1, path.Count - 1)] - path[Math.Max(k - 1, 0)]);
                fwd.y = 0;
                fwd.Normalize();
                var right = Vector3.Cross(Vector3.up, fwd).normalized;
                if (k > 0) along += (path[k] - path[k - 1]).magnitude;
                float around = 0;
                for (var i = 0; i < outline.Count; i++)
                {
                    if (i > 0) around += (outline[i] - outline[i - 1]).magnitude;
                    verts.Add(path[k] + right * outline[i].x + Vector3.up * outline[i].y);
                    uvs.Add(new Vector2(around / 4f, along / 4f));
                }
            }
            var n = outline.Count;
            for (var k = 0; k < path.Count - 1; k++)
                for (var i = 0; i < n - 1; i++)
                {
                    int a = k * n + i, b = a + 1, c = a + n, d = c + 1;
                    // Clockwise seen from inside (Unity's front faces), so the faces look inward.
                    tris.AddRange(new[] { a, b, c, b, d, c });
                }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A floor 4 m long outside the portal, a little under the formation, facing up.</summary>
        private static Mesh Apron()
        {
            const float w = HalfWidth + 0.5f, y = Floor - 0.02f, length = 4f;
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(-w, y, 0), new Vector3(w, y, 0), new Vector3(-w, y, -length), new Vector3(w, y, -length) });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(w / 1.5f, 0), new Vector2(0, length / 3f), new Vector2(w / 1.5f, length / 3f) });
            mesh.SetTriangles(new[] { 0, 1, 2, 1, 3, 2 }, 0);   // clockwise seen from above: facing up
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The far end: the closed outline filled, facing back toward the portal.</summary>
        private static Mesh EndCap()
        {
            var o = ClosedOutline();
            var verts = new List<Vector3> { new Vector3(0, (Floor + Spring + HalfWidth) / 2, 0) };
            verts.AddRange(o.Select(p => new Vector3(p.x, p.y, 0)));
            var tris = new List<int>();
            for (var i = 1; i < verts.Count - 1; i++)
                tris.AddRange(new[] { 0, i + 1, i });
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            // Face toward -z (the portal) whichever way the outline winds.
            if (mesh.normals.Length > 0 && mesh.normals[0].z > 0)
            {
                for (var i = 0; i < tris.Count; i += 3)
                    (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
            }
            return mesh;
        }

        /// <summary>The portal face: a wall 11 m wide and 9.5 m tall around the arch, facing out of the tunnel.</summary>
        /// <summary>
        /// The portal face: a headwall filling the cutting's section at the portal (its outline,
        /// which the terrain's end lies just behind) around the arch, facing out of the tunnel.
        /// Built as a strip between the arch and the outline, at every angle from the arch's
        /// centre that either outline turns at, so both keep their corners.
        /// </summary>
        private static Mesh PortalFace(List<Vector2> outline)
        {
            var arch = ClosedOutline();
            var centre = new Vector2(0, 2.5f);
            var angles = Enumerable.Range(0, 144).Select(i => 2 * Mathf.PI * i / 144)
                .Concat(arch.Concat(outline).Select(p => Mathf.Atan2(p.y - centre.y, p.x - centre.x)))
                .Select(a => (a + 2 * Mathf.PI) % (2 * Mathf.PI))
                .OrderBy(a => a).ToList();
            var verts = new List<Vector3>();
            foreach (var a in angles)
            {
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var inner = Hit(centre, dir, arch);
                var outer = Hit(centre, dir, outline);
                if ((outer - centre).sqrMagnitude < (inner - centre).sqrMagnitude)
                    outer = inner;
                verts.Add(new Vector3(inner.x, inner.y, 0));
                verts.Add(new Vector3(outer.x, outer.y, 0));
            }
            var tris = new List<int>();
            var n = angles.Count;
            for (var k = 0; k < n; k++)
            {
                int a = 2 * k, b = a + 1, c = 2 * ((k + 1) % n), d = c + 1;
                // Angles run anticlockwise, so this is clockwise seen from -z: facing out of the tunnel.
                tris.AddRange(new[] { a, c, b, b, c, d });
            }
            var mesh = new Mesh();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, verts.Select(v => new Vector2(v.x / 3f, v.y / 3f)).ToList());
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Where a ray from inside a closed outline first crosses it.</summary>
        private static Vector2 Hit(Vector2 from, Vector2 dir, List<Vector2> outline)
        {
            var best = float.MaxValue;
            for (var i = 0; i < outline.Count; i++)
            {
                Vector2 p = outline[i], q = outline[(i + 1) % outline.Count], e = q - p;
                var den = dir.x * e.y - dir.y * e.x;
                if (Mathf.Abs(den) < 1e-6f)
                    continue;
                var w = p - from;
                var t = (w.x * e.y - w.y * e.x) / den;    // along the ray
                var u = (w.x * dir.y - w.y * dir.x) / den; // along the edge
                if (t > 1e-4f && u >= -1e-4f && u <= 1 + 1e-4f && t < best)
                    best = t;
            }
            return best < float.MaxValue ? from + dir * best : from;
        }
    }
}
