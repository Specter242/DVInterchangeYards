using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityModManagerNet;

namespace Interchanges
{
    /// <summary>
    /// Unity Mod Manager entry point. Loads the layouts beside the DLL and
    /// patches the track registry so the layouts are built into the stock
    /// railway as it loads (Builder), without changing the saves' track keys.
    /// </summary>
    public static class Main
    {
        internal static UnityModManager.ModEntry Mod;
        internal static List<Layout> Layouts = new List<Layout>();
        private static Harmony harmony;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            try
            {
                Layouts = Layout.LoadAll(modEntry.Path);
                Log($"{Layouts.Count} layout(s): {string.Join(", ", Layouts.ConvertAll(l => l.Site))}");
#if DEVTOOLS
                // Inspection only: "vanilla" in the trigger leaves the game as without this mod
                // (nothing patched or built), with just the inspector, to test saves across uninstalling.
                if (Inspector.VanillaRequested(modEntry.Path))
                {
                    Log("inspection: running as if this mod were not installed");
                    Inspector.Install();
                    return true;
                }
#endif
                harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(typeof(Main).Assembly);
                TerrainEdits.Install();
                Offices.Install();
#if DEVTOOLS
                Inspector.Install();
#endif
                WorldStreamingInit.LoadingFinished += Extras.RefreshMaterial;
                WorldStreamingInit.LoadingFinished += () => DistantTrack.TryPaint(true);
                return true;
            }
            catch (Exception e)
            {
                Error($"failed to load: {e}");
                return false;
            }
        }

        internal static void Log(string text) => Mod?.Logger.Log(text);
        internal static void Warn(string text) => Mod?.Logger.Warning(text);
        internal static void Error(string text) => Mod?.Logger.Error(text);
    }
}
