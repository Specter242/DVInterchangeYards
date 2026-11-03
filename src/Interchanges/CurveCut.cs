using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Interchanges
{
    /// <summary>
    /// A location on a BezierCurve (segment and parameter), with closest-point
    /// search, walking by arc length, and exact cutting (de Casteljau), all in
    /// world coordinates.
    /// </summary>
    internal struct CurveCut
    {
        public int Segment;
        public float T;
        public Vector3 Point;
        public float Distance;

        public float GlobalT(BezierCurve c) => (Segment + T) / Math.Max(1, c.pointCount - 1);

        public static CurveCut Start(BezierCurve c) => new CurveCut { Segment = 0, T = 0, Point = c[0].position };

        public static CurveCut End(BezierCurve c) =>
            new CurveCut { Segment = c.pointCount - 2, T = 1, Point = c[c.pointCount - 1].position };

        private static void Controls(BezierCurve c, int s, out Vector3 a, out Vector3 b, out Vector3 d, out Vector3 e)
        {
            a = c[s].position;
            b = c[s].globalHandle2;
            d = c[s + 1].globalHandle1;
            e = c[s + 1].position;
        }

        private static Vector3 Eval(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            var u = 1 - t;
            return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        }

        public static Vector3 At(BezierCurve c, int s, float t)
        {
            Controls(c, s, out var a, out var b, out var d, out var e);
            return Eval(a, b, d, e, t);
        }

        /// <summary>The closest point of the curve to <paramref name="target"/>: a coarse scan, then a ternary refinement.</summary>
        public static CurveCut Closest(BezierCurve c, Vector3 target)
        {
            var best = new CurveCut { Distance = float.MaxValue };
            const int n = 400;
            for (var s = 0; s < c.pointCount - 1; s++)
                for (var i = 0; i <= n; i++)
                {
                    var t = (float)i / n;
                    var p = At(c, s, t);
                    var d = (p - target).magnitude;
                    if (d < best.Distance)
                        best = new CurveCut { Segment = s, T = t, Point = p, Distance = d };
                }
            float lo = Math.Max(0, best.T - 1f / n), hi = Math.Min(1, best.T + 1f / n);
            for (var k = 0; k < 60; k++)
            {
                float m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
                if ((At(c, best.Segment, m1) - target).magnitude < (At(c, best.Segment, m2) - target).magnitude) hi = m2;
                else lo = m1;
            }
            best.T = (lo + hi) / 2;
            best.Point = At(c, best.Segment, best.T);
            best.Distance = (best.Point - target).magnitude;
            return best;
        }

        /// <summary>The point <paramref name="metres"/> of arc away (negative toward the start), in small steps.</summary>
        public CurveCut Walk(BezierCurve c, float metres)
        {
            const float dt = 0.0005f;
            int s = Segment;
            float t = T, remaining = Math.Abs(metres), dir = Math.Sign(metres);
            var prev = At(c, s, t);
            while (remaining > 0)
            {
                t += dir * dt;
                if (t > 1) { if (s >= c.pointCount - 2) throw new InvalidOperationException("walked past the curve's end"); s++; t -= 1; }
                if (t < 0) { if (s <= 0) throw new InvalidOperationException("walked past the curve's start"); s--; t += 1; }
                var p = At(c, s, t);
                var step = (p - prev).magnitude;
                if (step >= remaining)
                {
                    var f = remaining / step;
                    t -= dir * dt * (1 - f);
                    p = At(c, s, t);
                    remaining = 0;
                }
                else
                {
                    remaining -= step;
                }
                prev = p;
            }
            return new CurveCut { Segment = s, T = t, Point = At(c, s, t) };
        }

        public float ArcFromStart(BezierCurve c)
        {
            float len = 0;
            var prev = c[0].position;
            for (var s = 0; s <= Segment; s++)
            {
                var tEnd = s == Segment ? T : 1f;
                for (var t = 0.002f; t <= tEnd + 1e-6f; t += 0.002f)
                {
                    var p = At(c, s, Math.Min(t, tEnd));
                    len += (p - prev).magnitude;
                    prev = p;
                }
            }
            return len;
        }

        public static float Length(BezierCurve c) => End(c).ArcFromStart(c);

        /// <summary>
        /// The curve between two cuts (a before b) as Bezier anchors (position,
        /// handle1, handle2), exact by de Casteljau; outer handles mirrored.
        /// </summary>
        public static List<(Vector3 Pos, Vector3 H1, Vector3 H2)> SubCurve(BezierCurve c, CurveCut a, CurveCut b)
        {
            if (a.Segment > b.Segment || a.Segment == b.Segment && a.T > b.T)
                throw new ArgumentException("cut a must come before cut b");
            var pts = new List<(Vector3 Pos, Vector3 H1, Vector3 H2)>();
            for (var s = a.Segment; s <= b.Segment; s++)
            {
                Controls(c, s, out var p0, out var p1, out var p2, out var p3);
                float t0 = s == a.Segment ? a.T : 0f, t1 = s == b.Segment ? b.T : 1f;
                Split(p0, p1, p2, p3, t0, t1, out var q0, out var q1, out var q2, out var q3);
                if (pts.Count == 0)
                    pts.Add((q0, q0 - (q1 - q0), q1));
                else
                {
                    var last = pts[pts.Count - 1];
                    pts[pts.Count - 1] = (last.Pos, last.H1, q1);
                }
                pts.Add((q3, q2, q3 + (q3 - q2)));
            }
            return pts;
        }

        /// <summary>The piece of a cubic between t0 and t1, as its four control points.</summary>
        private static void Split(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t0, float t1,
            out Vector3 q0, out Vector3 q1, out Vector3 q2, out Vector3 q3)
        {
            // The right part from t0 ...
            DeCasteljau(p0, p1, p2, p3, t0, out _, out _, out _, out var r0, out var r1, out var r2, out var r3);
            var t = t0 >= 1f ? 0f : (t1 - t0) / (1f - t0);
            // ... then the left part of that up to t.
            DeCasteljau(r0, r1, r2, r3, t, out q0, out q1, out q2, out q3, out _, out _, out _);
        }

        /// <summary>Splits a cubic at t into its left (l0..l3) and right (r0..r3) halves; l3 equals r0.</summary>
        private static void DeCasteljau(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t,
            out Vector3 l0, out Vector3 l1, out Vector3 l2, out Vector3 l3, out Vector3 r1, out Vector3 r2, out Vector3 r3)
        {
            var ab = Vector3.Lerp(a, b, t);
            var bc = Vector3.Lerp(b, c, t);
            var cd = Vector3.Lerp(c, d, t);
            var abc = Vector3.Lerp(ab, bc, t);
            var bcd = Vector3.Lerp(bc, cd, t);
            var abcd = Vector3.Lerp(abc, bcd, t);
            l0 = a; l1 = ab; l2 = abc; l3 = abcd;
            r1 = bcd; r2 = cd; r3 = d;
        }

        /// <summary>Replaces a curve's points with new anchors, keeping the first points' objects.</summary>
        public static void Rewrite(BezierCurve c, IList<(Vector3 Pos, Vector3 H1, Vector3 H2)> anchors)
        {
            while (c.pointCount > anchors.Count)
            {
                var p = c[c.pointCount - 1];
                c.RemovePoint(p);
                Object.DestroyImmediate(p.gameObject);
            }
            while (c.pointCount < anchors.Count)
                c.AddPointAt(anchors[c.pointCount].Pos);
            for (var i = 0; i < anchors.Count; i++)
            {
                var bp = c[i];
                bp.handleStyle = BezierPoint.HandleStyle.Broken;
                bp.position = anchors[i].Pos;
                bp.globalHandle1 = anchors[i].H1;
                bp.globalHandle2 = anchors[i].H2;
            }
            c.SetDirty();
        }
    }
}
