using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

namespace DRMultiplayer
{
    internal enum SessionRole { Off, Host, Guest }
    // Host is "Hosting" (and Active) the moment it starts hosting in-game — guests join
    // incrementally without changing the host's own play. A guest walks
    // Connecting -> AwaitWelcome -> Paired.
    internal enum SessionState { Idle, Hosting, Connecting, AwaitWelcome, Paired }

    // =====================================================================
    //  Session: the multiplayer state machine. Runs entirely on the main
    //  thread — Plugin.Update drains NetHub.Events into HandleEvent and
    //  calls Poll every frame.
    //
    //  N-player coop: one host (playerId 0) + up to 3 guests (playerId 1..3),
    //  one shared world, one shared score, independent tile stacks.
    //
    //  Authority model: the host orders ALL placements. Its own tiles go down
    //  immediately and are broadcast to every guest; a guest never places
    //  directly — it asks, the host validates (slot free + TileFitter legality)
    //  and applies, replies to the requester AND broadcasts the tile (tagged
    //  with the origin playerId) to all OTHER guests. Only on the accept does
    //  the requesting guest replay the placement through the normal game path
    //  (advancing its stack, earning its rewards); it ignores the broadcast it
    //  originated. A SyncStamp (tile count + score) rides on every placement
    //  message and any mismatch triggers an automatic full-world resync from
    //  the host.
    // =====================================================================
    internal static class Session
    {
        internal const int MaxGuests = 3;   // host + 3 = 4 players max

        internal static SessionRole Role { get; private set; } = SessionRole.Off;
        internal static SessionState State { get; private set; } = SessionState.Idle;

        // The host is "active" (playing, ordering placements) as soon as it is hosting in-game,
        // even before any guest has joined; a guest is active once fully paired.
        internal static bool Active =>
            Role == SessionRole.Host ? State == SessionState.Hosting :
            Role == SessionRole.Guest ? State == SessionState.Paired : false;

        /// <summary>This machine's stable player id: host = 0, guest learns it in Welcome.</summary>
        internal static int LocalPlayerId { get; private set; }

        /// <summary>The game mode this session runs in. Coop today; Versus lands in Phase 4 —
        /// the field/plumbing (persisted in the companion, sent in Welcome) exists now so the
        /// mode a world was hosted in is remembered when it's continued.</summary>
        internal static GameModeKind Mode { get; private set; } = GameModeKind.Coop;

        /// <summary>True when the host started hosting on a world that had a companion save — i.e.
        /// it's continuing a previously-saved multiplayer game. Drives a small UI hint.</summary>
        internal static bool ContinuingSaved { get; private set; }

        internal static int ResyncCount { get; private set; }

        // ---------------- Versus mode (Phase 4) ----------------
        //
        // Versus is turn-based: only ONE player (the "active" player) may place at a time, and each
        // player keeps an INDIVIDUAL score. The host is authoritative for both — it owns the active
        // player id (advanced after every accepted placement, ascending playerId, skipping anyone
        // out of tiles) and computes every player's score by measuring the shared RewardSystem.Score
        // delta around the placement it applies (host-own via the normal path, guest-own via
        // TryApplyRequest) and attributing it to whoever placed. All of this is gated behind
        // Mode == Versus; Coop is completely unaffected (shared score, simultaneous play).

        // Host: whose turn it is (also mirrored on guests via TurnPacket). 0 = host.
        private static int _activePlayerId;
        // Host: the host's own per-player score (id 0). Guests' scores live on GuestConn.VersusScore.
        private static int _hostVersusScore;
        // Host: the RewardSystem.Score value already attributed to some player. The next placement's
        // contribution is (current Score − this), folding in any quest/perfect bonus that resolved.
        private static int _versusScoreBaseline;
        // Host: the host's OWN placement completed; the shared score/stack settle within the frame,
        // so we reconcile + advance one frame later in Poll (guest placements are handled inline in
        // OnPlaceRequest, where the host's state is already settled).
        private static bool _hostPlacementPending;

        /// <summary>Whose turn it is (Versus). Meaningless in Coop; callers gate on Mode.</summary>
        internal static int ActivePlayerId => _activePlayerId;

        /// <summary>True when this machine may place right now: always in Coop; in Versus only when
        /// we're the active player.</summary>
        internal static bool IsLocalTurn => Mode != GameModeKind.Versus || _activePlayerId == LocalPlayerId;

        // ---------------- host: guest connections ----------------

        private class GuestConn
        {
            internal NetPeer Peer;
            internal string Name = "?";
            internal int Tiles = -1;    // last reported stack height, -1 = unknown
            internal TileDto TopOfStack; // last reported top-of-stack (for the companion save)
            internal int VersusScore;   // Phase 4 per-player score (0 in coop)
            internal bool Paired;       // Hello accepted, world sent
        }

        // Host: playerId (1..3) -> connection. Guests before their Hello sit in _pending.
        private static readonly Dictionary<int, GuestConn> _guests = new Dictionary<int, GuestConn>();
        private static readonly HashSet<NetPeer> _pending = new HashSet<NetPeer>();

        // Host: remembered player slots loaded from the companion save when continuing a saved
        // multiplayer game, keyed by lowercased player name. A returning guest is matched here to
        // reclaim its old playerId/color and saved stack; consumed (removed) once matched so two
        // players with the same name can't both claim the slot. New/unmatched players get a fresh
        // slot instead. Phase 4 will also read the remembered versusScore from these entries.
        private static readonly Dictionary<string, CompanionPlayer> _savedRoster =
            new Dictionary<string, CompanionPlayer>();

        // Guest: the single connection to the host, plus the roster the host broadcasts.
        private static NetPeer _host;
        private static readonly List<RosterPlayer> _roster = new List<RosterPlayer>();

        private static int _sessionId;             // host: random id so a dropped guest may rejoin
        private static int _joinedSessionId;       // guest: the host session we belong to

        private static int _lastSentStack = int.MinValue;
        private static int _lastRosterHostTiles = int.MinValue;
        private static float _lastResyncRequest = -999f;
        private static bool _gameOverSent;
        private static string _lastPreviewSig;     // dedupe our broadcast preview: only send on change

