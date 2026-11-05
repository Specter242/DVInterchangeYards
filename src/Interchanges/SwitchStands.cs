using System;
using System.Collections;
using System.Linq;
using UnityEngine;

namespace Interchanges
{
    /// <summary>
    /// Switch stands on the outside of a yard ladder (owner, 2026-11-02: "A lot of these switches
    /// are encroaching on adjacent tracks"; "hard code them"). The stock switch has its stand 2.57 m
    /// out on the diverging side; on a ladder that side holds the next yard track. A switch whose
    /// layout says <c>"stand": "outside"</c> has its stand moved once, as it is built, to the other
    /// side of its track: a rigid half turn about the vertical through the stand's middle, on the
    /// track's centre line. Nothing is mirrored and nothing is recomputed afterwards.
    ///
    /// The sign goes where the half turn puts it but is not turned: it faces along the track the
    /// way the stock switch's sign does (a Nexus bug report, 2026-11-04, "Interchange Switch Signs
    /// Backwards": turned with the stand, the ladder switches' signs faced the other way; owner,
    /// 2026-11-05: "Looks good. Continue").
    ///
    /// The stock animator (on the switch) sets the stand's parts by their path under Graphical.
    /// The moved parts go under copies of that path with animators of their own, on the same
    /// controller, played as the stock VisualSwitch plays its own: when the switch is thrown,
    /// then switched off after 2 s. From the other side of the track the linkage (lever, link,
    /// rod, bar) has to pull where it pushed, so it plays the other way; the sign plays as stock.
    /// The switch keeps its own VisualSwitch and button (the lever), which moves with the stand.
    /// </summary>
    internal static class SwitchStands
    {
        private static readonly string[] Linkage = { "switch_base", "switch_lever", "link", "rod", "bar" };
        private static readonly string[] Sign = { "switch_sign" };

        public static void MoveOutside(PlacedSwitch sw)
        {
            var root = sw.Root.transform;
            var graphical = root.Find("Graphical");
            var stock = sw.Root.GetComponent<Animator>();
            var visual = sw.Root.GetComponentInChildren<VisualSwitch>(true);
            var parts = Linkage.Concat(Sign).Select(n => graphical?.Find(n)).ToList();
            if (graphical == null || stock == null || visual == null || parts.Any(p => p == null))
                throw new InvalidOperationException($"switch {sw.Spec.Id}: its stand's parts or animator are not where the stock switch has them");
            // The turn's axis: the track's centre line, level with the stand's middle.
            var middle = parts.Average(p => root.InverseTransformPoint(p.position).z);
            Unit(root, graphical, stock, "[stand linkage]", Linkage, middle, sw.Junction, visual.invertDirection, reverse: true, turn: true);
            Unit(root, graphical, stock, "[stand sign]", Sign, middle, sw.Junction, visual.invertDirection, reverse: false, turn: false);
            var trigger = root.Find("SwitchTrigger");
            if (trigger != null)
            {
                var p = trigger.localPosition;
                trigger.localPosition = new Vector3(-p.x, p.y, 2f * middle - p.z);
                trigger.localRotation = Quaternion.Euler(0f, 180f, 0f) * trigger.localRotation;
            }
        }

        /// <summary>
        /// A holder with a "Graphical" under it, so each part keeps its stock local pose and path
        /// (the stock animator's). Turned: the holder is half-turned at (0, 0, middle) and its
        /// Graphical stands where the stock one stands relative to the turn. Not turned (the
        /// sign): the holder is not turned, and its Graphical is moved so the first part lands
        /// where the half turn would put it, facing as stock.
        /// </summary>
        private static void Unit(Transform root, Transform graphical, Animator stock, string name, string[] names, float middle,
            Junction junction, bool invertDirection, bool reverse, bool turn)
        {
            var unit = new GameObject(name).transform;
            unit.SetParent(root, false);
            var g = new GameObject("Graphical").transform;
            g.SetParent(unit, false);
            g.localRotation = graphical.localRotation;
            g.localScale = graphical.localScale;
            if (turn)
            {
                unit.localPosition = new Vector3(0f, 0f, middle);
                unit.localRotation = Quaternion.Euler(0f, 180f, 0f);
                g.localPosition = graphical.localPosition - new Vector3(0f, 0f, middle);
            }
            else
            {
                var pivot = root.InverseTransformPoint(graphical.Find(names[0]).position);
                var turned = new Vector3(-pivot.x, pivot.y, 2f * middle - pivot.z);
                unit.localPosition = Vector3.zero;
                unit.localRotation = Quaternion.identity;
                g.localPosition = graphical.localPosition + (turned - pivot);
            }
            foreach (var n in names)
                graphical.Find(n).SetParent(g, false);
            var animator = unit.gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = stock.runtimeAnimatorController;
            animator.cullingMode = stock.cullingMode;
            animator.updateMode = stock.updateMode;
            animator.applyRootMotion = false;
            var play = unit.gameObject.AddComponent<StandAnimation>();
            play.Junction = junction;
            play.Animator = animator;
            play.Invert = invertDirection != reverse;
        }
    }

    /// <summary>Plays a moved stand's animator as the stock VisualSwitch plays the switch's: when it is thrown.</summary>
    internal sealed class StandAnimation : MonoBehaviour
    {
        private const float Speed = 2f;   // VisualSwitch.speedMult
        public Junction Junction;
        public Animator Animator;
        public bool Invert;
        private Coroutine off;

        private void Start()
        {
            if (Junction == null || Animator == null)
                return;
            Junction.Switched += OnSwitched;
            Play();
        }

        private void OnDestroy()
        {
            if (Junction != null)
                Junction.Switched -= OnSwitched;
        }

        private void OnSwitched(Junction.SwitchMode mode, int branch) => Play();

        private void Play()
        {
            Animator.enabled = true;
            if (off != null)
                StopCoroutine(off);
            off = StartCoroutine(Off());
            var t = Animator.GetCurrentAnimatorStateInfo(0).normalizedTime;
            if (Invert ? Junction.selectedBranch == 1 : Junction.selectedBranch == 0)
            {
                Animator.SetFloat("speed", Speed);
                if (t < 0f)
                    Animator.Play("junction", 0, 0f);
            }
            else
            {
                Animator.SetFloat("speed", -Speed);
                if (t > 1f)
                    Animator.Play("junction", 0, 1f);
            }
        }

        private IEnumerator Off()
        {
            yield return new WaitForSeconds(2f);
            Animator.enabled = false;
            off = null;
        }
    }
}
