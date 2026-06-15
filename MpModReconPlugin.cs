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
            L.Info("=== MpModRecon Phase 1 — injection MP mods ===");

            var harmony = new Harmony("com.peraspera.wafhien.mpmodrecon");
            harmony.PatchAll(typeof(MpModReconPlugin).Assembly);
        }
    }

    // ─── PATCH 1 : CreateLobby ───────────────────────────────────────────────
    // Snapshot PlayerPrefs["Mods"] AVANT que le flow MP ne vide modList.
    // Sert de flag MP-only : ne fire jamais en solo.
    [HarmonyPatch(typeof(MultiplayerMainMenu), nameof(MultiplayerMainMenu.CreateLobby))]
    internal static class CreateLobbyPatch
    {
        internal static string? MpModsSnapshot = null;

        static void Prefix()
        {
            try
            {
                string prefs = UnityEngine.PlayerPrefs.GetString("Mods", "");
                MpModReconPlugin.L.Info($"[CreateLobby] snapshot PlayerPrefs[Mods] = \"{prefs}\"");
                MpModsSnapshot = string.IsNullOrEmpty(prefs) ? null : prefs;
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[CreateLobby] EXCEPTION: {ex.Message}");
            }
        }
    }

    // ─── PATCH 2 : InitializeUniverseStaticDependencies ─────────────────────
    // Si le snapshot MP est set → injecter les mods dans modList avant que
    // Universe.InitializeCollections ne le lise. Puis clear le snapshot.
    [HarmonyPatch(typeof(BaseGame), nameof(BaseGame.InitializeUniverseStaticDependencies))]
    internal static class InitUniversePatch
    {
        private static int _callCount = 0;

        static void Prefix()
        {
            _callCount++;
            try
            {
                string? snapshot = CreateLobbyPatch.MpModsSnapshot;

                if (snapshot == null)
                {
                    // Solo ou MP sans sélection — ne rien faire
                    MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] pas de snapshot MP — modList inchangée (Count={BaseGame.modList?.Count ?? -1})");
                    return;
                }

                // Flow MP : injecter les mods depuis le snapshot
                CreateLobbyPatch.MpModsSnapshot = null; // consommé une seule fois

                var modList = BaseGame.modList;
                if (modList == null)
                {
                    MpModReconPlugin.L.Warning($"[InitUniverse #{_callCount}] modList null — impossible d'injecter");
                    return;
                }

                modList.Clear();
                string[] ids = snapshot.Split(',');
                foreach (string raw in ids)
                {
                    string id = raw.Trim();
                    if (!string.IsNullOrEmpty(id))
                    {
                        modList.Add(id);
                        MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] injecté → modList += \"{id}\"");
                    }
                }

                BaseGame.hasMods = modList.Count > 0;
                MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] injection terminée — modList.Count={modList.Count}, hasMods={BaseGame.hasMods}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[InitUniverse #{_callCount}] EXCEPTION: {ex.Message}");
            }
        }

        static void Postfix()
        {
            try
            {
                MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] Postfix — modList.Count={BaseGame.modList?.Count ?? -1}, hasMods={BaseGame.hasMods}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[InitUniverse #{_callCount}] Postfix EXCEPTION: {ex.Message}");
            }
        }
    }

    // ─── PATCH 3 : ModsPanel.GenerateMods ───────────────────────────────────
    // Diagnostic — confirme le branchement online/offline au panneau mods.
    [HarmonyPatch(typeof(ModsPanel), nameof(ModsPanel.GenerateMods))]
    internal static class GenerateModsPatch
    {
        static void Prefix()
        {
            try
            {
                bool initialized = false;
                try { initialized = GameHubManager.Instance?.Initialized ?? false; } catch { }
                MpModReconPlugin.L.Info($"[GenerateMods] GameHubManager.Initialized={initialized}, modList.Count={BaseGame.modList?.Count ?? -1}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[GenerateMods] EXCEPTION: {ex.Message}");
            }
        }
    }
}
