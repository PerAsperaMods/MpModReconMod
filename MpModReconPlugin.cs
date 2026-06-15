using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PerAspera.Core;
using PerAspera.Networking;
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
            L.Info("=== MpModRecon Phase 1 v2 — OnLobbyEnter trigger (hôte + client) ===");

            var harmony = new Harmony("com.peraspera.wafhien.mpmodrecon");
            harmony.PatchAll(typeof(MpModReconPlugin).Assembly);
        }
    }

    // ─── PATCH 1 : OnLobbyEnter ──────────────────────────────────────────────
    // Fire hôte ET client → snapshot universel pour les deux côtés.
    // CreateLobby ne fire que sur l'hôte, c'est pourquoi le client désynçait.
    [HarmonyPatch(typeof(MultiplayerMainMenu), nameof(MultiplayerMainMenu.OnLobbyEnter))]
    internal static class OnLobbyEnterPatch
    {
        internal static string? MpModsSnapshot = null;

        static void Postfix(Lobby lobby)
        {
            try
            {
                string prefs = UnityEngine.PlayerPrefs.GetString("Mods", "");
                MpModReconPlugin.L.Info($"[OnLobbyEnter] snapshot PlayerPrefs[Mods] = \"{prefs}\"");
                MpModsSnapshot = string.IsNullOrEmpty(prefs) ? null : prefs;
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[OnLobbyEnter] EXCEPTION: {ex.Message}");
            }
        }
    }

    // ─── PATCH 2 : InitializeUniverseStaticDependencies ─────────────────────
    // Si snapshot set (= flow MP) → injecter mods dans modList avant le loader.
    [HarmonyPatch(typeof(BaseGame), nameof(BaseGame.InitializeUniverseStaticDependencies))]
    internal static class InitUniversePatch
    {
        private static int _callCount = 0;

        static void Prefix()
        {
            _callCount++;
            try
            {
                string? snapshot = OnLobbyEnterPatch.MpModsSnapshot;

                if (snapshot == null)
                {
                    MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] solo — modList.Count={BaseGame.modList?.Count ?? -1}");
                    return;
                }

                OnLobbyEnterPatch.MpModsSnapshot = null;

                var modList = BaseGame.modList;
                if (modList == null)
                {
                    MpModReconPlugin.L.Warning($"[InitUniverse #{_callCount}] modList null — injection impossible");
                    return;
                }

                modList.Clear();
                foreach (string raw in snapshot.Split(','))
                {
                    string id = raw.Trim();
                    if (!string.IsNullOrEmpty(id))
                    {
                        modList.Add(id);
                        MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] injecté \"{id}\"");
                    }
                }

                BaseGame.hasMods = modList.Count > 0;
                MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] injection OK — modList.Count={modList.Count}, hasMods={BaseGame.hasMods}");
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
    [HarmonyPatch(typeof(ModsPanel), nameof(ModsPanel.GenerateMods))]
    internal static class GenerateModsPatch
    {
        static void Prefix()
        {
            try
            {
                bool initialized = false;
                try { initialized = GameHubManager.Instance?.Initialized ?? false; } catch { }
                MpModReconPlugin.L.Info($"[GenerateMods] Initialized={initialized}, modList.Count={BaseGame.modList?.Count ?? -1}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[GenerateMods] EXCEPTION: {ex.Message}");
            }
        }
    }
}
