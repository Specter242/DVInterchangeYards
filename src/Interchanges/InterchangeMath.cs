using System;

namespace Interchanges
{
    /// <summary>
    /// Engine-free geometry, terrain, save-mapping, and distant-map math helpers shared between
    /// the mod assembly and the standalone unit tests.
    /// </summary>
    internal static class InterchangeMath
    {
        /// <summary>
        /// Whether a track name is the stock track a layout edits, or a piece of it another mod
        /// split it into (appending 'A' or 'B' per split, as Double Tracked Valley does).
        /// </summary>
        public static bool IsPieceOf(string name, string stock)
        {
            if (name == null || stock == null || !name.StartsWith(stock, StringComparison.Ordinal))
                return false;
            for (var i = stock.Length; i < name.Length; i++)
            {
                var ch = name[i];
                if (ch != 'A' && ch != 'B')
                    return false;
            }
            return true;
        }

        /// <summary>Maps a bogie span on a tie-in's stub or through leg back to its span on the original stock track.</summary>
        public static bool TieInToStock(bool isStub, bool isThrough, double jointSpan, double originalSpan, double span, out double stockSpan)
        {
            stockSpan = 0;
            if (isStub) stockSpan = jointSpan + span;
            else if (isThrough) stockSpan = jointSpan - span;
            else return false;
            stockSpan = Math.Max(0, Math.Min(originalSpan, stockSpan));
            return true;
        }

        /// <summary>Maps a span on the original stock track beyond the kept part onto a tie-in's stub or through leg.</summary>
        public static bool TieInFromStock(double stockSpan, double jointSpan, double throughSpan, double stubSpan, out bool onStub, out bool onThrough, out double span)
        {
            onStub = false;
            onThrough = false;
            span = 0;
            if (stockSpan >= jointSpan)
            {
                onStub = true;
                span = Math.Min(stockSpan - jointSpan, stubSpan);
                return true;
            }
            if (stockSpan > jointSpan - throughSpan)
            {
                onThrough = true;
                span = jointSpan - stockSpan;
                return true;
            }
            return false;
        }

        /// <summary>Target terrain height for a sample at horizontal distance <paramref name="dist"/> from an earthwork centreline.</summary>
        public static float TerrainTargetHeight(float orig, float formation, float dist, float halfWidth, float cut, float fill)
        {
            var beyond = dist - halfWidth;
            if (beyond <= 0f)
                return formation;
            if (orig > formation)
                return cut > 1e-4f && !float.IsInfinity(cut) ? Math.Min(orig, formation + beyond / cut) : orig;
            return fill > 1e-4f && !float.IsInfinity(fill) ? Math.Max(orig, formation - beyond / fill) : orig;
        }

        /// <summary>Station-ground splat weight at horizontal distance <paramref name="dist"/> from a painted earthwork centreline.</summary>
        public static float TerrainPaintWeight(float dist, float halfWidth, float inner, float outer)
        {
            var beyond = dist - halfWidth;
            if (beyond <= inner)
                return 1f;
            var t = 1f - (beyond - inner) / (outer - inner);
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }

        /// <summary>Collision-free 64-bit key for signed 32-bit 2D integer coordinates.</summary>
        public static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;

        /// <summary>Blends a distant-map byte channel toward <paramref name="to"/> by <paramref name="share"/> in [0, 1].</summary>
        public static byte DistantMix(byte from, float to, float share)
        {
            var t = share < 0f ? 0f : share > 1f ? 1f : share;
            var value = from + (to - from) * t;
            var rounded = (int)Math.Round(value);
            return (byte)(rounded < 0 ? 0 : rounded > 255 ? 255 : rounded);
        }

        /// <summary>Number of set bits in a 32-bit bitmask.</summary>
        public static int PopCount(int bits)
        {
            var n = 0;
            for (; bits != 0; bits &= bits - 1)
                n++;
            return n;
        }

        /// <summary>2D Euclidean distance from point (px, py) to segment [(ax, ay), (bx, by)].</summary>
        public static float PointSegmentDistance2D(float px, float py, float ax, float ay, float bx, float by)
        {
            var abx = bx - ax;
            var aby = by - ay;
            var sqrLen = abx * abx + aby * aby;
            float t;
            if (sqrLen < 1e-6f)
            {
                t = 0f;
            }
            else
            {
                t = ((px - ax) * abx + (py - ay) * aby) / sqrLen;
                if (t < 0f) t = 0f;
                else if (t > 1f) t = 1f;
            }
            var dx = px - (ax + abx * t);
            var dy = py - (ay + aby * t);
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
