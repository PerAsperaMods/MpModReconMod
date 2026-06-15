using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PerAspera.Core;
using PerAspera.Networking;
using System;

namespace PerAspera.MpModRecon
{
    [BepInPlugin("com.peraspera.wafhien.mpmodrecon", "MpModReconMod", "1.1.0")]
    public class MpModReconPlugin : BasePlugin
    {
        internal static LogAspera L = null!;

        public override void Load()
        {
            L = new LogAspera("MpModRecon");
            L.Info("=== MpModRecon v1.1.0 — handshake pa_modset + blocage client via MainMenu.StartGame ===");

            var harmony = new Harmony("com.peraspera.wafhien.mpmodrecon");
            harmony.PatchAll(typeof(MpModReconPlugin).Assembly);
        }
    }

    // ─── ÉTAT PARTAGÉ ────────────────────────────────────────────────────────
    internal static class MpState
    {
        internal const string PA_MODSET_KEY = "pa_modset";

        // Set dans CreateLobby : indique qu'on est l'hôte pour ce lobby
        internal static bool IsHost = false;

        // Snapshot PlayerPrefs["Mods"] capturé à OnLobbyEnter (hôte + client)
        internal static string? ModsSnapshot = null;

        // Résultat du handshake — null = pas encore vérifié
        internal static string? MismatchMessage = null;
    }

    // ─── PATCH 1 : CreateLobby — marquer hôte ────────────────────────────────
    [HarmonyPatch(typeof(MultiplayerMainMenu), nameof(MultiplayerMainMenu.CreateLobby))]
    internal static class CreateLobbyPatch
    {
        static void Prefix()
        {
            MpState.IsHost = true;
            MpState.MismatchMessage = null;
            MpModReconPlugin.L.Info("[CreateLobby] IsHost=true — sera l'hôte du lobby");
        }
    }

    // ─── PATCH 2 : OnLobbyEnter — hôte écrit, client lit et compare ──────────
    [HarmonyPatch(typeof(MultiplayerMainMenu), nameof(MultiplayerMainMenu.OnLobbyEnter))]
    internal static class OnLobbyEnterPatch
    {
        static void Postfix(Lobby lobby)
        {
            try
            {
                string localMods = UnityEngine.PlayerPrefs.GetString("Mods", "");
                MpState.ModsSnapshot = string.IsNullOrEmpty(localMods) ? null : localMods;

                if (MpState.IsHost)
                {
                    // Hôte : écrire les mods dans LobbyData
                    MpState.IsHost = false;
                    string toWrite = localMods ?? "";
                    lobby.Data[MpState.PA_MODSET_KEY] = toWrite;
                    MpModReconPlugin.L.Info($"[OnLobbyEnter] HÔTE — écrit pa_modset=\"{toWrite}\"");
                }
                else
                {
                    // Client : lire les mods de l'hôte et comparer
                    string hostMods = "";
                    try { hostMods = lobby.Data[MpState.PA_MODSET_KEY] ?? ""; } catch { }

                    MpModReconPlugin.L.Info($"[OnLobbyEnter] CLIENT — pa_modset hôte=\"{hostMods}\" | local=\"{localMods}\"");

                    if (NormalizeMods(hostMods) != NormalizeMods(localMods))
                    {
                        MpState.MismatchMessage = $"Mods incompatibles !\nHôte: [{hostMods}]\nLocal: [{localMods}]";
                        MpModReconPlugin.L.Warning($"[OnLobbyEnter] MISMATCH — {MpState.MismatchMessage}");
                    }
                    else
                    {
                        MpState.MismatchMessage = null;
                        MpModReconPlugin.L.Info("[OnLobbyEnter] CLIENT — mods identiques ✓");
                    }
                }
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[OnLobbyEnter] EXCEPTION: {ex.Message}");
            }
        }

        // Compare les deux listes indépendamment de l'ordre et des espaces
        static string NormalizeMods(string mods)
        {
            if (string.IsNullOrEmpty(mods)) return "";
            var parts = mods.Split(',');
            System.Array.Sort(parts);
            return string.Join(",", parts).Trim().ToLowerInvariant();
        }
    }

    // ─── PATCH 3 : InitializeUniverseStaticDependencies — injection MP ────────
    [HarmonyPatch(typeof(BaseGame), nameof(BaseGame.InitializeUniverseStaticDependencies))]
    internal static class InitUniversePatch
    {
        private static int _callCount = 0;

        static void Prefix()
        {
            _callCount++;
            try
            {
                string? snapshot = MpState.ModsSnapshot;

                if (snapshot == null)
                {
                    MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] solo — modList.Count={BaseGame.modList?.Count ?? -1}");
                    return;
                }

                MpState.ModsSnapshot = null;

                var modList = BaseGame.modList;
                if (modList == null)
                {
                    MpModReconPlugin.L.Warning($"[InitUniverse #{_callCount}] modList null");
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
                MpModReconPlugin.L.Info($"[InitUniverse #{_callCount}] OK — Count={modList.Count}, hasMods={BaseGame.hasMods}");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[InitUniverse #{_callCount}] EXCEPTION: {ex.Message}");
            }
        }
    }

    // ─── PATCH 4 : LobbyPanel.OnStart — bloquer si mismatch (hôte uniquement) ──
    // Ce patch bloque le bouton Start côté hôte et affiche le message dans l'UI.
    // Il ne suffit pas seul : le client reçoit le "start" via réseau, pas ce code.
    [HarmonyPatch(typeof(LobbyPanel), nameof(LobbyPanel.OnStart))]
    internal static class LobbyOnStartPatch
    {
        static bool Prefix(LobbyPanel __instance)
        {
            try
            {
                if (MpState.MismatchMessage != null)
                {
                    MpModReconPlugin.L.Warning($"[LobbyPanel.OnStart] BLOQUÉ (hôte) — {MpState.MismatchMessage}");

                    try
                    {
                        if (__instance.privacityTxt != null)
                            __instance.privacityTxt.text = MpState.MismatchMessage;
                    }
                    catch { }

                    return false;
                }
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[LobbyPanel.OnStart] EXCEPTION: {ex.Message}");
            }

            return true;
        }
    }

    // ─── PATCH 4b : MainMenu.StartGame — bloquer côté client ─────────────────
    // StartGame() est une méthode STATIQUE appelée sur toutes les machines (hôte
    // et client) quand une partie MP démarre. C'est le seul point commun qui tire
    // des deux côtés — c'est ici qu'on bloque le client.
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.StartGame))]
    internal static class StartGamePatch
    {
        static bool Prefix(bool isMultiplayer)
        {
            try
            {
                if (!isMultiplayer || MpState.MismatchMessage == null) return true;

                MpModReconPlugin.L.Warning($"[MainMenu.StartGame] BLOQUÉ (client) — {MpState.MismatchMessage}");
                return false; // Empêche le chargement de scène
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[MainMenu.StartGame] EXCEPTION: {ex.Message}");
                return true;
            }
        }
    }

    // ─── PATCH 5 : ModsPanel.GenerateMods — diagnostic ───────────────────────
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
