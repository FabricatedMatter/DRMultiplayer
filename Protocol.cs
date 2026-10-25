using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace DRMultiplayer
{
    // =====================================================================
    //  Wire protocol. Every frame on the TCP stream is
    //      [int32 little-endian payload length][UTF8 JSON of Envelope]
    //  The Envelope carries the packet type name plus the packet's own JSON.
    //  Serialized with the game's own Newtonsoft.Json, so nulls/lists/enums
    //  all behave (unlike Unity's JsonUtility).
    // =====================================================================

    /// <summary>Bump whenever a packet changes shape — mismatched peers refuse to pair.</summary>
    internal static class ProtocolVersion
    {
        // v3: N-player coop (host + up to 3 guests). Welcome carries the assigned
        // playerId + gamemode; TilePlaced/Preview carry an origin playerId; gifts carry
        // from/to; new RosterPacket broadcasts the full player list.
        // v4: save & re-host. Welcome carries a restore stack height + top-of-stack tile
        // (so a rejoining player gets its saved remaining tiles back); StackCount carries
        // the guest's top-of-stack (so the host can persist it); roster carries versusScore.
        // v5: Versus mode. New TurnPacket announces whose turn it is (host authoritative,
        // alternating play); GameOverPacket carries a winner/result message so everyone shows
        // the same outcome. Versus per-player scores ride on the existing roster.versusScore.
        // v6: transport heartbeat/keepalive. A zero-length frame (4-byte length header of 0, no
        // payload) is now a valid transport-level keepalive — this changes framing semantics, so
        // both peers must run a matching mod build. The Hello handshake enforces the version match.
        // v7: Creative mode. Hello.gameMode may now be Creative (must match the host's mode); new
        // DeleteRequest/TileDeleted packets sync the Creative delete tool; Resync(Request) carry a
        // rebuild flag so a guest that just switched Classic -> Creative also adopts the world seeds.
        internal const int Current = 7;
    }

    internal class Envelope
    {
        public string t;   // packet type name (nameof the class)
        public string p;   // packet JSON

        internal static Envelope Wrap<T>(T packet) =>
            new Envelope { t = typeof(T).Name, p = JsonConvert.SerializeObject(packet) };
    }

    // ---------------- handshake ----------------

    /// <summary>Guest -> host, immediately after connecting.</summary>
    internal class HelloPacket
    {
        public int protocol;
        public string plugin;      // plugin version string, informational
        public string name;        // player display name
        public int gameMode;       // GameModeId the guest is currently in
        public int placedTiles;    // non-initial tiles already on the guest's board (must be 0)
        public int sessionId;      // non-zero when rejoining a session after a drop
    }

    /// <summary>Host -> guest on acceptance. Carries the full world.</summary>
    internal class WelcomePacket
    {
        public string name;        // host display name
        public int sessionId;      // remember this to rejoin after a connection drop
        public int playerId;       // the stable id (1..3) the host assigned to this guest
        public int gameMode;       // GameModeKind: 0 = coop (Versus lands in Phase 4)

        // Save & re-host: when the host is continuing a saved multiplayer game and matched this
        // guest to a remembered slot, it hands the guest back its saved stack. restoreStackHeight
        // < 0 means "no saved slot — keep your fresh stack"; restoreTopOfStack is the saved
        // top-of-stack face (null when unknown / not restorable).
        public int restoreStackHeight = -1;
        public TileDto restoreTopOfStack;

        public SnapshotDto snapshot;
    }

    /// <summary>Host -> guest when the connection is not usable.</summary>
    internal class RefusePacket
    {
        public string reason;
    }

    // ---------------- placement flow ----------------

    /// <summary>Guest -> host: "please place this tile of mine here".</summary>
    internal class PlaceRequestPacket
    {
        public int reqId;
        public TileDto tile;
    }

    /// <summary>Host -> guest: verdict for a PlaceRequest. On accept the guest
    /// performs the placement locally through the normal game path.</summary>
    internal class PlaceResultPacket
    {
        public int reqId;
        public bool accepted;
        public string reason;
        public SyncStamp sync;     // host's state AFTER applying (accept only)
    }

    /// <summary>Host -> all guests: a tile was placed in the shared world. <c>origin</c> is the
    /// playerId that placed it (0 = host). A guest ignores a broadcast it originated (it applies
    /// its own tile via the PlaceResult replay path), so placements never double-apply.</summary>
    internal class TilePlacedPacket
    {
        public int origin;         // playerId that placed the tile (0 = host)
        public TileDto tile;
        public SyncStamp sync;     // host's state AFTER applying
    }

    /// <summary>Guest -> host (Creative): "please delete the placed tile at x,y". The host deletes
    /// it and broadcasts a TileDeletedPacket to everyone, the requester included.</summary>
    internal class DeleteRequestPacket
    {
        public int x;
        public int y;
    }

    /// <summary>Host -> all guests (Creative): the placed tile at x,y was deleted by <c>origin</c>.</summary>
    internal class TileDeletedPacket
    {
        public int origin;         // playerId that deleted the tile (0 = host)
        public int x;
        public int y;
        public SyncStamp sync;     // host's state AFTER deleting
    }

    // ---------------- coop bookkeeping ----------------

    /// <summary>Guest -> host: my tile stack height changed. The host merges it into the roster
    /// and rebroadcasts. (Host counts ride on the RosterPacket instead.)</summary>
    internal class StackCountPacket
    {
        public int count;
        public TileDto top;        // the guest's current top-of-stack, so the host can persist it
    }

    /// <summary>Host -> everyone: the authoritative player list (id, name, color, stack height).
    /// Rebroadcast whenever anything changes (join/leave/stack count).</summary>
    internal class RosterPacket
    {
        public List<RosterPlayer> players;
    }

    internal class RosterPlayer
    {
        public int id;
        public string name;
        public int colorIndex;
        public int tiles;          // stack height, -1 = unknown
        public int versusScore;    // Phase 4 per-player score (0 in coop)
    }

    /// <summary>Gift relay: <c>count</c> tiles from player <c>from</c> to player <c>to</c>.
    /// A guest sends this to the host; the host either grants (to == 0) or forwards it to the
    /// recipient guest. Tiles are already removed from the sender's stack.</summary>
    internal class SendTilesPacket
    {
        public int count;
        public int from;           // sender playerId
        public int to;             // recipient playerId
    }

    /// <summary>Host -> guests: all stacks are empty, show the game-over screen. In Versus
    /// <c>message</c> carries the winner/tie announcement so every machine shows the same
    /// result; it's null/empty in Coop (shared score, no winner).</summary>
    internal class GameOverPacket
    {
        public string message;
    }

    /// <summary>Host -> everyone (Versus only): whose turn it is now. Play alternates in ascending
    /// playerId order, skipping players who are out of tiles; the host advances this after every
    /// accepted placement and rebroadcasts it. Guests reject their own placement input unless
    /// <c>activePlayerId</c> is their id.</summary>
    internal class TurnPacket
    {
        public int activePlayerId;
    }

    /// <summary>Preview of a player's current hovering tile, so everyone else can render a
    /// translucent ghost tinted in that player's color. <c>playerId</c> is the owner of the
    /// preview (a guest sends its own; the host overrides it and relays to the others).
    /// present=false means "no visible preview".</summary>
    internal class PreviewPacket
    {
        public int playerId;
        public bool present;
        public TileDto tile;
    }

    /// <summary>Guest -> host: my world disagrees with your sync stamp, send everything again.</summary>
    internal class ResyncRequestPacket
    {
        public string why;
        public bool rebuild;       // also re-adopt the world seeds (after a Classic -> Creative switch)
    }

    /// <summary>Host -> guest: authoritative world replacement (response to ResyncRequest).</summary>
    internal class ResyncPacket
    {
        public SnapshotDto snapshot;
        public bool rebuild;       // wipe + rebuild incl. seeds, like a fresh join
    }

    // ---------------- data transfer objects ----------------

    /// <summary>Cheap fingerprint of the shared world, checked after every placement.</summary>
    internal class SyncStamp
    {
        public int placedTiles;    // non-initial placed tiles on the board
        public int score;
    }

    /// <summary>Mirrors the game's TileData_003 save format 1:1 — everything needed to
    /// rebuild a tile (segments, quest state, special id) with TileGenerator.CreateTileFromSaveData.</summary>
    internal class TileDto
    {
        public int x;
        public int y;
        public int rot;
        public int seed;
        public List<SegDto> segs;

        public int specialId;      // SpecialTileId, 0 = none

        // QuestTileData_002 mirror; questTileId 0 (= Undefined) means "not a quest tile"
        public int questTileId;
        public bool questActive;
        public int questQueueIndex;
        public int targetValue = -1;
        public int questLevel;
        public int questId;
        public int unlockedChallengeId;
    }

    internal class SegDto
    {
        public int g;              // GroupTypeId
        public int s;              // SegmentTypeId
        public int r;              // rotation
    }

    /// <summary>Full world state used both at join and for desync recovery.</summary>
    internal class SnapshotDto
    {
        // reward-system stats
        public int score;
        public int level;
        public int placedTileCount;
        public int perfectPlacements;
        public int questsFulfilled;
        public int questsFailed;
        public int consecutivePerfectFits;
        public int consecutiveNoRotate;
        public int surroundedTiles;

        // deterministic world generation (join + rebuild only; ignored on a plain resync)
        public int biomeSeed;
        public int preplacedSeed;
        public List<PreplacedDto> preplacedTiles;
        public List<int> pendingChallenges;   // ChallengeId values

        public List<TileDto> tiles;           // all placed non-initial tiles
        public int hostStack;                 // host stack height, for the status UI
    }

    internal class PreplacedDto
    {
        public int sx;
        public int sy;
        public int questTileId;
    }

    // ---------------- LAN discovery (UDP, not framed) ----------------

    internal static class Discovery
    {
        internal const string Probe = "DRMP_DISCOVER_1";
        internal const string ReplyPrefix = "DRMP_HOST_1|";   // + "tcpPort|hostName"
    }
}
