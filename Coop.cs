using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Dorfromantik;   // SegmentTypeId, SpecialTileId, QuestId, ChallengeId
using HarmonyLib;
using UnityEngine;

namespace DRMultiplayer
{
    // =====================================================================
    //  Coop: everything that touches the game. Main thread only.
    //
    //  Remote placements go through the game's own save-load machinery:
    //  TileDto mirrors TileData_003, TileGenerator.CreateTileFromSaveData
    //  rebuilds the tile (segments, quest state, special tiles) and
    //  TilePlacer.PlaceTileDirectly puts it on the board with all placement
    //  broadcasts firing at isPlacedByPlayer=false — which means: no local
    //  stack advance, no autosave-validation, no undo turn. We then call
    //  RewardSystem.AddTilePlacementScore ourselves so score/quests stay in
    //  lockstep with the placing machine, while the ApplyingRemote flag makes
    //  a Harmony patch swallow RewardSystem.GainTiles — reward tiles belong
    //  to the player who placed the tile, never to the spectator side.
    // =====================================================================
    internal static class Coop
    {
        /// <summary>True while a remote-originated placement (or world rebuild) is being
        /// applied. Patches use it to suppress tile rewards on this machine.</summary>
        internal static bool ApplyingRemote;

        /// <summary>True while the guest replays its own host-approved placement through
        /// the normal game path; lets it pass the PlaceCurrentTile intercept.</summary>
        internal static bool BypassPlaceIntercept;

        /// <summary>True during any programmatic placement; forces tile-mesh rotations to
        /// be instant so the game's auto-fit tween can't visually desync the mesh.</summary>
        internal static bool NetPlacing;

        /// <summary>Gate for RewardSystem.GameOver — in coop the run only ends when BOTH
        /// players are out of tiles, coordinated by the host.</summary>
        internal static bool AllowGameOver;

        // ---------------- live game references ----------------

        internal class GameRefs
        {
            internal TilePlacer Placer;
            internal TileStack Stack;
            internal TileSlotPreviewer Previewer;
            internal World World;
            internal TileGenerator Generator;
            internal RewardSystem Rewards;
            internal InputRouter Input;
            internal BiomeSectionManager BiomeSections;
            internal PreplacedTileSectionManager PreplacedSections;
            internal LoadingProgressRouter LoadingRouter;
            internal AutoSaver AutoSaver;
        }

        private static GameRefs _refs;

        /// <summary>Resolve (and cache) scene objects; null when not in a game scene.</summary>
        internal static GameRefs Refs
        {
            get
            {
                if (_refs != null && _refs.Placer != null && _refs.Stack != null) return _refs;
                _refs = null;
                var placer = UnityEngine.Object.FindObjectOfType<TilePlacer>();
                var stack = UnityEngine.Object.FindObjectOfType<TileStack>();
                var previewer = UnityEngine.Object.FindObjectOfType<TileSlotPreviewer>();
                var saveSys = UnityEngine.Object.FindObjectOfType<SaveLoadSystem>();
                if (placer == null || stack == null || previewer == null) return null;
                var t = Traverse.Create(placer);
                var refs = new GameRefs
                {
                    Placer = placer,
                    Stack = stack,
                    Previewer = previewer,
                    World = t.Field("world").GetValue<World>(),
                    Generator = t.Field("tileGenerator").GetValue<TileGenerator>(),
                    Rewards = t.Field("rewardSystem").GetValue<RewardSystem>(),
                    Input = t.Field("inputRouter").GetValue<InputRouter>(),
                };
                if (saveSys != null)
                {
                    var ts = Traverse.Create(saveSys);
                    refs.BiomeSections = ts.Field("biomeSectionManager").GetValue<BiomeSectionManager>();
                    refs.PreplacedSections = ts.Field("preplacedTileSectionManager").GetValue<PreplacedTileSectionManager>();
                    refs.LoadingRouter = ts.Field("loadingProgressRouter").GetValue<LoadingProgressRouter>();
                }
                refs.AutoSaver = UnityEngine.Object.FindObjectOfType<AutoSaver>();
                if (refs.World == null || refs.Generator == null || refs.Rewards == null) return null;
                _refs = refs;
                return _refs;
            }
        }

        internal static bool InGame => Refs != null;

        internal static GameModeId CurrentGameMode =>
            OverwritingSingleton<GameSession>.Instance ? OverwritingSingleton<GameSession>.Instance.GameMode.id : GameModeId.Undefined;