        // guest: the one in-flight place request
        private class PendingPlace { public int Id; public TileDto Dto; public float SentAt; }
        private static PendingPlace _pending2;
        private static int _reqCounter;
        // Guest: consecutive host DENIALS of our placements (excluding Versus not-your-turn). Two in
        // a row usually means our board silently diverged from the host's, so we force a resync.
        private static int _consecutivePlaceRejects;
        internal static bool HasPendingPlace => _pending2 != null;

        // status log shown in the overlay
        internal static readonly List<string> Messages = new List<string>();
        internal static void Info(string msg)
        {
            Plugin.Log.LogInfo(msg);
            Messages.Add(msg);
            if (Messages.Count > 6) Messages.RemoveAt(0);
        }

        // ---------------- roster views (for the overlay/HUD) ----------------

        internal struct PlayerView
        {
            public int Id;
            public string Name;
            public int ColorIndex;
            public int Tiles;
            public int VersusScore;   // Phase 4 per-player score (0 in coop)
            public bool Self;
        }

        /// <summary>Every player currently in the session (self first-ish), for the overlay/HUD.</summary>
        internal static List<PlayerView> Players()
        {
            var list = new List<PlayerView>();
            if (Role == SessionRole.Host)
            {
                list.Add(new PlayerView { Id = 0, Name = Plugin.PlayerName, ColorIndex = 0, Tiles = MyTiles, VersusScore = _hostVersusScore, Self = true });
                foreach (var kv in _guests.OrderBy(k => k.Key))
                    if (kv.Value.Paired)
                        list.Add(new PlayerView { Id = kv.Key, Name = kv.Value.Name, ColorIndex = kv.Key, Tiles = kv.Value.Tiles, VersusScore = kv.Value.VersusScore, Self = false });
            }
            else if (Role == SessionRole.Guest)
            {
                foreach (var p in _roster.OrderBy(p => p.id))
                    list.Add(new PlayerView
                    {
                        Id = p.id,
                        Name = p.name,
                        ColorIndex = p.colorIndex,
                        Tiles = p.id == LocalPlayerId ? MyTiles : p.tiles,   // our own count is freshest locally
                        VersusScore = p.versusScore,
                        Self = p.id == LocalPlayerId,
                    });
            }
            return list;
        }

        /// <summary>The other connected players (gift recipients).</summary>
        internal static List<PlayerView> OtherPlayers() => Players().Where(p => !p.Self).ToList();

        internal static int PlayerCount => Players().Count;

        /// <summary>True once every player in the session has an empty stack.</summary>
        internal static bool AllStacksEmpty()
        {
            var players = Players();
            if (players.Count == 0) return false;
            foreach (var p in players)
            {
                int tiles = p.Self ? MyTiles : p.Tiles;
                if (tiles != 0) return false;   // -1 (unknown) counts as "still has tiles"
            }
            return true;
        }

        // ---------------- preconditions ----------------

        internal static string HostBlocker()
        {
            if (!Coop.InGame) return "not in a game";
            if (Coop.CurrentGameMode != GameModeId.Classic) return "only classic mode is supported";
            if (Coop.Refs.Input != null && Coop.Refs.Input.IsLoading) return "game is still loading";
            // Phase 3: hosting an in-progress / continued world is allowed — the game's classic save
            // already carries the shared board + the host's stack, and the mod's companion file
            // (loaded in StartHost) carries the other players' remaining tiles.
            return null;
        }

        internal static string JoinBlocker()
        {
            if (!Coop.InGame) return "not in a game";
            if (Coop.CurrentGameMode != GameModeId.Classic) return "only classic mode is supported";
            if (Coop.Refs.Input != null && Coop.Refs.Input.IsLoading) return "game is still loading";
            // Phase 3: you can join from ANY classic game now, not just a fresh one — your board is
            // wiped and rebuilt from the host's snapshot on Welcome. (Your own solo autosave is
            // never overwritten: GuestSaveGuardPatch blocks the guest's SaveActiveGame.)
            return null;
        }

        // ---------------- lifecycle ----------------

        internal static void StartHost(int port, int discoveryPort, string name, GameModeKind mode)
        {
            var blocker = HostBlocker();
            if (blocker != null) { Info($"Can't host: {blocker}"); return; }
            Stop("re-hosting");
            try
            {
                NetHub.StartHost(port, discoveryPort, name);
            }
            catch (Exception e)
            {
                Info($"Can't open port {port}: {e.Message}");
                return;
            }
            _sessionId = new System.Random().Next(1, int.MaxValue);
            Role = SessionRole.Host;
            State = SessionState.Hosting;   // the host plays immediately; guests join incrementally
            LocalPlayerId = 0;
            _gameOverSent = false;

            // Versus: the host picks the mode BEFORE hosting. LoadCompanion may override it when
            // continuing a saved game (the persisted mode wins). Turn starts on the host (id 0);
            // the score baseline anchors on the current shared score so future deltas are per-player.
            Mode = mode;
            _activePlayerId = 0;
            _hostVersusScore = 0;
            _hostPlacementPending = false;
            _versusScoreBaseline = Coop.Refs?.Rewards.Score ?? 0;

            LoadCompanion();                // continuing a saved game? restore the remembered roster + mode + scores
            Info($"Hosting a {(Mode == GameModeKind.Versus ? "Versus" : "Coop")} game on port {port} — you can play now; friends can join anytime (up to {MaxGuests}).");
            if (Mode == GameModeKind.Versus) BroadcastTurn();
        }

        internal static void Join(string address, int port)
        {
            var blocker = JoinBlocker();
            if (blocker != null) { Info($"Can't join: {blocker}"); return; }
            Stop("re-joining");
            Role = SessionRole.Guest;
            State = SessionState.Connecting;
            Info($"Connecting to {address}:{port}…");
            NetHub.Connect(address, port);
        }

        internal static void Stop(string why)
        {
            if (Role == SessionRole.Off && _host == null && _guests.Count == 0 && _pending.Count == 0) return;
            NetHub.StopHost();
            _host?.Close(why);
            _host = null;
            foreach (var g in _guests.Values) g.Peer?.Close(why);
            foreach (var p in _pending) p?.Close(why);
            _guests.Clear();
            _pending.Clear();
            _roster.Clear();
            _savedRoster.Clear();
            Mode = GameModeKind.Coop;
            ContinuingSaved = false;
            Role = SessionRole.Off;
            State = SessionState.Idle;
            LocalPlayerId = 0;
            _pending2 = null;
            _consecutivePlaceRejects = 0;
            _lastSentStack = int.MinValue;
            _lastRosterHostTiles = int.MinValue;
            _lastPreviewSig = null;              // a fresh session should re-send our preview
            _activePlayerId = 0;
            _hostVersusScore = 0;
            _versusScoreBaseline = 0;
            _hostPlacementPending = false;
            Coop.ClearAllPartnerPreviews();      // no peers => no ghosts
        }

