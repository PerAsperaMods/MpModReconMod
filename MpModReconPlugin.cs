using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PerAspera.Core;
using System;

namespace PerAspera.MpModRecon
{
    [BepInPlugin("com.peraspera.wafhien.mpmodrecon", "MpModReconMod", "1.0.0")]
    public class MpModReconPlugin : BasePlugin
    {
        internal static LogAspera L = null!;

        public override void Load()
        {
            L = new LogAspera("MpModRecon");
            L.Info("=== MpModRecon chargé — diagnostic Phase 0-B ===");

            var harmony = new Harmony("com.peraspera.wafhien.mpmodrecon");
            harmony.PatchAll(typeof(MpModReconPlugin).Assembly);
        }
    }

    // ─── PATCH 1 : CreateLobby ───────────────────────────────────────────────
    // Capture l'état de BaseGame.modList AVANT le Clear() natif.
    // Confirme : (a) quels mods étaient actifs, (b) IsMultiplayer() vaut déjà true ici ?
    [HarmonyPatch(typeof(MultiplayerMainMenu), nameof(MultiplayerMainMenu.CreateLobby))]
    internal static class CreateLobbyPatch
    {
        static void Prefix()
        {
            try
            {
                var modList = BaseGame.modList;

                MpModReconPlugin.L.Info($"[CreateLobby Prefix] modList.Count={modList?.Count ?? -1}");

                if (modList != null)
                {
                    for (int i = 0; i < modList.Count; i++)
                        MpModReconPlugin.L.Info($"[CreateLobby Prefix]   modList[{i}] = {modList[i]}");
                }

                MpModReconPlugin.L.Info($"[CreateLobby Prefix] hasMods={BaseGame.hasMods}");

                // "Mods" = clé confirmée par Cpp2IL dump de ModsPanel
                string modsPrefs = UnityEngine.PlayerPrefs.GetString("Mods", "<absent>");
                MpModReconPlugin.L.Info($"[CreateLobby Prefix] PlayerPrefs[Mods] = {modsPrefs}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[CreateLobby Prefix] EXCEPTION: {ex.Message}");
            }
        }

        static void Postfix()
        {
            try
            {
                var modList = BaseGame.modList;
                MpModReconPlugin.L.Info($"[CreateLobby Postfix] modList.Count APRÈS Clear={modList?.Count ?? -1}");
                MpModReconPlugin.L.Info($"[CreateLobby Postfix] hasMods={BaseGame.hasMods}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[CreateLobby Postfix] EXCEPTION: {ex.Message}");
            }
        }
    }

    // ─── PATCH 2 : InitializeUniverseStaticDependencies ─────────────────────
    // Confirme : (a) modList vide en MP, (b) méthode appelée une seule fois par session.
    [HarmonyPatch(typeof(BaseGame), nameof(BaseGame.InitializeUniverseStaticDependencies))]
    internal static class InitUniversePatch
    {
        private static int _callCount = 0;

        static void Prefix()
        {
            _callCount++;
            try
            {
                var modList = BaseGame.modList;
                MpModReconPlugin.L.Info($"[InitUniverse Prefix] appel #{_callCount}");
                MpModReconPlugin.L.Info($"[InitUniverse Prefix] modList.Count={modList?.Count ?? -1}");
                MpModReconPlugin.L.Info($"[InitUniverse Prefix] hasMods={BaseGame.hasMods}");

                if (modList != null)
                {
                    for (int i = 0; i < modList.Count; i++)
                        MpModReconPlugin.L.Info($"[InitUniverse Prefix]   modList[{i}] = {modList[i]}");
                }

                // "Mods" = clé confirmée par Cpp2IL dump de ModsPanel
                string modsPrefs = UnityEngine.PlayerPrefs.GetString("Mods", "<absent>");
                MpModReconPlugin.L.Info($"[InitUniverse Prefix] PlayerPrefs[Mods] = {modsPrefs}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[InitUniverse Prefix] EXCEPTION: {ex.Message}");
            }
        }

        static void Postfix()
        {
            try
            {
                var modList = BaseGame.modList;
                MpModReconPlugin.L.Info($"[InitUniverse Postfix] appel #{_callCount} terminé — modList.Count={modList?.Count ?? -1}, hasMods={BaseGame.hasMods}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[InitUniverse Postfix] EXCEPTION: {ex.Message}");
            }
        }
    }

    // ─── PATCH 3 : ModsPanel.GenerateMods ───────────────────────────────────
    // Confirme : branchement réel entre online/offline/MP, contenu modsInPrefs.
    [HarmonyPatch(typeof(ModsPanel), nameof(ModsPanel.GenerateMods))]
    internal static class GenerateModsPatch
    {
        static void Prefix(ModsPanel __instance)
        {
            try
            {
                bool initialized = false;
                try { initialized = GameHubManager.Instance?.Initialized ?? false; } catch { }

                MpModReconPlugin.L.Info($"[GenerateMods Prefix] GameHubManager.Initialized={initialized}");
                MpModReconPlugin.L.Info($"[GenerateMods Prefix] BaseGame.modList.Count={BaseGame.modList?.Count ?? -1}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[GenerateMods Prefix] EXCEPTION: {ex.Message}");
            }
        }

        static void Postfix(ModsPanel __instance)
        {
            try
            {
                MpModReconPlugin.L.Info($"[GenerateMods Postfix] terminé");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[GenerateMods Postfix] EXCEPTION: {ex.Message}");
            }
        }
    }
}