        /// <summary>Non-initial tiles on the board — the shared world size.</summary>
        internal static int PlacedTileCount()
        {
            var r = Refs;
            return r == null ? 0 : r.World.GetAllPlacedTiles().Count(x => !x.IsInitialTile);
        }

        internal static SyncStamp Stamp()
        {
            var r = Refs;
            return new SyncStamp { placedTiles = PlacedTileCount(), score = r?.Rewards.Score ?? 0 };
        }

        // ---------------- save & re-host (Phase 3) ----------------

        /// <summary>Stable key for the current world, matching a companion save file to the
        /// game's own classic save. The biome + preplaced seeds are chosen once when the world
        /// is generated and never change while it's played, so they survive quit/continue and
        /// uniquely identify this world. Null when the seed sources aren't available yet.</summary>
        internal static string SaveKey()
        {
            var r = Refs;
            if (r?.BiomeSections == null || r.PreplacedSections == null) return null;
            return $"{r.BiomeSections.Seed}_{r.PreplacedSections.Seed}";
        }

        /// <summary>The current top-of-stack (the tile about to be placed) as a dto, or null when
        /// there's no current tile. This is the "next tile" whose face we persist + restore.</summary>
        internal static TileDto TopOfStackDto()
        {
            var r = Refs;
            var cur = r?.Placer.CurrentTile;
            if (cur == null) return null;
            var dto = ToDto(cur);
            dto.rot = cur.RotationIndex;   // keep the face orientation; grid pos is irrelevant here
            return dto;
        }

