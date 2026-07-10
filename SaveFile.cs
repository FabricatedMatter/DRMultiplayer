using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace DRMultiplayer
{
    // =====================================================================
    //  Companion save file (Phase 3 — save & re-host multiplayer games).
    //
    //  The host's SHARED world already survives quit/continue via the game's
    //  own classic autosave — continuing that save reloads every placed tile
    //  AND the host's own tile stack. The only thing the game does NOT know
    //  about is the OTHER players: each guest keeps an independent tile stack
    //  (remaining count + top-of-stack face) that lives only on the guest's
    //  machine at play time and would be lost when everyone quits.
    //
    //  So the mod writes a small JSON "companion" next to BepInEx config,
    //  keyed to the classic save it accompanies (by the world's deterministic
    //  biome/preplaced seeds — see Coop.SaveKey), remembering per-player
    //  bookkeeping so that when the host Continues + re-hosts, each returning
    //  guest is handed back its saved remaining-tile count and top-of-stack.
    //
    //  Everything here is best-effort and fully wrapped: companion I/O must
    //  never throw into the game loop, and a missing/corrupt file simply means
    //  "no saved bookkeeping — everyone starts fresh".
    // =====================================================================

    /// <summary>Coop today, Versus in Phase 4. Persisted in the companion + sent in Welcome so
    /// the mode a world was hosted in is remembered when it's continued. Named GameModeKind to
    /// avoid clashing with the game's own <c>GameModeId</c>.</summary>
    internal enum GameModeKind { Coop = 0, Versus = 1 }

    /// <summary>One remembered player slot. For the host (id 0) the stack fields are informational
    /// only (the game's classic save restores the host's real stack); for guests they are what we
    /// hand back on rejoin.</summary>
    internal class CompanionPlayer
    {
        public int playerId;
        public string name;
        public int colorIndex;
        public int stackHeight = -1;   // remaining tiles, -1 = unknown
        public TileDto topOfStack;     // the face of the next tile, null when unknown
        public int versusScore;        // Phase 4 will read/restore this
    }

    /// <summary>The whole companion document for one saved world.</summary>
    internal class CompanionSave
    {
        // Bump if the companion JSON shape changes incompatibly; an older/newer file is ignored.
        internal const int Format = 1;

        public int format = Format;
        public int protocol;           // ProtocolVersion.Current at save time (informational)
        public string modVersion;      // Plugin.Version at save time (informational)
        public int gameMode;           // GameModeKind (0 = coop). Versus lands in Phase 4.
        public int sessionId;          // host sessionId at save time
        public long savedAtTicks;      // DateTime.UtcNow.Ticks — newest wins if ever ambiguous
        public List<CompanionPlayer> roster = new List<CompanionPlayer>();
    }

    /// <summary>Reads/writes companion files under BepInEx/config/DRMultiplayer/. Every method
    /// swallows its own failures — companion persistence is a convenience, never load-bearing.</summary>
    internal static class CompanionStore
    {
        private static string Dir => Path.Combine(Paths.ConfigPath, "DRMultiplayer");

        // Sanitize the world key into a safe file name fragment ("<biomeSeed>_<preplacedSeed>").
        private static string PathFor(string key)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                key = key.Replace(c, '_');
            return Path.Combine(Dir, $"session-{key}.json");
        }

        internal static void Save(string key, CompanionSave data)
        {
            if (string.IsNullOrEmpty(key) || data == null) return;
            try
            {
                Directory.CreateDirectory(Dir);
                data.savedAtTicks = DateTime.UtcNow.Ticks;
                File.WriteAllText(PathFor(key), JsonConvert.SerializeObject(data, Formatting.Indented));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"companion save failed ({key}): {e.Message}");
            }
        }

        internal static CompanionSave Load(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            try
            {
                var path = PathFor(key);
                if (!File.Exists(path)) return null;
                var save = JsonConvert.DeserializeObject<CompanionSave>(File.ReadAllText(path));
                if (save == null || save.format != CompanionSave.Format) return null;
                if (save.roster == null) save.roster = new List<CompanionPlayer>();
                return save;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"companion load failed ({key}): {e.Message}");
                return null;
            }
        }
    }
}
