using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PerAspera.Core;
using PerAspera.Networking;
using System;
using UnityEngine;

namespace PerAspera.MpModRecon
{
    [BepInPlugin("com.peraspera.wafhien.mpmodrecon", "MpModReconMod", "1.4.0")]
    public class MpModReconPlugin : BasePlugin
    {
        internal static LogAspera L = null!;

        public override void Load()
        {
            L = new LogAspera("MpModRecon");
            L.Info("=== MpModRecon v1.4.0 — PlayerData bidirectional handshake (host+client both blocked) ===");

            var harmony = new Harmony("com.peraspera.wafhien.mpmodrecon");
            harmony.PatchAll(typeof(MpModReconPlugin).Assembly);
        }
    }

    // ─── ÉTAT PARTAGÉ ────────────────────────────────────────────────────────
    internal static class MpState
    {
        internal const string PA_MODSET_KEY = "pa_modset";

        internal static bool IsHost = false;
        internal static string? ModsSnapshot = null;
        internal static string? MismatchMessage = null;
        internal static Lobby? CurrentLobby = null;

        internal static string NormalizeMods(string mods)
        {
            if (string.IsNullOrEmpty(mods)) return "";
            var parts = mods.Split(',');
            Array.Sort(parts);
            return string.Join(",", parts).Trim().ToLowerInvariant();
        }
    }

    // ─── PATCH 1 : CreateLobby — marquer hôte ────────────────────────────────
    [HarmonyPatch(typeof(MultiplayerMainMenu), nameof(MultiplayerMainMenu.CreateLobby))]
    internal static class CreateLobbyPatch
    {
        static void Prefix()
        {
            MpState.IsHost = true;
            MpState.MismatchMessage = null;
            MpState.CurrentLobby = null;
            MpModReconPlugin.L.Info("[CreateLobby] IsHost=true");
        }
    }

    // ─── PATCH 2 : OnLobbyEnter — tous écrivent PlayerData + client compare ──
    // PlayerData = Steam member data : chaque membre écrit ses propres données,
    // lisibles par tous (notamment l'hôte dans LobbyPanel.OnStart).
    [HarmonyPatch(typeof(MultiplayerMainMenu), nameof(MultiplayerMainMenu.OnLobbyEnter))]
    internal static class OnLobbyEnterPatch
    {
        static void Postfix(Lobby lobby)
        {
            try
            {
                string localMods = PlayerPrefs.GetString("Mods", "");
                MpState.ModsSnapshot = string.IsNullOrEmpty(localMods) ? null : localMods;

                // Tous les joueurs écrivent leur modset dans PlayerData (Steam member data)
                try
                {
                    var self = Player.Self;
                    if (self?.Data != null)
                        self.Data[MpState.PA_MODSET_KEY] = localMods ?? "";
                    MpModReconPlugin.L.Info($"[OnLobbyEnter] PlayerData[pa_modset] = \"{localMods}\"");
                }
                catch (Exception ex)
                {
                    MpModReconPlugin.L.Warning($"[OnLobbyEnter] PlayerData write EXCEPTION: {ex.Message}");
                }

                if (MpState.IsHost)
                {
                    // Hôte : écrire aussi dans LobbyData (référence pour clients)
                    MpState.IsHost = false;
                    string toWrite = localMods ?? "";
                    lobby.Data[MpState.PA_MODSET_KEY] = toWrite;
                    MpModReconPlugin.L.Info($"[OnLobbyEnter] HÔTE — écrit pa_modset=\"{toWrite}\"");
                }
                else
                {
                    // Client : comparer contre le LobbyData de l'hôte
                    string hostMods = "";
                    try { hostMods = lobby.Data[MpState.PA_MODSET_KEY] ?? ""; } catch { }

                    MpModReconPlugin.L.Info($"[OnLobbyEnter] CLIENT — pa_modset hôte=\"{hostMods}\" | local=\"{localMods}\"");

                    if (MpState.NormalizeMods(hostMods) != MpState.NormalizeMods(localMods))
                    {
                        MpState.MismatchMessage = $"Mod mismatch!\nHost: [{hostMods}]\nLocal: [{localMods}]";
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
    }

    // ─── PATCH 2b : LobbyPanel.SetData — capturer la référence du lobby ─────
    [HarmonyPatch(typeof(LobbyPanel), nameof(LobbyPanel.SetData))]
    internal static class LobbyPanelSetDataPatch
    {
        static void Postfix(GameObject backPanel, Lobby lobby)
        {
            MpState.CurrentLobby = lobby;
            MpModReconPlugin.L.Info("[LobbyPanel.SetData] référence lobby capturée");
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

    // ─── PATCH 4 : LobbyPanel.OnStart — hôte vérifie PlayerData de tous les joueurs ──
    // Lit le pa_modset de chaque membre via Steam member data (écrit dans OnLobbyEnter).
    // Si un client a des mods différents → bloque le démarrage et affiche le message.
    [HarmonyPatch(typeof(LobbyPanel), nameof(LobbyPanel.OnStart))]
    internal static class LobbyOnStartPatch
    {
        static bool Prefix(LobbyPanel __instance)
        {
            try
            {
                string localMods = PlayerPrefs.GetString("Mods", "");
                MpState.MismatchMessage = null; // reset avant chaque vérification

                var lobby = MpState.CurrentLobby;
                if (lobby != null)
                {
                    try
                    {
                        string? selfId = null;
                        try { selfId = Player.Self?.ID; } catch { }

                        var players = lobby._players;
                        if (players != null)
                        {
                            for (int i = 0; i < players.Count; i++)
                            {
                                var player = players[i];
                                if (player == null) continue;

                                // Ignorer soi-même (on se compare à nos propres PlayerPrefs)
                                if (selfId != null && player.ID == selfId) continue;

                                string playerMods = "";
                                try { playerMods = player.Data?[MpState.PA_MODSET_KEY] ?? ""; } catch { }

                                MpModReconPlugin.L.Info($"[LobbyPanel.OnStart] \"{player.Name}\" pa_modset=\"{playerMods}\"");

                                if (MpState.NormalizeMods(playerMods) != MpState.NormalizeMods(localMods))
                                {
                                    MpState.MismatchMessage = $"Mod mismatch!\n{player.Name}: [{playerMods}]\nRequired: [{localMods}]";
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MpModReconPlugin.L.Warning($"[LobbyPanel.OnStart] Player check EXCEPTION: {ex.Message}");
                    }
                }

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
    // StartGame() est statique et s'exécute sur toutes les machines (hôte et client)
    // quand une partie MP démarre — c'est ici qu'on bloque le client.
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.StartGame))]
    internal static class StartGamePatch
    {
        static bool Prefix(bool isMultiplayer)
        {
            try
            {
                if (!isMultiplayer || MpState.MismatchMessage == null) return true;

                MpModReconPlugin.L.Warning($"[MainMenu.StartGame] BLOQUÉ (client) — {MpState.MismatchMessage}");
                return false;
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
