using System;
using System.Collections.Generic;
using System.Linq;
using DV.Items;
using DV.RenderTextureSystem.BookletRender;
using DV.Utils;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Interchanges
{
    /// <summary>
    /// The interchanges in the switch books (owner, 2026-11-01: "add a page in the station
    /// booklet for each interchange and update the switch map as well"; then "they need to have
    /// their own tab and not be associated with the nearby industry").
    ///
    /// - The station booklet (item MapSchematic) renders a page per station yard from the
    ///   template pages of a world-specific render prefab, each station a chapter with its tab
    ///   on the tab column down the page's edge. Each interchange gets a chapter of its own
    ///   after the stock ones: a page cloned from a station's first page, in the interchange's
    ///   name, code and colour, with the schematic replaced by its tiles (tools/schematic.py),
    ///   and a tab painted on the column after the stock tabs (TabColumn), its arrow on the
    ///   page pointing there. The tab column's touch strip grows by the new tabs.
    /// - The route map (item RouteMap) is one page of the same tiles. The stock straight at the
    ///   tie-in's cell gives way to the tie-in's switch, with a spur to the interchange's badge.
    /// - Each switch the player can touch sits in a cell of a page's touch grid, from the
    ///   generated switch data (JunctionGeneratedDataRuntime). The interchanges' pages are
    ///   appended there too, and the tie-ins added to the route map's page, each cell naming its
    ///   switch by its index in the registry's ordered junctions.
    ///
    /// The pages go after the last stock page in all three places: the render prefab's pages,
    /// the switch data's station pages and the booklet's chapters. If they disagree on where
    /// that is, the interchanges are left out of the booklet, so a touch never lands on the
    /// wrong page.
    /// </summary>
    internal static class Booklets
    {
        public static readonly List<string> Notes = new List<string>();

        /// <summary>An interchange's chapter: its page after the stock pages, its tab after the stock tabs.</summary>
        private sealed class Insert
        {
            public Layout Layout;
            public int Order;      // among the interchanges, in the order of their codes
            public int Page;       // its page index
        }

        private static List<Insert> plan;
        private static int stockPages = -1;
        private static bool planFailed;

        private const string WrapperPath = "Template Canvas/BackgroundImage/OuterWrapper";

        /// <summary>A new world: the plan is made again from the layouts built for it.</summary>
        public static void Reset()
        {
            plan = null;
            stockPages = -1;
            planFailed = false;
            Notes.Clear();
        }

        private static JunctionGeneratedDataRuntime StockData =>
            SingletonBehaviour<LevelInfo>.Instance != null ? SingletonBehaviour<LevelInfo>.Instance.junctionData : null;

        /// <summary>The interchanges' pages, after the stock switch data's station pages.</summary>
        private static List<Insert> Plan()
        {
            if (plan != null || planFailed)
                return plan;
            var data = StockData;
            if (data == null || Builder.Built.Count == 0)
                return null;
            stockPages = data.junctionPageDataStations.Length;
            plan = Builder.Built.Where(l => l.Booklet != null)
                .OrderBy(l => l.Code ?? l.Site, StringComparer.Ordinal)
                .Select((l, k) => new Insert { Layout = l, Order = k, Page = stockPages + k })
                .ToList();
            Notes.Add($"booklet plan: after the {stockPages} stock pages, " +
                      string.Join(", ", plan.Select(i => $"{i.Layout.Code} page {i.Page}")));
            return plan;
        }

        // ------------------------------------------------------------ switch data

        /// <summary>A switch's data for a page cell: its registry index, ids and position.</summary>
        private static bool DataFor(string site, string switchId, out Junction.JunctionData data)
        {
            data = default;
            var junction = Builder.JunctionById($"{site}/{switchId}");
            var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
            if (junction == null || registry == null)
                return false;
            var index = Array.IndexOf(registry.OrderedJunctions, junction);
            if (index < 0)
                return false;
            data = junction.junctionData;
            data.junctionIndex = index;
            data.position = Frames.Map(registry.TrackRootParent, junction.transform.position);
            data.excludeFromJunctionMap = false;
            data.isValid = true;
            data.linkedJunctions = new List<int>();
            return true;
        }

        private static List<JunctionGeneratedDataRuntime.DataCoord> Coords(Layout layout, Dictionary<string, int[]> cells, string what)
        {
            var coords = new List<JunctionGeneratedDataRuntime.DataCoord>();
            foreach (var kv in cells.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (!DataFor(layout.Site, kv.Key, out var data))
                {
                    Notes.Add($"{what} {layout.Site}: switch {kv.Key} not found");
                    continue;
                }
                coords.Add(new JunctionGeneratedDataRuntime.DataCoord { data = data, coord = new Vector2Int(kv.Value[0], kv.Value[1]) });
            }
            return coords;
        }

        private static JunctionGeneratedDataRuntime.JunctionPageData[] WithStationPages(JunctionGeneratedDataRuntime.JunctionPageData[] stock)
        {
            var inserts = Plan();
            if (inserts == null || inserts.Count == 0 || planFailed || stock.Length != stockPages)
                return stock;
            var pages = stock.ToList();
            foreach (var insert in inserts)
                pages.Add(new JunctionGeneratedDataRuntime.JunctionPageData
                {
                    categoryId = insert.Layout.Code,
                    junctionDataCoords = Coords(insert.Layout, insert.Layout.Booklet.Cells, "booklet"),
                });
            for (var i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                p.pageNumber = i;
                pages[i] = p;
            }
            return pages.ToArray();
        }

        private static JunctionGeneratedDataRuntime.JunctionPageData[] WithRouteMap(JunctionGeneratedDataRuntime.JunctionPageData[] stock)
        {
            if (stock.Length == 0 || Builder.Built.Count == 0)
                return stock;
            var page = stock[0];
            foreach (var layout in Builder.Built.Where(l => l.RouteMap != null).OrderBy(l => l.Site, StringComparer.Ordinal))
                page.junctionDataCoords.AddRange(Coords(layout, layout.RouteMap.Cells, "route map"));
            stock[0] = page;
            return stock;
        }

        [HarmonyPatch(typeof(JunctionGeneratedDataRuntime), nameof(JunctionGeneratedDataRuntime.GetPageData))]
        private static class PageData
        {
            private static void Postfix(JunctionGeneratedDataRuntime __instance, JunctionMap.JunctionMapType junctionMapType,
                                        ref JunctionGeneratedDataRuntime.JunctionPageData[] __result)
            {
                if (__result == null)
                    return;
                try
                {
                    switch (junctionMapType)
                    {
                        case JunctionMap.JunctionMapType.Station:
                            __result = WithStationPages(__result);
                            break;
                        case JunctionMap.JunctionMapType.World:
                            __result = WithRouteMap(__result);
                            break;
                        case JunctionMap.JunctionMapType.All:
                            var world = __instance.junctionPageDataWorld.Length;
                            __result = WithRouteMap(__result.Take(world).ToArray())
                                .Concat(WithStationPages(__result.Skip(world).ToArray())).ToArray();
                            break;
                    }
                }
                catch (Exception e)
                {
                    Main.Error($"switch book data: {e}");
                }
            }
        }

        // ------------------------------------------------------------ chapters

        /// <summary>Marks a booklet whose chapters the interchanges were appended to, and how many.</summary>
        internal sealed class Tabs : MonoBehaviour
        {
            public int Added;
        }

        [HarmonyPatch(typeof(ChapterSelector), "Start")]
        private static class Chapters
        {
            private static void Prefix(ChapterSelector __instance, List<ChapterSelector.PageBookChapter> ___allChapters)
            {
                try
                {
                    var map = __instance.GetComponent<JunctionMap>();
                    if (map == null || map.junctionMapType != JunctionMap.JunctionMapType.Station || ___allChapters == null ||
                        ___allChapters.Count == 0 || __instance.GetComponent<Tabs>() != null)
                        return;
                    var inserts = Plan();
                    if (inserts == null || inserts.Count == 0 || planFailed)
                        return;
                    var last = ___allChapters[___allChapters.Count - 1].endingPage;
                    if (last != stockPages - 1)
                    {
                        Notes.Add($"booklet: its chapters end at page {last}, not at the last stock page {stockPages - 1}; no chapters added");
                        return;
                    }
                    foreach (var insert in inserts)
                        ___allChapters.Add(new ChapterSelector.PageBookChapter(insert.Page, insert.Page, true));
                    __instance.gameObject.AddComponent<Tabs>().Added = inserts.Count;
                }
                catch (Exception e)
                {
                    Main.Error($"switch book chapters: {e}");
                }
            }
        }

        /// <summary>
        /// The tab column's touch strip (the chapter selector's touchscreen) grows by the new tabs:
        /// one more cell of the same size for each, past the last stock tab, where the tabs are
        /// painted. The touchscreen maps a touch to a cell from its own origin and its collider's
        /// size, so the collider grows and the touchscreen moves by half the growth: every stock
        /// cell stays where it was.
        /// </summary>
        [HarmonyPatch(typeof(ChapterSelector), "OnTouchscreenInitialized")]
        private static class TabStrip
        {
            private static void Prefix(ChapterSelector __instance, List<ChapterSelector.PageBookChapter> ___allChapters)
            {
                try
                {
                    var tabs = __instance.GetComponent<Tabs>();
                    var touchscreen = __instance.BookmarksTouchscreen;
                    if (tabs == null || touchscreen == null)
                        return;
                    var selectable = ___allChapters.Count(c => c.allowChapterSelector);
                    if (touchscreen.gridSize.x != selectable - tabs.Added)
                    {
                        Notes.Add($"booklet: the tab strip has {touchscreen.gridSize.x} cells for {selectable - tabs.Added} stock tabs; not widened");
                        return;
                    }
                    Widen(touchscreen, tabs.Added);
                }
                catch (Exception e)
                {
                    Main.Error($"switch book tabs: {e}");
                }
            }
        }

        private static void Widen(DV.CabControls.TouchscreenBase touchscreen, int added)
        {
            var t = Traverse.Create(touchscreen);
            var collider = t.Field("interactionCollider").GetValue<BoxCollider>();
            if (collider == null || collider.transform != touchscreen.transform)
            {
                Notes.Add("booklet: the tab strip's collider is not on its touchscreen; not widened");
                return;
            }
            var cell = touchscreen.sectionSize.x;
            var size = collider.size;
            size.x += cell * added;
            collider.size = size;
            touchscreen.gridSize = new Vector2Int(touchscreen.gridSize.x + added, touchscreen.gridSize.y);
            // Cell i lies at origin - half + i * cell, or mirrored when the strip counts from its other end.
            var shift = (touchscreen.flipHorizontalCoords ? -1f : 1f) * cell * added / 2f;
            touchscreen.transform.localPosition += touchscreen.transform.localRotation * new Vector3(shift, 0f, 0f);
            t.Field("localInteractionHalfSize").SetValue(new Vector2(size.x, size.z) * 0.5f);
            Notes.Add($"booklet: tab strip widened to {touchscreen.gridSize.x} cells of {cell * 1000f:F2} mm");
        }

        // ------------------------------------------------------------ pages

        [HarmonyPatch(typeof(StaticPagesRender), "GetStaticTemplatePaperData")]
        private static class Render
        {
            private static void Prefix(StaticPagesRender __instance)
            {
                try
                {
                    if (__instance.GetComponent<Drawn>() != null)
                        return;
                    var name = __instance.gameObject.name;
                    if (name.StartsWith("MapSchematicStaticRender", StringComparison.Ordinal))
                        AddPages(__instance);
                    else if (name.StartsWith("RouteMapStaticRender", StringComparison.Ordinal))
                        AddRouteMap(__instance);
                    else
                        return;
                    __instance.gameObject.AddComponent<Drawn>();
                }
                catch (Exception e)
                {
                    Main.Error($"switch book pages: {e}");
                }
            }
        }

        /// <summary>Marks a render prefab instance the interchanges are already drawn in.</summary>
        internal sealed class Drawn : MonoBehaviour
        {
        }

        private static string StationOfPage(StaticPageTemplatePaper page)
        {
            var n = page != null ? page.gameObject.name : "";
            var dash = n.LastIndexOf('-');
            return n.StartsWith("[", StringComparison.Ordinal) && dash > 1 ? n.Substring(1, dash - 1) : null;
        }

        private static void AddPages(StaticPagesRender render)
        {
            var inserts = Plan();
            if (inserts == null || inserts.Count == 0 || planFailed)
                return;
            var pages = render.staticPages;
            // The render prefab's pages must match the switch data's, page for page.
            if (pages.Count != stockPages)
            {
                planFailed = true;
                Notes.Add($"booklet: the render has {pages.Count} pages but the switch data {stockPages}; the interchanges are left out");
                return;
            }
            // The stations in their tabs' order: their pages' order.
            var stations = pages.Select(StationOfPage).Where(s => s != null).Distinct().ToList();
            var arrow = ArrowFit(pages, stations);

            // The tab column, with the interchanges' tabs, on every page.
            var navigators = pages.Select(p => p.transform.Find(WrapperPath + "/Navigator")?.GetComponent<Image>()).Where(i => i != null).ToList();
            var column = navigators.Count > 0
                ? TabColumn.Paint(navigators[0].sprite, stations, inserts.Select(i => (i.Layout.Code, i.Layout.Color32)).ToList(), Notes)
                : null;
            if (column != null)
                foreach (var n in navigators)
                    n.sprite = column;

            var prototypes = Prototypes(pages.Select(p => Find(p.transform, WrapperPath + "/SMap")));
            foreach (var insert in inserts)
            {
                var layout = insert.Layout;
                var spec = layout.Booklet;
                var source = pages.First(p => StationOfPage(p) == spec.Station);
                var copy = Object.Instantiate(source.gameObject, source.transform.parent, false);
                copy.name = $"[{layout.Code}-{spec.YardId}]";
                var wrapper = Find(copy.transform, WrapperPath);
                SetText(wrapper, "Text-StationName", layout.Title);
                SetText(wrapper, "Text-StationID", layout.Code);
                SetText(wrapper, "Text-YardID", spec.YardId);
                SetText(wrapper, "Text-YardType", spec.YardType);
                var colour = (Color)layout.Color32;
                var header = wrapper.Find("StationBg")?.GetComponent<Image>();
                if (header != null)
                    header.color = colour;
                var pointer = wrapper.Find("Arrow") as RectTransform;
                if (pointer != null && arrow.HasValue)
                    pointer.anchoredPosition = new Vector2(pointer.anchoredPosition.x, arrow.Value.At(stations.Count + insert.Order));
                var smap = Find(wrapper, "SMap");
                // The station's own track badge, with its type marks and texts, recoloured.
                var badge = pages.Where(p => StationOfPage(p) == spec.Station)
                    .Select(p => p.transform.Find(WrapperPath + "/SMap"))
                    .Where(m => m != null).SelectMany(m => m.Cast<Transform>())
                    .FirstOrDefault(t => SpriteOf(t) == "SM-ID")?.gameObject;
                foreach (var child in smap.Cast<Transform>().ToList())
                    Object.DestroyImmediate(child.gameObject);
                var made = Place(smap, spec.Tiles, prototypes, badge, colour);
                pages.Add(copy.GetComponent<StaticPageTemplatePaper>());
                Notes.Add($"booklet: page {copy.name} at {pages.Count - 1}, tab {stations.Count + insert.Order}, {made} tiles, {spec.Cells.Count} switches");
            }
        }

        /// <summary>
        /// Where a tab's arrow stands on a page: the stock first pages' arrows, one per tab,
        /// fitted to a line over the tab's index (they stand a tab apart, give or take a unit).
        /// </summary>
        private static (float A, float B, Func<int, float> At)? ArrowFit(List<StaticPageTemplatePaper> pages, List<string> stations)
        {
            var points = new List<(int Tab, float Y)>();
            for (var i = 0; i < stations.Count; i++)
            {
                var first = pages.FirstOrDefault(p => StationOfPage(p) == stations[i]);
                if (first != null && first.transform.Find(WrapperPath + "/Arrow") is RectTransform rt)
                    points.Add((i, rt.anchoredPosition.y));
            }
            if (points.Count < 2)
            {
                Notes.Add("booklet: too few stock arrows to place the interchanges' arrows");
                return null;
            }
            double n = points.Count, sx = points.Sum(p => p.Tab), sy = points.Sum(p => p.Y);
            double sxx = points.Sum(p => (double)p.Tab * p.Tab), sxy = points.Sum(p => p.Tab * (double)p.Y);
            var b = (float)((n * sxy - sx * sy) / (n * sxx - sx * sx));
            var a = (float)((sy - b * sx) / n);
            var worst = points.Max(p => Math.Abs(p.Y - (a + b * p.Tab)));
            Notes.Add($"booklet: tab arrows every {b:F2} from {a:F1}, the stock ones within {worst:F1} of it");
            return (a, b, k => a + b * k);
        }

        private static void AddRouteMap(StaticPagesRender render)
        {
            var layouts = Builder.Built.Where(l => l.RouteMap != null).ToList();
            var paper = render.staticPages.FirstOrDefault();
            if (layouts.Count == 0 || paper == null)
                return;
            var wrapper = Find(paper.transform, "Template Canvas/BackgroundImage/OuterWrapper");
            var tracks = Find(wrapper, "Tracks");
            var switches = Find(wrapper, "Switches");
            var stations = Find(wrapper, "Stations");
            var prototypes = Prototypes(new[] { tracks, switches, stations });
            foreach (var layout in layouts.OrderBy(l => l.Site, StringComparer.Ordinal))
            {
                var spec = layout.RouteMap;
                var cleared = 0;
                foreach (var cell in spec.Clear)
                    foreach (var t in tracks.Cast<Transform>().Concat(switches.Cast<Transform>()))
                        if (t.gameObject.activeSelf && RouteCell(t) == new Vector2Int(cell[0], cell[1]))
                        {
                            t.gameObject.SetActive(false);
                            cleared++;
                        }
                var badge = stations.Cast<Transform>().FirstOrDefault(t => BadgeText(t) == spec.Station)?.gameObject;
                var made = 0;
                foreach (var tile in spec.Tiles)
                {
                    var into = tile.Sprite == "SM-ID" ? stations : tile.Sprite == "SM-Switch" ? switches : tracks;
                    made += Place(into, new[] { tile }, prototypes, badge, (Color)layout.Color32);
                }
                Notes.Add($"route map: {layout.Site}: {cleared} stock tile(s) cleared, {made} tiles, tie-in at {string.Join(", ", spec.Cells.Select(c => $"{c.Key} ({c.Value[0]}, {c.Value[1]})"))}");
            }
        }

        private static Vector2Int RouteCell(Transform t)
        {
            var p = ((RectTransform)t).anchoredPosition;
            return new Vector2Int(Mathf.FloorToInt((p.x + 800f) / 50f), Mathf.FloorToInt((p.y + 800f) / 50f));
        }

        // ------------------------------------------------------------ tiles

        private static Transform Find(Transform root, string path) =>
            root.Find(path) ?? throw new InvalidOperationException($"no {path} under {root.name}");

        private static string SpriteOf(Transform t) => t.GetComponent<Image>()?.sprite?.name;

        private static string BadgeText(Transform badge)
        {
            var text = badge.Find("text-ID-Black")?.GetComponent<TMP_Text>()?.text ?? "";
            return text.Replace("\\n", "").Replace("\n", "").Trim();
        }

        /// <summary>A stock tile of each sprite, from the given containers' children.</summary>
        private static Dictionary<string, GameObject> Prototypes(IEnumerable<Transform> containers)
        {
            var result = new Dictionary<string, GameObject>();
            foreach (var c in containers.Where(c => c != null))
                foreach (Transform t in c)
                {
                    var s = SpriteOf(t);
                    if (s != null && !result.ContainsKey(s))
                        result[s] = t.gameObject;
                }
            return result;
        }

        /// <summary>Clones of stock tiles, placed, turned and mirrored as the specs say; badges in the interchange's colour.</summary>
        private static int Place(Transform into, IEnumerable<BookTile> tiles, Dictionary<string, GameObject> prototypes, GameObject badge, Color colour)
        {
            var made = 0;
            foreach (var tile in tiles)
            {
                var proto = tile.Sprite == "SM-ID" && badge != null ? badge : prototypes.TryGetValue(tile.Sprite, out var p) ? p : null;
                if (proto == null)
                {
                    Notes.Add($"no stock tile with sprite {tile.Sprite}");
                    continue;
                }
                var go = Object.Instantiate(proto, into, false);
                go.name = $"{Extras.OwnPrefix}{tile.Sprite}";
                var rt = (RectTransform)go.transform;
                rt.anchoredPosition = new Vector2(tile.X, tile.Y);
                rt.localRotation = Quaternion.Euler(0f, 0f, tile.Rotation);
                rt.localScale = new Vector3(tile.ScaleX, tile.ScaleY, 1f);
                go.SetActive(true);
                if (tile.Sprite == "SM-ID")
                {
                    SetBadge(go.transform, tile.Text ?? "");
                    var image = go.GetComponent<Image>();
                    if (image != null)
                        image.color = colour;
                }
                made++;
            }
            return made;
        }

        /// <summary>A badge showing only its text: no type mark, no second colour of text.</summary>
        private static void SetBadge(Transform badge, string text)
        {
            foreach (Transform child in badge)
            {
                var keep = child.name == "text-ID-Black";
                child.gameObject.SetActive(keep);
                if (keep)
                    SetTextOn(child, text);
            }
        }

        private static void SetText(Transform wrapper, string name, string text)
        {
            var t = wrapper.Find(name);
            if (t == null)
            {
                Notes.Add($"booklet page: no {name}");
                return;
            }
            SetTextOn(t, text);
        }

        /// <summary>Sets a text, taking off the localisation that would put the stock term back.</summary>
        private static void SetTextOn(Transform t, string text)
        {
            foreach (var l in t.GetComponents<DV.Localization.Localize>())
                Object.DestroyImmediate(l);
            foreach (var l in t.GetComponents<I2.Loc.Localize>())
                Object.DestroyImmediate(l);
            var tmp = t.GetComponent<TMP_Text>();
            if (tmp == null)
                return;
            var size = tmp.fontSize;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax = size;
            tmp.fontSizeMin = Mathf.Max(6f, size * 0.4f);
            tmp.text = text;
        }

        // ------------------------------------------------------------ for the inspector

        /// <summary>The plan's page index of each interchange page.</summary>
        public static Dictionary<string, int> PageIndices() =>
            (Plan() ?? new List<Insert>()).ToDictionary(i => i.Layout.Site, i => i.Page);

        public static int StockPages => stockPages;

        public static bool PlanFailed => planFailed;
    }
}
