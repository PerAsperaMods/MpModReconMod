using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PerAspera.Core;
using PerAspera.Networking;
using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace PerAspera.MpModRecon
{
    [BepInPlugin("com.peraspera.wafhien.mpmodrecon", "MpModReconMod", "1.5.0")]
    public class MpModReconPlugin : BasePlugin
    {
        internal static LogAspera L = null!;

        public override void Load()
        {
            L = new LogAspera("MpModRecon");
            L.Info("=== MpModRecon v1.5.0 — join-time check + mod versions in handshake ===");

            var harmony = new Harmony("com.peraspera.wafhien.mpmodrecon");
            harmony.PatchAll(typeof(MpModReconPlugin).Assembly);
        }
    }

    // ─── ÉTAT PARTAGÉ ────────────────────────────────────────────────────────
    internal static class MpState
    {
        internal const string PA_MODSET_KEY = "pa_modset";

        internal static bool IsHost = false;
        internal static string? ModsSnapshot = null; // IDs seuls — pour injection InitUniverse
        internal static string? MismatchMessage = null;
        internal static Lobby? CurrentLobby = null;

        // Construit "modId:version,modId:version,..." depuis StreamingAssets manifests + BepInEx
        internal static string GetLocalModSetWithVersions()
        {
            string modIds = PlayerPrefs.GetString("Mods", "");
            if (string.IsNullOrEmpty(modIds)) return "";

            var parts = new System.Collections.Generic.List<string>();
            foreach (string raw in modIds.Split(','))
            {
                string modId = raw.Trim();
                if (string.IsNullOrEmpty(modId)) continue;

                string? version = TryGetModVersion(modId);
                parts.Add(version != null ? $"{modId}:{version}" : modId);
            }
            return string.Join(",", parts);
        }

        // Cherche la version d'un mod : manifest YAML d'abord, puis BepInEx Chainloader
        static string? TryGetModVersion(string modId)
        {
            // 1. Manifest YAML (StreamingAssets/Mods/modId/manifest.yaml)
            try
            {
                string manifestPath = Path.Combine(
                    Application.streamingAssetsPath, "Mods", modId, "manifest.yaml");
                if (File.Exists(manifestPath))
                {
                    foreach (string line in File.ReadAllLines(manifestPath))
                    {
                        string trimmed = line.TrimStart();
                        if (trimmed.StartsWith("version:"))
                        {
                            string ver = trimmed.Substring("version:".Length).Trim().Trim('"', '\'');
                            if (!string.IsNullOrEmpty(ver)) return ver;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        // Normalise pour comparaison : tri alphabétique, casse insensible
        internal static string NormalizeMods(string mods)
        {
            if (string.IsNullOrEmpty(mods)) return "";
            var parts = mods.Split(',');
            Array.Sort(parts, StringComparer.OrdinalIgnoreCase);
            return string.Join(",", parts).Trim().ToLowerInvariant();
        }

        // Message de mismatch lisible avec listes formatées
        internal static string BuildMismatchMessage(string playerName, string theirMods, string localMods)
        {
            string them = string.IsNullOrEmpty(theirMods) ? "(none)" : theirMods.Replace(",", "\n  ");
            string me = string.IsNullOrEmpty(localMods) ? "(none)" : localMods.Replace(",", "\n  ");
            return $"Mod mismatch!\n{playerName}:\n  {them}\nRequired:\n  {me}";
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

    // ─── PATCH 2 : WaitingForJoin — vérification AVANT de rejoindre ──────────
    // Steam broadcast les LobbyData à tous les browsers → pa_modset de l'hôte est
    // disponible ici, AVANT que le client entre dans le lobby.
    // return false = bloque la coroutine → le client reste dans la liste des lobbies.
    [HarmonyPatch(typeof(MultiplayerMainMenu), "WaitingForJoin")]
    internal static class WaitingForJoinPatch
    {
        static bool Prefix(Lobby lobby)
        {
            try
            {
                string hostMods = "";
                try { hostMods = lobby.Data[MpState.PA_MODSET_KEY] ?? ""; } catch { }

                if (string.IsNullOrEmpty(hostMods))
                {
                    // Hôte sans le mod de validation ou pa_modset absent — laisser passer
                    MpModReconPlugin.L.Warning("[WaitingForJoin] pa_modset absent — hôte non contrôlé, join autorisé");
                    MpState.MismatchMessage = null;
                    return true;
                }

                string localMods = MpState.GetLocalModSetWithVersions();
                MpModReconPlugin.L.Info($"[WaitingForJoin] hôte=\"{hostMods}\" | local=\"{localMods}\"");

                if (MpState.NormalizeMods(hostMods) != MpState.NormalizeMods(localMods))
                {
                    MpState.MismatchMessage = MpState.BuildMismatchMessage("Host", hostMods, localMods);
                    MpModReconPlugin.L.Warning($"[WaitingForJoin] MISMATCH — join bloqué\n{MpState.MismatchMessage}");
                    return false; // bloque WaitingForJoin → le client ne rejoint pas
                }

                MpState.MismatchMessage = null;
                MpModReconPlugin.L.Info("[WaitingForJoin] mods OK ✓ — join autorisé");
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[WaitingForJoin] EXCEPTION: {ex.Message}");
            }
            return true;
        }
    }

    // ─── PATCH 3 : OnLobbyEnter — tous écrivent PlayerData + filet de sécurité ──
    [HarmonyPatch(typeof(MultiplayerMainMenu), nameof(MultiplayerMainMenu.OnLobbyEnter))]
    internal static class OnLobbyEnterPatch
    {
        static void Postfix(Lobby lobby)
        {
            try
            {
                string rawIds = PlayerPrefs.GetString("Mods", "");
                MpState.ModsSnapshot = string.IsNullOrEmpty(rawIds) ? null : rawIds; // IDs seuls pour InitUniverse

                string localModsWithVersions = MpState.GetLocalModSetWithVersions();

                // Tous les joueurs écrivent leur modset+version dans PlayerData (Steam member data)
                try
                {
                    var self = Player.Self;
                    if (self?.Data != null)
                        self.Data[MpState.PA_MODSET_KEY] = localModsWithVersions;
                    MpModReconPlugin.L.Info($"[OnLobbyEnter] PlayerData[pa_modset] = \"{localModsWithVersions}\"");
                }
                catch (Exception ex)
                {
                    MpModReconPlugin.L.Warning($"[OnLobbyEnter] PlayerData write EXCEPTION: {ex.Message}");
                }

                if (MpState.IsHost)
                {
                    // Hôte : écrire dans LobbyData (visible par tous avant join)
                    MpState.IsHost = false;
                    lobby.Data[MpState.PA_MODSET_KEY] = localModsWithVersions;
                    MpModReconPlugin.L.Info($"[OnLobbyEnter] HÔTE — pa_modset=\"{localModsWithVersions}\"");
                }
                else
                {
                    // Client : filet de sécurité (WaitingForJoin a déjà vérifié)
                    string hostMods = "";
                    try { hostMods = lobby.Data[MpState.PA_MODSET_KEY] ?? ""; } catch { }

                    if (!string.IsNullOrEmpty(hostMods) &&
                        MpState.NormalizeMods(hostMods) != MpState.NormalizeMods(localModsWithVersions))
                    {
                        MpState.MismatchMessage = MpState.BuildMismatchMessage("Host", hostMods, localModsWithVersions);
                        MpModReconPlugin.L.Warning($"[OnLobbyEnter] MISMATCH (filet) — {MpState.MismatchMessage}");
                    }
                    else if (!string.IsNullOrEmpty(hostMods))
                    {
                        MpState.MismatchMessage = null;
                        MpModReconPlugin.L.Info("[OnLobbyEnter] CLIENT — mods OK ✓");
                    }
                }
            }
            catch (Exception ex)
            {
                MpModReconPlugin.L.Warning($"[OnLobbyEnter] EXCEPTION: {ex.Message}");
            }
        }
    }

    // ─── PATCH 3b : LobbyPanel.SetData — capturer la référence du lobby ──────
    [HarmonyPatch(typeof(LobbyPanel), nameof(LobbyPanel.SetData))]
    internal static class LobbyPanelSetDataPatch
    {
        static void Postfix(GameObject backPanel, Lobby lobby)
        {
            MpState.CurrentLobby = lobby;
            MpModReconPlugin.L.Info("[LobbyPanel.SetData] lobby capturé");
        }
    }

    // ─── PATCH 4 : InitializeUniverseStaticDependencies — injection MP ────────
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

    // ─── PATCH 5 : LobbyPanel.OnStart — hôte vérifie PlayerData de chaque joueur ──
    [HarmonyPatch(typeof(LobbyPanel), nameof(LobbyPanel.OnStart))]
    internal static class LobbyOnStartPatch
    {
        static bool Prefix(LobbyPanel __instance)
        {
            try
            {
                string localMods = MpState.GetLocalModSetWithVersions();
                MpState.MismatchMessage = null;

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
                                if (selfId != null && player.ID == selfId) continue;

                                string playerMods = "";
                                try { playerMods = player.Data?[MpState.PA_MODSET_KEY] ?? ""; } catch { }

                                MpModReconPlugin.L.Info($"[LobbyPanel.OnStart] \"{player.Name}\" pa_modset=\"{playerMods}\"");

                                if (MpState.NormalizeMods(playerMods) != MpState.NormalizeMods(localMods))
                                {
                                    MpState.MismatchMessage = MpState.BuildMismatchMessage(player.Name, playerMods, localMods);
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

    // ─── PATCH 6 : MainMenu.StartGame — bloquer côté client (filet final) ─────
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

    // ─── PATCH 7 : ModsPanel.GenerateMods — diagnostic ───────────────────────
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