        // ---------------- event pump ----------------

        internal static void HandleEvent(NetEvent ev)
        {
            switch (ev)
            {
                case PeerConnectedEvent pc when pc.Incoming:
                    OnIncomingConnection(pc.Peer);
                    break;
                case PeerConnectedEvent pc:
                    OnConnectedToHost(pc.Peer);
                    break;
                case ConnectFailedEvent cf:
                    Info($"Connection failed: {cf.Error}");
                    if (Role == SessionRole.Guest) { Role = SessionRole.Off; State = SessionState.Idle; }
                    break;
                case PeerDisconnectedEvent pd:
                    OnDisconnected(pd);
                    break;
                case PacketEvent pk:
                    HandlePacket(pk.Peer, pk.Envelope);
                    break;
                case HostFoundEvent hf:
                    Plugin.OnHostDiscovered(hf);
                    break;
                case ScanFinishedEvent _:
                    Plugin.OnScanFinished();
                    break;
            }
        }

        private static void OnIncomingConnection(NetPeer peer)
        {
            if (Role != SessionRole.Host)
            {
                peer.Send(new RefusePacket { reason = "host is not accepting games right now" });
                peer.Close("not hosting");
                return;
            }
            if (_guests.Count + _pending.Count >= MaxGuests)
            {
                peer.Send(new RefusePacket { reason = "game is full" });
                peer.Close("full");
                return;
            }
            // Hold the peer until its Hello arrives; then we assign it a playerId.
            _pending.Add(peer);
        }

        private static void OnConnectedToHost(NetPeer peer)
        {
            if (Role != SessionRole.Guest) { peer.Close("stale connect"); return; }
            _host = peer;
            State = SessionState.AwaitWelcome;
            peer.Send(new HelloPacket
            {
                protocol = ProtocolVersion.Current,
                plugin = Plugin.Version,
                name = Plugin.PlayerName,
                gameMode = (int)Coop.CurrentGameMode,
                placedTiles = Coop.PlacedTileCount(),
                sessionId = _joinedSessionId,
            });
        }

        private static void OnDisconnected(PeerDisconnectedEvent pd)
        {
            if (Role == SessionRole.Host)
            {
                _pending.Remove(pd.Peer);
                int gid = GuestIdByPeer(pd.Peer);
                if (gid < 0) return;
                string name = _guests[gid].Name;
                _guests.Remove(gid);
                Coop.ClearPartnerPreview(gid);   // their ghost goes away here…
                Broadcast(new PreviewPacket { playerId = gid, present = false, tile = null });  // …and on the other guests
                BroadcastRoster();
                WriteCompanion("player left");   // persist their last-known stack so they can rejoin later
                Info($"{name} disconnected ({pd.Reason}) — still hosting, they can rejoin.");
            }
            else if (Role == SessionRole.Guest)
            {
                if (pd.Peer != _host) return;
                _host = null;
                _pending2 = null;
                _roster.Clear();
                Coop.ClearAllPartnerPreviews();
                Role = SessionRole.Off;
                State = SessionState.Idle;
                Info($"Disconnected from host ({pd.Reason}).");
            }
        }

        private static int GuestIdByPeer(NetPeer peer)
        {
            foreach (var kv in _guests)
                if (kv.Value.Peer == peer) return kv.Key;
            return -1;
        }

        private static int LowestFreeGuestId()
        {
            for (int i = 1; i <= MaxGuests; i++)
                if (!_guests.ContainsKey(i)) return i;
            return -1;
        }

        /// <summary>Send a packet to every paired guest, optionally skipping one playerId.</summary>
        private static void Broadcast<T>(T packet, int exceptId = -1)
        {
            foreach (var kv in _guests)
                if (kv.Value.Paired && kv.Key != exceptId)
                    kv.Value.Peer.Send(packet);
        }

        // ---------------- packets ----------------

