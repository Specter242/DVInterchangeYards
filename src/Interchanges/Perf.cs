using System;
using System.Diagnostics;
#if DEVTOOLS
using System.Collections.Generic;
using System.Linq;
#endif

namespace Interchanges
{
    /// <summary>
    /// Times the mod's work in the game (Sayori, 2026-11-19: two-second freezes in single
    /// player with only this mod on). A call slower than <see cref="SlowMs"/> is logged as it
    /// happens, in every build, so a player's log shows it. Development builds also count each
    /// kind of work and keep the work done in each frame, for the inspector's drive; a release
    /// keeps nothing.
    /// </summary>
    internal static class Perf
    {
        public const double SlowMs = 100;

        public static long Start() => Stopwatch.GetTimestamp();

        public static double Stop(string what, long started, string detail = null)
        {
            var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
#if DEVTOOLS
            Count(what, ms, detail);
#endif
            if (ms >= SlowMs)
                Main.Warn(FormattableString.Invariant($"slow: {what} took {ms:F0} ms") + (detail != null ? $" ({detail})" : ""));
            return ms;
        }

#if DEVTOOLS
        private sealed class Entry
        {
            public int Count;
            public double TotalMs, MaxMs;
            public string MaxDetail;
        }

        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();

        /// <summary>The work timed since the inspector last took it: total and the calls of 5 ms or more.</summary>
        private static double frameMs;
        private static readonly List<string> frameCalls = new List<string>();

        private static void Count(string what, double ms, string detail)
        {
            if (!entries.TryGetValue(what, out var e))
                entries[what] = e = new Entry();
            e.Count++;
            e.TotalMs += ms;
            if (ms > e.MaxMs)
            {
                e.MaxMs = ms;
                e.MaxDetail = detail;
            }
            frameMs += ms;
            if (ms >= 5)
                frameCalls.Add(FormattableString.Invariant($"{what} {ms:F0} ms") + (detail != null ? $" ({detail})" : ""));
        }

        /// <summary>The work done since the last call, for one frame's line, and starts the next.</summary>
        public static (double Ms, string Calls) TakeFrame()
        {
            var taken = (frameMs, frameCalls.Count > 0 ? string.Join("; ", frameCalls) : null);
            frameMs = 0;
            frameCalls.Clear();
            return taken;
        }

        /// <summary>Each kind of work: how often, how long in all, the longest and where.</summary>
        public static IEnumerable<string> Report() =>
            entries.OrderByDescending(kv => kv.Value.TotalMs).Select(kv =>
                FormattableString.Invariant($"{kv.Key}: {kv.Value.Count} call(s), {kv.Value.TotalMs:F0} ms in all, longest {kv.Value.MaxMs:F1} ms") +
                (kv.Value.MaxDetail != null ? $" ({kv.Value.MaxDetail})" : ""));
#endif
    }
}