        /// <summary>Restore a (re)joining player's own stack: set the remaining-tile count and,
        /// when possible, reproduce the exact top-of-stack tile the player had when the game was
        /// saved. Height is authoritative; the top-of-stack face is best-effort — if it can't be
        /// rebuilt we keep the randomly generated one (documented limitation). Fully defensive so
        /// a stack hiccup can never break the join.</summary>
        internal static void SetGuestStack(int height, TileDto top)
        {
            var r = Refs;
            if (r == null || height < 0) return;
            try
            {
                r.Stack.SetHeight(height);   // SetHeight(0) empties the stack (out of tiles)

                // Reproduce the saved top-of-stack by swapping stack[0] for a tile rebuilt from
                // the saved dto. ReplaceStackedTile(randomizeSeed:false, generateDuplicate:false)
                // adopts our tile directly, re-initializes it as a prebuilt stack tile and makes
                // it the current tile via DisplayStack — i.e. the game's own stack-editing path.
                if (top != null && r.Stack.Height > 0)
                {
                    var tile = r.Generator.CreateTileFromSaveData(ToTileData(top));
                    if (tile != null)
                    {
                        tile.Rotate(top.rot, animate: false);
                        r.Stack.ReplaceStackedTile(0, tile, randomizeSeed: false, generateDuplicate: false);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"restoring saved stack (height {height}) failed: {e.Message}");
            }
        }

        // ---------------- tile <-> wire conversion ----------------

        internal static TileDto ToDto(Tile tile)
        {
            var td = new TileData_003(tile);   // game-native capture incl. quest/special state
            var dto = new TileDto
            {
                x = td.gridPos[0],
                y = td.gridPos[1],
                rot = td.rotation,
                seed = td.seed,
                specialId = (int)td.specialTileId,
            };
            if (td.segments != null)
                dto.segs = td.segments.Select(s => new SegDto { g = (int)s.groupType, s = (int)s.segmentType, r = s.rotation }).ToList();
            if (td.questTileData != null && td.questTileData.questTileId != QuestTileId.Undefined)
            {
                var q = td.questTileData;
                dto.questTileId = (int)q.questTileId;
                dto.questActive = q.questActive;
                dto.questQueueIndex = q.questQueueIndex;
                dto.targetValue = q.targetValue;
                dto.questLevel = q.questLevel;
                dto.questId = (int)q.questId;
                dto.unlockedChallengeId = (int)q.unlockedChallengeId;
            }
            return dto;
        }

        internal static TileData_003 ToTileData(TileDto dto)
        {
            // TileData_003 only has capture constructors; build an empty one and fill it.
            var td = (TileData_003)FormatterServices.GetUninitializedObject(typeof(TileData_003));
            td.version = 3;
            td.gridPos = new[] { dto.x, dto.y };
            td.rotation = dto.rot;
            td.seed = dto.seed;
            td.specialTileId = (SpecialTileId)dto.specialId;
            td.segments = new List<SegmentData002>();
            if (dto.segs != null)
                foreach (var s in dto.segs)
                    td.segments.Add(new SegmentData002
                    {
                        groupType = (GroupTypeId)s.g,
                        segmentType = (SegmentTypeId)s.s,
                        rotation = s.r,
                    });
            if (dto.questTileId != (int)QuestTileId.Undefined)
                td.questTileData = new QuestTileData_002
                {
                    questTileId = (QuestTileId)dto.questTileId,
                    questActive = dto.questActive,
                    questQueueIndex = dto.questQueueIndex,
                    targetValue = dto.targetValue,
                    questLevel = dto.questLevel,
                    questId = (QuestId)dto.questId,
                    unlockedChallengeId = (ChallengeId)dto.unlockedChallengeId,
                };
            return td;
        }

        /// <summary>Rough identity check: is the local preview tile still the tile we asked
        /// the host to place? (It can change if the player discards during the round-trip.)</summary>
        internal static bool MatchesDto(Tile tile, TileDto dto)
        {
            if (tile == null) return false;
            var cur = ToDto(tile);
            return cur.seed == dto.seed
                && cur.questTileId == dto.questTileId
                && cur.specialId == dto.specialId
                && (cur.segs?.Count ?? 0) == (dto.segs?.Count ?? 0);
        }

        // ---------------- applying placements ----------------

        /// <summary>Build the Tile object for a dto (rotated, edges initialized) without
        /// placing it. Destroy it with tile.DestroyTile() if it ends up unused.</summary>
        internal static Tile BuildTile(TileDto dto)
        {
            var r = Refs;
            var tile = r.Generator.CreateTileFromSaveData(ToTileData(dto));
            if (tile == null) return null;
            tile.Rotate(dto.rot, animate: false);
            tile.Initialize();   // builds tileEdges so TileFitter can inspect it pre-placement
            return tile;
        }

        /// <summary>Host-side verdict + application for a guest place request.</summary>
        internal static bool TryApplyRequest(TileDto dto, out string reason)
        {
            var r = Refs;
            if (r == null) { reason = "host is not in a game"; return false; }
            var pos = new Vector2Int(dto.x, dto.y);
            if (r.World.GetTile(pos) != null && r.World.GetTile(pos).State == TileState.placed)
            {
                reason = "slot already occupied";
                return false;
            }
            var slot = r.Previewer.GetTileSlot(pos);
            if (slot == null) { reason = "no open slot at that position"; return false; }
            var tile = BuildTile(dto);
            if (tile == null) { reason = "tile data could not be rebuilt"; return false; }
            if (!TileFitter.TileFitsInSlot(tile, slot, 0))
            {
                tile.DestroyTile();
                reason = "illegal placement (rail/river mismatch)";
                return false;
            }
            ApplyConstructed(tile, pos, addScore: true);
            reason = null;
            return true;
        }

        /// <summary>Apply a remote placement that we already trust (host broadcast).</summary>
        internal static bool ApplyRemote(TileDto dto, bool addScore)
        {
            var r = Refs;
            if (r == null) return false;
            var pos = new Vector2Int(dto.x, dto.y);
            var existing = r.World.GetTile(pos);
            if (existing != null && existing.State == TileState.placed) return false;
            var tile = BuildTile(dto);
            if (tile == null) return false;
            ApplyConstructed(tile, pos, addScore);
            return true;
        }

        /// <summary>The shared placement core. Lifts our hovering preview tile out of the
        /// world first — the preview participates in group merges, and quest fulfillment
        /// executes during placement broadcasts, so a floating preview could complete a
        /// quest here that never completed on the placing machine.</summary>
        internal static void ApplyConstructed(Tile tile, Vector2Int pos, bool addScore)
        {
            var r = Refs;
            var prevSlot = r.Placer.CurrentTileSlot;
            if (r.Placer.CurrentTile != null)
                r.Placer.ShowPreviewTileAt(null);

            ApplyingRemote = true;
            NetPlacing = true;
            try
            {
                r.Placer.PlaceTileDirectly(tile, pos);
                if (addScore)
                    r.Rewards.AddTilePlacementScore(tile);
            }
            finally
            {
                ApplyingRemote = false;
                NetPlacing = false;
            }

            // Put the preview back where it was (unless that slot just got filled/destroyed).
            if (prevSlot && r.World.GetTile(prevSlot.GridPos) == null && r.Placer.CurrentTile != null)
                r.Placer.ShowPreviewTileAt(prevSlot);
            r.Placer.UpdateTileSlotValidity();

            // The game only flags the autosave dirty for player placements; make sure a
            // host whose partner does all the placing still autosaves the shared world.
            if (Session.Role == SessionRole.Host && r.AutoSaver != null)
                Traverse.Create(r.AutoSaver).Field("validSaveGame").SetValue(true);
        }

        // ---------------- snapshots (join + desync recovery) ----------------

        internal static SnapshotDto BuildSnapshot()
        {
            var r = Refs;
            var rw = r.Rewards;
            var snap = new SnapshotDto
            {
                score = rw.Score,
                level = rw.Level,
                placedTileCount = rw.PlacedTileCount,
                perfectPlacements = rw.PerfectPlacementCount,
                questsFulfilled = rw.QuestFulfilledCount,
                questsFailed = rw.QuestFailedCount,
                consecutivePerfectFits = rw.ConsecutivePerfectFits,
                consecutiveNoRotate = rw.ConsecutivePlacementsWithoutRotate,
                surroundedTiles = rw.SurroundedTilesCount,
                tiles = r.World.GetAllPlacedTiles().Where(x => !x.IsInitialTile).Select(ToDto).ToList(),
                hostStack = r.Stack.RawHeight,
            };
            if (r.BiomeSections != null) snap.biomeSeed = r.BiomeSections.Seed;
            if (r.PreplacedSections != null)
            {
                snap.preplacedSeed = r.PreplacedSections.Seed;
                snap.preplacedTiles = r.PreplacedSections.predefinedPreplacedTiles
                    .Select(kv => new PreplacedDto { sx = kv.Key.x, sy = kv.Key.y, questTileId = (int)kv.Value }).ToList();
                snap.pendingChallenges = r.PreplacedSections.pendingLockedChallenges.Select(q => (int)q.id).ToList();
            }
            return snap;
        }

        /// <summary>Fresh join: adopt the host's world-generation seeds (biome look and
        /// preplaced challenge tiles), then place every host tile. Mirrors the order the
        /// game's own SetupLoadedGame uses.</summary>
        internal static void ApplySnapshotOnJoin(SnapshotDto snap)
        {
            var r = Refs;
            if (r.BiomeSections != null)
                r.BiomeSections.SetWorldSeed(r.World.transform, snap.biomeSeed);
            if (r.PreplacedSections != null)
            {
                var pre = new List<PreplacedTileData_002>();
                if (snap.preplacedTiles != null)
                    pre.AddRange(snap.preplacedTiles.Select(p =>
                        new PreplacedTileData_002(new Vector2Int(p.sx, p.sy), (QuestTileId)p.questTileId)));
                r.PreplacedSections.SetupPredefinedTiles(pre);
                r.PreplacedSections.SetupPendingLockedChallenges(
                    snap.pendingChallenges?.Select(i => (ChallengeId)i).ToList() ?? new List<ChallengeId>());
                r.PreplacedSections.SetWorldSeed(r.World.transform, snap.preplacedSeed);
            }
            PlaceSnapshotTiles(snap);
            SetStats(snap);
            r.World.UpdateBiomesForAllTiles(forceUpdateBiomes: true);
            r.Placer.UpdateTileSlotValidity();
        }

        /// <summary>Desync recovery: throw away our copy of the shared world and rebuild it
        /// from the host's authoritative snapshot. Seeds are left alone — they were adopted
        /// at join and regenerating sections mid-game would disturb connected hints.</summary>
        internal static void ResyncWorld(SnapshotDto snap)
        {
            var r = Refs;
            foreach (var tile in r.World.GetAllPlacedTiles().Where(x => !x.IsInitialTile).ToList())
                r.Placer.DestroyTile(tile);
            PlaceSnapshotTiles(snap);
            SetStats(snap);
            r.Placer.UpdateTileSlotValidity();
        }

        private static void PlaceSnapshotTiles(SnapshotDto snap)
        {
            var r = Refs;
            var prevSlot = r.Placer.CurrentTileSlot;
            if (r.Placer.CurrentTile != null)
                r.Placer.ShowPreviewTileAt(null);
            ApplyingRemote = true;
            NetPlacing = true;
            // Snapshot tiles arrive in arbitrary order, so intermediate board states never
            // existed on the host. QuestWatcher defers fulfillment execution while
            // LoadingProgressRouter.IsLoading — exactly how the game's own save-load keeps
            // half-loaded worlds from completing/failing quests. Borrow that gate.
            SetLoadingGate(true);
            try
            {
                foreach (var dto in snap.tiles)
                {
                    var pos = new Vector2Int(dto.x, dto.y);
                    if (r.World.GetTile(pos) != null) continue;
                    var tile = r.Generator.CreateTileFromSaveData(ToTileData(dto));
                    if (tile == null)
                    {
                        Plugin.Log.LogWarning($"snapshot tile at {pos} could not be rebuilt — skipped");
                        continue;
                    }
                    tile.Rotate(dto.rot, animate: false);
                    r.Placer.PlaceTileDirectly(tile, pos);
                }
            }
            finally
            {
                SetLoadingGate(false);
                ApplyingRemote = false;
                NetPlacing = false;
            }
            if (prevSlot && r.World.GetTile(prevSlot.GridPos) == null && r.Placer.CurrentTile != null)
                r.Placer.ShowPreviewTileAt(prevSlot);
        }

        private static void SetLoadingGate(bool loading)
        {
            var router = Refs?.LoadingRouter;
            if (router != null)
                Traverse.Create(router).Property("IsLoading").SetValue(loading);
        }

        /// <summary>Overwrite the local score/statistics with the host's numbers.
        /// ResetScore pokes OnScoreChanged so the score UI refreshes.</summary>
        internal static void SetStats(SnapshotDto s)
        {
            var rw = Refs.Rewards;
            var t = Traverse.Create(rw);
            t.Property("Level").SetValue(s.level);
            t.Property("PlacedTileCount").SetValue(s.placedTileCount);
            t.Property("PerfectPlacementCount").SetValue(s.perfectPlacements);
            t.Property("QuestFulfilledCount").SetValue(s.questsFulfilled);
            t.Property("ConsecutivePerfectFits").SetValue(s.consecutivePerfectFits);
            t.Property("ConsecutivePlacementsWithoutRotate").SetValue(s.consecutiveNoRotate);
            rw.QuestFailedCount = s.questsFailed;
            rw.SurroundedTilesCount = s.surroundedTiles;
            rw.ResetScore(s.score);
        }

        // ---------------- tile stack manipulation (coop gifting) ----------------

        /// <summary>Take up to <paramref name="count"/> tiles off our own stack (bottom
        /// first) and report how many were actually removed.</summary>
        internal static int RemoveStackTiles(int count)
        {
            var r = Refs;
            if (r == null) return 0;
            int have = r.Stack.RawHeight;
            count = Mathf.Clamp(count, 0, have);
            if (count == 0) return 0;
            if (count >= have && r.Placer.CurrentTile != null)
                r.Placer.RemoveCurrentTile();   // giving away the last tile: retire the hovering preview too
            r.Stack.SetHeight(have - count);
            return count;
        }

        /// <summary>Receive gifted tiles — same animated flow as quest rewards.</summary>
        internal static void GrantStackTiles(int count)
        {
            var r = Refs;
            if (r == null || count <= 0) return;
            r.Stack.AddRandomTiles(count, useDelay: true);
        }

        /// <summary>Self-heal: after refilling an empty stack (gift while at 0 tiles) the
        /// game's delayed-add path never re-arms the preview tile. Detect and fix.</summary>
        internal static void EnsureCurrentTile()
        {
            var r = Refs;
            if (r == null) return;
            if (r.Placer.CurrentTile == null && r.Stack.Height > 0 && r.Stack.RawHeight == r.Stack.Height)
                r.Stack.DisplayStack();
        }

        // ---------------- player color palette ----------------
        //
        // One palette for the whole mod: player index (== playerId; host = 0, guests 1..3)
        // maps to a well-separated color. Reused for ghost tints AND overlay/HUD rich-text
        // labels so a player's color means the same thing everywhere. See DESIGN.md.

        private static readonly Color[] Palette =
        {
            new Color(0.40f, 0.80f, 1.00f),   // 0 host:  soft cyan
            new Color(1.00f, 0.60f, 0.20f),   // 1:       warm orange
            new Color(0.45f, 0.90f, 0.45f),   // 2:       green
            new Color(1.00f, 0.45f, 0.80f),   // 3:       magenta/pink
        };

        /// <summary>The color for a player index (wraps defensively if ever out of range).</summary>
        internal static Color PlayerColor(int idx) => Palette[((idx % Palette.Length) + Palette.Length) % Palette.Length];

        /// <summary>The same color as an "rrggbb" hex string for IMGUI &lt;color=#…&gt; rich text.</summary>
        internal static string PlayerColorHex(int idx)
        {
            var c = PlayerColor(idx);
            return $"{(int)(c.r * 255):x2}{(int)(c.g * 255):x2}{(int)(c.b * 255):x2}";
        }

        // ---------------- per-player preview ghosts ----------------
        //
        // Each player broadcasts its own hovering preview tile; everyone else renders a
        // translucent, non-interactive copy at the same grid pos + rotation, tinted in that
        // player's color, so you can see what each other player is about to place. Ghosts are
        // built via CreateTileFromSaveData WITHOUT PlaceTileDirectly, so they never enter the
        // world, element groups, neighbour lists, quests, rewards or the sync stamp — inert.
        //
        // Keyed by playerId so up to 3 remote previews can be shown at once.

        private class GhostEntry
        {
            internal Tile Tile;
            internal string Sig;   // identity only (seed/special/quest/segCount), NOT pos/rot
            internal int Rot;      // tracks the ghost's current RotationIndex (Rotate is relative)
        }

        private static readonly Dictionary<int, GhostEntry> _ghosts = new Dictionary<int, GhostEntry>();

        /// <summary>The local hovering preview tile as a dto (with grid pos + rotation), or null
        /// when nothing is visibly previewed. Used to broadcast our preview to the partner.</summary>
        internal static TileDto CurrentPreviewDto()
        {
            var r = Refs;
            if (r == null) return null;
            var cur = r.Placer.CurrentTile;
            var slot = r.Placer.CurrentTileSlot;
            // gameObject.activeSelf is false while the preview hovers an invalid/occupied
            // slot — treat that as "no visible preview" so the partner's ghost hides too.
            if (cur == null || slot == null || !cur.gameObject.activeSelf) return null;
            var dto = ToDto(cur);
            dto.x = slot.GridPos.x;
            dto.y = slot.GridPos.y;
            dto.rot = cur.RotationIndex;
            return dto;
        }

        /// <summary>Render/update the translucent ghost of a player's preview tile, tinted in
        /// that player's color. Pass a null dto to hide that player's ghost. Fully defensive:
        /// any failure logs and clears that ghost so a rendering hiccup can never break the
        /// core session.</summary>
        internal static void UpdatePartnerPreview(int playerId, TileDto dto)
        {
            try
            {
                var r = Refs;
                if (r == null || dto == null) { ClearPartnerPreview(playerId); return; }

                _ghosts.TryGetValue(playerId, out var entry);

                // Rebuild only when the tile IDENTITY changes; pos/rot are cheap updates.
                string idSig = $"{dto.seed},{dto.specialId},{dto.questTileId},{dto.segs?.Count ?? 0}";
                if (entry == null || entry.Tile == null || idSig != entry.Sig)
                {
                    ClearPartnerPreview(playerId);
                    var tile = r.Generator.CreateTileFromSaveData(ToTileData(dto));
                    if (tile == null) return;
                    tile.Initialize();
                    entry = new GhostEntry { Tile = tile, Sig = idSig, Rot = 0 };  // fresh tile starts at RotationIndex 0
                    _ghosts[playerId] = entry;
                    tile.transform.SetParent(r.World.transform, worldPositionStays: false);

                    // Apply a biome so the pooled decoration/ground visuals are ALLOWED to
                    // spawn. InstanceableVisual.ChangeDisplayState(Regular) early-returns
                    // unless the tile has had a biome applied; stackBiome is what the game
                    // uses for unplaced hand tiles, so grab it off the TileStack.
                    var biome = Traverse.Create(r.Stack).Field("stackBiome").GetValue<Biome>();
                    if (biome != null) BiomeManager.ApplyBiomeToTile(tile, biome, null, true);

                    tile.gameObject.SetActive(true);
                    tile.Rotate(dto.rot, animate: false); entry.Rot = dto.rot;

                    // placementPreview -> every visible InstanceableVisual goes to Regular
                    // display, spawning the pooled MeshRenderers (ground, trees, houses) as
                    // real child renderers that GetComponentsInChildren<Renderer> can find.
                    tile.ChangeTileState(TileState.placementPreview);
                    DisableGhostColliders(tile.gameObject);
                    MakeGhostly(tile.gameObject, playerId);   // MUST run AFTER ChangeTileState
                }

                var ghost = entry.Tile;

                // Position: prefer the exact slot transform (accounts for any world offset),
                // fall back to grid math under World.transform when no slot exists there.
                // Lift +0.03 on Y to avoid z-fighting with the board ground.
                var pos = new Vector2Int(dto.x, dto.y);
                var slot = r.Previewer.GetTileSlot(pos);
                if (slot != null) ghost.transform.position = slot.transform.position + Vector3.up * 0.03f;
                else ghost.transform.localPosition = GridCalculator.GridToWorldPos(pos) + Vector3.up * 0.03f;

                // Rotation: Tile.Rotate is RELATIVE, so apply only the delta (mod 6).
                int delta = ((dto.rot - entry.Rot) % 6 + 6) % 6;
                if (delta != 0) { ghost.Rotate(delta, animate: false); entry.Rot = dto.rot; }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"partner preview ghost update failed: {e}");
                ClearPartnerPreview(playerId);
            }
        }

