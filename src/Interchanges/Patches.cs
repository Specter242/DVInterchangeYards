using System.Linq;
using HarmonyLib;

namespace Interchanges
{
    /// <summary>
    /// Build before anything reads the track registry: its track list, its
    /// ordered tracks or junctions, or its hashes.
    /// </summary>
    [HarmonyPatch(typeof(RailTrackRegistryBase), nameof(RailTrackRegistryBase.AllTracks), MethodType.Getter)]
    internal static class AllTracksPatch
    {
        private static void Prefix(RailTrackRegistryBase __instance, RailTrack[] ____allTracks)
        {
            if (____allTracks == null)
                Builder.EnsureBuilt(__instance);
        }
    }

    [HarmonyPatch(typeof(RailTrackRegistryBase), nameof(RailTrackRegistryBase.OrderedRailtracks), MethodType.Getter)]
    internal static class OrderedRailtracksPatch
    {
        private static void Prefix(RailTrackRegistryBase __instance) => Builder.EnsureBuilt(__instance);
    }

    [HarmonyPatch(typeof(RailTrackRegistryBase), nameof(RailTrackRegistryBase.OrderedJunctions), MethodType.Getter)]
    internal static class OrderedJunctionsPatch
    {
        private static void Prefix(RailTrackRegistryBase __instance) => Builder.EnsureBuilt(__instance);
    }

    /// <summary>
    /// Saves key cars and jobs by a hash of the track objects' paths, and
    /// switch states by a hash of the junctions'. Our objects are left out,
    /// so a save made with or without this mod keeps the same keys.
    /// </summary>
    [HarmonyPatch(typeof(RailTrackRegistryBase), "GetTracksHash")]
    internal static class TracksHashPatch
    {
        private static void Prefix(ref RailTrack[] tracks)
        {
            if (tracks != null && Builder.OurTracks.Count > 0)
                tracks = tracks.Where(t => !Builder.IsOurs(t)).ToArray();
        }
    }

    [HarmonyPatch(typeof(RailTrackRegistryBase), "GetJunctionsHash")]
    internal static class JunctionsHashPatch
    {
        private static void Prefix(ref Junction[] junctions)
        {
            if (junctions != null && Builder.OurJunctions.Count > 0)
                junctions = junctions.Where(j => !Builder.IsOurs(j)).ToArray();
        }
    }
}