        private static void HandlePacket(NetPeer peer, Envelope env)
        {
            try
            {
                if (Role == SessionRole.Host)
                {
                    if (_pending.Contains(peer))
                    {
                        if (env.t == nameof(HelloPacket)) OnHello(peer, Read<HelloPacket>(env));
                        // Anything else before Hello is ignored (peer isn't paired yet).
                        return;
                    }
                    int gid = GuestIdByPeer(peer);
                    if (gid < 0) return;   // unknown/closed peer
                    switch (env.t)
                    {
                        case nameof(PlaceRequestPacket): OnPlaceRequest(gid, peer, Read<PlaceRequestPacket>(env)); break;
                        case nameof(StackCountPacket): OnGuestStackCount(gid, Read<StackCountPacket>(env)); break;
                        case nameof(SendTilesPacket): OnSendTilesToHost(Read<SendTilesPacket>(env)); break;
                        case nameof(PreviewPacket): OnGuestPreview(gid, Read<PreviewPacket>(env)); break;
                        case nameof(ResyncRequestPacket): OnResyncRequest(peer, Read<ResyncRequestPacket>(env)); break;
                        default: Plugin.Log.LogWarning($"unexpected packet '{env.t}' from guest {gid}"); break;
                    }
                }
                else if (Role == SessionRole.Guest)
                {
                    if (peer != _host) return;
                    switch (env.t)
                    {
                        case nameof(WelcomePacket): OnWelcome(Read<WelcomePacket>(env)); break;
                        case nameof(RefusePacket): OnRefuse(Read<RefusePacket>(env)); break;
                        case nameof(PlaceResultPacket): OnPlaceResult(Read<PlaceResultPacket>(env)); break;
                        case nameof(TilePlacedPacket): OnTilePlaced(Read<TilePlacedPacket>(env)); break;
                        case nameof(RosterPacket): OnRoster(Read<RosterPacket>(env)); break;
                        case nameof(SendTilesPacket): OnTilesGifted(Read<SendTilesPacket>(env)); break;
                        case nameof(TurnPacket): OnTurn(Read<TurnPacket>(env)); break;
                        case nameof(GameOverPacket): OnGameOver(Read<GameOverPacket>(env)); break;
                        case nameof(PreviewPacket): OnHostRelayedPreview(Read<PreviewPacket>(env)); break;
                        case nameof(ResyncPacket): OnResync(Read<ResyncPacket>(env)); break;
                        default: Plugin.Log.LogWarning($"unexpected packet '{env.t}' from host"); break;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"error handling {env.t}: {e}");
                Info($"Error handling {env.t} — requesting resync.");
                if (Role == SessionRole.Guest) RequestResync($"exception in {env.t}");
            }
        }

        private static T Read<T>(Envelope env) => JsonConvert.DeserializeObject<T>(env.p);

        // ----- handshake (host) -----

        private static void OnHello(NetPeer peer, HelloPacket hello)
        {
            string refuse = null;
            if (hello.protocol != ProtocolVersion.Current)
                refuse = $"version mismatch (host {ProtocolVersion.Current}, guest {hello.protocol})";
            else if ((GameModeId)hello.gameMode != GameModeId.Classic)
                refuse = "guest must be in a classic game";
            else if (!Coop.InGame)
                refuse = "host is not in a game";
            // Phase 3: a guest may join from ANY classic game (fresh or mid-game); its board is
            // rebuilt from our snapshot, so we no longer refuse a non-fresh / non-matching guest.

            string connName = string.IsNullOrEmpty(hello.name) ? peer.RemoteAddress : hello.name;

            string nameKey = connName.Trim().ToLowerInvariant();

            // Live-name takeover: on flaky internet a guest's OLD connection may still occupy its
            // _guests slot when the same person rejoins. Detect that first (case-insensitive name
            // match against a CURRENT guest) and treat this Hello as a takeover of that live slot —
            // reuse the same playerId and inherit the old conn's stack/score so it continues rather
            // than resetting. Closing the old peer will later fire PeerDisconnectedEvent, but by then
            // _guests[id].Peer is the NEW peer, so GuestIdByPeer(oldPeer) returns -1 and
            // OnDisconnected is a no-op for it (nothing else keys off the old peer).
            int takeoverId = -1;
            GuestConn oldConn = null;
            if (refuse == null)
            {
                foreach (var kv in _guests)
                {
                    if (kv.Value.Name != null && kv.Value.Name.Trim().ToLowerInvariant() == nameKey)
                    {
                        takeoverId = kv.Key;
                        oldConn = kv.Value;
                        break;
                    }
                }
            }

            // Player re-identification: if we're continuing a saved game and this player's name
            // matches a remembered slot (and that id is still free), give them their old
            // playerId/color and saved stack back. Otherwise assign the lowest free id + fresh stack.
            // A live takeover wins over the savedRoster path (it uses the takeover id instead).
            CompanionPlayer saved = null;
            if (refuse == null && takeoverId < 0
                && _savedRoster.TryGetValue(nameKey, out var candidate)
                && candidate.playerId >= 1 && candidate.playerId <= MaxGuests
                && !_guests.ContainsKey(candidate.playerId))
                saved = candidate;

            int id = refuse != null ? -1
                   : takeoverId >= 0 ? takeoverId
                   : (saved?.playerId ?? LowestFreeGuestId());
            if (refuse == null && id < 0) refuse = "game is full";

            if (refuse != null)
            {
                peer.Send(new RefusePacket { reason = refuse });
                peer.Close(refuse);
                _pending.Remove(peer);
                Info($"Refused join: {refuse}");
                return;
            }

            _pending.Remove(peer);
            if (takeoverId >= 0)
            {
                // Retire the stale connection and free its slot; the new conn below reuses the id.
                oldConn.Peer?.Close("replaced by rejoin");
                _guests.Remove(takeoverId);
            }
            var conn = new GuestConn
            {
                Peer = peer,
                Name = connName,
                // Inherit a live takeover's in-flight stack/score; otherwise seed from a saved slot.
                Tiles = takeoverId >= 0 ? oldConn.Tiles : (saved?.stackHeight ?? -1),
                TopOfStack = takeoverId >= 0 ? oldConn.TopOfStack : saved?.topOfStack,
                VersusScore = takeoverId >= 0 ? oldConn.VersusScore : (saved?.versusScore ?? 0),
                Paired = true,
            };
            _guests[id] = conn;
            _gameOverSent = false;
            if (saved != null) _savedRoster.Remove(nameKey);   // this slot has been reclaimed

            peer.Send(new WelcomePacket
            {
                name = Plugin.PlayerName,
                sessionId = _sessionId,
                playerId = id,
                gameMode = (int)Mode,
                restoreStackHeight = saved?.stackHeight ?? -1,   // < 0 = new player, keep fresh stack
                restoreTopOfStack = saved?.topOfStack,
                snapshot = Coop.BuildSnapshot(),
            });
            BroadcastRoster();              // tell everyone (including the newcomer) the new roster
            if (Mode == GameModeKind.Versus) BroadcastTurn();   // …and whose turn it currently is
            WriteCompanion("player joined");
            Info(takeoverId >= 0
                ? $"{conn.Name} reconnected, replacing a stale connection as player {id}."
                : saved != null
                    ? $"{conn.Name} rejoined as player {id} — restored {saved.stackHeight} saved tiles."
                    : $"{conn.Name} joined as player {id}!");
        }

        // ----- handshake (guest) -----

        private static void OnWelcome(WelcomePacket welcome)
        {
            if (Role != SessionRole.Guest) return;
            LocalPlayerId = welcome.playerId;
            _joinedSessionId = welcome.sessionId;
            Mode = (GameModeKind)welcome.gameMode;
            Info($"Joined {(string.IsNullOrEmpty(welcome.name) ? "host" : welcome.name)}'s game as player {LocalPlayerId} — loading world ({welcome.snapshot.tiles.Count} tiles)…");
            if (Coop.PlacedTileCount() == 0)
                Coop.ApplySnapshotOnJoin(welcome.snapshot);
            else
                Coop.ResyncWorld(welcome.snapshot);   // mid-game join / rejoin: wipe our board & rebuild
            // Save & re-host: if the host matched us to a saved slot, adopt our remembered
            // remaining-tile count (and top-of-stack face when it could be reproduced).
            if (welcome.restoreStackHeight >= 0)
            {
                Coop.SetGuestStack(welcome.restoreStackHeight, welcome.restoreTopOfStack);
                Info($"Restored your saved stack — {welcome.restoreStackHeight} tiles remaining.");
            }
            State = SessionState.Paired;
            _gameOverSent = false;
            SendStackCount(force: true);
            Info("World loaded. Play!");
        }

        private static void OnRefuse(RefusePacket refuse)
        {
            Info($"Host refused: {refuse.reason}");
            Stop("refused");
        }

        // ----- roster -----

        private static void BroadcastRoster()
        {
            if (Role != SessionRole.Host) return;
            _lastRosterHostTiles = MyTiles;
            var players = new List<RosterPlayer>
            {
                new RosterPlayer { id = 0, name = Plugin.PlayerName, colorIndex = 0, tiles = MyTiles, versusScore = _hostVersusScore },
            };
            foreach (var kv in _guests.OrderBy(k => k.Key))
                if (kv.Value.Paired)
                    players.Add(new RosterPlayer { id = kv.Key, name = kv.Value.Name, colorIndex = kv.Key, tiles = kv.Value.Tiles, versusScore = kv.Value.VersusScore });
            Broadcast(new RosterPacket { players = players });
        }

        private static void OnRoster(RosterPacket packet)
        {
            if (Role != SessionRole.Guest) return;
            _roster.Clear();
            if (packet.players != null) _roster.AddRange(packet.players);

            // Drop ghosts for players who left (their id no longer appears).
            // (Coop simply won't get updates for them; clearing is handled on disconnect
            // relaying, but be safe about stale ghosts from a departed player.)
        }

        private static void OnGuestStackCount(int gid, StackCountPacket packet)
        {
            if (_guests.TryGetValue(gid, out var conn))
            {
                conn.Tiles = packet.count;
                if (packet.top != null) conn.TopOfStack = packet.top;   // remember for the companion save
                BroadcastRoster();
            }
        }

        // ----- placement -----

        /// <summary>Called from the PlaceCurrentTile intercept on the guest.</summary>
        internal static void RequestPlace(TilePlacer placer, TileSlot slot)
        {
            if (_host == null || placer.CurrentTile == null || slot == null) return;
            if (_pending2 != null) return;   // one request in flight; extra clicks are dropped
            var dto = Coop.ToDto(placer.CurrentTile);
            dto.x = slot.GridPos.x;
            dto.y = slot.GridPos.y;
            dto.rot = placer.CurrentTile.RotationIndex;
            _pending2 = new PendingPlace { Id = ++_reqCounter, Dto = dto, SentAt = Time.realtimeSinceStartup };
            _host.Send(new PlaceRequestPacket { reqId = _pending2.Id, tile = dto });
        }

        private static void OnPlaceRequest(int gid, NetPeer peer, PlaceRequestPacket req)
        {
            // Versus: only the active player may place. Reject a request from anyone else outright.
            if (Mode == GameModeKind.Versus && gid != _activePlayerId)
            {
                peer.Send(new PlaceResultPacket { reqId = req.reqId, accepted = false, reason = "it's not your turn" });
                return;
            }

            bool ok = Coop.TryApplyRequest(req.tile, out var reason);
            peer.Send(new PlaceResultPacket
            {
                reqId = req.reqId,
                accepted = ok,
                reason = reason,
                sync = ok ? Coop.Stamp() : null,
            });
            if (ok)
            {
                // Broadcast to all OTHER guests; the requesting guest applies via its own replay path.
                Broadcast(new TilePlacedPacket { origin = gid, tile = req.tile, sync = Coop.Stamp() }, exceptId: gid);
                // Versus: the shared score already reflects this guest's placement (TryApplyRequest ran
                // synchronously here), so attribute the delta to the guest and pass the turn on now.
                if (Mode == GameModeKind.Versus) VersusOnPlacement(gid);
            }
            else
                Info($"Rejected {GuestName(gid)}'s placement: {reason}");
        }

        private static void OnPlaceResult(PlaceResultPacket result)
        {
            if (_pending2 == null || result.reqId != _pending2.Id) return;
            var pending = _pending2;
            _pending2 = null;
            var r = Coop.Refs;
            if (r == null) return;

            if (!result.accepted)
            {
                Info($"Placement rejected: {result.reason}");
                // A legitimate Versus not-your-turn rejection is expected — never treat it as divergence.
                if (result.reason != "it's not your turn")
                {
                    // Repeated denials usually mean our board diverged from the host's (a delayed
                    // PlaceResult let the host apply+broadcast a tile our timeout path discarded), so
                    // our next placements land on slots already occupied on the host. Self-heal.
                    if (++_consecutivePlaceRejects >= 2)
                    {
                        _consecutivePlaceRejects = 0;
                        RequestResync("repeated placement rejections");
                    }
                }
                // Refresh the preview — the slot may just have been taken by another player.
                if (r.Placer.CurrentTile != null)
                    r.Placer.ShowPreviewTileAt(r.Placer.CurrentTileSlot);
                r.Placer.UpdateTileSlotValidity();
                return;
            }
            _consecutivePlaceRejects = 0;   // an accepted placement clears the divergence suspicion

            var pos = new Vector2Int(pending.Dto.x, pending.Dto.y);
            var slot = r.Previewer.GetTileSlot(pos);
            // ShowPreviewTileAt registers our own hovering preview tile in the world at
            // this slot, so GetTile(pos) is never null here. Only treat the slot as
            // occupied by a *real* tile (a different tile that is actually placed);
            // otherwise the normal replay path (stack advance + rewards) is skipped.
            var occupant = r.World.GetTile(pos);
            bool slotOccupied = occupant != null && occupant != r.Placer.CurrentTile && occupant.State == TileState.placed;
            bool placedNormally = false;
            if (slot != null && !slotOccupied && Coop.MatchesDto(r.Placer.CurrentTile, pending.Dto))
            {
                // Replay our own placement through the regular game path so the stack
                // advances and rewards (perfect tiles, quest tiles) land in OUR stack.
                Coop.BypassPlaceIntercept = true;
                Coop.NetPlacing = true;
                try
                {
                    if (r.Placer.CurrentTileSlot != slot)
                        r.Placer.ShowPreviewTileAt(slot);
                    if (r.Placer.CurrentTile.RotationIndex != pending.Dto.rot)
                        r.Placer.RotatePreviewTileTo(pending.Dto.rot, animate: false);
                    r.Placer.PlaceCurrentTile(slot);
                    placedNormally = true;
                }
                finally
                {
                    Coop.BypassPlaceIntercept = false;
                    Coop.NetPlacing = false;
                }
            }
            if (!placedNormally)
            {
                // The preview changed (e.g. discarded) during the round-trip — the host
                // already placed our tile, so mirror it without consuming a stack tile.
                Info("Preview changed during placement — tile applied without stack advance.");
                Coop.ApplyRemote(pending.Dto, addScore: true);
            }
            CompareStamp(result.sync, "own placement");
        }

        /// <summary>Called from the TilePlacer.PlaceTile postfix when the HOST places a
        /// tile of its own through the normal game path.</summary>
        internal static void OnLocalHostPlacement(Tile tile)
        {
            if (Role != SessionRole.Host || !Active) return;
            Broadcast(new TilePlacedPacket { origin = 0, tile = Coop.ToDto(tile), sync = Coop.Stamp() });
            // Versus: this postfix fires mid-placement, before the host's stack/score fully settle,
            // so defer the score reconcile + turn advance to the next Poll (a frame later).
            if (Mode == GameModeKind.Versus) _hostPlacementPending = true;
        }

        private static void OnTilePlaced(TilePlacedPacket packet)
        {
            if (Role != SessionRole.Guest) return;
            if (packet.origin == LocalPlayerId) return;   // we originated this; already applied via replay
            if (!Coop.ApplyRemote(packet.tile, addScore: true))
            {
                RequestResync("remote tile could not be applied");
                return;
            }
            CompareStamp(packet.sync, "remote placement");
        }

        // ----- desync handling -----

        private static void CompareStamp(SyncStamp remote, string context)
        {
            if (remote == null) return;
            var local = Coop.Stamp();
            if (local.placedTiles == remote.placedTiles && local.score == remote.score) return;
            Info($"Desync after {context}: tiles {local.placedTiles}/{remote.placedTiles}, score {local.score}/{remote.score}.");
            RequestResync(context);
        }

        private static void RequestResync(string why)
        {
            if (Role != SessionRole.Guest || _host == null) return;
            if (Time.realtimeSinceStartup - _lastResyncRequest < 5f) return;   // don't spam while one is in flight
            _lastResyncRequest = Time.realtimeSinceStartup;
            Info("Requesting world resync from host…");
            _host.Send(new ResyncRequestPacket { why = why });
        }

        private static void OnResyncRequest(NetPeer peer, ResyncRequestPacket req)
        {
            Info($"A guest requested a resync ({req.why}) — sending world.");
            peer.Send(new ResyncPacket { snapshot = Coop.BuildSnapshot() });
        }

        private static void OnResync(ResyncPacket packet)
        {
            if (Role != SessionRole.Guest) return;
            Coop.ResyncWorld(packet.snapshot);
            ResyncCount++;
            _consecutivePlaceRejects = 0;   // board is authoritative again — clear the reject streak
            Info($"World resynced from host ({packet.snapshot.tiles.Count} tiles).");
        }

        // ----- coop gifting -----

        /// <summary>Take tiles off our stack and give them to a chosen recipient.</summary>
        internal static void SendTilesTo(int recipientId, int count)
        {
            if (!Active) return;
            if (Mode == GameModeKind.Versus) { Info("Sending tiles is disabled in Versus."); return; }
            if (recipientId == LocalPlayerId) { Info("Pick another player to send to."); return; }
            if (Role == SessionRole.Guest && _pending2 != null) { Info("Wait for your placement to finish first."); return; }
            int removed = Coop.RemoveStackTiles(count);
            if (removed <= 0) { Info("No tiles to send."); return; }

            string toName = PlayerName(recipientId);
            if (Role == SessionRole.Host)
            {
                if (_guests.TryGetValue(recipientId, out var conn) && conn.Paired)
                    conn.Peer.Send(new SendTilesPacket { count = removed, from = 0, to = recipientId });
                BroadcastRoster();   // host tiles changed
            }
            else // guest
            {
                _host.Send(new SendTilesPacket { count = removed, from = LocalPlayerId, to = recipientId });
                SendStackCount(force: true);
            }
            Info($"Sent {removed} tile{(removed == 1 ? "" : "s")} to {toName}.");
        }

        /// <summary>Host received a gift from a guest: grant it (if for us) or forward it.</summary>
        private static void OnSendTilesToHost(SendTilesPacket packet)
        {
            if (Mode == GameModeKind.Versus) return;   // gifting is disabled in Versus; drop stray packets
            if (packet.to == 0)
            {
                Coop.GrantStackTiles(packet.count);
                Info($"{PlayerName(packet.from)} sent you {packet.count} tile{(packet.count == 1 ? "" : "s")}!");
            }
            else if (_guests.TryGetValue(packet.to, out var conn) && conn.Paired)
            {
                conn.Peer.Send(packet);   // relay guest -> guest unchanged
            }
            // Roster refreshes on the next stack-count report from the affected players.
        }

        /// <summary>Guest received a gift the host forwarded to us.</summary>
        private static void OnTilesGifted(SendTilesPacket packet)
        {
            if (Mode == GameModeKind.Versus) return;   // gifting is disabled in Versus; drop stray packets
            Coop.GrantStackTiles(packet.count);
            Info($"{PlayerName(packet.from)} sent you {packet.count} tile{(packet.count == 1 ? "" : "s")}!");
        }

        // ----- previews -----

        private static void OnGuestPreview(int gid, PreviewPacket p)
        {
            if (!Active) return;
            // Render the guest's ghost locally (override the id with the trusted guest id)…
            Coop.UpdatePartnerPreview(gid, p.present ? p.tile : null);
            // …and relay it to all OTHER guests tagged with the origin.
            Broadcast(new PreviewPacket { playerId = gid, present = p.present, tile = p.tile }, exceptId: gid);
        }

        private static void OnHostRelayedPreview(PreviewPacket p)
        {
            if (!Active) return;
            if (p.playerId == LocalPlayerId) return;   // never render our own ghost
            Coop.UpdatePartnerPreview(p.playerId, p.present ? p.tile : null);
        }

        /// <summary>Broadcast our own hovering preview tile, but only when it actually changed
        /// (slot/rotation/identity), to avoid flooding the link every frame. Skipped while
        /// NetPlacing because the preview transiently vanishes/restores then.</summary>
        private static void SendPreviewIfChanged()
        {
            if (!Active || Coop.NetPlacing) return;
            // Versus: only the active player has a live preview — a non-active player's null dto
            // sends present=false so everyone clears its ghost.
            var dto = (Mode == GameModeKind.Versus && !IsLocalTurn) ? null : Coop.CurrentPreviewDto();
            string sig = dto == null ? "none" : $"{dto.x},{dto.y},{dto.rot},{dto.seed},{dto.specialId},{dto.questTileId}";
            if (sig == _lastPreviewSig) return;
            _lastPreviewSig = sig;
            var packet = new PreviewPacket { playerId = LocalPlayerId, present = dto != null, tile = dto };
            if (Role == SessionRole.Host) Broadcast(packet);
            else _host?.Send(packet);
        }

        // ----- stack counts -----

        private static void SendStackCount(bool force = false)
        {
            var r = Coop.Refs;
            if (r == null) return;
            int count = r.Stack.RawHeight;
            if (!force && count == _lastSentStack) return;
            _lastSentStack = count;
            // Ride our top-of-stack along so the host can persist it in the companion save.
            if (Role == SessionRole.Guest) _host?.Send(new StackCountPacket { count = count, top = Coop.TopOfStackDto() });
            else if (Role == SessionRole.Host) BroadcastRoster();   // host's own count rides on the roster
        }

        internal static int MyTiles => Coop.Refs?.Stack.RawHeight ?? -1;

        // ----- helpers -----

        private static string GuestName(int gid) => _guests.TryGetValue(gid, out var c) ? c.Name : $"player {gid}";

        internal static string PlayerName(int id)
        {
            if (id == LocalPlayerId) return Plugin.PlayerName;
            if (Role == SessionRole.Host)
                return id == 0 ? Plugin.PlayerName : GuestName(id);
            var p = _roster.FirstOrDefault(x => x.id == id);
            return p != null ? p.name : $"player {id}";
        }

        // ---------------- Versus turns & scoring (Phase 4) ----------------

        /// <summary>Host: tell everyone whose turn it is now (Versus only).</summary>
        private static void BroadcastTurn()
        {
            if (Role != SessionRole.Host || Mode != GameModeKind.Versus) return;
            Broadcast(new TurnPacket { activePlayerId = _activePlayerId });
        }

        /// <summary>Guest: adopt the turn the host announced. Announce our own turn locally.</summary>
        private static void OnTurn(TurnPacket packet)
        {
            if (Role != SessionRole.Guest) return;
            bool wasMine = _activePlayerId == LocalPlayerId;
            _activePlayerId = packet.activePlayerId;
            if (_activePlayerId == LocalPlayerId && !wasMine) Info("Your turn — place a tile!");
        }

        /// <summary>Guest handler: show the (possibly Versus winner) game-over message + end the run.</summary>
        private static void OnGameOver(GameOverPacket packet)
        {
            Info(string.IsNullOrEmpty(packet.message) ? "Everyone's stacks are empty — game over!" : $"Versus over — {packet.message}");
            Coop.TriggerCoopGameOver();
        }

        /// <summary>Host: a placement by <paramref name="placerId"/> just settled. Attribute its score
        /// (the shared-score delta since the last placement, which folds in any quest/perfect bonus)
        /// to that player, then pass the turn to the next player who still has tiles.</summary>
        private static void VersusOnPlacement(int placerId)
        {
            ReconcileVersusScore(placerId);
            int next = NextPlayerWithTiles(_activePlayerId, placerId);
            if (next >= 0 && next != _activePlayerId) { _activePlayerId = next; BroadcastTurn(); }
            // next < 0 (nobody has tiles) or next == active (solo player continues) leaves the turn put;
            // the all-empty game-over check in Poll ends the run when everyone is finally out.
        }

        /// <summary>Host: fold the shared RewardSystem.Score delta since the last placement into
        /// <paramref name="placerId"/>'s individual score and rebroadcast the authoritative roster.</summary>
        private static void ReconcileVersusScore(int placerId)
        {
            var r = Coop.Refs;
            if (r == null) return;
            int score = r.Rewards.Score;
            int delta = score - _versusScoreBaseline;
            _versusScoreBaseline = score;
            if (delta == 0) return;
            if (placerId == 0) _hostVersusScore += delta;
            else if (_guests.TryGetValue(placerId, out var conn)) conn.VersusScore += delta;
            BroadcastRoster();   // everyone agrees on the authoritative per-player scores
        }

        /// <summary>Host: the next player (ascending playerId, cyclic from <paramref name="from"/>)
        /// who still has tiles, checking <paramref name="from"/> itself LAST so others get priority.
        /// Returns -1 when nobody has any tiles left.</summary>
        private static int NextPlayerWithTiles(int from, int justPlacedId)
        {
            for (int step = 1; step <= MaxGuests + 1; step++)
            {
                int cand = (from + step) % (MaxGuests + 1);   // 0..MaxGuests, wrapping back to `from` last
                if (VersusHasTiles(cand, justPlacedId)) return cand;
            }
            return -1;
        }

        /// <summary>Host: does player <paramref name="id"/> still have tiles to place? The player who
        /// just placed (<paramref name="justPlacedId"/>) is treated as one fewer tile than they last
        /// REPORTED — a guest's own count report lags its placement by a round-trip, whereas the
        /// host's stack (id 0) is read live and needs no adjustment. Unknown (-1) counts as "has
        /// tiles" so we never skip a player before their first report.</summary>
        private static bool VersusHasTiles(int id, int justPlacedId)
        {
            if (id == 0) return MyTiles > 0;   // host stack is live/settled by the time we ask
            if (!_guests.TryGetValue(id, out var conn) || !conn.Paired) return false;
            int t = conn.Tiles;
            if (t < 0) return true;            // not reported yet — assume they still have tiles
            if (id == justPlacedId && t > 0) t -= 1;   // optimistic: their report hasn't caught up
            return t > 0;
        }

        /// <summary>Host: the Versus outcome line — highest score wins, ties are reported.</summary>
        private static string VersusResult()
        {
            var players = Players();
            if (players.Count == 0) return "no players";
            int best = players.Max(p => p.VersusScore);
            var winners = players.Where(p => p.VersusScore == best).Select(p => p.Name).ToList();
            return winners.Count == 1
                ? $"{winners[0]} wins with {best} points!"
                : $"tie at {best} points between {string.Join(", ", winners)}.";
        }

        // ---------------- companion save & re-host (Phase 3) ----------------

        /// <summary>Host: on StartHost, load the companion file for the world we're about to host.
        /// If one exists we're continuing a saved multiplayer game: adopt its game mode and stash
        /// the remembered per-player slots so returning guests can be matched by name and handed
        /// back their saved stacks. Purely a convenience — any failure just means "fresh start".</summary>
        private static void LoadCompanion()
        {
            _savedRoster.Clear();
            ContinuingSaved = false;
            // NOTE: Mode was set from the host's chosen toggle in StartHost. We only OVERRIDE it below
            // when a companion save exists — the persisted mode of a continued world wins.
            try
            {
                var key = Coop.SaveKey();
                if (key == null) return;
                var save = CompanionStore.Load(key);
                if (save == null) return;

                ContinuingSaved = true;
                Mode = save.gameMode == (int)GameModeKind.Versus ? GameModeKind.Versus : GameModeKind.Coop;
                foreach (var p in save.roster)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.name)) continue;
                    // The host's own remembered slot (id 0) restores its Versus score; guest slots are
                    // stashed for name-matching on rejoin (they also carry the guest's saved score).
                    if (p.playerId == 0) { _hostVersusScore = p.versusScore; continue; }
                    _savedRoster[p.name.Trim().ToLowerInvariant()] = p;
                }
                Info($"Continuing a saved multiplayer game — remembering {_savedRoster.Count} other player{(_savedRoster.Count == 1 ? "" : "s")} for rejoin.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"loading companion save failed: {e.Message}");
            }
        }