        /// <summary>Hide and destroy one player's ghost.</summary>
        internal static void ClearPartnerPreview(int playerId)
        {
            // The ghost is never registered anywhere, so destroying the root GameObject fully
            // cleans it up. Do NOT call Tile.DestroyTile() — with animate=false it only tears
            // down element-group children and logs errors when neighbours exist.
            if (_ghosts.TryGetValue(playerId, out var entry))
            {
                if (entry.Tile != null) UnityEngine.Object.Destroy(entry.Tile.gameObject);
                _ghosts.Remove(playerId);
            }
        }

        /// <summary>Hide and destroy every player's ghost (session teardown).</summary>
        internal static void ClearAllPartnerPreviews()
        {
            foreach (var entry in _ghosts.Values)
                if (entry.Tile != null) UnityEngine.Object.Destroy(entry.Tile.gameObject);
            _ghosts.Clear();
        }

        // How solid the ghost reads. Phase 1 shipped a flat 0.5-alpha hologram; Phase 2 bumps
        // it so the preview is clearly-more-solid without hiding the board underneath.
        private const float GhostAlpha = 0.72f;
        // How much of the tile's REAL material color to fold into the player tint (0 = pure
        // player color, 1 = pure original). ~0.45 keeps the player's color dominant (you can
        // still tell whose ghost it is at a glance) while letting the biome/feature hues bleed
        // through enough to read what's actually on the tile.
        private const float GhostOriginalBlend = 0.45f;

