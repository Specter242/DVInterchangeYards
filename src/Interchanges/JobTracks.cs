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
    /// - Each yard track gets a job ID under the station whose sub-yard the interchange is: yard
    ///   track N of West Interchange is MF-WI-0(N+1)-X (shown "MF-WI1X"), of Northeast Interchange
    ///   IME-NEI-0(N+1)-X. The registry makes a track's ID from its object's name; the name stays
    ///   as it is (saves know the track by it), and the ID is given where the registry makes it.
    /// - Type X is no stock type, and no station lists these tracks among its storage, transfer or
    ///   loading tracks, so stock and Persistent Jobs' generation never send cars here. Only a mod
    ///   that asks for these tracks by ID does (the contract mod).
    /// - Each is registered with the yard organizer as the stations register theirs, before saved
    ///   jobs load: a job saved with an interchange track finds it, and its space can be reserved.
    /// </summary>
    internal static class JobTracks
    {
        public const string TrackType = "X";
        private static readonly Dictionary<GameObject, TrackID> byObject = new Dictionary<GameObject, TrackID>();
        private static bool registered;
        public static readonly List<string> Notes = new List<string>();

        public static void Reset()
        {
            byObject.Clear();
            registered = false;
            Notes.Clear();
        }

        /// <summary>A built layout's yard tracks get their job IDs, in yard order.</summary>
        public static void Assign(Layout layout, IEnumerable<RailTrack> tracks)
        {
            var station = layout.Booklet?.Station;
            if (string.IsNullOrEmpty(station) || string.IsNullOrEmpty(layout.Code))
                return;
            foreach (var t in tracks)
            {
                var m = Regex.Match(t.name, @" yard (\d+)$");
                if (!m.Success)
                    continue;
                var id = new TrackID(station, layout.Code, (int.Parse(m.Groups[1].Value) + 1).ToString("00"), TrackType);
                byObject[t.gameObject] = id;
                Notes.Add($"{t.name}: job track {id.FullID}");
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
