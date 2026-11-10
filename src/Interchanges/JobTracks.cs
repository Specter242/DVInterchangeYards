using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DV.Logic.Job;
using HarmonyLib;
using UnityEngine;

namespace Interchanges
{
    /// <summary>
    /// The interchange yards' tracks as job tracks (DVContracts docs/design.md, "Anchoring", spike
    /// S4), so stock jobs can start and end on them and their booklets name them.
    ///
    /// - Each yard track gets a job ID in the interchange's own yard, its code (owner, 2026-11-05:
    ///   "Let's get these working as their own industry locations"): yard track N of West
    ///   Interchange is WI-A-0(N+1)-X (shown "WI-A1X"), of Northeast Interchange NEI-A-0(N+1)-X,
    ///   yard A as on its page in the station booklet. A job mod can then make each interchange a
    ///   station of its own (the contract mod does). The registry makes a track's ID from its
    ///   object's name; the name stays as it is (saves know the track by it), and the ID is given
    ///   where the registry makes it.
    /// - Up to 0.1.4 the IDs were under the station whose sub-yard the interchange was:
    ///   MF-WI-0(N+1)-X and IME-NEI-0(N+1)-X. A job saved with one finds its track by it still.
    /// - Type X is no stock type, and no station lists these tracks among its storage, transfer or
    ///   loading tracks, so stock and Persistent Jobs' generation never send cars here. Only a mod
    ///   that asks for these tracks by ID does (the contract mod).
    /// - Each is registered with the yard organizer as the stations register theirs, before saved
    ///   jobs load: a job saved with an interchange track finds it, and its space can be reserved.
    /// - Each is found by the name a stock track with its ID would have ("[Y]_[WI]_[A-01-X]") as
    ///   well as by its own: the Multiplayer mod sends a job's tracks to the players by that name,
    ///   and a player's game looks them up by it (Blaze221, Nexus, 2026-11-08: the players other
    ///   than the host saw no job booklets, as nearly every contract job uses an interchange track).
    /// </summary>
    internal static class JobTracks
    {
        public const string TrackType = "X";
        private static readonly Dictionary<GameObject, TrackID> byObject = new Dictionary<GameObject, TrackID>();
        private static readonly Dictionary<string, string> byOldId = new Dictionary<string, string>();
        private static readonly Dictionary<string, GameObject> byStockName = new Dictionary<string, GameObject>();
        private static bool registered;
        public static readonly List<string> Notes = new List<string>();

        public static void Reset()
        {
            byObject.Clear();
            byOldId.Clear();
            byStockName.Clear();
            registered = false;
            Notes.Clear();
        }

        /// <summary>A built layout's yard tracks get their job IDs, in yard order.</summary>
        public static void Assign(Layout layout, IEnumerable<RailTrack> tracks)
        {
            if (string.IsNullOrEmpty(layout.Code))
                return;
            var yard = string.IsNullOrEmpty(layout.Booklet?.YardId) ? "A" : layout.Booklet.YardId;
            var station = layout.Booklet?.Station;
            foreach (var t in tracks)
            {
                var m = Regex.Match(t.name, @" yard (\d+)$");
                if (!m.Success)
                    continue;
                var number = (int.Parse(m.Groups[1].Value) + 1).ToString("00");
                var id = new TrackID(layout.Code, yard, number, TrackType);
                byObject[t.gameObject] = id;
                byStockName[id.RailTrackGameObjectID] = t.gameObject;
                if (!string.IsNullOrEmpty(station))
                    byOldId[new TrackID(station, layout.Code, number, TrackType).FullID] = id.FullID;
                Notes.Add($"{t.name}: job track {id.FullID}");
            }
        }

        /// <summary>A job saved with a track's ID of 0.1.4 or before (MF-WI-01-X) finds the track by its ID now (WI-A-01-X).</summary>
        [HarmonyPatch(typeof(JobSaveManager), "GetYardTrackWithId")]
        private static class OldIds
        {
            private static void Postfix(string trackId, ref Track __result)
            {
                if (__result != null || trackId == null || !byOldId.TryGetValue(trackId, out var id))
                    return;
                var organizer = DV.Utils.SingletonBehaviour<YardTracksOrganizer>.Instance;
                if (organizer != null && organizer.yardTrackIdToTrack.TryGetValue(id, out var track) && track != null)
                {
                    __result = track;
                    Main.Log($"job tracks: a saved job's track {trackId} is {id}");
                }
            }
        }

        /// <summary>A track asked for by the name a stock track with an interchange track's ID would have is that track.</summary>
        [HarmonyPatch(typeof(RailTrackRegistryBase), nameof(RailTrackRegistryBase.GetTrackWithName))]
        private static class ByStockName
        {
            private static void Postfix(string trackName, ref RailTrack __result)
            {
                if (__result == null && trackName != null && byStockName.TryGetValue(trackName, out var go) && go != null)
                    __result = go.GetComponent<RailTrack>();
            }
        }

        /// <summary>The interchange job tracks of this load, by their IDs.</summary>
        public static IEnumerable<TrackID> Ids => byObject.Values;

        [HarmonyPatch(typeof(RailTrackRegistry), "TryGenerateTrackIdFromName")]
        private static class IdFromName
        {
            private static void Postfix(GameObject trackObject, ref TrackID __result)
            {
                if (trackObject != null && byObject.TryGetValue(trackObject, out var id))
                    __result = id;
            }
        }

        [HarmonyPatch(typeof(YardTracksOrganizer), nameof(YardTracksOrganizer.InitializeYardTrack))]
        private static class Register
        {
            private static void Postfix(YardTracksOrganizer __instance)
            {
                if (registered || byObject.Count == 0)
                    return;
                registered = true;
                var added = 0;
                foreach (var go in byObject.Keys.Where(g => g != null))
                {
                    var rail = go.GetComponent<RailTrack>();
                    var track = rail != null ? rail.LogicTrack() : null;
                    if (track == null)
                        continue;
                    if (!__instance.IsTrackManagedByOrganizer(track))
                        __instance.InitializeYardTrack(track);
                    __instance.yardTrackIdToTrack[track.ID.FullID] = track;
                    added++;
                }
                Notes.Add($"{added} interchange job track(s) registered with the yard organizer");
                Main.Log($"job tracks: {added} registered with the yard organizer");
            }
        }
    }
}