        // Translucent tint materials, cached by (colorIdx, quantized-original-color). The game's
        // custom tile shaders ignore the URP _Surface/alpha runtime switch, so we can't just make
        // the real materials translucent — instead we override each renderer with a guaranteed
        // translucent material whose color is Lerp(playerColor, thisRenderer'sOriginalColor). That
        // keeps the ghost obviously "player X" while hinting the tile's real contents. We quantize
        // the original color to 4 bits/channel before keying so the cache stays bounded (at most
        // ~16 shades * a handful of colors * <=4 players) — we never leak a fresh material per tile.
        private static readonly Dictionary<long, Material> _ghostMats = new Dictionary<long, Material>();

        /// <summary>Read a renderer material's authored base color, trying the common property
        /// names (URP _BaseColor / legacy _Color, plus the game's tile shader _FrontCol). Returns
        /// null when nothing readable exists so the caller falls back to the flat player color.</summary>
        private static Color? TryReadBaseColor(Material m)
        {
            try
            {
                if (m == null) return null;
                if (m.HasProperty("_BaseColor")) return m.GetColor("_BaseColor");
                if (m.HasProperty("_Color")) return m.GetColor("_Color");
                if (m.HasProperty("_FrontCol")) return m.GetColor("_FrontCol");
            }
            catch { /* some pooled materials throw on odd shaders — treat as "no color" */ }
            return null;
        }

