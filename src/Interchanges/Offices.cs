using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Interchanges
{
#pragma warning disable CS0649
    /// <summary>An office beside a yard, cloned from a stock station office (tools/export_layout.py, office()).</summary>
    internal sealed class OfficeSpec
    {
        [JsonProperty("name")] public string Name;           // the interchange's name, e.g. "MF West"
        [JsonProperty("source")] public string Source;       // the stock office under Offices/Offices_w3
        [JsonProperty("position")] public float[] Position;  // the office's origin, map frame
        [JsonProperty("yaw")] public float Yaw;              // degrees about +y; the porch faces local -x
        [JsonProperty("floor")] public float Floor;          // the pad's level, under the floor
        [JsonProperty("mapMarker")] public float[] MapMarker; // its fast travel marker on the maps (x, z), clear of the stock markers
    }
#pragma warning restore CS0649

    /// <summary>
    /// An office at each interchange: a copy of a stock station office with its career
    /// manager kiosk, job validator, booklet table, job bin, sign and beacon, standing on
    /// the yard's pad (TerrainEdits shapes the pad; Extras masks its vegetation).
    ///
    /// The stock offices are in the game content scene, which loads after the railway, so
    /// the interchanges are built (Builder.Built) by then. Each office is copied as the
    /// scene loads, before the game moves the scene's objects under the origin shift, so it
    /// moves with the stock offices and is placed in the same frame they are: the map frame.
    /// The copy is made inside an inactive holder and trimmed there, so nothing in it wakes
    /// before it stands in place.
    ///
    /// What is left out of the copy: the loose items on the desks (world items the game's
    /// storage keeps track of), and the stock vegetation mask (the mod masks the pad itself,
    /// in Vegetation Studio's own frame). The copy is not a station of its own; its kiosk and
    /// validator are the stock components, which serve any station's jobs. The contract mod
    /// finds each office by name: "[interchange office] MF West" under Offices/Offices_w3.
    /// </summary>
    internal static class Offices
    {
        public const string Prefix = "[interchange office] ";
        public static readonly List<(OfficeSpec Spec, GameObject Office)> Built = new List<(OfficeSpec, GameObject)>();
        public static readonly List<string> Notes = new List<string>();

        private static readonly string[] LeftOut = { "ItemsOffice_", "[vegmask]" };

        public static void Install()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            GameObject content;
            try
            {
                content = scene.GetRootGameObjects().FirstOrDefault(g => g.name == "[origin shift content]");
            }
            catch (Exception)
            {
                return;
            }
            var stock = content != null ? content.transform.Find("Offices/Offices_w3") : null;
            if (stock == null)
                return;
            Built.Clear();
            Notes.Clear();
            foreach (var layout in Main.Layouts.Where(Builder.Built.Contains))
                foreach (var spec in layout.Offices)
                {
                    try
                    {
                        Build(layout, spec, stock, content.transform);
                    }
                    catch (Exception e)
                    {
                        Notes.Add($"office {spec.Name}: failed: {e.Message}");
                        Main.Error($"office {spec.Name}: {e}");
                    }
                }
            Main.Log($"offices: {Built.Count} built" + (Notes.Count > 0 ? "; " + string.Join("; ", Notes) : ""));
            // The interchanges' codes on every office's wall map, the copies' too.
            MapNames.Content = content.transform;
            try
            {
                MapNames.AddToWalls(content.transform);
            }
            catch (Exception e)
            {
                Main.Error($"wall maps: {e}");
            }
        }

        private static void Build(Layout layout, OfficeSpec spec, Transform stock, Transform mapFrame)
        {
            var source = stock.Find(spec.Source);
            if (source == null)
            {
                Notes.Add($"office {spec.Name}: no stock office {spec.Source}; not built");
                return;
            }
            var holder = new GameObject("[interchange office staging]");
            holder.SetActive(false);
            try
            {
                var office = Object.Instantiate(source.gameObject, holder.transform, false);
                office.name = Prefix + spec.Name;
                var removed = new List<string>();
                foreach (Transform child in office.transform.Cast<Transform>().ToList())
                    if (LeftOut.Any(p => child.name.StartsWith(p, StringComparison.Ordinal)))
                    {
                        removed.Add(child.name);
                        Object.DestroyImmediate(child.gameObject);
                    }
                var reset = ResetControls(office);
                var foreign = ResetOtherMods(office);
                var marker = MapMarks.AddFastTravel(layout, spec, office);
                office.transform.SetPositionAndRotation(mapFrame.TransformPoint(Vec.Of(spec.Position)),
                                                        mapFrame.rotation * Quaternion.Euler(0f, spec.Yaw, 0f));
                if (spec.MapMarker != null && spec.MapMarker.Length == 2)
                    marker.position = mapFrame.TransformPoint(new Vector3(spec.MapMarker[0], spec.Position[1], spec.MapMarker[1]));
                // Into place beside the stock offices: it wakes here.
                office.transform.SetParent(stock, true);
                Built.Add((spec, office));
                Notes.Add($"office {spec.Name}: copy of {spec.Source}, without {string.Join(", ", removed)}; " +
                          $"{reset} control part(s) reset" + (foreign.Count > 0 ? $"; other mods' kiosk parts taken out: {string.Join(", ", foreign)}" : ""));
            }
            finally
            {
                Object.DestroyImmediate(holder);
            }
        }

        /// <summary>
        /// Takes out of a copied office what its parts added to themselves when the stock office
        /// woke: each control's implementation (ButtonNonVR for the kiosk's buttons), the grab
        /// handler that implementation adds, the static interaction area's detector, a button's
        /// runtime rigidbody and joint, and the I2 localizer each of DV's Localize adds. Each control then sets itself up afresh when the
        /// copy wakes, as the stock one did. Left in, the copy's controls fail to add their
        /// implementation ("Can't add 'ButtonNonVR'... already added"), and the pointer finds the
        /// copied grab handler first, which nothing listens to: the kiosk's buttons do nothing.
        ///
        /// In VR a button's implementation (ButtonVRTK) adds other parts: the VR interactable, the
        /// tele-grab target, and, unless the button has a touch behaviour of its own, a touch
        /// behaviour with a helper object beside the button for its direction. Left in, the copy's
        /// button adds a second interactable and tele-grab target behind the copied ones; a hand
        /// or the tele-grab finds the copied ones first, which nothing listens to (a Nexus comment,
        /// audit of 2026-11-12: the interchange kiosks did not answer to touch in VR). A switch's lever is
        /// such a button too (VisualSwitch), and its copy goes through here.
        /// </summary>
        internal static int ResetControls(GameObject office)
        {
            var parts = new List<Component>();
            var helpers = new List<GameObject>();
            foreach (var spec in office.GetComponentsInChildren<DV.CabControls.Spec.ControlSpec>(true))
            {
                var go = spec.gameObject;
                parts.AddRange(go.GetComponents<DV.CabControls.ControlImplBase>());
                parts.AddRange(go.GetComponents<DV.Interaction.AGrabHandler>());
                parts.AddRange(go.GetComponents<MonoBehaviour>().Where(IsAreaDetector));
                foreach (var part in go.GetComponents<MonoBehaviour>())
                {
                    var kind = part != null ? part.GetType().FullName : null;
                    if (VrButtonParts.Contains(kind))
                        parts.Add(part);
                    else if (kind == VrTouch && MadeTouchHelper(part) is Transform helper)
                    {
                        parts.Add(part);
                        helpers.Add(helper.gameObject);
                    }
                }
                if (spec is DV.CabControls.Spec.Button button)
                {
                    if (button.useJoints)
                        parts.AddRange(go.GetComponents<ConfigurableJoint>());
                    if (button.createRigidbody)
                        parts.AddRange(go.GetComponents<Rigidbody>());
                }
            }
            // DV's Localize adds I2's localizer (and its parameters) to its object as it wakes.
            foreach (var localize in office.GetComponentsInChildren<DV.Localization.Localize>(true))
            {
                parts.AddRange(localize.GetComponents<I2.Loc.Localize>());
                parts.AddRange(localize.GetComponents<I2.Loc.LocalizationParamsManager>());
            }
            // The touch helpers before their behaviours, which would otherwise destroy them late.
            foreach (var helper in helpers.Distinct())
                Object.DestroyImmediate(helper);
            // Implementations first: nothing requires them, and they hold the handlers. The VR
            // interactable last, after the parts that work through it.
            foreach (var part in parts.Distinct().OrderBy(p => p is DV.CabControls.ControlImplBase ? 0 : p is Joint ? 1
                                                                : p.GetType().FullName == VrInteractable ? 3 : 2))
                Object.DestroyImmediate(part);
            return parts.Count + helpers.Count;
        }

        /// <summary>The VR interactable every VR control adds to itself as it wakes (the game's type, by name: this mod does not reference VRTK).</summary>
        internal const string VrInteractable = "DV.CabControls.VRTK.VRTK_ControlImplBaseInteractableObject";

        /// <summary>What a VR button (ButtonVRTK) always adds to itself as it wakes: the interactable and the tele-grab target.</summary>
        internal static readonly string[] VrButtonParts = { VrInteractable, "DV.VR.TelegrabbableButton" };

        /// <summary>The touch behaviour a VR button makes for itself when it has none of its own (SpeedZoneControlTouchBehaviour.Setup).</summary>
        internal const string VrTouch = "DV.CabControls.SpeedZoneControlTouchBehaviour";

        /// <summary>The helper object that touch behaviour takes its direction from, made beside the button.</summary>
        internal const string VrTouchHelper = "[interaction direction]";

        /// <summary>
        /// The helper of a touch behaviour the button made for itself, or null for a behaviour the
        /// office came with, which stays (the button uses the one it finds). The made one is
        /// known by its helper: an empty object of that name beside the button.
        /// </summary>
        private static Transform MadeTouchHelper(MonoBehaviour touch)
        {
            var direction = touch.GetType().GetField("direction")?.GetValue(touch) as Transform;
            return direction != null && direction.name == VrTouchHelper && direction.parent == touch.transform.parent
                   && direction.childCount == 0 && direction.GetComponents<Component>().Length == 1
                ? direction : null;
        }

        /// <summary>The components other mods add to a kiosk as it wakes, by type name (this mod references neither).</summary>
        private static readonly string[] OtherModsParts = { "CareerManagerAPI.CareerManagerScreenTracker" };

        /// <summary>Railroad Contracts' kiosk screens, on an object of their own that goes with them.</summary>
        private static readonly string[] OtherModsScreens = { "Contracts.Mod.Kiosk.ContractsScreen", "Contracts.Mod.Kiosk.ContractScreen" };

        /// <summary>
        /// Takes out of a copied office what other mods attached to the stock office's kiosk when it
        /// woke; each attaches its own to the copy as the copy wakes. Left in, Career Manager API's
        /// copied tracker has no screen: its Start throws, and the kiosk's input goes to it first and
        /// throws on every press (a Discord report, 2026-11-03: "null exception using the career
        /// managers in the new terminals"). Railroad Contracts' copied screens would be left unset.
        /// Returns what it took out.
        /// </summary>
        internal static List<string> ResetOtherMods(GameObject office)
        {
            var found = office.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(m => m != null && (OtherModsParts.Contains(m.GetType().FullName) || OtherModsScreens.Contains(m.GetType().FullName)))
                .ToList();
            var taken = found.Select(m => m.GetType().Name).ToList();
            var screens = found.Where(m => OtherModsScreens.Contains(m.GetType().FullName)).Select(m => m.gameObject).Distinct().ToList();
            foreach (var part in found.Where(m => OtherModsParts.Contains(m.GetType().FullName)))
                Object.DestroyImmediate(part);
            foreach (var go in screens)
                Object.DestroyImmediate(go);
            return taken;
        }

        // StaticInteractionArea's private GOActiveDetector, added to the control at runtime.
        private static bool IsAreaDetector(MonoBehaviour m) =>
            m != null && m.GetType().Name == "GOActiveDetector" &&
            m.GetType().DeclaringType == typeof(DV.Interaction.StaticInteractionArea);

        /// <summary>The office of an interchange, if it was built this load.</summary>
        public static GameObject Of(string name) =>
            Built.FirstOrDefault(b => b.Spec.Name == name && b.Office != null).Office;
    }
}
