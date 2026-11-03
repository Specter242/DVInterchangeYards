using System;
using System.Collections.Generic;
using System.Linq;
using DV.JObjectExtstensions;
using DV.Utils;
using UnityEngine;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace Interchanges
{
    /// <summary>
    /// Keeps saves safe with or without this mod. The game saves a car's
    /// bogies by their track's index in the registry's ordered list; our
    /// tracks sit at the end of it, which a game without this mod doesn't
    /// have.
    ///
    /// Where a switch was put on a stock track, the switch's through leg and
    /// the stub beyond it follow the stock curve, so a bogie there is saved at
    /// its place on the original stock track; this mod puts it back on the
    /// through leg or stub when loading, and a game without the mod loads it
    /// where it stood.
    ///
    /// Any other car with a bogie on our track is written as derailed at its
    /// world position, which a game without the mod restores where it stood.
    /// Under our own save key goes each of its bogies' place: a stable track
    /// identity and span (Builder.IdOf). Loading with the mod puts both bogies
    /// back on their tracks before the game creates the car and restores its
    /// couplings, hoses and brakes, as for any car on stock track.
    ///
    /// A record belongs to the derailed entry written beside it: it is used only
    /// while that entry still has both bogies derailed at the same pose. The game
    /// keeps our key in saves made without the mod, so a car the player has since
    /// re-railed or moved without it keeps where the game last saved it.
    ///
    /// The record is written as the game gathers the save (the patches on
    /// GetCarsSaveData and GetJunctionsSaveData), into the save data then
    /// current, so it never depends on a scene object staying the same.
    /// </summary>
    internal static class CarSaves
    {
        private const string SaveKey = "interchanges";
        private const int Format = 2;
        private const string StockPrefix = "#stock/";

        private static bool saving;
        private static JObject placements = new JObject();   // car GUID -> [rear bogie, front bogie]
        private static JObject junctions = new JObject();    // junction id -> selected branch

        public static int MappedOnSave, PlacedOnSave, MappedOnLoad, PlacedOnLoad, LeftDerailedOnLoad, StaleOnLoad, JunctionsRestored;

        /// <summary>A new world: nothing carried over from the last.</summary>
        public static void Reset()
        {
            saving = false;
            placements = new JObject();
            junctions = new JObject();
            MappedOnSave = PlacedOnSave = MappedOnLoad = PlacedOnLoad = LeftDerailedOnLoad = StaleOnLoad = JunctionsRestored = 0;
        }

        // Bogie 1 is the rear bogie, bogie 2 the front (CarsSaveManager.GetCarSaveData).
        internal static readonly (string Index, string Span, string Derailed)[] BogieKeys =
        {
            ("bog1TrackChildInd", "bog1PosOnTrack", "bog1Derailed"),
            ("bog2TrackChildInd", "bog2PosOnTrack", "bog2Derailed"),
        };

        private static SaveGameData Data =>
            SingletonBehaviour<SaveGameManager>.Instance != null ? SingletonBehaviour<SaveGameManager>.Instance.data : null;

        /// <summary>Our record in the save data now current (the save being loaded, or the one being written).</summary>
        public static JObject Record => Data?.GetJObject(SaveKey);

        private static void WriteRecord()
        {
            var data = Data;
            if (data == null)
                return;
            data.SetJObject(SaveKey, new JObject
            {
                ["format"] = Format,
                ["placements"] = placements.DeepClone(),
                ["junctions"] = junctions.DeepClone(),
            });
        }

        // ------------------------------------------------------------ cars: saving

        [HarmonyPatch(typeof(CarsSaveManager), nameof(CarsSaveManager.GetCarsSaveData))]
        private static class SavingCars
        {
            private static void Prefix()
            {
                saving = true;
                placements = new JObject();
            }

            private static void Postfix()
            {
                if (Builder.OurTracks.Count > 0)
                    WriteRecord();
            }

            private static void Finalizer() => saving = false;
        }

        [HarmonyPatch(typeof(CarsSaveManager), nameof(CarsSaveManager.GetCarSaveData))]
        private static class SaveCar
        {
            private static void Postfix(TrainCar car, RailTrack[] tracks, bool includeWorldBogieCouplerData, JObject __result)
            {
                if (!saving || !includeWorldBogieCouplerData || __result == null || car == null || tracks == null)
                    return;
                try
                {
                    var bogies = new[] { car.RearBogie, car.FrontBogie };
                    bool OnOurs(Bogie b) => b != null && !b.HasDerailed && Builder.IsOurs(b.track);
                    if (!bogies.Any(OnOurs))
                        return;

                    // On a tie-in's through leg or stub only: saved at its place on the stock track.
                    var mapped = new (int Index, double Span)?[2];
                    for (var i = 0; i < 2; i++)
                    {
                        if (!OnOurs(bogies[i]))
                            continue;
                        var b = bogies[i];
                        var tie = Builder.TieIns.FirstOrDefault(t => t.Through == b.track || t.Stub == b.track);
                        if (tie != null && tie.ToStock(b.track, b.traveller.Span, out var stockSpan))
                        {
                            var index = Array.IndexOf(tracks, tie.Stock);
                            if (index >= 0)
                                mapped[i] = (index, stockSpan);
                        }
                    }
                    if (Enumerable.Range(0, 2).All(i => !OnOurs(bogies[i]) || mapped[i] != null))
                    {
                        for (var i = 0; i < 2; i++)
                            if (mapped[i].HasValue)
                            {
                                __result[BogieKeys[i].Index] = mapped[i].Value.Index;
                                __result[BogieKeys[i].Span] = mapped[i].Value.Span;
                            }
                        MappedOnSave++;
                        return;
                    }

                    // Elsewhere on our track: derailed in place for the game, each bogie's place for us,
                    // with the pose of that derailed entry it belongs to.
                    foreach (var k in BogieKeys)
                    {
                        __result.Remove(k.Index);
                        __result.Remove(k.Span);
                        __result[k.Derailed] = true;
                    }
                    placements[car.logicCar.carGuid] = new JObject
                    {
                        ["bogies"] = new JArray(bogies.Select(b => Place(b, tracks))),
                        ["position"] = __result["position"]?.DeepClone(),
                        ["rotation"] = __result["rotation"]?.DeepClone(),
                    };
                    PlacedOnSave++;
                }
                catch (Exception e)
                {
                    Main.Error($"saving car {car?.ID}: {e}");
                }
            }
        }

        /// <summary>A bogie's place: its track's stable identity and its span; null when derailed.</summary>
        private static JToken Place(Bogie b, RailTrack[] tracks)
        {
            if (b == null || b.HasDerailed || b.track == null)
                return JValue.CreateNull();
            string id;
            if (Builder.IsOurs(b.track))
                id = Builder.IdOf(b.track);
            else
            {
                var index = Array.IndexOf(tracks, b.track);
                id = index >= 0 ? StockPrefix + index : null;
            }
            return id == null ? JValue.CreateNull() : new JObject { ["track"] = id, ["span"] = b.traveller.Span };
        }

        // ------------------------------------------------------------ cars: loading

        /// <summary>Before the game creates a saved car: its bogies go back on our tracks, by our record or a tie-in.</summary>
        [HarmonyPatch(typeof(CarsSaveManager), "InstantiateCarFromSavegame")]
        private static class LoadCar
        {
            private static void Prefix(JObject carData, RailTrack[] tracks)
            {
                try
                {
                    switch (PrepareForLoad(carData, tracks, Record, out var mapped))
                    {
                        case LoadOutcome.Placed: PlacedOnLoad++; break;
                        case LoadOutcome.LeftDerailed: LeftDerailedOnLoad++; break;
                        case LoadOutcome.Mapped: MappedOnLoad += mapped; break;
                        case LoadOutcome.Stale: StaleOnLoad++; MappedOnLoad += mapped; break;
                    }
                }
                catch (Exception e)
                {
                    Main.Error($"loading a car: {e}");
                }
            }
        }

        /// <summary>
        /// Puts a saved car's bogies on our tracks: from our record of its bogies' places if it
        /// has one, else from beyond a trimmed stock track's kept part onto the through leg or stub.
        /// A record that no longer resolves (a layout not built) leaves the car derailed in place.
        /// </summary>
        public enum LoadOutcome { None, Placed, Mapped, LeftDerailed, Stale }

        private const float PoseTolerance = 0.5f, AngleTolerance = 5f;   // m, degrees

        /// <summary>Whether a saved car's entry is still the derailed one our record was written with.</summary>
        private static bool Matches(JObject carData, JObject place)
        {
            if (BogieKeys.Any(k => carData[k.Derailed]?.Value<bool?>() != true))
                return false;
            Vector3? pos = carData.GetVector3("position"), rot = carData.GetVector3("rotation");
            Vector3? wasPos = place.GetVector3("position"), wasRot = place.GetVector3("rotation");
            if (pos == null || rot == null || wasPos == null || wasRot == null)
                return false;
            return (pos.Value - wasPos.Value).magnitude <= PoseTolerance &&
                   Quaternion.Angle(Quaternion.Euler(rot.Value), Quaternion.Euler(wasRot.Value)) <= AngleTolerance;
        }

        public static LoadOutcome PrepareForLoad(JObject carData, RailTrack[] tracks, JObject record, out int mappedBogies)
        {
            mappedBogies = 0;
            if (carData == null || tracks == null)
                return LoadOutcome.None;
            var guid = carData["carGuid"]?.Value<string>();
            if (guid != null && record?["placements"]?[guid] is JObject place && place["bogies"] is JArray places && places.Count == 2)
            {
                if (!Matches(carData, place))
                {
                    // Saved again without the mod since (re-railed, moved): the game's entry is newer.
                    mappedBogies = MapBogies(carData, tracks);
                    return LoadOutcome.Stale;
                }
                var resolved = new (int Index, double Span)?[2];
                for (var i = 0; i < 2; i++)
                {
                    if (!(places[i] is JObject p))
                        continue;   // derailed when saved
                    var track = Resolve(p["track"]?.Value<string>(), tracks);
                    var span = p["span"]?.Value<double?>();
                    var length = track?.GetKinkedPointSet()?.span;
                    if (track == null || span == null || length == null || span < -0.01 || span > length + 0.01)
                    {
                        Main.Warn($"car {carData["id"]}: its saved place {p.ToString(Newtonsoft.Json.Formatting.None)} no longer exists; it stays derailed where it stood");
                        return LoadOutcome.LeftDerailed;
                    }
                    resolved[i] = (Array.IndexOf(tracks, track), Math.Max(0, Math.Min(length.Value, span.Value)));
                }
                for (var i = 0; i < 2; i++)
                    if (resolved[i].HasValue)
                    {
                        carData[BogieKeys[i].Index] = resolved[i].Value.Index;
                        carData[BogieKeys[i].Span] = resolved[i].Value.Span;
                        carData[BogieKeys[i].Derailed] = false;
                    }
                return LoadOutcome.Placed;
            }
            mappedBogies = MapBogies(carData, tracks);
            return mappedBogies > 0 ? LoadOutcome.Mapped : LoadOutcome.None;
        }

        private static RailTrack Resolve(string id, RailTrack[] tracks)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            if (id.StartsWith(StockPrefix, StringComparison.Ordinal))
                return int.TryParse(id.Substring(StockPrefix.Length), out var i) && i >= 0 && i < tracks.Length && !Builder.IsOurs(tracks[i]) ? tracks[i] : null;
            var t = Builder.TrackById(id);
            return t != null && Array.IndexOf(tracks, t) >= 0 ? t : null;
        }

        /// <summary>Moves a saved car's bogies from beyond a trimmed stock track's kept part onto the through leg or stub.</summary>
        public static int MapBogies(JObject carData, RailTrack[] tracks)
        {
            if (carData == null || tracks == null || Builder.TieIns.Count == 0)
                return 0;
            var mapped = 0;
            foreach (var (indexKey, spanKey, derailedKey) in BogieKeys)
            {
                if (carData[derailedKey]?.Value<bool?>() == true)
                    continue;
                var i = carData[indexKey]?.Value<int?>();
                var s = carData[spanKey]?.Value<double?>();
                if (i == null || s == null || i < 0 || i >= tracks.Length)
                    continue;
                var tie = Builder.TieIns.FirstOrDefault(t => t.Stock == tracks[i.Value]);
                if (tie == null || s.Value <= Builder.TrimmedStock[tie.Stock])
                    continue;
                if (!tie.FromStock(s.Value, out var track, out var span))
                    continue;
                var index = Array.IndexOf(tracks, track);
                if (index < 0)
                    continue;
                carData[indexKey] = index;
                carData[spanKey] = span;
                mapped++;
            }
            return mapped;
        }

        // ------------------------------------------------------------ switch states

        /// <summary>
        /// The game saves switch states by index in its ordered list, where ours come after the
        /// stock ones; those indices would shift with the layouts built. Ours are taken out of the
        /// game's list and kept under our own key by stable identity instead.
        /// </summary>
        [HarmonyPatch(typeof(JunctionsSaveManager), nameof(JunctionsSaveManager.GetJunctionsSaveData))]
        private static class SavingJunctions
        {
            private static void Postfix(JObject __result)
            {
                if (__result == null || Builder.OurJunctions.Count == 0)
                    return;
                try
                {
                    var ordered = SingletonBehaviour<RailTrackRegistryBase>.Instance.OrderedJunctions;
                    var indices = __result["indices"]?.ToObject<int[]>() ?? new int[0];
                    var states = __result["states"]?.ToObject<int[]>() ?? new int[0];
                    var keep = Enumerable.Range(0, Math.Min(indices.Length, states.Length))
                        .Where(i => indices[i] >= 0 && indices[i] < ordered.Length && !Builder.IsOurs(ordered[indices[i]])).ToList();
                    __result["indices"] = new JArray(keep.Select(i => indices[i]));
                    __result["states"] = new JArray(keep.Select(i => states[i]));
                    junctions = new JObject();
                    foreach (var j in Builder.OurJunctions.Where(j => j != null && j.selectedBranch != j.defaultSelectedBranch))
                        junctions[Builder.IdOf(j)] = (int)j.selectedBranch;
                    WriteRecord();
                }
                catch (Exception e)
                {
                    Main.Error($"saving switch states: {e}");
                }
            }
        }

        [HarmonyPatch(typeof(JunctionsSaveManager), nameof(JunctionsSaveManager.Load))]
        private static class LoadingJunctions
        {
            /// <summary>Indices past the stock junctions are never the game's to set (an older save of ours may have them).</summary>
            private static void Prefix(JObject savedData)
            {
                if (savedData == null || Builder.OurJunctions.Count == 0)
                    return;
                var ordered = SingletonBehaviour<RailTrackRegistryBase>.Instance.OrderedJunctions;
                var indices = savedData["indices"]?.ToObject<int[]>();
                var states = savedData["states"]?.ToObject<int[]>();
                if (indices == null || states == null || indices.Length != states.Length)
                    return;
                var keep = Enumerable.Range(0, indices.Length).Where(i => indices[i] >= 0 && indices[i] < ordered.Length && !Builder.IsOurs(ordered[indices[i]])).ToList();
                if (keep.Count == indices.Length)
                    return;
                savedData["indices"] = new JArray(keep.Select(i => indices[i]));
                savedData["states"] = new JArray(keep.Select(i => states[i]));
            }

            private static void Postfix() => JunctionsRestored += RestoreJunctions(Record);
        }

        /// <summary>Sets our switches as the record has them; returns how many it set.</summary>
        public static int RestoreJunctions(JObject record)
        {
            if (!(record?["junctions"] is JObject saved))
                return 0;
            var set = 0;
            foreach (var kv in saved)
            {
                var j = Builder.JunctionById(kv.Key);
                var branch = kv.Value?.Value<int?>();
                if (j == null || branch == null || branch < 0 || branch >= j.outBranches.Count)
                    continue;
                j.Switch(Junction.SwitchMode.NO_SOUND, (byte)branch.Value);
                set++;
            }
            return set;
        }
    }
}