        /// <summary>Bounded cache key: player color index in the low byte, a has-color flag, and
        /// the original color quantized to 4 bits per channel. Keeps distinct materials finite.</summary>
        private static long GhostMatKey(int colorIdx, Color? orig)
        {
            long k = (uint)colorIdx & 0xFF;
            if (orig.HasValue)
            {
                var o = orig.Value;
                int r = Mathf.Clamp((int)(o.r * 15f), 0, 15);
                int g = Mathf.Clamp((int)(o.g * 15f), 0, 15);
                int b = Mathf.Clamp((int)(o.b * 15f), 0, 15);
                k |= 0x100;                                   // "has readable original color" flag
                k |= (long)((r << 8) | (g << 4) | b) << 16;
            }
            return k;
        }

        /// <summary>A translucent player-tinted material for one (colorIdx, originalColor) pair,
        /// created on demand and cached. Color = Lerp(playerColor, originalColor, GhostOriginalBlend)
        /// at GhostAlpha, or the flat player color when no original color was readable.</summary>
        private static Material GhostMaterial(int colorIdx, Color? orig)
        {
            long key = GhostMatKey(colorIdx, orig);
            if (_ghostMats.TryGetValue(key, out var cached) && cached != null) return cached;

            var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Transparent") ?? Shader.Find("Sprites/Default");
            var m = new Material(sh) { name = $"DRMP_GhostTint_{colorIdx}_{key:X}" };
            var pc = PlayerColor(colorIdx);
            // Blend a hint of the tile's real color in, but keep the player color dominant.
            var rgb = orig.HasValue ? Color.Lerp(pc, orig.Value, GhostOriginalBlend) : pc;
            var col = new Color(rgb.r, rgb.g, rgb.b, GhostAlpha);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);
            if (m.HasProperty("_Color")) m.SetColor("_Color", col);
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_SURFACE_TYPE_OPAQUE");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = 3000;   // Transparent (RenderQueue enum isn't in the referenced CoreModule)
            _ghostMats[key] = m;
            return m;
        }

