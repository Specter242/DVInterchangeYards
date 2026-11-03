using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DV.Common;
using DV.PointSet;
using DV.TerrainSystem;
using DV.ThingTypes;
using DV.UserManagement;
using DV.Utils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Interchanges
{
    /// <summary>
    /// In-game inspection for development, run only when <c>inspect.txt</c>
    /// sits in the mod's folder (the release never ships one). It continues
    /// the latest free-roam session from the main menu, then writes
    /// <c>inspect.log</c>:
    /// <list type="bullet">
    /// <item>the build notes, every new track's connections, and a throw of each new switch;</item>
    /// <item>the tie-ins' through legs and stubs against the stock track they replaced;</item>
    /// <item>a short cut of cars standing on each yard, and how the save writes them;</item>
    /// <item>a car pushed along each layout's test routes through the new switches,
    ///   with a save and load round trip of a car standing on a tie-in;</item>
    /// <item>a camera at each layout view (<c>inspect-NAME.png</c>), and the rail's height
    ///   over the ground wherever the terrain was loaded, against the designed bed;</item>
    /// <item>the errors and exceptions the game logged meanwhile.</item>
    /// </list>
    /// It quits afterwards if the trigger says "quit".
    /// </summary>
    internal sealed class Inspector : MonoBehaviour
    {
        public static Inspector Host;
        private static bool requested, quitAfter, continued, started;
        private static bool saveTest, reloadTest;   // a real save of cars on our track, then a fresh game loading it
        private static bool saveTestOnly, vanillaTest;

        public static bool VanillaRequested(string folder)
        {
            var trigger = Path.Combine(folder, "inspect.txt");
            return File.Exists(trigger) && File.ReadAllText(trigger).IndexOf("vanilla", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static float nextMenuCheck;
        private StreamWriter log;
        private int problems;

        private const float PushSpeed = 8f;            // m/s along the test routes
        private const float BedTolerance = 0.15f;      // ground vs designed formation, m

        private static string Folder => Main.Mod.Path;

        public static void Install()
        {
            var go = new GameObject("[interchanges]");
            DontDestroyOnLoad(go);
            Host = go.AddComponent<Inspector>();
            var trigger = Path.Combine(Folder, "inspect.txt");
            requested = File.Exists(trigger);
            if (requested)
            {
                var text = File.ReadAllText(trigger);
                quitAfter = text.IndexOf("quit", StringComparison.OrdinalIgnoreCase) >= 0;
                saveTest = text.IndexOf("savetest", StringComparison.OrdinalIgnoreCase) >= 0;
                reloadTest = text.IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0;
                saveTestOnly = text.IndexOf("savetestonly", StringComparison.OrdinalIgnoreCase) >= 0;
                vanillaTest = text.IndexOf("vanilla", StringComparison.OrdinalIgnoreCase) >= 0;
                var fault = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("fault:", StringComparison.Ordinal));
                if (fault != null)
                {
                    // fault:SITE or fault:SITE@POINT
                    var spec = fault.Substring("fault:".Length).Trim().Split('@');
                    Builder.FaultSite = spec[0].Trim();
                    Builder.FaultPoint = spec.Length > 1 ? spec[1].Trim() : "trim";
                }
                Application.logMessageReceived += OnLog;
                Main.Log("inspection requested");
            }
            WorldStreamingInit.LoadingFinished += () =>
            {
                loaded = true;
                if (requested && !started)
                {
                    started = true;
                    Host.StartCoroutine(Host.Run());
                }
            };
        }

        // ------------------------------------------------------------ the game's own log

        private static readonly Dictionary<string, int> logged = new Dictionary<string, int>();
        private static readonly List<string> loggedOrder = new List<string>();

        // Errors that the owner's copied save brings with it while it loads, with the other mods
        // turned off (their licenses, items, cargo and cars), the game's own VR probe, and one at quitting.
        private static readonly string[] Expected =
        {
            "Bad load data for CarDebtData", "Higher number of cargo types", "Bolt.SceneVariables", "OpenVR",
            "Unknown thing (", "Unknown saved license", "Couldn't find item prefab", "license item duplicate",
            "Error while loading car data (not all data was present)",
        };

        private static bool loaded;

        private static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception && type != LogType.Error)
                return;
            var where = (stack ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            var key = $"{(loaded ? "playing" : "loading")} {type}: {(condition ?? "").Split('\n')[0]}{(where.Length > 0 ? " @ " + where : "")}";
            if (!logged.ContainsKey(key))
            {
                logged[key] = 0;
                loggedOrder.Add(key);
            }
            logged[key]++;
        }

        // ------------------------------------------------------------ menu

        private static float menuSince = -1f;
        private static bool menuShot;

        private void Update()
        {
            if (!requested || continued || WorldStreamingInit.IsLoaded || Time.unscaledTime < nextMenuCheck)
                return;
            nextMenuCheck = Time.unscaledTime + 2f;
            var menu = FindObjectOfType<DV.UI.MainMenu>();
            if (menu == null)
                return;
            if (menuSince < 0)
                menuSince = Time.unscaledTime;
            if (!menuShot && Time.unscaledTime - menuSince > 45f)
            {
                menuShot = true;
                ScreenCapture.CaptureScreenshot(Path.Combine(Folder, "inspect-menu.png"));
                Main.Log("inspection: still at the main menu after 45 s; captured inspect-menu.png");
            }
            // Launched with the game's own -continueGame, the game continues by itself.
            if (Array.Exists(Environment.GetCommandLineArgs(), a => a.StartsWith("-continueGame", StringComparison.Ordinal)))
                return;
            var user = SingletonBehaviour<UserManager>.Instance?.CurrentUser;
            // The harness selects its own copy of a free-roam session; prefer that over the current mode.
            var session = user?.CurrentSession;
            if (user != null && user.CurrentSessionPerMode.TryGetValue("FreeRoam", out var free))
                session = free;
            if (session?.LatestSave == null)
            {
                Main.Log($"inspection: at the menu, user {(user == null ? "none" : "found")}, free-roam session {(session == null ? "none" : "without a save")}");
                return;
            }
            continued = true;
            Main.Log($"inspection: continuing session '{session.Name}'");
            AStartGameData.Continue(session.LatestSave, useSessionDifficulty: true).MakeCurrent();
            SceneSwitcher.SwitchToScene(DVScenes.Game);
        }

        // ------------------------------------------------------------ report

        private void Line(string text)
        {
            Main.Log("inspect: " + text);
            log?.WriteLine(text);
            log?.Flush();
        }

        private void Problem(string text)
        {
            problems++;
            Line("PROBLEM " + text);
        }

        private void Try(string what, Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Problem($"{what} threw: {e}");
            }
        }

        private static Transform Root => SingletonBehaviour<RailTrackRegistryBase>.Instance.TrackRootParent;

        private static string Short(RailTrack t)
        {
            if (t == null) return "none";
            var parent = t.transform.parent;
            if (t.name.StartsWith("[track ", StringComparison.Ordinal) && parent != null)
                return $"{parent.name} {t.name.Substring(7).TrimEnd(']')}";
            return t.name;
        }

        private IEnumerator Run()
        {
            if (Builder.FaultSite != null)
            {
                yield return new WaitForSeconds(8f);
                FaultCheck();
                if (quitAfter)
                {
                    yield return new WaitForSeconds(2f);
                    Application.Quit();
                }
                yield break;
            }
            if (vanillaTest)
            {
                yield return Vanilla();
                if (quitAfter)
                {
                    yield return new WaitForSeconds(2f);
                    Application.Quit();
                }
                yield break;
            }
            if (saveTestOnly)
            {
                log = new StreamWriter(Path.Combine(Folder, "inspect.log"), false);
                Line($"inspection {DateTime.Now:yyyy-MM-dd HH:mm:ss}: save test only");
                yield return new WaitForSeconds(8f);
                Line($"build failure: {Builder.Failure ?? "none"}");
                yield return SaveTest();
                LogReport();
                Line($"INSPECTION DONE: {problems} problem(s)");
                log.Dispose();
                log = null;
                if (quitAfter)
                {
                    yield return new WaitForSeconds(2f);
                    Application.Quit();
                }
                yield break;
            }
            if (reloadTest)
            {
                yield return Reload();
                if (quitAfter)
                {
                    yield return new WaitForSeconds(2f);
                    Application.Quit();
                }
                yield break;
            }
            log = new StreamWriter(Path.Combine(Folder, "inspect.log"), false);
            Line($"inspection {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            yield return new WaitForSeconds(8f);
            Try("structure check", Structure);
            Try("buffer stops", BufferStops);
            Try("job tracks", JobTrackCheck);
            Try("switch throws", Switches);
            Try("switch save round trip", JunctionRoundTrip);
            Try("tie-in geometry", TieInGeometry);
            yield return Cars();
            foreach (var layout in Main.Layouts)
                foreach (var run in layout.Runs)
                    yield return Push(run);
            Try("office check", OfficeCheck);
            foreach (var layout in Main.Layouts)
                foreach (var view in layout.Views)
                {
                    if (view.Kiosk != null)
                    {
                        yield return Kiosk(view);
                        continue;
                    }
                    yield return Shot(view);
                    Try("ground check", Ground);
                    Try("office ground", OfficeGround);
                }
            // Each buffer stop up close, from beyond the track's end.
            foreach (var layout in Builder.Built)
                for (var i = 0; i < layout.BufferStops.Count; i++)
                {
                    var b = layout.BufferStops[i];
                    var at = Vec.Of(b.At);
                    var outward = Vec.Heading(b.Heading, 0f);
                    var side = Vector3.Cross(Vector3.up, outward);
                    var eye = at + outward * 11f + side * 4f + Vector3.up * 3.5f;
                    var target = at - outward * 3f + Vector3.up * 0.8f;
                    yield return Shot(new ViewSpec
                    {
                        Name = $"buffer-{layout.Code?.ToLowerInvariant() ?? layout.Site}-{i}", Eye = new[] { eye.x, eye.y, eye.z },
                        Target = new[] { target.x, target.y, target.z }, Fov = 50f, Wait = 4f,
                    });
                }
            yield return MapAndFastTravel();
            yield return Books();
            Try("ground report", GroundReport);
            Try("office ground report", OfficeGroundReport);
            Line($"terrain: {TerrainEdits.TilesEdited} tile loads edited, {TerrainEdits.SamplesChanged} height samples changed; " +
                 $"{TerrainEdits.TexelsPainted} splat texels painted with layer {TerrainEdits.StationLayer} ({TerrainEdits.StationLayerName ?? "not seen"}); " +
                 $"vegetation masks {Extras.Masks.Count(m => m != null)}");
            foreach (var note in Extras.Notes.Where(n => n.StartsWith("tunnel lining", StringComparison.Ordinal)))
                Line("note: " + note);
            Line("lining candidates loaded now: " + string.Join("; ", Extras.MaterialCandidates()));
            Line($"saves this session: {CarSaves.MappedOnSave} car(s) saved onto stock track, {CarSaves.PlacedOnSave} saved derailed with their places; " +
                 $"on load: {CarSaves.PlacedOnLoad} placed, {CarSaves.MappedOnLoad} bogie(s) mapped, {CarSaves.LeftDerailedOnLoad} left derailed, {CarSaves.StaleOnLoad} stale record(s) ignored, {CarSaves.JunctionsRestored} switch(es) set");
            if (saveTest)
                yield return SaveTest();
            LogReport();
            Line($"INSPECTION DONE: {problems} problem(s)");
            log.Dispose();
            log = null;
            if (quitAfter)
            {
                yield return new WaitForSeconds(2f);
                Application.Quit();
            }
        }

        // ------------------------------------------------------------ structure

        private static string Describe(RailTrack t, bool first)
        {
            var j = first ? t.inJunction : t.outJunction;
            var b = first ? t.inBranch : t.outBranch;
            if (j != null) return $"junction {j.transform.parent?.name ?? j.name}";
            if (b?.track != null) return $"{Short(b.track)} ({(b.first ? "start" : "end")})";
            return "nothing";
        }

        /// <summary>
        /// The interchanges' buffer stops stand at their track ends and show: a stock buffer stop
        /// hides itself as it wakes and shows itself once the save's cars are in.
        /// </summary>
        private void BufferStops()
        {
            var root = Root;
            var stock = root.Cast<Transform>().FirstOrDefault(t => t.name == "term-buffer")?.GetComponentInChildren<BufferStop>(true);
            Line($"buffer stops: a stock one {(stock != null && stock.gameObject.activeInHierarchy ? "shows" : "is hidden")}");
            foreach (var layout in Builder.Built)
            {
                var ours = root.Cast<Transform>().Where(t => t.name == $"term-buffer ({layout.Site})").ToList();
                if (ours.Count != layout.BufferStops.Count)
                    Problem($"buffer stops {layout.Site}: {ours.Count} built of {layout.BufferStops.Count}");
                foreach (var b in ours)
                {
                    var stop = b.GetComponentInChildren<BufferStop>(true);
                    var shown = stop != null && stop.gameObject.activeInHierarchy;
                    var drawn = b.GetComponentsInChildren<MeshRenderer>(false).Count(r => r.enabled);
                    var at = Frames.Map(root, b.position);
                    Line($"buffer stop {layout.Site} at ({at.x:F1}, {at.y:F2}, {at.z:F1}): {(shown ? "shows" : "hidden")}, {drawn} mesh(es) drawn");
                    if (!shown || drawn == 0)
                        Problem($"buffer stop {layout.Site} at ({at.x:F0}, {at.z:F0}) does not show");
                }
            }
        }

        /// <summary>Each interchange yard track carries its job ID and is known to the yard organizer.</summary>
        private void JobTrackCheck()
        {
            foreach (var note in JobTracks.Notes)
                Line("job tracks: " + note);
            var organizer = SingletonBehaviour<YardTracksOrganizer>.Instance;
            var expected = JobTracks.Ids.Select(i => i.FullID).ToList();
            foreach (var id in expected)
            {
                var found = organizer != null && organizer.yardTrackIdToTrack.TryGetValue(id, out var track) ? track : null;
                var rail = found != null && RailTrackRegistry.LogicToRailTrack.TryGetValue(found, out var r) ? r : null;
                Line($"job track {id}: {(found == null ? "not in the organizer" : $"{rail?.name ?? "?"}, {found.length:F0} m, managed {organizer.IsTrackManagedByOrganizer(found)}, shown {found.ID.FullDisplayID}")}");
                if (found == null || !organizer.IsTrackManagedByOrganizer(found))
                    Problem($"job track {id} is not registered with the yard organizer");
            }
            if (expected.Count != Builder.Built.Sum(l => l.Tracks.Count(t => System.Text.RegularExpressions.Regex.IsMatch(t.Name, @" yard \d+$"))))
                Problem($"job tracks: {expected.Count} assigned");
        }

        private void Structure()
        {
            var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
            Line($"build failure: {Builder.Failure ?? "none"}");
            if (Builder.Failure != null)
                Problem("the build failed");
            foreach (var note in Builder.Report.Concat(Extras.Notes))
                Line("note: " + note);
            Line($"our tracks {Builder.OurTracks.Count}, our junctions {Builder.OurJunctions.Count}, trimmed stock {Builder.TrimmedStock.Count}");
            Line($"tracks hash {registry.TracksHash}, junctions hash {registry.JunctionsHash}");
            var all = registry.OrderedRailtracks;
            var firstOurs = Array.FindIndex(all, Builder.IsOurs);
            var lastStock = Array.FindLastIndex(all, t => !Builder.IsOurs(t));
            Line($"ordered tracks {all.Length}; first of ours at {firstOurs}, last stock at {lastStock}");
            if (firstOurs >= 0 && firstOurs < lastStock)
                Problem("our tracks are not all after the stock tracks in the ordered list");
            var junctions = registry.OrderedJunctions;
            var firstOurJ = Array.FindIndex(junctions, Builder.IsOurs);
            var lastStockJ = Array.FindLastIndex(junctions, j => !Builder.IsOurs(j));
            Line($"ordered junctions {junctions.Length}; first of ours at {firstOurJ}, last stock at {lastStockJ}");
            if (firstOurJ >= 0 && firstOurJ < lastStockJ)
                Problem("our junctions are not all after the stock junctions in the ordered list");

            foreach (var t in Builder.OurTracks.Concat(Builder.TrimmedStock.Keys).OrderBy(Short))
            {
                var ps = t.GetKinkedPointSet();
                if (ps == null)
                {
                    Problem($"{Short(t)}: no point set");
                    continue;
                }
                Line($"track {Short(t)}: {ps.span:F1} m; in: {Describe(t, true)}; out: {Describe(t, false)}");
                if (!t.gameObject.activeInHierarchy)
                    Problem($"{Short(t)} is inactive");
                CheckSymmetric(t, true);
                CheckSymmetric(t, false);
            }
        }

        private void CheckSymmetric(RailTrack t, bool first)
        {
            var b = first ? t.inBranch : t.outBranch;
            var j = first ? t.inJunction : t.outJunction;
            if (j != null)
            {
                if (!(j.inBranch != null && j.inBranch.track == t && j.inBranch.first == first) && !j.outBranches.Any(o => o.track == t && o.first == first))
                    Problem($"{Short(t)} {(first ? "in" : "out")}: junction does not list it");
                return;
            }
            if (b?.track == null)
                return;
            var back = b.first ? b.track.inBranch : b.track.outBranch;
            if (back?.track != t || back.first != first)
                Problem($"{Short(t)} {(first ? "in" : "out")}: {Short(b.track)} does not link back");
        }

        private void Switches()
        {
            foreach (var j in Builder.OurJunctions)
            {
                var name = j.transform.parent != null ? j.transform.parent.name : j.name;
                Line($"junction {name}: in {Short(j.inBranch?.track)}, out {string.Join(" / ", j.outBranches.Select(b => Short(b.track)))}, selected {j.selectedBranch}");
                if (j.inBranch?.track == null)
                    Problem($"junction {name} has no in branch");
                try
                {
                    var before = j.selectedBranch;
                    j.Switch(Junction.SwitchMode.NO_SOUND);
                    var after = j.selectedBranch;
                    j.Switch(Junction.SwitchMode.NO_SOUND, before);
                    if (after == before)
                        Problem($"junction {name} did not switch");
                }
                catch (Exception e)
                {
                    Problem($"throwing junction {name} threw: {e.GetBaseException().Message}");
                }
            }
        }

        /// <summary>The through leg and stub lie on the stock track they replaced (the save mapping relies on it).</summary>
        private void TieInGeometry()
        {
            foreach (var tie in Builder.TieIns)
            {
                double joint = tie.JointSpan, stubSpan = tie.Stub.GetKinkedPointSet().span, throughSpan = tie.Through.GetKinkedPointSet().span;
                double stubDev = 0, throughDev = 0;
                for (double s = 0; s <= stubSpan; s += 5)
                    stubDev = Math.Max(stubDev, (At(tie.Stub.GetKinkedPointSet(), s) - At(tie.Original, joint + s)).magnitude);
                for (double s = 0; s <= throughSpan; s += 2)
                    throughDev = Math.Max(throughDev, (At(tie.Through.GetKinkedPointSet(), s) - At(tie.Original, joint - s)).magnitude);
                var kept = Builder.TrimmedStock[tie.Stock];
                Line($"tie-in {Short(tie.Stock)}: original {tie.OriginalSpan:F2} m, kept {kept:F2} m, through leg {throughSpan:F2} m, joint at {joint:F2} m, stub {stubSpan:F2} m; " +
                     $"off the stock curve: stub {stubDev:F3} m, through leg {throughDev:F3} m; kept + leg - joint {kept + throughSpan - joint:F3} m");
                // The stub's first handle lies along the switch, and the through leg is the straight
                // stock asset, so on a curved stock track both stray a few centimetres from it.
                if (stubDev > 0.15)
                    Problem($"tie-in {Short(tie.Stock)}: the stub strays {stubDev:F3} m from the stock curve");
                if (throughDev > 0.15)
                    Problem($"tie-in {Short(tie.Stock)}: the through leg strays {throughDev:F3} m from the stock curve");
                if (Math.Abs(kept + throughSpan - joint) > 0.05)
                    Problem($"tie-in {Short(tie.Stock)}: kept part and through leg do not add up to the joint");
            }
        }

        /// <summary>A point set's position at a span (the point sets' own, unshifted frame).</summary>
        private static Vector3 At(EquiPointSet ps, double span)
        {
            var pts = ps.points;
            span = Math.Max(0, Math.Min(ps.span, span));
            int lo = 0, hi = pts.Length - 1;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (pts[mid].span <= span) lo = mid; else hi = mid;
            }
            var a = pts[lo];
            var b = pts[hi];
            var len = b.span - a.span;
            var w = len > 1e-9 ? (float)((span - a.span) / len) : 0f;
            return Vector3.Lerp((Vector3)a.position, (Vector3)b.position, w);
        }

        private static Vector3 TrackWorld(RailTrack t, double span) => At(t.GetKinkedPointSet(), span) + WorldMover.currentMove;

        // ------------------------------------------------------------ standing cars

        private static TrainCarLivery TestLivery =>
            DV.Globals.G.Types.Liveries.FirstOrDefault(l => l.id.IndexOf("Flatbed", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? DV.Globals.G.Types.Liveries.First(l => !l.id.StartsWith("Loco", StringComparison.Ordinal));

        private IEnumerator Cars()
        {
            var yards = Builder.OurTracks.Where(t => t.name.EndsWith(" yard 1", StringComparison.Ordinal)).OrderBy(t => t.name).ToList();
            if (yards.Count == 0)
                Line("car test skipped: no yard track");
            foreach (var track in yards)
                yield return CarsOn(track);
        }

        private IEnumerator CarsOn(RailTrack track)
        {
            var livery = TestLivery;
            PlayerManager.TeleportPlayer(TrackWorld(track, 100) + Vector3.up * 2f, Quaternion.identity, null, false);
            yield return new WaitForSeconds(10f);
            List<TrainCar> cars = null;
            try
            {
                var liveries = new List<TrainCarLivery> { livery, livery, livery };
                cars = SingletonBehaviour<CarSpawner>.Instance.SpawnCarTypesOnTrack(liveries, new List<bool> { false, false, false }, track, true, true, 60.0);
            }
            catch (Exception e)
            {
                Problem("spawning test cars threw: " + e.GetBaseException().Message);
            }
            if (cars == null || cars.Count == 0)
            {
                Problem($"could not spawn {livery.id} on {track.name}");
                yield break;
            }
            yield return new WaitForSeconds(8f);
            var derailed = cars.Count(c => c.derailed);
            Line($"car test: {cars.Count} {livery.id} on {track.name}, {derailed} derailed after 8 s");
            if (derailed > 0)
                Problem("test cars derailed on a yard track");
            // Saved, they are written derailed in place for the game, with their places for us,
            // and loading them puts each bogie back where it stood.
            foreach (var c in cars)
                Try("saving a yard car", () => SaveRoundTrip(c, Saved.Placed));
            SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(cars, true);
        }

        private static List<JObject> SavedEntries(IEnumerable<TrainCar> cars)
        {
            var data = CarsSaveManager.GetCarsSaveData();
            var all = (data?["carsData"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            return cars.Select(c => all.FirstOrDefault(o => o["carGuid"]?.Value<string>() == c.logicCar.carGuid)).ToList();
        }

        // ------------------------------------------------------------ test routes

        private sealed class Route
        {
            public bool Forward;   // the car starts toward the start track's end
            public readonly List<(Junction J, byte Branch)> Settings = new List<(Junction, byte)>();
            public readonly List<RailTrack> Path = new List<RailTrack>();
        }

        /// <summary>Breadth-first over (track, direction) to the target, through junctions in either direction.</summary>
        private static Route FindRoute(RailTrack from, RailTrack to)
        {
            var prev = new Dictionary<(RailTrack, bool), ((RailTrack, bool) From, Junction J, byte B)>();
            var queue = new Queue<(RailTrack, bool)>();
            foreach (var s in new[] { (from, true), (from, false) })
            {
                prev[s] = ((null, false), null, 0);
                queue.Enqueue(s);
            }
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (cur.Item1 == to && to != from)
                {
                    var route = new Route();
                    var at = cur;
                    while (true)
                    {
                        route.Path.Insert(0, at.Item1);
                        var p = prev[at];
                        if (p.J != null)
                            route.Settings.Insert(0, (p.J, p.B));
                        if (p.From.Item1 == null)
                        {
                            route.Forward = at.Item2;
                            break;
                        }
                        at = p.From;
                    }
                    return route;
                }
                foreach (var (next, j, b) in Next(cur.Item1, cur.Item2))
                    if (!prev.ContainsKey(next))
                    {
                        prev[next] = (cur, j, b);
                        queue.Enqueue(next);
                    }
            }
            return null;
        }

        /// <summary>Where a car leaving a track at one end goes next: entering at a track's start means moving forward.</summary>
        private static IEnumerable<((RailTrack, bool) Next, Junction J, byte B)> Next(RailTrack t, bool forward)
        {
            var first = !forward;   // leaving by the start when moving backward
            var j = forward ? t.outJunction : t.inJunction;
            var br = forward ? t.outBranch : t.inBranch;
            if (j != null)
            {
                if (j.inBranch != null && j.inBranch.track == t && j.inBranch.first == first)
                {
                    for (var k = 0; k < j.outBranches.Count; k++)
                    {
                        var ob = j.outBranches[k];
                        if (ob?.track != null)
                            yield return ((ob.track, ob.first), j, (byte)k);
                    }
                }
                else if (j.inBranch?.track != null)
                {
                    for (var k = 0; k < j.outBranches.Count; k++)
                        if (j.outBranches[k]?.track == t && j.outBranches[k].first == first)
                            yield return ((j.inBranch.track, j.inBranch.first), j, (byte)k);
                }
                yield break;
            }
            if (br?.track != null)
                yield return ((br.track, br.first), null, 0);
        }

        /// <summary>
        /// Drives a car toward a speed along its own forward, as a locomotive's traction does
        /// (DrivingForce): a force shared among the bogies, which the track then guides.
        /// Setting the bodies' velocities instead fights the bogies' own travel along the track.
        /// </summary>
        private static void Drive(TrainCar car, float targetSpeed)
        {
            if (car.isEligibleForSleep)
                car.ForceOptimizationState(sleep: false);
            var mass = car.massController != null && car.massController.TotalMass > 0 ? car.massController.TotalMass : car.rb.mass;
            var accel = Mathf.Clamp((targetSpeed - car.GetForwardSpeed()) * 1.5f, -2f, 2f);
            var perBogie = mass * accel / car.Bogies.Length;
            foreach (var b in car.Bogies)
                if (b.enabled)
                    b.rb.AddForce(b.transform.forward * perBogie);
        }

        private static RailTrack Find(string name) => Builder.OurTracks.FirstOrDefault(t => t.name == name);

        private IEnumerator Push(RunSpec run)
        {
            var from = Find(run.From);
            var to = Find(run.To);
            if (from == null || to == null)
            {
                Problem($"run {run.Name}: track {(from == null ? run.From : run.To)} not found");
                yield break;
            }
            var route = FindRoute(from, to);
            if (route == null)
            {
                Problem($"run {run.Name}: no route from {run.From} to {run.To}");
                yield break;
            }
            foreach (var (j, b) in route.Settings)
                j.Switch(Junction.SwitchMode.NO_SOUND, b);
            PlayerManager.TeleportPlayer(TrackWorld(from, run.FromSpan) + Vector3.up * 3f, Quaternion.identity, null, false);
            yield return new WaitForSeconds(8f);
            List<TrainCar> cars = null;
            try
            {
                cars = SingletonBehaviour<CarSpawner>.Instance.SpawnCarTypesOnTrackStrict(new List<TrainCarLivery> { TestLivery }, from, true, false, run.FromSpan);
            }
            catch (Exception e)
            {
                Problem($"run {run.Name}: spawning threw: {e.GetBaseException().Message}");
            }
            if (cars == null || cars.Count == 0)
            {
                Problem($"run {run.Name}: could not spawn a car on {run.From} at {run.FromSpan} m");
                yield break;
            }
            var car = cars[0];
            yield return new WaitForSeconds(3f);
            if (Builder.TieIns.Any(t => t.Stub == from || t.Through == from))
                Try($"run {run.Name}: save round trip", () => SaveRoundTrip(car, Saved.OnStock));
            var straddleSaved = false;

            // Push the car's way along the route: the direction it must go at the start.
            var ps = from.GetKinkedPointSet();
            var here = At(ps, run.FromSpan + 6);
            var ahead = At(ps, run.FromSpan + 6 + (route.Forward ? 2 : -2));
            var sign = Vector3.Dot(car.transform.forward, ahead - here) >= 0 ? 1f : -1f;
            var lead = sign > 0 ? car.FrontBogie : car.RearBogie;
            var length = route.Path.Sum(t => t.GetKinkedPointSet().span);
            var timeout = (float)(length / PushSpeed) * 1.5f + 30f;
            var visited = new List<RailTrack>();
            double? entered = null;
            var t0 = Time.time;
            var lastTeleport = Time.time;
            var reached = false;
            var trace = new List<string>();
            var lastTrace = Time.time;
            while (Time.time - t0 < timeout)
            {
                yield return new WaitForFixedUpdate();
                if (car == null || car.derailed || lead.HasDerailed)
                    break;
                Drive(car, sign * PushSpeed);
                if (Time.time - lastTrace > 15f)
                {
                    trace.Add($"{Time.time - t0:F0} s: {Short(lead.track)} at {lead.traveller.Span:F0} m, {car.GetForwardSpeed() * sign:F1} m/s");
                    lastTrace = Time.time;
                }
                if (lead.track != null && (visited.Count == 0 || visited[visited.Count - 1] != lead.track))
                    visited.Add(lead.track);
                // Once, with the car across a joint onto our track: saved and loaded bogie by bogie.
                var trailing = lead == car.FrontBogie ? car.RearBogie : car.FrontBogie;
                if (!straddleSaved && trailing.track != null && lead.track != null && trailing.track != lead.track &&
                    (Builder.IsOurs(lead.track) || Builder.IsOurs(trailing.track)))
                {
                    straddleSaved = true;
                    Try($"run {run.Name}: save round trip across a joint", () => SaveRoundTrip(car, Saved.Either));
                }
                if (lead.track == to)
                {
                    entered = entered ?? lead.traveller.Span;
                    if (Math.Abs(lead.traveller.Span - entered.Value) >= run.After)
                    {
                        reached = true;
                        break;
                    }
                }
                if (Time.time - lastTeleport > 10f)
                {
                    PlayerManager.TeleportPlayer(car.transform.position + car.transform.right * 8f + Vector3.up * 3f, Quaternion.identity, null, false);
                    lastTeleport = Time.time;
                }
            }
            var elapsed = Time.time - t0;
            for (var i = 0; i < 150 && car != null && !car.derailed; i++)
            {
                Drive(car, 0f);
                yield return new WaitForFixedUpdate();
            }
            var derailed = car == null || car.derailed;
            var path = string.Join(" > ", visited.Select(Short));
            Line($"run {run.Name}: {(reached ? "reached" : "did NOT reach")} {run.To} +{run.After} m after {elapsed:F0} s at {PushSpeed} m/s; derailed: {derailed}");
            Line($"  path: {path}");
            if (!reached || derailed)
            {
                Problem($"run {run.Name} failed");
                foreach (var tr in trace)
                    Line("    " + tr);
            }
            var expected = route.Path.Where(t => t != from).ToList();
            var got = visited.Where(t => t != from).ToList();
            if (reached && !expected.SequenceEqual(got))
                Problem($"run {run.Name}: went {path}, expected {string.Join(" > ", expected.Select(Short))}");
            if (car != null)
                SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(new List<TrainCar> { car }, true);
        }

        private enum Saved { OnStock, Placed, Either }

        /// <summary>
        /// Saves a car as the game would and loads its entry back as the mod would (before the
        /// game creates the car), and checks each bogie comes back where it stood. A car on a
        /// tie-in's through leg or stub is expected on the stock track; elsewhere on our track,
        /// derailed for the game with its places in our record. The game's own entry must never
        /// point at one of our tracks: a game without the mod lacks them.
        /// </summary>
        private void SaveRoundTrip(TrainCar car, Saved expect)
        {
            var tracks = SingletonBehaviour<RailTrackRegistryBase>.Instance.OrderedRailtracks;
            var entry = SavedEntries(new[] { car })[0];
            if (entry == null)
            {
                Problem("save round trip: the car is not in the save");
                return;
            }
            var bogies = new[] { car.RearBogie, car.FrontBogie };
            var keys = CarSaves.BogieKeys;
            var onStock = 0;
            var derailed = 0;
            var saved = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                var index = entry[keys[i].Index]?.Value<int?>();
                if (entry[keys[i].Derailed]?.Value<bool?>() == true)
                {
                    derailed++;
                    saved.Add("derailed in place");
                    continue;
                }
                if (index == null || index < 0 || index >= tracks.Length || Builder.IsOurs(tracks[index.Value]))
                {
                    Problem($"save round trip: bogie {i + 1} on {Short(bogies[i].track)} was saved on track index {index}, which a game without the mod does not have");
                    return;
                }
                onStock++;
                saved.Add($"{Short(tracks[index.Value])} at {entry[keys[i].Span]?.Value<double>():F2} m");
            }
            var record = CarSaves.Record;
            var hasPlaces = record?["placements"]?[car.logicCar.carGuid] is JObject;
            var kind = onStock == 2 ? Saved.OnStock : derailed == 2 && hasPlaces ? Saved.Placed : (Saved?)null;
            if (kind == null || (expect != Saved.Either && kind != expect))
            {
                Problem($"save round trip: saved as {string.Join(", ", saved)}{(hasPlaces ? " with places" : "")}; expected {expect}");
                return;
            }
            var copy = (JObject)entry.DeepClone();
            var outcome = CarSaves.PrepareForLoad(copy, tracks, record, out _);
            var back = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                if (copy[keys[i].Derailed]?.Value<bool?>() == true || copy[keys[i].Index] == null)
                {
                    Problem($"save round trip: bogie {i + 1} would load derailed ({outcome})");
                    continue;
                }
                var track = tracks[copy[keys[i].Index].Value<int>()];
                var span = copy[keys[i].Span].Value<double>();
                var gap = (At(track.GetKinkedPointSet(), span) - At(bogies[i].track.GetKinkedPointSet(), bogies[i].traveller.Span)).magnitude;
                back.Add($"{Short(track)} at {span:F2} m ({gap:F3} m off)");
                if (gap > 0.05f)
                    Problem($"save round trip: bogie {i + 1} would load {gap:F3} m from where it stood");
            }
            Line($"  save round trip ({kind}): saved {string.Join(", ", saved)}; loads {string.Join(", ", back)}");
        }

        /// <summary>A switch of ours set off its default: kept out of the game's list, in our record by name, and set again on load.</summary>
        private void JunctionRoundTrip()
        {
            var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
            var j = Builder.OurJunctions.OrderBy(Builder.IdOf).First();
            var before = j.selectedBranch;
            var other = (byte)((j.defaultSelectedBranch + 1) % j.outBranches.Count);
            try
            {
                j.Switch(Junction.SwitchMode.NO_SOUND, other);
                var data = JunctionsSaveManager.GetJunctionsSaveData();
                var indices = data["indices"]?.ToObject<int[]>() ?? new int[0];
                var leaked = indices.Count(i => i >= 0 && i < registry.OrderedJunctions.Length && Builder.IsOurs(registry.OrderedJunctions[i]));
                var record = CarSaves.Record;
                var recorded = record?["junctions"]?[Builder.IdOf(j)]?.Value<int?>();
                j.Switch(Junction.SwitchMode.NO_SOUND, (byte)Math.Max(0, (int)j.defaultSelectedBranch));
                CarSaves.RestoreJunctions(record);
                var restored = j.selectedBranch;
                Line($"switch save round trip: {Builder.IdOf(j)} set to {other}; in the game's list {leaked} of ours; in our record {recorded?.ToString() ?? "none"}; set again on load to {restored}");
                if (leaked > 0)
                    Problem("our switches are in the game's own switch states");
                if (recorded != other || restored != other)
                    Problem($"switch {Builder.IdOf(j)} did not come back as saved");
            }
            finally
            {
                j.Switch(Junction.SwitchMode.NO_SOUND, before);
            }
        }

        // ------------------------------------------------------------ a layout failing part way

        /// <summary>
        /// One layout's Apply was made to fail after it had trimmed its stock track: the stock
        /// track and its connections are as in the stock game, nothing of that layout is left,
        /// and the other layout is built.
        /// </summary>
        private void FaultCheck()
        {
            var problemsHere = 0;
            using (var w = new StreamWriter(Path.Combine(Folder, "inspect-fault.log"), false))
            {
                void Say(string text) { Main.Log("fault: " + text); w.WriteLine(text); w.Flush(); }
                void Fail(string text) { problemsHere++; Say("PROBLEM " + text); }
                var site = Builder.FaultSite;
                var layout = Main.Layouts.FirstOrDefault(l => l.Site == site);
                Say($"fault check {DateTime.Now:yyyy-MM-dd HH:mm:ss}: Apply of {site} failed on purpose ({Builder.FaultPoint})");
                foreach (var note in Builder.Report.Where(n => n.StartsWith("rollback", StringComparison.Ordinal)))
                    Say("note: " + note);
                Say($"build failure: {Builder.Failure?.Split('\n')[0] ?? "none"}");
                if (layout == null) { Fail($"no layout {site}"); goto done; }
                if (Builder.Failure == null || !Builder.Failure.Contains(site))
                    Fail("the failure was not reported for that layout");
                if (Builder.Built.Contains(layout))
                    Fail("the failed layout counts as built");
                var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
                Say($"tracks hash {registry.TracksHash}, junctions hash {registry.JunctionsHash}; ordered tracks {registry.OrderedRailtracks.Length}, junctions {registry.OrderedJunctions.Length}");
                // Nothing of the layout under the railway, in the registry, or anywhere in the scene.
                var names = new HashSet<string>(layout.Tracks.Select(t => t.Name).Concat(layout.StockEdits.Select(e => e.Stub)));
                var ids = new HashSet<string>(layout.Switches.Select(sw => sw.Id));
                var leftovers = FindObjectsOfType<RailTrack>().Where(t => names.Contains(t.name) || ids.Any(id => t.transform.parent != null && t.transform.parent.name.Contains($"({id})"))).ToList();
                var inRegistry = registry.OrderedRailtracks.Count(t => t == null || names.Contains(t.name));
                Say($"left of {site}: {leftovers.Count} track(s) in the scene, {inRegistry} in the registry");
                if (leftovers.Count > 0 || inRegistry > 0)
                    Fail($"{site} left {leftovers.Count} track(s) in the scene and {inRegistry} in the registry");
                // The stock track it trimmed is whole again, with its end connection back.
                foreach (var edit in layout.StockEdits)
                {
                    var at = Frames.World(Root, Vec.Of(edit.TrimEndTo));
                    var stock = registry.OrderedRailtracks.Where(t => t != null && t.name == edit.Track && !Builder.IsOurs(t))
                        .OrderBy(t => RailTrack.GetClosestPoint(t, at).distanceToPoint).FirstOrDefault();
                    if (stock == null) { Fail($"{edit.Track} not found"); continue; }
                    var span = stock.GetKinkedPointSet().span;
                    var endTo = stock.outJunction != null ? $"junction {stock.outJunction.transform.parent?.name ?? stock.outJunction.name}"
                        : stock.outBranch?.track != null ? stock.outBranch.track.name : "nothing";
                    var back = stock.outJunction != null
                        ? (stock.outJunction.inBranch?.track == stock || stock.outJunction.outBranches.Any(b => b.track == stock))
                        : stock.outBranch?.track != null && (stock.outBranch.track.inBranch?.track == stock || stock.outBranch.track.outBranch?.track == stock);
                    Say($"{edit.Track}: {span:F2} m; its end meets {endTo}, which {(back ? "links back" : "does NOT link back")}");
                    if (Builder.IsOurs(stock.outBranch?.track) || Builder.IsOurs(stock.outJunction) || endTo.Contains("[#]") || !back)
                        Fail($"{edit.Track}'s end connection was not restored");
                    // Trimmed, it would end at the switch's through end; restored, it ends where it always did.
                    var curve = stock.curve;
                    var endGap = (curve[curve.pointCount - 1].position - at).magnitude;
                    Say($"{edit.Track}: its end is {endGap:F1} m from where the trim would end it");
                    if (endGap < 5f)
                        Fail($"{edit.Track} is still shortened ({span:F1} m)");
                }
                var others = Main.Layouts.Where(l => l != layout).ToList();
                Say($"other layouts built: {string.Join(", ", others.Where(Builder.Built.Contains).Select(l => l.Site))}; our tracks {Builder.OurTracks.Count}, junctions {Builder.OurJunctions.Count}, tie-ins {Builder.TieIns.Count}");
                if (others.Any(l => !Builder.Built.Contains(l)))
                    Fail("another layout was not built");
                done:
                LogReportTo(Say, Fail);
                Say($"FAULT DONE: {problemsHere} problem(s)");
            }
        }

        // ------------------------------------------------------------ a real save, then a fresh game

        private const string SaveTestFile = "inspect-savetest.json";

        private static string PlaceOf(Bogie b, RailTrack[] tracks)
        {
            if (b == null || b.HasDerailed || b.track == null || b.traveller == null) return "derailed";
            return Builder.IsOurs(b.track) ? Builder.IdOf(b.track) : "#stock/" + Array.IndexOf(tracks, b.track);
        }

        private static JObject Describe(TrainCar c, RailTrack[] tracks) => new JObject
        {
            ["guid"] = c.logicCar.carGuid,
            ["id"] = c.ID,
            ["bogies"] = new JArray(new[] { c.RearBogie, c.FrontBogie }.Select(b => new JObject
            {
                ["track"] = PlaceOf(b, tracks),
                ["span"] = b != null && !b.HasDerailed && b.traveller != null ? b.traveller.Span : 0.0,
            })),
            ["front"] = c.frontCoupler.coupledTo?.train?.logicCar.carGuid,
            ["rear"] = c.rearCoupler.coupledTo?.train?.logicCar.carGuid,
            ["handbrake"] = c.brakeSystem != null ? c.brakeSystem.handbrakePosition : -1f,
        };

        /// <summary>
        /// Leaves a coupled cut of cars on a yard track (one handbrake half on), a car across a
        /// switch joint, and one of our switches off its default, saves the game as an autosave,
        /// and writes what it left (inspect-savetest.json) for the next launch to check.
        /// </summary>
        private IEnumerator SaveTest()
        {
            var tracks = SingletonBehaviour<RailTrackRegistryBase>.Instance.OrderedRailtracks;
            var yard = Find("[#] MFW yard 2");
            var tail = Find("[#] MFW tail");
            if (yard == null || tail == null)
            {
                Problem("save test: MF West's yard 2 or tail not found");
                yield break;
            }
            // The inspection's own session carries earlier runs' test cars: clear them from our track,
            // with the player standing well clear of them.
            var old = SingletonBehaviour<CarSpawner>.Instance.AllCars.Where(c => c != null && OnOrBeside(c)).ToList();
            if (old.Count > 0)
            {
                PlayerManager.TeleportPlayer(TrackWorld(yard, 180) + Vector3.up * 3f + Vector3.Cross(Vector3.up, (TrackWorld(yard, 181) - TrackWorld(yard, 179)).normalized) * 40f,
                    Quaternion.identity, null, false);
                yield return new WaitForSeconds(5f);
                SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(old, true);
                Line($"save test: cleared {old.Count} earlier test car(s) from the interchanges");
                yield return new WaitForSeconds(2f);
            }
            PlayerManager.TeleportPlayer(TrackWorld(yard, 180) + Vector3.up * 3f, Quaternion.identity, null, false);
            yield return new WaitForSeconds(8f);
            var livery = TestLivery;
            var cut = SingletonBehaviour<CarSpawner>.Instance.SpawnCarTypesOnTrack(new List<TrainCarLivery> { livery, livery, livery },
                new List<bool> { false, false, false }, yard, true, true, 150.0);
            if (cut == null || cut.Count != 3)
            {
                Problem("save test: could not spawn the cut of cars");
                yield break;
            }
            yield return new WaitForSeconds(5f);
            cut[1].brakeSystem.SetHandbrakePosition(0.5f);

            // A car across the joint between the tail and the south ladder's first switch.
            PlayerManager.TeleportPlayer(TrackWorld(tail, 10) + Vector3.up * 3f, Quaternion.identity, null, false);
            yield return new WaitForSeconds(6f);
            var single = SingletonBehaviour<CarSpawner>.Instance.SpawnCarTypesOnTrackStrict(new List<TrainCarLivery> { livery }, tail, true, false, 4.0);
            if (single == null || single.Count != 1)
            {
                Problem("save test: could not spawn the car for the joint");
                yield break;
            }
            var car = single[0];
            yield return new WaitForSeconds(3f);
            var ps = tail.GetKinkedPointSet();
            var towardStart = At(ps, 6) - At(ps, 9);
            var sign = Vector3.Dot(car.transform.forward, towardStart) >= 0 ? 1f : -1f;
            for (var t = 0f; t < 20f && car.FrontBogie.track == car.RearBogie.track; t += Time.fixedDeltaTime)
            {
                Drive(car, sign * 1.5f);
                yield return new WaitForFixedUpdate();
            }
            for (var i = 0; i < 150; i++)
            {
                Drive(car, 0f);
                yield return new WaitForFixedUpdate();
            }
            car.brakeSystem.SetHandbrakePosition(1f);
            yield return new WaitForSeconds(3f);
            if (car.FrontBogie.track == car.RearBogie.track)
                Problem("save test: the car did not come to stand across the joint");

            var junction = Builder.JunctionById("MF West/MFW-N-2");
            var branch = (byte)((junction.defaultSelectedBranch + 1) % junction.outBranches.Count);
            junction.Switch(Junction.SwitchMode.NO_SOUND, branch);

            var saved = SingletonBehaviour<SaveGameManager>.Instance.Save(SaveType.Auto);
            var expected = new JObject
            {
                ["cars"] = new JArray(cut.Concat(single).Select(c => Describe(c, tracks))),
                ["junction"] = new JObject { ["id"] = Builder.IdOf(junction), ["branch"] = (int)branch },
            };
            File.WriteAllText(Path.Combine(Folder, SaveTestFile), expected.ToString());
            Line($"save test: {(saved != null ? "saved" : "NOT saved")} with {cut.Count} coupled cars on {cut[0].FrontBogie.track.name}, " +
                 $"one across {Short(car.RearBogie.track)} / {Short(car.FrontBogie.track)}, and {Builder.IdOf(junction)} set to {branch}");
            foreach (var c in expected["cars"])
                Line("  left: " + c.ToString(Newtonsoft.Json.Formatting.None));
            if (saved == null)
                Problem("save test: the game did not save");
        }

        /// <summary>
        /// The game without this mod has loaded the save test's save. The cars that stood on our
        /// track load derailed where they stood; the one not coupled is re-railed onto the nearest stock track and
        /// moved, as a player might, and the game saved again. Our record of that car is now stale,
        /// and the next launch with the mod must leave it where this save has it.
        /// </summary>
        private IEnumerator Vanilla()
        {
            var problemsHere = 0;
            using (var w = new StreamWriter(Path.Combine(Folder, "inspect-vanilla.log"), false))
            {
                void Say(string text) { Main.Log("vanilla: " + text); w.WriteLine(text); w.Flush(); }
                void Fail(string text) { problemsHere++; Say("PROBLEM " + text); }
                Say($"without the mod {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                yield return new WaitForSeconds(12f);
                var file = Path.Combine(Folder, SaveTestFile);
                var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
                var tracks = registry.OrderedRailtracks;
                Say($"tracks hash {registry.TracksHash}; ordered tracks {tracks.Length}; our key in the save: {(CarSaves.Record != null ? "kept" : "gone")}");
                if (!File.Exists(file))
                {
                    Fail("no save test record");
                }
                else
                {
                    var expected = JObject.Parse(File.ReadAllText(file));
                    var cars = SingletonBehaviour<CarSpawner>.Instance.AllCars;
                    TrainCar mover = null;
                    JObject moverEntry = null;
                    foreach (var e in expected["cars"].OfType<JObject>())
                    {
                        var car = cars.FirstOrDefault(c => c != null && c.logicCar.carGuid == e["guid"].Value<string>());
                        if (car == null) { Fail($"car {e["id"]} is gone"); continue; }
                        Say($"car {e["id"]}: {(car.derailed ? "derailed where it stood" : "ON TRACK")} at {(car.transform.position - WorldMover.currentMove)}");
                        if (!car.derailed)
                            Fail($"car {e["id"]} loaded on track without the mod");
                        if (e["front"].Type == JTokenType.Null && e["rear"].Type == JTokenType.Null)
                        {
                            mover = car;
                            moverEntry = e;
                        }
                    }
                    if (mover == null)
                    {
                        Fail("no uncoupled car to move");
                    }
                    else
                    {
                        // As a player would: onto the nearest track that is long enough, a short move away.
                        var from = mover.transform.position;
                        var road = tracks.Where(t => t != null && t.GetKinkedPointSet() != null && t.GetKinkedPointSet().span > 60)
                            .OrderBy(t => RailTrack.GetClosestPoint(t, from).distanceToPoint).First();
                        var ps = road.GetKinkedPointSet();
                        var nearest = RailTrack.GetClosestPoint(road, from).closestPoint;
                        var span = Math.Max(20.0, Math.Min(ps.span - 20.0, nearest?.span ?? ps.span / 2));
                        var at = TrackWorld(road, span);
                        var forward = (At(ps, span + 1) - At(ps, span - 1)).normalized;
                        PlayerManager.TeleportPlayer(at + Vector3.up * 3f + Vector3.Cross(Vector3.up, forward) * 25f, Quaternion.identity, null, false);
                        yield return new WaitForSeconds(6f);
                        mover.Rerail(road, at, forward);
                        for (var t = 0f; t < 20f && (mover.IsTeleporting || mover.derailed || mover.RearBogie.track == null); t += 0.5f)
                            yield return new WaitForSeconds(0.5f);
                        yield return new WaitForSeconds(3f);
                        Say($"car {moverEntry["id"]} re-railed onto {road.name} at {span:F0} m ({Math.Sqrt(RailTrack.GetClosestPoint(road, from).distanceToPoint):F0} m from where it stood): " +
                            $"{(mover.derailed ? "STILL DERAILED" : "on track")}, bogies on {Short(mover.RearBogie.track)} / {Short(mover.FrontBogie.track)}");
                        if (mover.derailed || mover.RearBogie.track != road || mover.FrontBogie.track != road)
                            Fail($"the car could not be re-railed onto {road.name}");
                        var saved = SingletonBehaviour<SaveGameManager>.Instance.Save(SaveType.Auto);
                        if (saved == null)
                            Fail("the game did not save");
                        // Next launch: this car where this save has it; the others back on our track.
                        var moved = Describe(mover, tracks);
                        var list = (JArray)expected["cars"];
                        list[list.IndexOf(moverEntry)] = moved;
                        expected["moved"] = moved["guid"];
                        File.WriteAllText(file, expected.ToString());
                        Say($"saved without the mod; expect {moved.ToString(Newtonsoft.Json.Formatting.None)}");
                    }
                }
                LogReportTo(Say, Fail);
                Say($"VANILLA DONE: {problemsHere} problem(s)");
            }
        }

        /// <summary>A car on our track, or derailed within 15 m of it.</summary>
        private static bool OnOrBeside(TrainCar c)
        {
            if (c.Bogies.Any(b => b != null && !b.HasDerailed && Builder.IsOurs(b.track)))
                return true;
            if (!c.derailed)
                return false;
            var p = c.transform.position;
            return Builder.OurTracks.Any(t => RailTrack.GetClosestPoint(t, p).distanceToPoint < 15f * 15f);
        }

        /// <summary>A fresh game has loaded the save test's save: the cars and the switch are as they were left.</summary>
        private IEnumerator Reload()
        {
            var problemsHere = 0;
            using (var w = new StreamWriter(Path.Combine(Folder, "inspect-reload.log"), false))
            {
                void Say(string text)
                {
                    Main.Log("reload: " + text);
                    w.WriteLine(text);
                    w.Flush();
                }
                void Fail(string text)
                {
                    problemsHere++;
                    Say("PROBLEM " + text);
                }
                Say($"reload check {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                yield return new WaitForSeconds(12f);
                var file = Path.Combine(Folder, SaveTestFile);
                if (!File.Exists(file))
                {
                    Fail("no save test record");
                }
                else
                {
                    var expected = JObject.Parse(File.ReadAllText(file));
                    var tracks = SingletonBehaviour<RailTrackRegistryBase>.Instance.OrderedRailtracks;
                    Say($"build failure: {Builder.Failure ?? "none"}; on load: {CarSaves.PlacedOnLoad} placed, {CarSaves.MappedOnLoad} bogie(s) mapped, " +
                        $"{CarSaves.LeftDerailedOnLoad} left derailed, {CarSaves.StaleOnLoad} stale record(s) ignored, {CarSaves.JunctionsRestored} switch(es) set");
                    var cars = SingletonBehaviour<CarSpawner>.Instance.AllCars;
                    foreach (var e in expected["cars"].OfType<JObject>())
                    {
                        var guid = e["guid"].Value<string>();
                        var car = cars.FirstOrDefault(c => c != null && c.logicCar.carGuid == guid);
                        if (car == null)
                        {
                            Fail($"car {e["id"]} is gone");
                            continue;
                        }
                        var now = Describe(car, tracks);
                        Say($"car {e["id"]}: was {e.ToString(Newtonsoft.Json.Formatting.None)}");
                        Say($"         now {now.ToString(Newtonsoft.Json.Formatting.None)}");
                        if (car.derailed)
                            Fail($"car {e["id"]} loaded derailed");
                        for (var i = 0; i < 2; i++)
                        {
                            var was = e["bogies"][i];
                            var nowB = now["bogies"][i];
                            if (was["track"].Value<string>() != nowB["track"].Value<string>() ||
                                Math.Abs(was["span"].Value<double>() - nowB["span"].Value<double>()) > 0.5)
                                Fail($"car {e["id"]} bogie {i + 1}: was {was["track"]} at {was["span"]:F2} m, now {nowB["track"]} at {nowB["span"]:F2} m");
                        }
                        foreach (var end in new[] { "front", "rear" })
                            if (e[end]?.Value<string>() != now[end]?.Value<string>())
                                Fail($"car {e["id"]} {end} coupler: was coupled to {e[end]}, now {now[end]}");
                        if (Math.Abs(e["handbrake"].Value<float>() - now["handbrake"].Value<float>()) > 0.02f)
                            Fail($"car {e["id"]} handbrake: was {e["handbrake"]}, now {now["handbrake"]}");
                    }
                    if (expected["moved"] != null && CarSaves.StaleOnLoad < 1)
                        Fail("the car moved without the mod was not recognised as a stale record");
                    var jid = expected["junction"]["id"].Value<string>();
                    var j = Builder.JunctionById(jid);
                    var wantBranch = expected["junction"]["branch"].Value<int>();
                    Say($"switch {jid}: was {wantBranch}, now {(j == null ? "missing" : j.selectedBranch.ToString())}");
                    if (j == null || j.selectedBranch != wantBranch)
                        Fail($"switch {jid} did not come back as saved");
                }
                LogReportTo(Say, Fail);
                Say($"RELOAD DONE: {problemsHere} problem(s)");
            }
        }

        // ------------------------------------------------------------ views

        /// <summary>
        /// Late-morning light for the views: the game's clock runs on through a long inspection,
        /// so the time is set before each view, as photo mode does
        /// (WeatherDriver.manager.todSky.Cycle.DateTime, then RefreshTimeOfDay).
        /// </summary>
        private void SetDaylight()
        {
            try
            {
                var type = AccessTools.TypeByName("DV.WeatherSystem.WeatherDriver");
                var driver = type != null ? FindObjectOfType(type) : null;
                var manager = Member(driver, "manager");
                var cycle = Member(Member(manager, "todSky"), "Cycle");
                if (!(Member(cycle, "DateTime") is DateTime now))
                {
                    if (!daylightNoted) Line("views: the time of day could not be set");
                    daylightNoted = true;
                    return;
                }
                var tc = Traverse.Create(cycle);
                var member = tc.Field("DateTime").FieldExists() ? tc.Field("DateTime") : tc.Property("DateTime");
                member.SetValue(now.Date.AddHours(11));
                AccessTools.Method(manager.GetType(), "RefreshTimeOfDay")?.Invoke(manager, null);
                if (!daylightNoted)
                    Line($"views: time of day set from {now:HH:mm} to {Member(cycle, "DateTime"):HH:mm}");
                daylightNoted = true;
            }
            catch (Exception e)
            {
                if (!daylightNoted) Line($"views: setting the time of day threw: {e.GetBaseException().Message}");
                daylightNoted = true;
            }
        }

        private bool daylightNoted;

        private static object Member(object o, string name)
        {
            if (o == null) return null;
            var t = Traverse.Create(o);
            var f = t.Field(name);
            if (f.FieldExists()) return f.GetValue();
            var p = t.Property(name);
            return p.PropertyExists() ? p.GetValue() : null;
        }

        private IEnumerator Shot(ViewSpec view)
        {
            var root = Root;
            SetDaylight();
            var eye = Frames.World(root, Vec.Of(view.Eye));
            // The player stands under the camera so terrain, scenery and rail meshes stream in there.
            var groundAt = eye;
            if (Physics.Raycast(eye + Vector3.up * 500f, Vector3.down, out var hit, 2000f, LayerMask.GetMask("Terrain")))
                groundAt = hit.point;
            PlayerManager.TeleportPlayer(groundAt + Vector3.up * 1.5f, Quaternion.identity, null, false);
            yield return new WaitForSeconds(3f);
            // Wait for the terrain tiles around the player, then for vegetation and meshes.
            var grid = SingletonBehaviour<TerrainGrid>.Instance;
            var waited = 0f;
            while (grid != null && grid.IsLoadingInProgress() && waited < 60f)
            {
                yield return new WaitForSeconds(1f);
                waited += 1f;
            }
            if (waited >= 60f)
                Line($"view {view.Name}: terrain still loading after 60 s");
            yield return new WaitForSeconds(view.Wait > 0 ? view.Wait : 8f);
            yield return Capture(view.Name, () => (Frames.World(root, Vec.Of(view.Eye)), Frames.World(root, Vec.Of(view.Target))), view.Fov);
        }

        /// <summary>
        /// The player's own camera, with the game's sky, fog and post effects, is held at a view
        /// just before it renders, then given back to the player. The view's world positions are
        /// read as the capture starts, after the world may have moved its origin.
        /// </summary>
        private IEnumerator Capture(string name, Func<(Vector3 Eye, Vector3 Target)> at, float fovDeg)
        {
            SetDaylight();
            var (eye, target) = at();
            var cam = PlayerManager.PlayerCamera;
            if (cam == null)
            {
                Problem("no player camera for the views");
                yield break;
            }
            var t = cam.transform;
            var parent = t.parent;
            var lp = t.localPosition;
            var lr = t.localRotation;
            var fov = cam.fieldOfView;
            var rot = Quaternion.LookRotation(target - eye, Vector3.up);
            var wantFov = fovDeg > 0 ? fovDeg : 60f;
            var near = cam.nearClipPlane;
            // The game sets the camera's field of view again each frame; hold ours as it renders.
            Camera.CameraCallback hold = c =>
            {
                if (c == cam)
                {
                    c.transform.SetPositionAndRotation(eye, rot);
                    c.fieldOfView = wantFov;
                }
            };
            t.SetParent(null, true);
            cam.fieldOfView = fovDeg > 0 ? fovDeg : 60f;
            Camera.onPreCull += hold;
            // The camera itself is moved every frame too, not only as it renders: the game shows
            // each terrain tile only while it lies in Camera.main's view, judged in LateUpdate
            // (MicroSplatVisibilityHack), so tiles out of the player's own view would be missing.
            for (var hold0 = Time.unscaledTime; Time.unscaledTime - hold0 < 3f;)
            {
                t.SetPositionAndRotation(eye, rot);
                yield return null;
            }
            if (name.Contains("portal"))
                Try("hole report", () => HoleReport(cam));
            var file = Path.Combine(Folder, $"inspect-{name}.png");
            ScreenCapture.CaptureScreenshot(file);
            for (var hold0 = Time.unscaledTime; Time.unscaledTime - hold0 < 1.5f;)
            {
                t.SetPositionAndRotation(eye, rot);
                yield return null;
            }
            Camera.onPreCull -= hold;
            t.SetParent(parent, false);
            t.localPosition = lp;
            t.localRotation = lr;
            cam.fieldOfView = fov;
            cam.nearClipPlane = near;
            Line($"view {name}: {Path.GetFileName(file)}");
        }

        /// <summary>
        /// Our portals' terrain holes as the game's hole manager sees them: registered, on which
        /// tile, facing the camera; the hole it opened; and the hole set on that tile's material.
        /// </summary>
        private void HoleReport(Camera cam)
        {
            var manager = SingletonBehaviour<TerrainHoleManager>.Instance;
            var chosen = Traverse.Create(manager).Field("prevClosestHole").GetValue<TerrainHole>();
            var visible = Traverse.Create(manager).Field("potentiallyVisibleHoles").GetValue<HashSet<TerrainHole>>();
            Line($"  holes: manager camera {(manager.playerCamera == cam ? "is" : "is NOT")} this camera; {visible?.Count ?? -1} potentially visible; " +
                 $"open: {(chosen == null ? "none" : chosen.transform.parent?.name ?? chosen.name)}" +
                 (chosen == null ? "" : $" {(chosen.transform.position - cam.transform.position).magnitude:F0} m away"));
            foreach (var hole in FindObjectsOfType<TerrainHole>().Where(h => h.transform.parent != null && h.transform.parent.name.StartsWith("tunnel_collision_ignore (", StringComparison.Ordinal)))
            {
                var d = (hole.transform.position - cam.transform.position).magnitude;
                if (d > 400f)
                    continue;
                var facing = Vector3.Dot(cam.transform.forward, hole.transform.forward);
                string vector = "no MicroSplat";
                var ms = hole.Terrain != null ? hole.Terrain.GetComponentsInChildren<Component>().FirstOrDefault(c => c != null && c.GetType().Name == "MicroSplatTerrain") : null;
                var mat = ms != null ? Traverse.Create(ms).Field("matInstance").GetValue<Material>() : null;
                if (mat != null && mat.HasProperty("_AlphaHoleDV_1"))
                {
                    var v = mat.GetVector("_AlphaHoleDV_1");
                    vector = $"material hole at {(new Vector3(v.x, v.y, v.z) - hole.transform.position).magnitude:F1} m from this one, radius {Mathf.Sqrt(Mathf.Max(0, v.w)):F1}";
                }
                Line($"  hole {hole.transform.parent.name}: index {hole.managerIndex}, tile {(hole.Terrain != null ? hole.Terrain.name : "none")}, {d:F0} m away, " +
                     $"facing the camera {(facing < 0 ? "yes" : "NO")} ({facing:F2}), visible set {(visible != null && visible.Contains(hole) ? "yes" : "no")}; {vector}");
            }
        }

        // ------------------------------------------------------------ ground

        private sealed class GroundStat
        {
            public readonly HashSet<int> Seen = new HashSet<int>();
            public float Min = float.MaxValue, Max = float.MinValue, WorstDev;
            public int Samples, OnBed;
            public string WorstAt;
        }

        private readonly Dictionary<RailTrack, GroundStat> ground = new Dictionary<RailTrack, GroundStat>();
        private readonly Dictionary<RailTrack, float> tunnelCover = new Dictionary<RailTrack, float>();
        private const float LiningTop = 7.4f;   // the arch crown, 7.3 m over the rail, and a little
        private Dictionary<RailTrack, double> portalSpans;

        /// <summary>Rail height over the loaded terrain along every track, and the terrain against the designed bed.</summary>
        private void Ground()
        {
            var root = Root;
            var grid = SingletonBehaviour<TerrainGrid>.Instance;
            if (grid == null)
                return;
            if (portalSpans == null)
            {
                // Inside a tunnel the ground above is the hill: stop short of each portal.
                portalSpans = new Dictionary<RailTrack, double>();
                foreach (var layout in Main.Layouts)
                    foreach (var tunnel in layout.Tunnels)
                    {
                        var t = Find(tunnel.Track);
                        if (t == null) continue;
                        var portal = Frames.World(root, Vec.Of(tunnel.Portal)) - WorldMover.currentMove;
                        var best = t.GetKinkedPointSet().points.OrderBy(p => ((Vector3)p.position - portal).sqrMagnitude).First();
                        portalSpans[t] = best.span;
                    }
            }
            var terrain = LayerMask.GetMask("Terrain");
            foreach (var t in Builder.OurTracks.Concat(Builder.TrimmedStock.Keys))
            {
                if (!ground.TryGetValue(t, out var stat))
                    ground[t] = stat = new GroundStat();
                var ps = t.GetKinkedPointSet();
                var stop = portalSpans.TryGetValue(t, out var portalSpan) ? portalSpan - 6 : double.MaxValue;
                for (var i = 0; i < ps.points.Length; i += 4)
                {
                    var p = ps.points[i];
                    // Over the first 25 m of a tunnel, past where the cutting ends behind the face
                    // (export_layout.PORTAL_CUT_PAST and the grid's step), the hill must stay above the lining.
                    if (p.span > portalSpan + 4 && p.span < portalSpan + 29 && portalSpans.ContainsKey(t))
                    {
                        var w = (Vector3)p.position + WorldMover.currentMove;
                        if (grid.IsInLoadedCell(w) && Physics.Raycast(w + Vector3.up * 80f, Vector3.down, out var top, 120f, terrain))
                        {
                            var cover = top.point.y - w.y;
                            tunnelCover[t] = tunnelCover.TryGetValue(t, out var c) ? Mathf.Min(c, cover) : cover;
                        }
                        continue;
                    }
                    if (p.span > stop || stat.Seen.Contains(i))
                        continue;
                    var world = (Vector3)p.position + WorldMover.currentMove;
                    if (!grid.IsInLoadedCell(world))
                        continue;
                    if (!Physics.Raycast(world + Vector3.up * 30f, Vector3.down, out var hit, 80f, terrain))
                        continue;
                    stat.Seen.Add(i);
                    var rail = Frames.Map(root, world);
                    var groundY = Frames.Map(root, hit.point).y;
                    var h = rail.y - groundY;
                    stat.Samples++;
                    stat.Min = Mathf.Min(stat.Min, h);
                    stat.Max = Mathf.Max(stat.Max, h);
                    if (TerrainEdits.Formation(rail, out var formation))
                    {
                        stat.OnBed++;
                        var dev = Mathf.Abs(groundY - formation);
                        if (dev > stat.WorstDev)
                        {
                            stat.WorstDev = dev;
                            stat.WorstAt = $"{p.span:F0} m";
                        }
                    }
                }
            }
        }

        private void GroundReport()
        {
            foreach (var kv in tunnelCover)
            {
                Line($"tunnel {Short(kv.Key)}: the ground over its first 25 m is at least {kv.Value:F1} m over the rail");
                if (kv.Value < LiningTop)
                    Problem($"tunnel {Short(kv.Key)}: the ground dips to {kv.Value:F1} m over the rail, into the lining");
            }
            foreach (var kv in ground.OrderBy(k => Short(k.Key)))
            {
                var s = kv.Value;
                if (s.Samples == 0)
                {
                    Line($"ground {Short(kv.Key)}: never loaded");
                    continue;
                }
                var bed = s.OnBed > 0 ? $"; {s.OnBed} on the bed, ground off the design by up to {s.WorstDev:F2} m (at {s.WorstAt})" : "; no bed";
                Line($"ground {Short(kv.Key)}: rail over ground {s.Min:F2}..{s.Max:F2} m ({s.Samples} samples){bed}");
                if (s.OnBed > 0 && s.WorstDev > BedTolerance)
                    Problem($"{Short(kv.Key)}: the ground is {s.WorstDev:F2} m off the designed bed at {s.WorstAt}");
                if (s.Min < 0.05f)
                    Problem($"{Short(kv.Key)}: rail only {s.Min:F2} m over the ground");
            }
        }

        // ------------------------------------------------------------ offices

        private static IEnumerable<OfficeSpec> OfficeSpecs =>
            Main.Layouts.Where(Builder.Built.Contains).SelectMany(l => l.Offices);

        /// <summary>
        /// Each office: built where designed, with the stock office's working parts and without
        /// the parts left out of the copy, and its floor at the pad.
        /// </summary>
        private void OfficeCheck()
        {
            var root = Root;
            foreach (var note in Offices.Notes)
                Line("office note: " + note);
            foreach (var spec in OfficeSpecs)
            {
                var office = Offices.Of(spec.Name);
                if (office == null)
                {
                    Problem($"office {spec.Name}: not built");
                    continue;
                }
                var map = Frames.Map(root, office.transform.position);
                var off = (map - Vec.Of(spec.Position)).magnitude;
                var yaw = (Quaternion.Inverse(root.parent != null ? root.parent.rotation : Quaternion.identity) * office.transform.rotation).eulerAngles.y;
                var yawOff = Mathf.Abs(Mathf.DeltaAngle(yaw, spec.Yaw));
                Line($"office {spec.Name}: at ({map.x:F2}, {map.y:F2}, {map.z:F2}), {off:F3} m and {yawOff:F2} deg off the design; " +
                     $"under {office.transform.parent?.name}; active {office.activeInHierarchy}");
                if (off > 0.01f || yawOff > 0.1f)
                    Problem($"office {spec.Name}: not where designed");
                var parts = new (string What, Type Type)[]
                {
                    ("kiosk", typeof(DV.ServicePenalty.UI.DisplayScreenSwitcher)),
                    ("kiosk main screen", typeof(DV.ServicePenalty.UI.CareerManagerMainScreen)),
                    ("kiosk input", typeof(DV.ServicePenalty.UI.CareerManagerInputHandler)),
                    ("job validator", typeof(JobValidator)),
                    ("booklet table", typeof(PointOnPlane)),
                    ("job bin", typeof(JobAbandoner)),
                };
                foreach (var (what, type) in parts)
                {
                    var n = office.GetComponentsInChildren(type, true).Length;
                    Line($"office {spec.Name}: {what}: {n}");
                    if (n != 1)
                        Problem($"office {spec.Name}: {n} {what}(s), expected 1");
                }
                var items = office.GetComponentsInChildren<DV.CabControls.ItemBase>(true).Length;
                var masks = office.GetComponentsInChildren<AwesomeTechnologies.VegetationMaskArea>(true).Length;
                Line($"office {spec.Name}: loose items {items}, stock vegetation masks {masks}");
                if (items > 0 || masks > 0)
                    Problem($"office {spec.Name}: parts left in that should be left out");
                SameAsStock(spec, office);
                // The floor, from above, and the pad under the porch.
                var top = office.transform.TransformPoint(new Vector3(0f, 12f, 0f));
                var floorHits = Physics.RaycastAll(top, Vector3.down, 15f).OrderBy(h => h.distance)
                    .Where(h => h.collider.transform.IsChildOf(office.transform) && h.collider.name.StartsWith("Floor", StringComparison.Ordinal)).ToList();
                if (floorHits.Count == 0)
                    Line($"office {spec.Name}: no floor collider found from above");
                else
                {
                    var floorY = Frames.Map(root, floorHits[0].point).y;
                    Line($"office {spec.Name}: floor at {floorY:F2} m, pad at {spec.Floor:F2} m");
                    if (floorY < spec.Floor - 0.05f || floorY > spec.Floor + 0.4f)
                        Problem($"office {spec.Name}: floor {floorY - spec.Floor:F2} m off the pad");
                }
            }
        }

        /// <summary>
        /// Every object in the copy carries the same components as its stock original, now both
        /// have woken: a control that set itself up twice (a copied handler beside its own)
        /// shows here as an extra component.
        /// </summary>
        private void SameAsStock(OfficeSpec spec, GameObject office)
        {
            var stock = office.transform.parent != null ? office.transform.parent.Find(spec.Source) : null;
            if (stock == null)
            {
                Problem($"office {spec.Name}: stock office {spec.Source} not found to compare");
                return;
            }
            string Path(Transform t, Transform top) => t == top ? "" : Path(t.parent, top) + "/" + t.name;
            Dictionary<string, string> Kinds(Transform top) => top.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => Path(t, top)).ToDictionary(g => g.Key, g => string.Join(",",
                    g.First().GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name).OrderBy(n => n, StringComparer.Ordinal)));
            var mine = Kinds(office.transform);
            var theirs = Kinds(stock);
            // An object not yet active in the copy has not woken, so it has not set itself up yet.
            var asleep = new HashSet<string>(office.GetComponentsInChildren<Transform>(true)
                .Where(t => !t.gameObject.activeInHierarchy).Select(t => Path(t, office.transform)));
            var differ = mine.Where(m => !asleep.Contains(m.Key) && theirs.TryGetValue(m.Key, out var t) && t != m.Value).ToList();
            Line($"office {spec.Name}: {mine.Count - asleep.Count} awake objects compared with {spec.Source}; {differ.Count} differ in components");
            foreach (var d in differ.Take(10))
                Problem($"office {spec.Name}: {d.Key}: [{d.Value}] where the stock office has [{theirs[d.Key]}]");
        }

        private readonly Dictionary<string, (int Samples, float Worst, string At)> officeGround = new Dictionary<string, (int, float, string)>();

        /// <summary>The pad's ground around each office (where the terrain is loaded) against its level.</summary>
        private void OfficeGround()
        {
            var root = Root;
            var grid = SingletonBehaviour<TerrainGrid>.Instance;
            var terrain = LayerMask.GetMask("Terrain");
            foreach (var spec in OfficeSpecs)
            {
                var office = Offices.Of(spec.Name);
                if (office == null || grid == null)
                    continue;
                officeGround.TryGetValue(spec.Name, out var stat);
                // Around the building, on the pad: 9 m along local x (porch to back), 8 m along z.
                foreach (var (x, z) in new[] { (-9f, 0f), (9f, 0f), (0f, -8f), (0f, 8f), (-9f, -8f), (-9f, 8f), (9f, -8f), (9f, 8f) })
                {
                    var w = office.transform.TransformPoint(new Vector3(x, 0f, z));
                    if (!grid.IsInLoadedCell(w) || !Physics.Raycast(w + Vector3.up * 30f, Vector3.down, out var hit, 60f, terrain))
                        continue;
                    var dev = Mathf.Abs(Frames.Map(root, hit.point).y - spec.Floor);
                    stat.Samples++;
                    if (dev > stat.Worst)
                        stat = (stat.Samples, dev, $"local ({x:F0}, {z:F0})");
                }
                officeGround[spec.Name] = stat;
            }
        }

        private void OfficeGroundReport()
        {
            foreach (var spec in OfficeSpecs)
            {
                if (!officeGround.TryGetValue(spec.Name, out var s) || s.Samples == 0)
                {
                    Line($"office {spec.Name}: pad never loaded");
                    continue;
                }
                Line($"office {spec.Name}: pad ground off its level by up to {s.Worst:F2} m ({s.Samples} samples, worst at {s.At})");
                if (s.Worst > BedTolerance)
                    Problem($"office {spec.Name}: the pad's ground is {s.Worst:F2} m off its level at {s.At}");
            }
        }

        /// <summary>
        /// The office's kiosk, as a player uses it: the player stands at the screen and presses
        /// the kiosk's own buttons the way the pointer does (Click): down to Stats, confirm,
        /// cancel back. The screen is captured at the main menu and at Stats. Another mod's menu
        /// entries (the contract mod's) show in the main menu's lines.
        /// </summary>
        private IEnumerator Kiosk(ViewSpec view)
        {
            var office = Offices.Of(view.Kiosk);
            var switcher = office != null ? office.GetComponentInChildren<DV.ServicePenalty.UI.DisplayScreenSwitcher>(true) : null;
            if (switcher == null)
            {
                Problem($"kiosk {view.Kiosk}: no office or kiosk");
                yield break;
            }
            var root = Root;
            var specFloor = OfficeSpecs.First(o => o.Name == view.Kiosk).Floor;
            // The screen faces the kiosk's local -x; its text lines are centred about 1.3 m up.
            Vector3 Facing()
            {
                var f = switcher.transform.TransformDirection(Vector3.left);
                f.y = 0f;
                return f.normalized;
            }
            Vector3 Screen() => switcher.transform.TransformPoint(new Vector3(-0.10f, 1.32f, -0.08f));
            var stand = Screen() + Facing() * 0.8f;
            stand.y = Frames.World(root, new Vector3(0f, specFloor, 0f)).y + 0.15f;
            PlayerManager.TeleportPlayer(stand, Quaternion.LookRotation(-Facing(), Vector3.up), null, true);
            yield return new WaitForSeconds(3f);
            var grid = SingletonBehaviour<TerrainGrid>.Instance;
            for (var waited = 0f; grid != null && grid.IsLoadingInProgress() && waited < 60f; waited += 1f)
                yield return new WaitForSeconds(1f);
            yield return new WaitForSeconds(6f);
            var player = PlayerManager.PlayerTransform;
            Line($"kiosk {view.Kiosk}: player {(player.position - Screen()).magnitude:F2} m from the screen");
            var main = switcher.GetComponentInChildren<DV.ServicePenalty.UI.CareerManagerMainScreen>(true);
            string Texts(DV.ServicePenalty.UI.CareerManagerMainScreen m) =>
                string.Join(" | ", switcher.allTextFields.Where(f => f != null && !string.IsNullOrEmpty(f.text)).Select(f => $"{f.name}: {f.text}"));
            Line($"kiosk {view.Kiosk}: screen {switcher.CurrentScreen?.GetType().Name ?? "none"}; lines {Texts(main)}");
            if (main != null)
                Line($"kiosk {view.Kiosk}: main menu lines: title {main.title?.name}, fees {main.fees?.name}, licenses {main.licenses?.name}, " +
                     $"owned vehicles {main.ownedVehicles?.name}, stats {main.stats?.name}; text fields {switcher.allTextFields.Count}");
            if (!(switcher.CurrentScreen is DV.ServicePenalty.UI.CareerManagerMainScreen))
                Problem($"kiosk {view.Kiosk}: not at the main menu");
            yield return Capture(view.Name, () => (Screen() + Facing() * 0.55f, Screen()), 50f);
            var input = office.GetComponentInChildren<DV.ServicePenalty.UI.CareerManagerInputHandler>(true);
            if (input == null)
            {
                Problem($"kiosk {view.Kiosk}: no input handler");
                yield break;
            }
            bool Press(GameObject button, string name)
            {
                var ok = Click(button, out var how);
                Line($"kiosk {view.Kiosk}: pressed {name}: {how}");
                if (!ok)
                    Problem($"kiosk {view.Kiosk}: {name}: {how}");
                return ok;
            }
            // Down to the main menu's fourth stock entry, Stats, and confirm.
            for (var i = 0; i < 3; i++)
            {
                Press(input.downButtonGO, "down");
                yield return new WaitForSeconds(0.3f);
            }
            Press(input.confirmButtonGO, "confirm");
            yield return new WaitForSeconds(0.5f);
            var at = switcher.CurrentScreen?.GetType().Name ?? "none";
            Line($"kiosk {view.Kiosk}: down 3, confirm: {at}; lines {Texts(main)}");
            if (!(switcher.CurrentScreen is DV.ServicePenalty.UI.CareerManagerStatsScreen))
                Problem($"kiosk {view.Kiosk}: pressing down 3 and confirm opened {at}, not Stats");
            yield return Capture(view.Name + "-stats", () => (Screen() + Facing() * 0.55f, Screen()), 50f);
            Press(input.cancelButtonGO, "cancel");
            yield return new WaitForSeconds(0.5f);
            Line($"kiosk {view.Kiosk}: cancel: {switcher.CurrentScreen?.GetType().Name ?? "none"}");
            if (!(switcher.CurrentScreen is DV.ServicePenalty.UI.CareerManagerMainScreen))
                Problem($"kiosk {view.Kiosk}: cancel did not return to the main menu");
        }

        /// <summary>
        /// The interchanges on the maps and fast travel to them (MapMarks): each office's
        /// destination is active, every map in the world has its marker and draws the yards, a
        /// close-up of a map shows the drawing, and the stock fast travel, asked as a pressed
        /// marker asks it, brings the player to each office's porch.
        /// </summary>
        private IEnumerator MapAndFastTravel()
        {
            var root = Root;
            var destinations = new List<(OfficeSpec Spec, DV.Teleporters.WorldFastTravelDestination D)>();
            foreach (var spec in OfficeSpecs)
            {
                var d = MapMarks.Destination(spec.Name);
                if (d == null)
                {
                    Problem($"fast travel {spec.Name}: no destination");
                    continue;
                }
                var active = DV.Teleporters.FastTravelDestination.ActiveDestinations.Contains(d);
                var at = Frames.Map(root, d.playerTeleportAnchor.position);
                Line($"fast travel {spec.Name}: '{d.MarkerName}', {(active ? "active" : "not active")}, lands at ({at.x:F1}, {at.y:F2}, {at.z:F1})");
                if (!active)
                    Problem($"fast travel {spec.Name}: destination not active");
                destinations.Add((spec, d));
            }
            foreach (var note in MapPicture.Notes)
                Line(note);
            Line($"map picture: painted {MapPicture.Painted}, shown by {MapPicture.Materials} material(s)");
            if (!MapPicture.Painted || MapPicture.Materials == 0)
                Problem("map picture: the interchanges are not painted into the maps' picture");
            var maps = Resources.FindObjectsOfTypeAll<MapMarkersController>().Where(m => m.gameObject.scene.IsValid()).ToList();
            Line($"maps in the world: {maps.Count}");
            if (maps.Count == 0)
                Problem("no map in the world to check");
            foreach (var m in maps)
            {
                var markers = Traverse.Create(m).Field("markers").GetValue<Dictionary<DV.Teleporters.FastTravelDestination, MapMarker>>();
                var ours = destinations.Count(x => markers != null && markers.ContainsKey(x.D));
                var overlay = m.GetComponent<MapMarks.Overlay>();
                Line($"map {PathOf(m.transform)}: active {m.gameObject.activeInHierarchy}, {markers?.Count ?? 0} markers, {ours} of the interchanges; " +
                     $"interchanges drawn {overlay?.Drawn.ToString() ?? "no overlay"}");
                if (m.gameObject.activeInHierarchy && (ours != destinations.Count || overlay == null || overlay.Drawn != Builder.Built.Count))
                    Problem($"map {PathOf(m.transform)}: {ours} interchange marker(s), {overlay?.Drawn ?? 0} drawn");
                MarkerClearance(m, markers, destinations);
            }
            yield return FastTravelChecks(destinations);
            // The painted picture itself, around each yard (what every map's paper shows), and the
            // names each map shows beside it.
            foreach (var drawing in MapMarks.Drawings())
            {
                var crop = MapPicture.Crop(drawing, 40);
                if (crop == null)
                {
                    Problem($"map picture: no painted picture of {drawing.Name}");
                    continue;
                }
                var big = Scale(crop, 6);
                var file = Path.Combine(Folder, $"inspect-map-{drawing.Name.ToLowerInvariant().Replace(' ', '-')}.png");
                File.WriteAllBytes(file, DV.RenderTextureSystem.BookletRender.StaticTextureRenderBase.EncodeGenericTextureToPNG(big));
                UnityEngine.Object.Destroy(crop);
                UnityEngine.Object.Destroy(big);
                Line($"map picture: saved {Path.GetFileName(file)}");
            }
            foreach (var m in maps)
            {
                var names = m.transform.Find("MapPaper/Names");
                var ours = names == null ? new List<Transform>() : names.Cast<Transform>().Where(t => t.name.StartsWith(MapNames.Prefix, StringComparison.Ordinal)).ToList();
                Line($"map {PathOf(m.transform)}: names {string.Join(", ", ours.Select(t => $"{t.GetComponent<TMPro.TMP_Text>()?.text} at {t.localPosition}"))}");
                if (m.GetComponent<MapMarks.Overlay>() != null && ours.Count != Builder.Built.Count && m.GetComponent<MapMarks.Overlay>().Drawn > 0)
                    Problem($"map {PathOf(m.transform)}: {ours.Count} interchange names");
            }
            yield return WallMaps(maps);
        }

        /// <summary>
        /// The offices' wall maps (MapPaperOffice): each shows the interchanges' codes, at the
        /// places the handheld map shows them, and the West Interchange office's own is seen
        /// up close (inspect-wall-map.png).
        /// </summary>
        private IEnumerator WallMaps(List<MapMarkersController> maps)
        {
            foreach (var note in MapNames.Notes)
                Line("map note: " + note);
            var walls = Resources.FindObjectsOfTypeAll<Transform>().Where(t => t.name == "MapPaperOffice" && t.gameObject.scene.IsValid()).ToList();
            var hand = maps.Select(m => m.transform.Find("MapPaper/Names")).FirstOrDefault(n => n != null && n.Cast<Transform>().Any(t => t.name.StartsWith(MapNames.Prefix, StringComparison.Ordinal)));
            int named = 0, apart = 0;
            foreach (var w in walls)
            {
                var names = w.Find("Names");
                var ours = names == null ? new List<Transform>() : names.Cast<Transform>().Where(t => t.name.StartsWith(MapNames.Prefix, StringComparison.Ordinal)).ToList();
                if (ours.Count == Builder.Built.Count)
                    named++;
                // Placed beside its marker on both, a name stands at the same place on both sheets.
                foreach (var o in ours)
                {
                    var h = hand?.Find(o.name);
                    var placed = (o.GetComponent<NameBeside>()?.Placed ?? true) && (h?.GetComponent<NameBeside>()?.Placed ?? true);
                    if (h != null && placed && (h.localPosition - o.localPosition).magnitude > 1e-3f)
                        apart++;
                }
            }
            Line($"wall maps: {named} of {walls.Count} show the interchanges' codes; {apart} code(s) placed apart from the handheld map's");
            if (walls.Count == 0 || named != walls.Count || apart > 0)
                Problem($"wall maps: {named} of {walls.Count} named, {apart} code(s) out of place");
            // The West Interchange office's own wall map, from the room.
            var spec = OfficeSpecs.FirstOrDefault(o => o.Name == "MF West") ?? OfficeSpecs.FirstOrDefault();
            var office = spec != null ? Offices.Of(spec.Name) : null;
            var paper = office != null ? office.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "MapPaperOffice") : null;
            var sheet = paper != null ? paper.Find("Names") : null;
            if (sheet == null)
            {
                Line("wall maps: no wall map in the interchange office to photograph");
                yield break;
            }
            var porch = MapMarks.Destination(spec.Name)?.playerTeleportAnchor;
            if (porch != null)
                PlayerManager.TeleportPlayer(porch.position + Vector3.up * 0.1f, porch.rotation, null, false);
            yield return new WaitForSeconds(5f);
            yield return Capture("wall-map", () =>
            {
                var centre = sheet.position;
                var n = sheet.forward;
                if (Vector3.Dot(n, office.transform.position - centre) < 0f)
                    n = -n;
                return (centre + n * 1.3f, centre);
            }, 45f);
        }

        /// <summary>
        /// Each interchange's marker on a map stands clear of every other station's marker and its
        /// row of service markers, laid out as the map lays them, and of its own name.
        /// </summary>
        private void MarkerClearance(MapMarkersController m, Dictionary<DV.Teleporters.FastTravelDestination, MapMarker> markers,
                                     List<(OfficeSpec Spec, DV.Teleporters.WorldFastTravelDestination D)> destinations)
        {
            if (markers == null || markers.Count == 0)
                return;
            var offset = Traverse.Create(m).Field("sideMarkerOffset").GetValue<float>();
            var spots = new List<(string Name, Vector3 At)>();
            foreach (var kv in markers)
            {
                var d = kv.Key;
                if (d == null || kv.Value == null || d.IsDynamic || destinations.Any(x => x.D == d))
                    continue;
                var at = kv.Value.transform.localPosition;
                spots.Add((d.gameObject.name, at));
                var dir = Quaternion.Euler(0f, d.sideMarkersStackingYRotation, 0f) * Vector3.forward;
                for (var i = 0; i < (d.sideMarkers?.Length ?? 0); i++)
                    spots.Add(($"{d.gameObject.name} service {i + 1}", at + dir * ((i + 1) * offset)));
            }
            var overlay = m.GetComponent<MapMarks.Overlay>();
            var drawings = MapMarks.Drawings();
            foreach (var (spec, d) in destinations)
            {
                if (!markers.TryGetValue(d, out var mine) || mine == null || spots.Count == 0)
                    continue;
                var at = mine.transform.localPosition;
                var across = overlay != null ? overlay.MarkerDiameter() : 0f;
                float Flat(Vector3 v) => new Vector2(v.x, v.z).magnitude;
                var nearest = spots.OrderBy(sp => Flat(sp.At - at)).First();
                var gap = Flat(nearest.At - at);
                // The name's text box: clear of its own marker's disc and of every other marker's.
                var drawing = drawings.FirstOrDefault(x => x.Name == spec.Name);
                var box = overlay != null && drawing != null ? overlay.NameBox(drawing.Code) : null;
                float ToBox(Vector3 p)
                {
                    if (box == null)
                        return float.PositiveInfinity;
                    var r = box.Value;
                    float dx = Mathf.Max(r.xMin - p.x, 0f, p.x - r.xMax), dz = Mathf.Max(r.yMin - p.z, 0f, p.z - r.yMax);
                    return Mathf.Sqrt(dx * dx + dz * dz);
                }
                var label = ToBox(at);
                var crowded = spots.Where(sp => ToBox(sp.At) < across / 2f).Select(sp => sp.Name).ToList();
                Line($"map {PathOf(m.transform)}: {spec.Name}'s marker {(across > 0 ? gap / across : 0):F2} marker widths from {nearest.Name}; " +
                     $"its name {drawing?.Code} {(box == null ? "not found" : $"{box.Value.width / across:F2} x {box.Value.height / across:F2} widths, its edge {label / across:F2} from the marker's centre")}" +
                     (crowded.Count > 0 ? $", over {string.Join(", ", crowded)}" : ""));
                if (across <= 0f || gap < 1.25f * across || label < 0.6f * across || crowded.Count > 0)
                    Problem($"map {PathOf(m.transform)}: {spec.Name}'s marker or name crowds {(gap < 1.25f * across ? nearest.Name : crowded.Count > 0 ? string.Join(", ", crowded) : "its own marker")}");
            }
        }

        private static Texture2D Scale(Texture2D src, int k)
        {
            var dst = new Texture2D(src.width * k, src.height * k, TextureFormat.RGBA32, false);
            var px = src.GetPixels();
            var outPx = new Color[dst.width * dst.height];
            for (var y = 0; y < dst.height; y++)
                for (var x = 0; x < dst.width; x++)
                    outPx[y * dst.width + x] = px[(y / k) * src.width + x / k];
            dst.SetPixels(outPx);
            dst.Apply();
            return dst;
        }

        /// <summary>Fast travel to each office, as a pressed map marker asks for it.</summary>
        private IEnumerator FastTravelChecks(List<(OfficeSpec Spec, DV.Teleporters.WorldFastTravelDestination D)> destinations)
        {
            var travel = SingletonBehaviour<FastTravelController>.Instance;
            if (travel == null)
            {
                Problem("no fast travel controller");
                yield break;
            }
            foreach (var (spec, d) in destinations)
            {
                var data = FastTravelController.ExtractFastTravelData(d, PlayerManager.Car);
                Line($"fast travel {spec.Name}: price ${data.fastTravelPrice}, {data.fastTravelDuration} s of game time, " +
                     $"can travel {data.CanTravelWithoutLoco}");
                Traverse.Create(travel).Field("lastMarkerClicked").SetValue(d);
                Traverse.Create(travel).Method("OnFastTravelRequested", new[] { typeof(bool) }).GetValue(false);
                yield return new WaitForSeconds(1f);
                for (var waited = 0f; FastTravelController.IsFastTravelling && waited < 120f; waited += 1f)
                    yield return new WaitForSeconds(1f);
                yield return new WaitForSeconds(3f);
                var off = (PlayerManager.PlayerTransform.position - d.playerTeleportAnchor.position).magnitude;
                Line($"fast travel {spec.Name}: player {off:F2} m from the porch anchor");
                if (off > 2.5f)
                    Problem($"fast travel {spec.Name}: player ended {off:F1} m from the office");
            }
        }

        /// <summary>
        /// The switch books (Booklets): the switch data with the interchanges' pages and tie-ins,
        /// each cell naming its own switch; both books rendered through their render prefabs
        /// (our pages, their neighbours and the route map saved as pictures); and a booklet and
        /// a route map spawned as items, whose chapters and touched cells are checked.
        /// </summary>
        private IEnumerator Books()
        {
            foreach (var note in Booklets.Notes)
                Line("book note: " + note);
            var data = SingletonBehaviour<LevelInfo>.Instance?.junctionData;
            var registry = SingletonBehaviour<RailTrackRegistryBase>.Instance;
            if (data == null)
            {
                Problem("books: no switch data");
                yield break;
            }
            var stations = data.GetPageData(DV.Items.JunctionMap.JunctionMapType.Station);
            var indices = Booklets.PageIndices();
            Line($"books: station pages {stations.Length} (stock {data.junctionPageDataStations.Length}); plan failed {Booklets.PlanFailed}");
            string Resolve(JunctionGeneratedDataRuntime.DataCoord c)
            {
                var j = c.data.junctionIndex >= 0 && c.data.junctionIndex < registry.OrderedJunctions.Length ? registry.OrderedJunctions[c.data.junctionIndex] : null;
                return Builder.IdOf(j) ?? j?.junctionData.junctionIdLong ?? "none";
            }
            foreach (var layout in Builder.Built.Where(l => l.Booklet != null))
            {
                if (!indices.TryGetValue(layout.Site, out var idx) || idx >= stations.Length)
                {
                    Problem($"books: {layout.Site} has no page");
                    continue;
                }
                var page = stations[idx];
                var before = idx > 0 ? stations[idx - 1].categoryId : "-";
                var after = idx + 1 < stations.Length ? stations[idx + 1].categoryId : "-";
                var wrong = page.junctionDataCoords.Where(c => Resolve(c) != $"{layout.Site}/{layout.Booklet.Cells.FirstOrDefault(k => k.Value[0] == c.coord.x && k.Value[1] == c.coord.y).Key}").ToList();
                var dup = page.junctionDataCoords.GroupBy(c => c.coord).Count(g => g.Count() > 1);
                Line($"books: {layout.Site} page {idx} '{page.categoryId}' (after {before}, before {after}): {page.junctionDataCoords.Count} switches; " +
                     string.Join(", ", page.junctionDataCoords.Select(c => $"({c.coord.x},{c.coord.y}) {Resolve(c)}")));
                if (page.categoryId != layout.Code || page.junctionDataCoords.Count != layout.Booklet.Cells.Count || wrong.Count > 0 || dup > 0)
                    Problem($"books: {layout.Site}'s page is wrong ({wrong.Count} cells name another switch, {dup} shared cells)");
            }
            var world = data.GetPageData(DV.Items.JunctionMap.JunctionMapType.World);
            foreach (var layout in Builder.Built.Where(l => l.RouteMap != null))
                foreach (var cell in layout.RouteMap.Cells)
                {
                    var here = world[0].junctionDataCoords.Where(c => c.coord.x == cell.Value[0] && c.coord.y == cell.Value[1]).ToList();
                    Line($"books: route map ({cell.Value[0]}, {cell.Value[1]}): {string.Join(", ", here.Select(Resolve))}");
                    if (here.Count != 1 || Resolve(here[0]) != $"{layout.Site}/{cell.Key}")
                        Problem($"books: route map cell of {cell.Key} holds {here.Count} switch(es)");
                }
            // Both books rendered as the game renders them.
            foreach (var which in new[] { LevelInfo.WorldSpecificPrefabs.SchematicMapRender, LevelInfo.WorldSpecificPrefabs.RouteMapRender })
            {
                var prefab = LevelInfo.GetWorldSpecificPrefab(which);
                if (prefab == null)
                {
                    Problem($"books: no render prefab {which}");
                    continue;
                }
                var go = UnityEngine.Object.Instantiate(prefab, SingletonBehaviour<DV.RenderTextureSystem.RenderTextureSystem>.Instance.transform.position, Quaternion.identity);
                var render = go.GetComponent<DV.RenderTextureSystem.BookletRender.StaticTextureRenderBase>();
                Texture[] pages = null;
                render.TexturesGenerated += (t, _) => pages = t;
                render.GenerateStaticPagesTextures();
                for (var waited = 0f; pages == null && waited < 180f; waited += 0.5f)
                    yield return new WaitForSeconds(0.5f);
                if (pages == null)
                {
                    Problem($"books: {which} never finished rendering");
                    continue;
                }
                Line($"books: {which} rendered {pages.Length} pages");
                var save = which == LevelInfo.WorldSpecificPrefabs.RouteMapRender ? new[] { 0 }
                    : indices.Values.SelectMany(i => new[] { i - 1, i, i + 1 }).Distinct().Where(i => i >= 0 && i < pages.Length).ToArray();
                foreach (var i in save)
                {
                    var file = Path.Combine(Folder, $"inspect-book-{(which == LevelInfo.WorldSpecificPrefabs.RouteMapRender ? "route" : "booklet")}-{i:00}.png");
                    File.WriteAllBytes(file, DV.RenderTextureSystem.BookletRender.StaticTextureRenderBase.EncodeGenericTextureToPNG(pages[i]));
                    Line($"books: saved {Path.GetFileName(file)}");
                }
                foreach (var t in pages)
                    UnityEngine.Object.Destroy(t);
            }
            // The books as items: the booklet's chapters and touched cells.
            foreach (var item in new[] { "MapSchematic", "RouteMap" })
            {
                var prefab = Resources.Load<GameObject>(item);
                if (prefab == null)
                {
                    Line($"books: no item prefab '{item}' in Resources; item check skipped");
                    continue;
                }
                var player = PlayerManager.PlayerTransform;
                var go = UnityEngine.Object.Instantiate(prefab, player.position + Vector3.up * 1.2f + player.forward * 0.8f, Quaternion.identity);
                var book = go.GetComponent<PageBook>();
                var map = go.GetComponent<DV.Items.JunctionMap>();
                // A booklet is ready once its pages are generated; the route map once its switch data is read.
                for (var waited = 0f; waited < 180f; waited += 0.5f)
                {
                    if (book != null ? book.PagesGenerated : map != null && Traverse.Create(map).Field("junctionDataMap").GetValue() != null)
                        break;
                    yield return new WaitForSeconds(0.5f);
                }
                yield return new WaitForSeconds(1f);
                Line($"books: item {item}: {book?.pageTextures?.Length ?? 0} pages, map type {map?.junctionMapType}");
                var chapters = go.GetComponent<ChapterSelector>() is ChapterSelector cs
                    ? Traverse.Create(cs).Field("allChapters").GetValue<List<ChapterSelector.PageBookChapter>>() : null;
                if (chapters != null)
                    foreach (var kv in indices)
                    {
                        var ch = chapters.FindIndex(c => c.startingPage <= kv.Value && kv.Value <= c.endingPage);
                        Line($"books: {kv.Key}'s page {kv.Value} is in chapter {ch} ({(ch >= 0 ? $"{chapters[ch].startingPage}-{chapters[ch].endingPage}" : "none")}); " +
                             $"chapters {chapters.Count}, last ends at {chapters.Last().endingPage}");
                        if (ch < 0 || chapters[ch].startingPage != kv.Value || chapters[ch].endingPage != kv.Value)
                            Problem($"books: {kv.Key}'s page is not a chapter of its own");
                    }
                if (chapters != null && go.GetComponent<ChapterSelector>() is ChapterSelector selector)
                    yield return TabStrip(go, prefab, selector, chapters, book, indices);
                if (map != null)
                {
                    var touch = map.junctionMapType == DV.Items.JunctionMap.JunctionMapType.World
                        ? Builder.Built.Where(l => l.RouteMap != null).SelectMany(l => l.RouteMap.Cells.Select(c => (Site: l.Site, Id: c.Key, Cell: c.Value, Page: 0)))
                        : Builder.Built.Where(l => l.Booklet != null && indices.ContainsKey(l.Site)).SelectMany(l => l.Booklet.Cells.Select(c => (Site: l.Site, Id: c.Key, Cell: c.Value, Page: indices[l.Site])));
                    var good = 0;
                    var all = 0;
                    foreach (var (site, id, cell, page) in touch)
                    {
                        all++;
                        // As a page flip does: the page and the touchscreen's valid cells for it.
                        Traverse.Create(map).Method("OnPageFlipped", new[] { typeof(int) }).GetValue(page);
                        var j = Traverse.Create(map).Method("JunctionFromCoord", new[] { typeof(Vector2Int) }).GetValue<Junction>(new Vector2Int(cell[0], cell[1]));
                        if (Builder.IdOf(j) == $"{site}/{id}")
                            good++;
                        else
                            Problem($"books: item {item} page {page} cell ({cell[0]}, {cell[1]}) finds {Builder.IdOf(j) ?? j?.junctionData.junctionIdLong ?? "nothing"}, not {id}");
                    }
                    Line($"books: item {item}: {good} of {all} interchange cells find their switch");
                }
                UnityEngine.Object.Destroy(go);
                yield return null;
            }
        }

        /// <summary>
        /// The booklet's tab strip: a cell for every chapter, the stock cells where the stock strip
        /// has them (from the item's prefab), and each interchange's tab turning to its page.
        /// </summary>
        private IEnumerator TabStrip(GameObject go, GameObject prefab, ChapterSelector selector, List<ChapterSelector.PageBookChapter> chapters,
                                     PageBook book, Dictionary<string, int> indices)
        {
            var strip = selector.BookmarksTouchscreen;
            for (var waited = 0f; (strip == null || !strip.IsInitialized) && waited < 20f; waited += 0.5f)
            {
                yield return new WaitForSeconds(0.5f);
                strip = selector.BookmarksTouchscreen;
            }
            if (strip == null || !strip.IsInitialized)
            {
                Problem("books: the booklet's tab strip never came up");
                yield break;
            }
            var added = go.GetComponent<Booklets.Tabs>()?.Added ?? 0;
            var selectable = chapters.Count(c => c.allowChapterSelector);
            Line($"books: tab strip {strip.gridSize.x} cells for {selectable} chapters, {added} added; cell {strip.sectionSize.x * 1000f:F2} mm");
            if (strip.gridSize.x != selectable || added != indices.Count)
                Problem($"books: the tab strip has {strip.gridSize.x} cells for {selectable} chapters");
            // The stock cells, from the prefab's own strip, in the item's frame.
            var path = new List<string>();
            for (var t = strip.transform; t != null && t != go.transform; t = t.parent)
                path.Insert(0, t.name);
            var stockStrip = prefab.transform.Find(string.Join("/", path));
            var stockBox = stockStrip != null ? stockStrip.GetComponent<BoxCollider>() : null;
            if (stockBox != null)
            {
                var cells = strip.gridSize.x - added;
                var worst = 0f;
                for (var i = 0; i < cells; i++)
                {
                    var live = go.transform.InverseTransformPoint(strip.transform.TransformPoint(strip.SectionLocalCenter(new Vector2Int(i, 0))));
                    var si = strip.flipHorizontalCoords ? cells - i - 1 : i;
                    var local = new Vector3(stockBox.size.x / cells * (si + 0.5f) - stockBox.size.x / 2f, strip.hoverHeightOffset,
                                            strip.sectionSize.y * 0.5f - stockBox.size.z / 2f);
                    var stock = prefab.transform.InverseTransformPoint(stockStrip.TransformPoint(local));
                    worst = Mathf.Max(worst, (live - stock).magnitude);
                }
                Line($"books: the {cells} stock tab cells are within {worst * 1000f:F3} mm of the stock strip's");
                // A touch picks the cell it falls in: a tenth of a cell is far from the edges.
                if (worst > strip.sectionSize.x * 0.1f)
                    Problem($"books: the stock tab cells moved by up to {worst * 1000f:F2} mm, over a tenth of a cell");
            }
            else
                Line("books: no stock strip in the prefab to compare the cells with");
            // Each interchange's tab turns to its page, as pressing it does.
            var order = indices.OrderBy(kv => kv.Value).ToList();
            for (var k = 0; k < order.Count; k++)
            {
                var tab = selectable - order.Count + k;
                Traverse.Create(selector).Method("OnChapterSelected", new[] { typeof(Vector2Int) }).GetValue(new Vector2Int(tab, 0));
                yield return new WaitForSeconds(3f);
                Line($"books: tab {tab} turns to page {book?.currentPage} ({order[k].Key}'s is {order[k].Value})");
                if (book == null || book.currentPage != order[k].Value)
                    Problem($"books: tab {tab} turns to page {book?.currentPage}, not {order[k].Key}'s {order[k].Value}");
            }
        }

        private static string PathOf(Transform t) => t.parent == null ? t.name : PathOf(t.parent) + "/" + t.name;

        /// <summary>
        /// Presses a control as the non-VR pointer does: GrabberRaycaster takes the first grab
        /// handler above the collider it hits, and pressing starts and ends an interaction with
        /// it. A copied control whose stale handler sits first does nothing here, as in play.
        /// </summary>
        private static bool Click(GameObject control, out string how)
        {
            if (control == null)
            {
                how = "no button";
                return false;
            }
            var spec = control.GetComponent<DV.CabControls.Spec.ControlSpec>();
            var collider = (spec?.colliderGameObjects ?? Array.Empty<GameObject>())
                .Where(g => g != null).Select(g => g.GetComponentInChildren<Collider>(true))
                .FirstOrDefault(c => c != null) ?? control.GetComponentInChildren<Collider>(true);
            if (collider == null)
            {
                how = "no collider";
                return false;
            }
            var handlers = control.GetComponents<DV.Interaction.AGrabHandler>().Length;
            var impls = control.GetComponents<DV.CabControls.ControlImplBase>().Length;
            var handler = collider.GetComponentInParent<DV.Interaction.AGrabHandler>();
            if (handler == null)
            {
                how = $"no grab handler ({impls} implementation(s))";
                return false;
            }
            handler.StartInteraction(collider.bounds.center, null);
            handler.EndInteraction();
            how = $"{handler.GetType().Name} on {collider.name} ({handlers} grab handler(s), {impls} implementation(s))";
            return handlers == 1 && impls == 1;
        }

        // ------------------------------------------------------------ log

        private void LogReport() => LogReportTo(Line, Problem);

        private static void LogReportTo(Action<string> line, Action<string> problem)
        {
            if (loggedOrder.Count == 0)
            {
                line("game log: no errors or exceptions");
                return;
            }
            foreach (var key in loggedOrder)
            {
                var expected = Expected.Any(e => key.IndexOf(e, StringComparison.Ordinal) >= 0);
                var text = $"game log ({logged[key]}x){(expected ? " expected" : "")}: {key}";
                if (expected)
                    line(text);
                else
                    problem(text);
            }
        }
    }
}
