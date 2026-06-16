using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PerAspera.Core;
using PerAspera.GameAPI.Multiplayer;

namespace PerAspera.MpModRecon
{
    /// <summary>
    /// MpModReconMod — vérification de compatibilité des mods en multijoueur.
    /// Délègue toute la logique au SDK MultiplayerModsService (opt-in via Activate).
    /// </summary>
    [BepInPlugin("com.peraspera.wafhien.mpmodrecon", "MpModReconMod", "1.6.0")]
    public class MpModReconPlugin : BasePlugin
    {
        internal static LogAspera L = null!;

        public override void Load()
        {
            L = new LogAspera("MpModRecon");
            L.Info("=== MpModRecon v1.6.0 — délégation SDK MultiplayerModsService ===");

            var harmony = new Harmony("com.peraspera.wafhien.mpmodrecon");

            // Active les patches MP du SDK (join-time + start-time + modList injection)
            MultiplayerModsService.Activate(harmony);

            // S'abonner aux événements pour logging diagnostique
            MultiplayerModsService.OnModSetMismatch += (_, args) =>
                L.Warning($"[MISMATCH] {args.PlayerName} — {args.Message}");

            MultiplayerModsService.OnLobbyEntered += (_, _) =>
                L.Info("[LOBBY] Lobby entré — modset publié");
        }
    }
}