        /// <summary>Override every renderer under the ghost with a translucent player-tinted
        /// material blended from that renderer's ORIGINAL color, so the ghost reads as "player X"
        /// while hinting the tile's real contents. Text renderers (quest-marker numbers) are the
        /// exception: their SDF font material is left intact — swapping it turns the glyphs to
        /// garbage — and they are instead recolored via the TMP component below so the partner can
        /// still READ the quest number. Uses sharedMaterials (not .materials): pooled decoration
        /// objects get their real sharedMaterial reassigned by the game via ChangeDisplayState next
        /// time it reuses them, so this self-heals; plain child renderers die with the ghost.
        /// Materials are cached, never disposed. Per-renderer failures are swallowed so a single
        /// odd renderer can never break the ghost (which itself can never break the session).</summary>
        private static void MakeGhostly(GameObject go, int colorIdx)
        {
            var pc = PlayerColor(colorIdx);
            foreach (var rend in go.GetComponentsInChildren<Renderer>(true))
            {
                try
                {
                    // A TextMeshPro's MeshRenderer lives on the same GameObject as the TMP
                    // component. Skip it here (keep the font material); it's tinted below.
                    if (rend.GetComponent<TMPro.TextMeshPro>() != null) continue;

                    // Read the renderer's authored color BEFORE we override it, so the tint can
                    // fold in a hint of the real biome/feature hue.
                    var orig = TryReadBaseColor(rend.sharedMaterial);
                    var mat = GhostMaterial(colorIdx, orig);

                    var arr = rend.sharedMaterials;             // cover multi-submesh renderers
                    for (int i = 0; i < arr.Length; i++) arr[i] = mat;
                    rend.sharedMaterials = arr;
                    rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    rend.receiveShadows = false;
                }
                catch (Exception e) { Plugin.Log.LogWarning($"ghost renderer tint skipped: {e.Message}"); }
            }

            // Quest-marker number(s): the target value renders as a world-space TMPro.TextMeshPro
            // (QuestElementIcon.countLabel). Recolor it to the player's ghost tint at the ghost
            // alpha WITHOUT touching the shared SDF font material — that keeps it legible, which is
            // the whole point of showing the partner's quest number. .color carries the alpha.
            foreach (var tmp in go.GetComponentsInChildren<TMPro.TextMeshPro>(true))
            {
                try { tmp.color = new Color(pc.r, pc.g, pc.b, GhostAlpha); }
                catch (Exception e) { Plugin.Log.LogWarning($"ghost quest-number tint skipped: {e.Message}"); }
            }
        }

        /// <summary>Disable every collider on the ghost so it never intercepts the local
        /// player's slot picking / raycasts.</summary>
        private static void DisableGhostColliders(GameObject go)
        {
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
        }

        /// <summary>Trigger the real game-over screen (both stacks empty). setHighscore is
        /// false — a two-player score doesn't belong on the solo leaderboard.</summary>
        internal static void TriggerCoopGameOver()
        {
            var r = Refs;
            if (r == null || r.Rewards.IsGameOver) return;
            AllowGameOver = true;
            try { r.Rewards.GameOver(animate: true, setHighscore: false); }
            finally { AllowGameOver = false; }
        }
    }
}