        /// <summary>Host: (re)write the companion file for the current world. Called on clean session
        /// changes (join/leave) and piggybacked on the host autosave. Cheap + fully wrapped so it can
        /// never throw into the game loop. Persists the current roster incl. each player's remaining
        /// tiles + top-of-stack (the host's own comes from the game's classic save, but we record it
        /// too for completeness) and the game mode (Phase 4 will persist real versusScores here).</summary>
        internal static void WriteCompanion(string why)
        {
            if (Role != SessionRole.Host) return;
            try
            {
                var key = Coop.SaveKey();
                if (key == null) return;
                var save = new CompanionSave
                {
                    protocol = ProtocolVersion.Current,
                    modVersion = Plugin.Version,
                    gameMode = (int)Mode,
                    sessionId = _sessionId,
                    roster = new List<CompanionPlayer>
                    {
                        new CompanionPlayer
                        {
                            playerId = 0,
                            name = Plugin.PlayerName,
                            colorIndex = 0,
                            stackHeight = MyTiles,
                            topOfStack = Coop.TopOfStackDto(),
                            versusScore = _hostVersusScore,   // Phase 4: host's own versus score
                        },
                    },
                };
                foreach (var kv in _guests)
                    if (kv.Value.Paired)
                        save.roster.Add(new CompanionPlayer
                        {
                            playerId = kv.Key,
                            name = kv.Value.Name,
                            colorIndex = kv.Key,
                            stackHeight = kv.Value.Tiles,
                            topOfStack = kv.Value.TopOfStack,
                            versusScore = kv.Value.VersusScore,
                        });
                CompanionStore.Save(key, save);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"writing companion save ({why}) failed: {e.Message}");
            }
        }

        /// <summary>Hook from the host-only SaveActiveGame postfix: whenever the game autosaves the
        /// shared world, refresh the companion alongside it so the two stay in step.</summary>
        internal static void OnHostAutosave()
        {
            if (Role == SessionRole.Host) WriteCompanion("host autosave");
        }

        /// <summary>Per-frame housekeeping (main thread).</summary>
        internal static void Poll()
        {
            if (Role == SessionRole.Off) return;

            // Leaving the game scene ends the session.
            if (!Coop.InGame)
            {
                Info("Left the game — multiplayer session ended.");
                Stop("left game");
                return;
            }

            if (Active)
            {
                SendStackCount();
                SendPreviewIfChanged();
                Coop.EnsureCurrentTile();

                // Host: keep the roster fresh if our own count drifted without a SendStackCount.
                if (Role == SessionRole.Host && MyTiles != _lastRosterHostTiles)
                    BroadcastRoster();

                // Versus turn management (host authoritative).
                if (Role == SessionRole.Host && Mode == GameModeKind.Versus)
                {
                    if (_hostPlacementPending)
                    {
                        // The host's own placement has now settled (score + stack) — attribute it and
                        // pass the turn on, exactly like a guest placement does inline.
                        _hostPlacementPending = false;
                        VersusOnPlacement(0);
                    }
                    else if (!AllStacksEmpty() && !VersusHasTiles(_activePlayerId, -1))
                    {
                        // Self-heal: the active player's reported stack has since hit 0 (e.g. a guest's
                        // count caught up after its placement) while others still have tiles — pass on.
                        int next = NextPlayerWithTiles(_activePlayerId, -1);
                        if (next >= 0 && next != _activePlayerId) { _activePlayerId = next; BroadcastTurn(); }
                    }
                }

                // The host declares game over once EVERY player's stack is empty.
                if (Role == SessionRole.Host && !_gameOverSent && AllStacksEmpty())
                {
                    _gameOverSent = true;
                    string result = Mode == GameModeKind.Versus ? VersusResult() : null;
                    Broadcast(new GameOverPacket { message = result });
                    Info(Mode == GameModeKind.Versus ? $"Versus over — {result}" : "Everyone's stacks are empty — game over!");
                    Coop.TriggerCoopGameOver();
                }
            }

            // Time out a stuck place request (packet loss = dead TCP link anyway).
            if (_pending2 != null && Time.realtimeSinceStartup - _pending2.SentAt > 8f)
            {
                Info("Placement request timed out.");
                _pending2 = null;
                // The host may actually have applied+broadcast this tile before the (delayed) result
                // arrived, silently diverging our board. Resync to be safe (throttled to once/5s).
                RequestResync("placement request timed out");
            }
        }
    }
}
