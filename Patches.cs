using HarmonyLib;
using UnityEngine;

namespace DRMultiplayer
{
    // =====================================================================
    //  Game hooks. Chokepoints (verified against the decompile):
    //
    //  * TilePlacer.PlaceCurrentTile(TileSlot) — every player-initiated
    //    placement funnels through here. On the GUEST we intercept it and
    //    ask the host instead; the approved placement is replayed with
    //    Coop.BypassPlaceIntercept set.
    //  * TilePlacer.PlaceTile(Tile, Vector2Int, bool) [private] — fires for
    //    every completed placement with isPlacedByPlayer telling us whether
    //    it was the local player. The HOST broadcasts its own from here.
    //  * RewardSystem.GainTiles(int) — the single path through which perfect
    //    placements and fulfilled quests grant stack tiles. Suppressed while
    //    a remote placement is applied: rewards belong to whoever placed.
    //  * RewardSystem.GameOver — in coop, only fires when the host says so.
    //  * Dorfromantik.UndoTracker.Undo — undo would fork the shared world.
    //  * SaveLoadSystem.SaveActiveGame — the guest must not overwrite its
    //    own solo autosave with the host's world.
    //  * RewardSystem.UpdateHighscore — a two-player score stays off the
    //    solo leaderboards.
    // =====================================================================

    [HarmonyPatch(typeof(TilePlacer), nameof(TilePlacer.PlaceCurrentTile))]
    internal static class GuestPlacementInterceptPatch
    {
        private static bool Prefix(TilePlacer __instance, TileSlot targetSlot)
        {
            // Our own replay of a host-approved placement always passes straight through.
            if (Coop.BypassPlaceIntercept) return true;
            // No session (solo play) or the host in Coop: place normally, unchanged.
            if (Session.Role == SessionRole.Off) return true;
            if (!Session.Active) return false;             // handshaking: don't let the board change under us

            // Versus: only the ACTIVE player may place — this blocks both a non-active guest AND the
            // host when it isn't the host's turn. Coop is unaffected (IsLocalTurn is always true).
            if (Session.Mode == GameModeKind.Versus && !Session.IsLocalTurn)
            {
                Session.Info($"Not your turn — it's {Session.PlayerName(Session.ActivePlayerId)}'s move.");
                return false;
            }

            if (Session.Role == SessionRole.Guest)
            {
                Session.RequestPlace(__instance, targetSlot);  // no-op while a request is already pending
                return false;
            }
            return true;   // host, on its turn (or Coop): place through the normal game path
        }
    }

    [HarmonyPatch(typeof(TilePlacer), "PlaceTile")]
    internal static class HostBroadcastPlacementPatch
    {
        private static void Postfix(Tile newTile, bool isPlacedByPlayer)
        {
            if (!isPlacedByPlayer || Coop.ApplyingRemote) return;
            Session.OnLocalHostPlacement(newTile);
        }
    }

    [HarmonyPatch(typeof(RewardSystem), nameof(RewardSystem.GainTiles))]
    internal static class SuppressRemoteTileRewardsPatch
    {
        private static bool Prefix() => !Coop.ApplyingRemote;
    }

    [HarmonyPatch(typeof(RewardSystem), nameof(RewardSystem.GameOver))]
    internal static class CoopGameOverPatch
    {
        private static bool Prefix()
        {
            if (Session.Role == SessionRole.Off || Coop.AllowGameOver) return true;
            // Out of tiles but someone else is still playing: spectate, don't end the run.
            if (!Session.AllStacksEmpty())
                Session.Info("You're out of tiles — ask another player to send you some, or wait. The game ends when everyone's stacks are empty.");
            return false;
        }
    }

    [HarmonyPatch(typeof(Dorfromantik.UndoTracker), "Undo")]
    internal static class BlockUndoPatch
    {
        private static bool Prefix()
        {
            if (Session.Role == SessionRole.Off) return true;
            Session.Info("Undo is disabled in multiplayer.");
            return false;
        }
    }

    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.SaveActiveGame))]
    internal static class GuestSaveGuardPatch
    {
        private static bool Prefix() => Session.Role != SessionRole.Guest;
    }

    /// <summary>Phase 3 — save & re-host. The host autosaves the shared world normally (its own
    /// classic save); piggyback the mod's companion write here so per-player bookkeeping (the other
    /// players' remaining tiles + top-of-stack) is persisted alongside it. Runs only for the host,
    /// so the guest guard above (which skips the method) never reaches this postfix.</summary>
    [HarmonyPatch(typeof(SaveLoadSystem), nameof(SaveLoadSystem.SaveActiveGame))]
    internal static class HostCompanionSavePatch
    {
        private static void Postfix() => Session.OnHostAutosave();
    }

    [HarmonyPatch(typeof(RewardSystem), nameof(RewardSystem.UpdateHighscore))]
    internal static class NoCoopHighscorePatch
    {
        private static bool Prefix() => Session.Role == SessionRole.Off;
    }

    /// <summary>
    /// TileVisual.Rotate is the only thing that spins a tile mesh (a 0.1s DOTween when
    /// animate=true). When we place programmatically, the auto-fit tween started by
    /// ShowPreviewTileAt can land AFTER our instant rotation and leave the mesh at the
    /// wrong angle (logical rotation stays correct — a pure visual desync). Forcing
    /// rotations instant while we drive the placer prevents the tween from starting.
    /// </summary>
    [HarmonyPatch(typeof(TileVisual), "Rotate", new[] { typeof(int), typeof(bool) })]
    internal static class ForceInstantNetRotationPatch
    {
        private static void Prefix(ref bool animate)
        {
            if (Coop.NetPlacing) animate = false;
        }
    }

    // =====================================================================
    //  Overlay input blocking — the IMGUI overlay is not a uGUI element, so
    //  the game would otherwise click/drag/zoom straight through it.
    // =====================================================================

    [HarmonyPatch(typeof(Dorfromantik.CameraUtility), "PointerGameObject", new[] { typeof(int[]) })]
    internal static class BlockPointerOverUiPatch
    {
        private static GameObject _sentinel;
        private static void Postfix(ref GameObject __result)
        {
            if (__result != null || !Plugin.CursorOverOverlay()) return;
            if (_sentinel == null)
            {
                _sentinel = new GameObject("DRMultiplayerUiBlocker");
                Object.DontDestroyOnLoad(_sentinel);
            }
            __result = _sentinel;
        }
    }

    [HarmonyPatch(typeof(PointerDragInput), "ClickStarted")]
    internal static class BlockCameraDragPatch
    {
        private static bool Prefix() => !Plugin.CursorOverOverlay();
    }

    [HarmonyPatch(typeof(ScrollWheelInput), "Update")]
    internal static class BlockScrollPatch
    {
        private static bool Prefix() => !Plugin.CursorOverOverlay();
    }

    [HarmonyPatch(typeof(MouseController), "Update")]
    internal static class BlockClickThroughPatch
    {
        private static void Postfix(MouseController __instance)
        {
            if (!Plugin.CursorOverOverlay()) return;
            Traverse.Create(__instance).Field("currentTileSlot").SetValue(null);
        }
    }
}
