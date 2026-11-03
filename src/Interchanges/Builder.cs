using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Interchanges
{
    /// <summary>
    /// Builds every layout into the stock railway once per game load, at the
    /// registry's first read of its tracks (WorldStreamingInit reads
    /// RailTrackRegistryBase.AllTracks right after the railway scene loads,
    /// before logic tracks, rail meshes, switch states and cars).
    ///
    /// New objects go under [railway] as its last children, so every stock
    /// track and switch keeps its index in the ordered lists that saves use;
    /// the hash patches leave our objects out, so the saves' keys don't
    /// change. Everything is prepared inactive in a staging object and only
    /// then attached: a failure while preparing leaves the stock map as it was.
    /// </summary>
    internal static class Builder
    {
        public static readonly HashSet<RailTrack> OurTracks = new HashSet<RailTrack>();
        public static readonly HashSet<Junction> OurJunctions = new HashSet<Junction>();

        /// <summary>Stock tracks this mod shortened, with their new lengths.</summary>
        public static readonly Dictionary<RailTrack, double> TrimmedStock = new Dictionary<RailTrack, double>();

        /// <summary>
        /// Where a switch was put on a stock track: the stock track now ends at the switch's
        /// through end, the through leg runs back to the joint, and the stub carries on to the
        /// old end. All three follow the stock curve, so a place on the through leg or stub is
        /// also a place on the original stock track (CarSaves saves cars there that way).
        /// </summary>
        public sealed class TieIn
        {
            public RailTrack Stock;
            public RailTrack Through;
            public RailTrack Stub;
            public DV.PointSet.EquiPointSet Original;   // the stock track's points before the trim
            public double OriginalSpan => Original?.span ?? 0;

            /// <summary>The joint's span on the original stock track.</summary>
            public double JointSpan => OriginalSpan - (Stub.GetKinkedPointSet()?.span ?? 0);

            /// <summary>A bogie on the through leg or stub, as a span on the original stock track.</summary>
            public bool ToStock(RailTrack track, double span, out double stockSpan)
            {
                stockSpan = 0;
                if (track == Stub) stockSpan = JointSpan + span;
                else if (track == Through) stockSpan = JointSpan - span;
                else return false;
                stockSpan = Math.Max(0, Math.Min(OriginalSpan, stockSpan));
                return true;
            }

            /// <summary>A span on the original stock track beyond the kept part, as a place on the through leg or stub.</summary>
            public bool FromStock(double stockSpan, out RailTrack track, out double span)
            {
                var joint = JointSpan;
                var through = Through.GetKinkedPointSet()?.span ?? 24;
                track = null;
                span = 0;
                if (stockSpan >= joint)
                {
                    track = Stub;
                    span = Math.Min(stockSpan - joint, Stub.GetKinkedPointSet()?.span ?? 0);
                }
                else if (stockSpan > joint - through)
                {
                    track = Through;
                    span = joint - stockSpan;
                }
                return track != null;
            }
        }

        public static readonly List<TieIn> TieIns = new List<TieIn>();

        /// <summary>The layouts built into this load's railway.</summary>
        public static readonly HashSet<Layout> Built = new HashSet<Layout>();

        // Stable identities for our tracks and junctions, as saves name them: "site/track",
        // "site/switch/through" or "site/switch/diverging", and "site/switch".
        private static readonly Dictionary<string, RailTrack> tracksById = new Dictionary<string, RailTrack>();
        private static readonly Dictionary<RailTrack, string> trackIds = new Dictionary<RailTrack, string>();
        private static readonly Dictionary<string, Junction> junctionsById = new Dictionary<string, Junction>();
        private static readonly Dictionary<Junction, string> junctionIds = new Dictionary<Junction, string>();

        public static string IdOf(RailTrack t) => t != null && trackIds.TryGetValue(t, out var id) ? id : null;
        public static string IdOf(Junction j) => j != null && junctionIds.TryGetValue(j, out var id) ? id : null;
        public static RailTrack TrackById(string id) => id != null && tracksById.TryGetValue(id, out var t) ? t : null;
        public static Junction JunctionById(string id) => id != null && junctionsById.TryGetValue(id, out var j) ? j : null;

        internal static void Publish(string id, RailTrack t)
        {
            OurTracks.Add(t);
            tracksById[id] = t;
            trackIds[t] = id;
        }

        internal static void Publish(string id, Junction j)
        {
            OurJunctions.Add(j);
            junctionsById[id] = j;
            junctionIds[j] = id;
        }

        public static readonly List<string> Report = new List<string>();
        public static string Failure;

        /// <summary>
        /// Inspection only: the site whose Apply fails on purpose, to test the rollback, and where:
        /// "trim" (after the stock trim), "attach" (after attaching), or "rollback" (after attaching,
        /// and again as the rollback refreshes the stock track).
        /// </summary>
        // Set only by the inspector, which a release build leaves out.
#pragma warning disable CS0649
        public static string FaultSite, FaultPoint;
#pragma warning restore CS0649

        private static RailTrackRegistryBase builtFor;
        private static bool building;

        public static bool IsOurs(RailTrack t) => t != null && OurTracks.Contains(t);
        public static bool IsOurs(Junction j) => j != null && OurJunctions.Contains(j);

        public static void EnsureBuilt(RailTrackRegistryBase registry)
        {
            if (registry == null || building || ReferenceEquals(builtFor, registry))
                return;
            if (GameObject.Find("[railway]") == null)
                return;
            building = true;
            builtFor = registry;
            OurTracks.Clear();
            OurJunctions.Clear();
            TrimmedStock.Clear();
            TieIns.Clear();
            Built.Clear();
            tracksById.Clear();
            trackIds.Clear();
            junctionsById.Clear();
            junctionIds.Clear();
            Extras.Clear();
            MapMarks.Reset();
            Booklets.Reset();
            CarSaves.Reset();
            JobTracks.Reset();
            Report.Clear();
            Failure = null;
            try
            {
                var root = registry.TrackRootParent;
                foreach (var layout in Main.Layouts)
                {
                    // A layout that fails is dropped whole: before the stock map changes (preparing,
                    // planning its connections), or with its stock changes rolled back (applying).
                    // The other layouts still build.
                    var site = new SiteBuild(layout, root);
                    try
                    {
                        site.Prepare();
                        site.Plan();
                        site.Apply();
                    }
                    catch (Exception e)
                    {
                        site.Abandon();
                        Report.AddRange(site.Notes.Where(n => n.StartsWith("rollback", StringComparison.Ordinal)));
                        Failure = (Failure == null ? "" : Failure + "\n") + $"{layout.Site}: {e}";
                        Main.Error($"{layout.Site} was not built; the stock map is unchanged there: {e}");
                        continue;
                    }
                    Built.Add(layout);
                    JobTracks.Assign(layout, OurTracks.Where(t => (IdOf(t) ?? "").StartsWith(layout.Site + "/", StringComparison.Ordinal)));
                    try
                    {
                        Extras.Build(layout, root);
                    }
                    catch (Exception e)
                    {
                        // The track is in; only its masks or tunnel dressing are missing.
                        Failure = (Failure == null ? "" : Failure + "\n") + $"{layout.Site} (masks and tunnel): {e}";
                        Main.Error($"{layout.Site}: its vegetation masks or tunnel could not be added: {e}");
                    }
                    Report.AddRange(site.Notes);
                    Main.Log($"built {layout.Site}: {site.TrackCount} tracks, {site.SwitchCount} switches, {site.BufferCount} buffer stops");
                }
            }
            catch (Exception e)
            {
                Failure = (Failure == null ? "" : Failure + "\n") + e;
                Main.Error($"building the interchanges failed part way: {e}");
            }
            finally
            {
                building = false;
            }
        }
    }

    /// <summary>One layout's objects, prepared inactive and then attached.</summary>
    internal sealed class SiteBuild
    {
        private const float SnapRange = 0.5f;
        private const float SwitchLength = 24f;

        private readonly Layout layout;
        private readonly Transform root;
        private readonly GameObject staging;
        private readonly Dictionary<string, RailTrack> tracks = new Dictionary<string, RailTrack>();
        private readonly List<PlacedSwitch> switches = new List<PlacedSwitch>();
        private readonly List<GameObject> buffers = new List<GameObject>();
        private readonly List<PendingTrim> trims = new List<PendingTrim>();
        private readonly HashSet<RailTrack> trimStubs = new HashSet<RailTrack>();
        private List<(Node End, Node Partner, float Gap)> plan;
        private RailTrack style;

        public readonly List<string> Notes = new List<string>();
        public int TrackCount => tracks.Count;
        public int SwitchCount => switches.Count;
        public int BufferCount => buffers.Count;

        public SiteBuild(Layout layout, Transform root)
        {
            this.layout = layout;
            this.root = root;
            staging = new GameObject($"[interchanges staging {layout.Site}]");
            staging.SetActive(false);
        }

        // Layout coordinates are the railway scene's own frame, which already includes
        // [railway]'s local offset (it sits 1 m up); so convert through its parent.
        private Vector3 W(Vector3 abs) => Frames.World(root, abs);
        private Vector3 WDir(Vector3 dir) => Frames.WorldDir(root, dir);

        // ---------------------------------------------------------------- prepare

        public void Prepare()
        {
            var junctionTemplates = new Dictionary<string, GameObject>();
            GameObject bufferTemplate = null;
            foreach (Transform child in root)
            {
                var name = child.name;
                if ((name == "junc-left" || name == "junc-right") && !junctionTemplates.ContainsKey(name)
                    && Mathf.Abs(child.rotation.eulerAngles.x) < 0.01f && Mathf.Abs(child.rotation.eulerAngles.z) < 0.01f)
                    junctionTemplates[name] = child.gameObject;
                else if (name == "term-buffer" && bufferTemplate == null)
                    bufferTemplate = child.gameObject;
            }
            if (junctionTemplates.Count < 2 || bufferTemplate == null)
                throw new InvalidOperationException("stock junc-left, junc-right or term-buffer not found under [railway]");

            // Tracks take their rail and base types from the stock track they meet, else any stock track.
            style = root.GetComponentsInChildren<RailTrack>(true).First(t => t.generateMeshes && t.railType != null);

            foreach (var spec in layout.Tracks)
                tracks[spec.Name] = MakeTrack(spec);

            foreach (var spec in layout.Switches)
            {
                if (!junctionTemplates.TryGetValue(spec.Asset, out var template))
                    throw new InvalidOperationException($"switch {spec.Id}: unknown asset {spec.Asset}");
                switches.Add(new PlacedSwitch(spec, template, staging.transform, W(spec.JointV), WDir(spec.Forward), switches.Count));
            }

            foreach (var edit in layout.StockEdits)
                trims.Add(PrepareTrim(edit));

            foreach (var spec in layout.BufferStops)
            {
                var go = Object.Instantiate(bufferTemplate, staging.transform);
                go.name = $"term-buffer ({layout.Site})";
                // A stock buffer stop hides itself as it wakes and shows itself again once the
                // save's cars are in (unless a car stands in it). The template has already woken,
                // so its copy comes hidden, and would stay so: it never wakes to show itself.
                foreach (var stop in go.GetComponentsInChildren<BufferStop>(true))
                    stop.gameObject.SetActive(true);
                var outward = WDir(Vec.Heading(spec.Heading, 0));
                go.transform.SetPositionAndRotation(W(Vec.Of(spec.At)), Quaternion.LookRotation(-outward, Vector3.up));
                buffers.Add(go);
            }
        }

        private RailTrack MakeTrack(TrackSpec spec)
        {
            var points = spec.Points.Select(p => (W(Vec.Of(p.Position)), W(Vec.Of(p.Handle1)), W(Vec.Of(p.Handle2)))).ToList();
            return NewTrack(spec.Name, points);
        }

        private RailTrack NewTrack(string name, IList<(Vector3 Pos, Vector3 H1, Vector3 H2)> points)
        {
            if (points.Count < 2)
                throw new InvalidOperationException($"track {name} has {points.Count} points");
            var go = new GameObject(name);
            go.transform.SetParent(staging.transform, false);
            go.transform.position = points[0].Pos;
            var curve = go.AddComponent<BezierCurve>();
            curve.resolution = RailTrack.DEFAULT_BEZIER_RESOLUTION;
            foreach (var (pos, h1, h2) in points)
            {
                var bp = curve.AddPointAt(pos);
                bp.handleStyle = BezierPoint.HandleStyle.Broken;
                bp.globalHandle1 = h1;
                bp.globalHandle2 = h2;
            }
            var rt = go.AddComponent<RailTrack>();
            rt.generateMeshes = true;
            rt.generateColliders = true;
            rt.dontChange = false;
            rt.age = style.age;
            rt.railType = style.railType;
            rt.baseType = style.baseType;
            return rt;
        }

        // ------------------------------------------------------------ stock edits

        private sealed class PendingTrim
        {
            public RailTrack Stock;
            public PlacedSwitch Switch;
            public CurveCut CutAtThrough;   // where the kept stock part ends
            public RailTrack Stub;          // the stock part beyond the joint, rebuilt as our track
            public Vector3 Forward;         // joint toward through end, from the stock curve itself
            public DV.PointSet.EquiPointSet Original;   // the stock track's points before the trim
            public double OriginalSpan => Original?.span ?? 0;
        }

        private PendingTrim PrepareTrim(StockEdit edit)
        {
            var throughEnd = W(Vec.Of(edit.TrimEndTo));
            var sw = switches.OrderBy(s => (s.ThroughEndLayout - throughEnd).sqrMagnitude).First();
            if ((sw.ThroughEndLayout - throughEnd).magnitude > 1f)
                throw new InvalidOperationException($"stock edit on {edit.Track}: no switch whose through end is at the trim point");
            var stock = root.GetComponentsInChildren<RailTrack>(true)
                .Where(t => t.name == edit.Track && !Builder.IsOurs(t))
                .Select(t => (Track: t, Cut: CurveCut.Closest(t.curve, sw.JointWorld)))
                .OrderBy(c => c.Cut.Distance).FirstOrDefault();
            if (stock.Track == null || stock.Cut.Distance > 2f)
                throw new InvalidOperationException($"stock track {edit.Track} not found within 2 m of switch {sw.Spec.Id}'s joint");
            var curve = stock.Track.curve;
            var joint = stock.Cut;
            var original = stock.Track.GetKinkedPointSet();
            // Walk the switch length along the stock curve toward the through end.
            var ahead = CurveCut.At(curve, joint.Segment, Mathf.Min(1f, joint.T + 0.001f)) - CurveCut.At(curve, joint.Segment, Mathf.Max(0f, joint.T - 0.001f));
            var towardStart = Vector3.Dot(ahead, sw.ForwardWorld) < 0;
            if (!towardStart)
                throw new InvalidOperationException($"{edit.Track}: the switch's through end lies toward the track's end; only end trims are supported");
            var through = joint.Walk(curve, -SwitchLength);
            var forward = (through.Point - joint.Point).normalized;
            sw.PlaceExactly(joint.Point, forward);

            // The stub: the stock curve from the joint to the old end, its first handle along the switch.
            var stubPoints = CurveCut.SubCurve(curve, joint, CurveCut.End(curve));
            var first = stubPoints[0];
            var len1 = (first.H2 - first.Pos).magnitude;
            stubPoints[0] = (joint.Point, joint.Point + forward * len1, joint.Point - forward * len1);
            var stub = NewTrack(edit.Stub, stubPoints);
            tracks[edit.Stub] = stub;
            // Apply connects the joint, the through end and both stub ends to the stock track.
            sw.JointConnected = sw.ThroughConnected = true;
            trimStubs.Add(stub);
            Notes.Add($"{edit.Track}: switch {sw.Spec.Id} placed on the stock curve {joint.Distance:F3} m from the layout joint; " +
                      $"kept {through.ArcFromStart(curve):F1} m of {CurveCut.Length(curve):F1} m");
            return new PendingTrim { Stock = stock.Track, Switch = sw, CutAtThrough = through, Stub = stub, Forward = forward, Original = original };
        }

        // ------------------------------------------------------------------ apply

        /// <summary>Matches every new track end with its partner, changing nothing yet; throws if one has none.</summary>
        public void Plan() => plan = PlanConnections();

        /// <summary>Drops a layout that failed to prepare or plan: nothing of it was attached.</summary>
        public void Abandon()
        {
            if (staging != null)
                Object.DestroyImmediate(staging);
        }

        /// <summary>
        /// Changes the stock map and attaches the layout. If anything fails part way, the stock
        /// tracks and connections it touched are put back as they were, everything of the layout
        /// is destroyed at once (before the registry reads the tracks), and the failure goes up.
        /// Our tracks, junctions and tie-ins are published only once the whole layout is in.
        /// </summary>
        public void Apply()
        {
            if (plan == null)
                throw new InvalidOperationException("Apply before Plan");
            var snapshots = trims.Select(t => new StockSnapshot(t.Stock)).ToList();
            var attached = new List<GameObject>();
            var fault = Builder.FaultSite == layout.Site ? Builder.FaultPoint ?? "trim" : null;
            var done = false;
            try
            {
                // 1. Shorten the stock tracks and hand their old end connections to the stubs.
                foreach (var trim in trims)
                    ApplyTrim(trim);
                if (fault == "trim")
                    throw new InvalidOperationException("fault injected by the inspection after the stock trim");

                // 2. Connect everything new as planned, snapping our ends onto the partner.
                foreach (var (end, partner, gap) in plan)
                {
                    Snap(end, partner);
                    Link(end, partner);
                    if (gap > 0.02f)
                        Notes.Add($"{end.Name}: snapped {gap:F3} m onto {partner.Name}");
                }

                // 3. Attach: switches first, then tracks and buffer stops, as [railway]'s last children.
                foreach (var sw in switches)
                {
                    attached.Add(sw.Root);
                    sw.Root.transform.SetParent(root, true);
                }
                foreach (var t in tracks.Values)
                {
                    attached.Add(t.gameObject);
                    t.transform.SetParent(root, true);
                }
                foreach (var b in buffers)
                {
                    attached.Add(b);
                    b.transform.SetParent(root, true);
                }
                if (fault == "attach" || fault == "rollback")
                    throw new InvalidOperationException($"fault injected by the inspection after attaching ({fault})");

                // 4. Stock tracks recompute their points and colliders from the new curve.
                foreach (var trim in trims)
                    Refresh(trim.Stock);
                done = true;
            }
            finally
            {
                if (!done)
                {
                    // Each step on its own, so one failing cannot skip the rest.
                    foreach (var snapshot in snapshots)
                        Attempt("restoring a stock track", snapshot.Restore);
                    foreach (var trim in trims)
                        Attempt($"refreshing {trim.Stock.name}", () =>
                        {
                            if (fault == "rollback")
                                throw new InvalidOperationException("fault injected by the inspection while rolling back");
                            Refresh(trim.Stock);
                        });
                    foreach (var go in attached)
                        Attempt("removing an attached object", () => { if (go != null) Object.DestroyImmediate(go); });
                    Attempt("removing the staging", () => { if (staging != null) Object.DestroyImmediate(staging); });
                }
            }
            Object.Destroy(staging);

            // 5. Published: from here the rest of the mod treats these as ours.
            foreach (var sw in switches)
            {
                var id = $"{layout.Site}/{sw.Spec.Id}";
                Builder.Publish(id, sw.Junction);
                Builder.Publish(id + "/through", sw.Through);
                Builder.Publish(id + "/diverging", sw.Diverging);
            }
            foreach (var kv in tracks)
                Builder.Publish($"{layout.Site}/{kv.Key}", kv.Value);
            foreach (var trim in trims)
            {
                Builder.TrimmedStock[trim.Stock] = trim.Stock.GetKinkedPointSet()?.span ?? 0;
                Builder.TieIns.Add(new Builder.TieIn { Stock = trim.Stock, Original = trim.Original, Through = trim.Switch.Through, Stub = trim.Stub });
            }
        }

        private void Attempt(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Notes.Add($"rollback: {what} failed: {e.Message}");
                Main.Error($"{layout.Site} rollback: {what} failed: {e}");
            }
        }

        private static void Refresh(RailTrack stock)
        {
            ResetPointSet(stock);
            stock.DestroyProceduralColliders();
            stock.GetKinkedPointSet();
            if (stock.generateColliders)
                stock.CreateCollider();
        }

        /// <summary>A stock track's curve and the connections a trim changes, to put back on failure.</summary>
        private sealed class StockSnapshot
        {
            private readonly RailTrack track;
            private readonly List<(Vector3 Pos, Vector3 H1, Vector3 H2)> points = new List<(Vector3, Vector3, Vector3)>();
            private readonly Junction.Branch outBranch;
            private readonly Junction outJunction;
            private readonly Junction junction;
            private readonly Junction.Branch junctionIn;
            private readonly List<Junction.Branch> junctionOut;
            private readonly RailTrack partner;
            private readonly Junction.Branch partnerIn, partnerOut;

            public StockSnapshot(RailTrack stock)
            {
                track = stock;
                var c = stock.curve;
                for (var i = 0; i < c.pointCount; i++)
                    points.Add((c[i].position, c[i].globalHandle1, c[i].globalHandle2));
                outBranch = stock.outBranch;
                outJunction = stock.outJunction;
                junction = stock.outJunction;
                if (junction != null)
                {
                    junctionIn = junction.inBranch;
                    junctionOut = new List<Junction.Branch>(junction.outBranches);
                }
                partner = stock.outBranch?.track;
                if (partner != null)
                {
                    partnerIn = partner.inBranch;
                    partnerOut = partner.outBranch;
                }
            }

            public void Restore()
            {
                CurveCut.Rewrite(track.curve, points);
                track.outBranch = outBranch;
                track.outJunction = outJunction;
                if (junction != null)
                {
                    junction.inBranch = junctionIn;
                    junction.outBranches.Clear();
                    junction.outBranches.AddRange(junctionOut);
                }
                if (partner != null)
                {
                    partner.inBranch = partnerIn;
                    partner.outBranch = partnerOut;
                }
            }
        }

        private static readonly FieldInfo PointSetField = AccessTools.Field(typeof(RailTrack), "pointSet");

        private static void ResetPointSet(RailTrack t)
        {
            PointSetField.SetValue(t, null);
            RailTrack.pointSets.Remove(t);
        }

        private void ApplyTrim(PendingTrim trim)
        {
            var stock = trim.Stock;
            var curve = stock.curve;
            var kept = CurveCut.SubCurve(curve, CurveCut.Start(curve), trim.CutAtThrough);
            var last = kept[kept.Count - 1];
            var lenIn = (last.H1 - last.Pos).magnitude;
            var end = trim.Switch.ThroughEndWorld;
            kept[kept.Count - 1] = (end, end + trim.Forward * lenIn, end - trim.Forward * lenIn);
            CurveCut.Rewrite(curve, kept);

            // The old end connection now belongs to the stub's end.
            var stub = trim.Stub;
            if (stock.outJunction != null)
            {
                var j = stock.outJunction;
                if (j.inBranch != null && j.inBranch.track == stock && !j.inBranch.first)
                    j.inBranch = new Junction.Branch(stub, false);
                for (var i = 0; i < j.outBranches.Count; i++)
                    if (j.outBranches[i].track == stock && !j.outBranches[i].first)
                        j.outBranches[i] = new Junction.Branch(stub, false);
                stub.outJunction = j;
            }
            if (stock.outBranch != null && stock.outBranch.track != null)
            {
                var partner = stock.outBranch.track;
                if (partner.inBranch != null && partner.inBranch.track == stock && !partner.inBranch.first)
                    partner.inBranch = new Junction.Branch(stub, false);
                if (partner.outBranch != null && partner.outBranch.track == stock && !partner.outBranch.first)
                    partner.outBranch = new Junction.Branch(stub, false);
                stub.outBranch = stock.outBranch;
            }

            // The kept stock part now ends on the switch's through leg; the stub starts at its joint.
            stock.outJunction = null;
            stock.outBranch = new Junction.Branch(trim.Switch.Through, false);
            trim.Switch.Through.outBranch = new Junction.Branch(stock, false);
            trim.Switch.Through.outJunction = null;
            trim.Switch.Junction.inBranch = new Junction.Branch(stub, true);
            stub.inJunction = trim.Switch.Junction;
            Notes.Add($"{stock.name}: now ends on {trim.Switch.Spec.Id}'s through leg; {stub.name} carries its old end connection");
        }

        private sealed class Node
        {
            public string Name;
            public Vector3 Pos;
            public Vector3 Outward;          // direction leaving the node's owner
            public RailTrack Track;          // track end, or a switch leg track (its last point)
            public bool First;               // which end of Track
            public PlacedSwitch JointOf;     // a switch's joint
            public bool Used;
        }

        private List<(Node End, Node Partner, float Gap)> PlanConnections()
        {
            var result = new List<(Node, Node, float)>();
            var nodes = new List<Node>();
            foreach (var sw in switches)
            {
                if (!sw.JointConnected)
                    nodes.Add(new Node { Name = sw.Spec.Id + ":joint", Pos = sw.JointWorld, Outward = -sw.ForwardWorld, JointOf = sw });
                if (!sw.ThroughConnected)
                    nodes.Add(LegNode(sw.Spec.Id + ":through", sw.Through));
                nodes.Add(LegNode(sw.Spec.Id + ":diverging", sw.Diverging));
            }
            var ends = new List<Node>();
            foreach (var t in tracks.Values.Where(t => !trimStubs.Contains(t)))
            {
                if (!(t.inJunction != null || t.inBranch?.track != null))
                    ends.Add(EndNode(t, true));
                if (!(t.outJunction != null || t.outBranch?.track != null))
                    ends.Add(EndNode(t, false));
            }
            nodes.AddRange(ends);
            var bufferAt = buffers.Select(b => b.transform.position).ToList();

            foreach (var end in ends)
            {
                if (end.Used)
                    continue;
                var partner = nodes.Where(n => !n.Used && n != end && n.Track != end.Track)
                    .OrderBy(n => (n.Pos - end.Pos).sqrMagnitude).FirstOrDefault();
                if (partner == null || (partner.Pos - end.Pos).magnitude > SnapRange)
                {
                    if (bufferAt.Any(b => (b - end.Pos).magnitude < 1f))
                    {
                        end.Used = true;
                        Notes.Add($"{end.Name}: ends at a buffer stop");
                        continue;
                    }
                    throw new InvalidOperationException($"{end.Name} has no partner within {SnapRange} m " +
                                                        $"(nearest {(partner == null ? "none" : partner.Name + $" at {(partner.Pos - end.Pos).magnitude:F3} m")})");
                }
                result.Add((end, partner, (partner.Pos - end.Pos).magnitude));
                end.Used = partner.Used = true;
            }
            foreach (var n in nodes.Where(n => !n.Used && (n.JointOf != null || !tracks.ContainsValue(n.Track))))
                throw new InvalidOperationException($"switch leg {n.Name} is not connected");
            return result;
        }

        private static Node LegNode(string name, RailTrack leg)
        {
            var c = leg.curve;
            var last = c[c.pointCount - 1];
            return new Node { Name = name, Pos = last.position, Outward = (last.position - last.globalHandle1).normalized, Track = leg, First = false };
        }

        private static Node EndNode(RailTrack t, bool first)
        {
            var c = t.curve;
            var p = first ? c[0] : c[c.pointCount - 1];
            var outward = first ? (p.position - p.globalHandle2).normalized : (p.position - p.globalHandle1).normalized;
            return new Node { Name = $"{t.name}:{(first ? "start" : "end")}", Pos = p.position, Outward = outward, Track = t, First = first };
        }

        /// <summary>Moves our track end onto the partner node and lines its handles up with the partner's direction.</summary>
        private void Snap(Node end, Node partner)
        {
            var c = end.Track.curve;
            var p = end.First ? c[0] : c[c.pointCount - 1];
            var inward = partner.Outward; // our track continues the partner's outward direction
            var lin = end.First ? (p.globalHandle2 - p.position).magnitude : (p.globalHandle1 - p.position).magnitude;
            p.position = partner.Pos;
            if (end.First)
            {
                p.globalHandle2 = partner.Pos + inward * lin;
                p.globalHandle1 = partner.Pos - inward * lin;
            }
            else
            {
                p.globalHandle1 = partner.Pos + inward * lin;
                p.globalHandle2 = partner.Pos - inward * lin;
            }
        }

        private static void Link(Node end, Node partner)
        {
            var mine = new Junction.Branch(end.Track, end.First);
            if (partner.JointOf != null)
            {
                partner.JointOf.Junction.inBranch = mine;
                if (end.First) end.Track.inJunction = partner.JointOf.Junction;
                else end.Track.outJunction = partner.JointOf.Junction;
                partner.JointOf.JointConnected = true;
                return;
            }
            var theirs = new Junction.Branch(partner.Track, partner.First);
            if (partner.First) partner.Track.inBranch = mine; else partner.Track.outBranch = mine;
            if (end.First) end.Track.inBranch = theirs; else end.Track.outBranch = theirs;
        }
    }

    /// <summary>A stock switch asset cloned for the layout, its joint on the layout's joint.</summary>
    internal sealed class PlacedSwitch
    {
        public readonly SwitchSpec Spec;
        public readonly GameObject Root;
        public readonly Junction Junction;
        public readonly RailTrack Through;
        public readonly RailTrack Diverging;
        public Vector3 JointWorld;
        public Vector3 ForwardWorld;
        public bool JointConnected;
        public bool ThroughConnected;

        public PlacedSwitch(SwitchSpec spec, GameObject template, Transform parent, Vector3 jointWorld, Vector3 forwardWorld, int index)
        {
            Spec = spec;
            Root = Object.Instantiate(template, parent);
            Root.name = $"{template.name} ({spec.Id})";
            // A stock switch that has already started carries the sign hover its Start added.
            // The copy would find that hover first and fail on it, so remove it; the clone's
            // own Start adds a fresh one.
            foreach (var hover in Root.GetComponentsInChildren<DV.Signs.SignHover>(true))
                Object.DestroyImmediate(hover);
            // Its lever is a stock button that has woken too: take out what it added to itself
            // (as for the offices' copies), so the copy's lever sets itself up afresh and clicks.
            Offices.ResetControls(Root);
            Junction = Root.GetComponentInChildren<Junction>(true);
            foreach (var t in Root.GetComponentsInChildren<RailTrack>(true))
            {
                if (t.name == "[track through]") Through = t;
                else if (t.name == "[track diverging]") Diverging = t;
            }
            if (Junction == null || Through == null || Diverging == null)
                throw new InvalidOperationException($"switch {spec.Id}: the cloned asset lacks its junction or legs");
            // The clone still points at the stock tracks the original met; clear those links.
            Junction.inBranch = null;
            Through.outBranch = null;
            Through.outJunction = null;
            Diverging.outBranch = null;
            Diverging.outJunction = null;
            Junction.selectedBranch = 0;
            var data = Junction.junctionData;
            data.junctionId = 9000 + index;
            data.junctionIdLong = $"W-{9000 + index}";
            data.excludeFromJunctionMap = true;
            data.linkedJunctions = new List<int>();
            Junction.junctionData = data;
            if (spec.Stand == "outside")
                SwitchStands.MoveOutside(this);
            PlaceExactly(jointWorld, forwardWorld);
        }

        public Vector3 ThroughEndLayout => JointWorld + ForwardWorld * 24f;

        public Vector3 ThroughEndWorld
        {
            get
            {
                var c = Through.curve;
                return c[c.pointCount - 1].position;
            }
        }

        public void PlaceExactly(Vector3 jointWorld, Vector3 forwardWorld)
        {
            JointWorld = jointWorld;
            ForwardWorld = forwardWorld;
            Root.transform.rotation = Quaternion.LookRotation(forwardWorld, Vector3.up);
            Root.transform.position += jointWorld - Junction.transform.position;
        }
    }
}
