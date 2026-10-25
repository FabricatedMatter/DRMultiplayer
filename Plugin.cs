using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DRMultiplayer
{
    [BepInPlugin(Guid, "DRMultiplayer", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "fabricatedmatter.drmultiplayer";
        public const string Version = "0.1.1";
        internal static ManualLogSource Log;

        // Sentinel meaning "no explicit name configured" — an empty PlayerName in
        // config triggers auto-detection (Steam persona → OS username → "Player").
        private static ConfigEntry<string> s_playerName;
        // Cached ONLY once we obtain an explicit config name or a real Steam persona
        // name. We never cache the OS-username/"Player" fallback, so an early read
        // (before Steam finished initializing) can still upgrade to the Steam name.
        private static string _resolvedName;

        // Player name resolves lazily with this precedence:
        //   1. an explicit, non-blank name from config,
        //   2. else the Steam persona name (once the game's SteamManager is up),
        //   3. else the OS username, 4. else "Player".
        // It's a property (not a field) so all existing call sites stay unchanged.
        internal static string PlayerName
        {
            get
            {
                if (_resolvedName != null) return _resolvedName;

                // 1. explicit config name wins.
                var configured = s_playerName?.Value;
                if (!string.IsNullOrWhiteSpace(configured))
                    return _resolvedName = configured.Trim();

                // 2. Steam persona name — guarded and cached only when available,
                //    since Steam may not be ready yet during early reads.
                var steam = TrySteamPersonaName();
                if (steam != null)
                    return _resolvedName = steam;

                // 3./4. transient fallbacks — deliberately NOT cached so a later
                //    read can still pick up the Steam name once it's ready.
                var user = Environment.UserName;
                return string.IsNullOrWhiteSpace(user) ? "Player" : user.Trim();
            }
        }

        // The shipped Steamworks.NET is stripped: SteamFriends.GetPersonaName() is
        // gone, so we ask for our OWN persona name via GetFriendPersonaName(our id).
        // Any Steam call can throw if the client isn't ready, so wrap it all.
        //
        // Never touch SteamManager.Initialized itself: the GOG build ships an older
        // SteamManager whose getter CREATES the manager on first access, and its Awake
        // calls Application.Quit() when steam_api64.dll is missing (issue #1). Peek at
        // the static instance instead — it only exists if the game made one itself.
        private static string TrySteamPersonaName()
        {
            try
            {
                var instance = Traverse.Create(typeof(SteamManager)).Field("s_instance").GetValue<SteamManager>();
                if (instance == null || !Traverse.Create(instance).Field("m_bInitialized").GetValue<bool>()) return null;
                var name = Steamworks.SteamFriends.GetFriendPersonaName(Steamworks.SteamUser.GetSteamID());
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
            catch
            {
                return null;
            }
        }

        private ConfigEntry<KeyCode> _overlayKey;
        private ConfigEntry<KeyCode> _overlayKeyAlt;
        private ConfigEntry<bool> _debugInput;
        private ConfigEntry<int> _port;
        private ConfigEntry<int> _discoveryPort;

        private Harmony _harmony;

        // The same physical keypress can reach us via BOTH the legacy-Input path
        // (Update) and the IMGUI KeyDown event, sometimes one frame apart. We dedupe
        // by source+time: if the OTHER source toggled moments ago, it's the same
        // press arriving twice — ignore it. Same-source repeats always pass.
        private float _lastToggleAt = -10f;
        private bool _lastToggleViaImgui;
        // One-time liveness proofs that MonoBehaviour callbacks actually run.
        private static bool s_updateAlive;
        private static bool s_onGuiAlive;

        // ---------------- overlay state ----------------

        private bool _overlayVisible;
        private Rect _overlayRect = new Rect(24, 24, 560, 0);
        private bool _dragging;
        private GUIStyle _labelStyle;
        private GUIStyle _smallStyle;

        private string _hostPortText;
        private string _joinAddressText = "192.168.";
        private string _joinPortText;
        private string _sendCountText = "5";
        private int _sendRecipientId = -1;   // selected gift recipient (playerId)
        private GameModeKind _hostMode = GameModeKind.Coop;   // Coop/Versus chosen before hosting

        private static bool _scanning;
        private static readonly List<HostFoundEvent> _foundHosts = new List<HostFoundEvent>();

        // Static mirrors so the input-blocking patches (no plugin instance) see the rect.
        private static bool s_overlayVisible;
        private static Rect s_overlayRect;

        private const float OverlayWidth = 560f;
        private const float OverlayHeight = 760f;

        private void Awake()
        {
            Log = Logger;

            _overlayKey = Config.Bind("Keys", "Overlay", KeyCode.F6, "Toggle the multiplayer window");
            _overlayKeyAlt = Config.Bind("Keys", "OverlayAlt", KeyCode.M, "Alternate toggle key — F-keys are unreliable on macOS");
            _debugInput = Config.Bind("Keys", "DebugInputLog", false, "Log every IMGUI KeyDown keycode (Info level) — for diagnosing dead keys");
            _port = Config.Bind("Network", "Port", 24680, "TCP port used for hosting");
            _discoveryPort = Config.Bind("Network", "DiscoveryPort", 24681, "UDP port used for LAN game discovery (must match on both machines)");
            // Default blank => auto-detect (Steam persona → OS username). Stored in a
            // static so the lazy PlayerName getter can read it (this plugin is a
            // singleton, so assigning the static from Awake is safe).
            s_playerName = Config.Bind("Network", "PlayerName", "",
                "Name shown to your coop partner. Leave blank to auto-detect from Steam (or your OS username).");

            _hostPortText = _port.Value.ToString();
            _joinPortText = _port.Value.ToString();

            _harmony = new Harmony(Guid);
            try { _harmony.PatchAll(); }
            catch (Exception e) { Log.LogError($"Harmony patching failed: {e}"); }

            Log.LogInfo($"DRMultiplayer v{Version} ready — {_overlayKey.Value} (or {_overlayKeyAlt.Value}) opens the multiplayer window.");
        }

        // Start runs after every plugin's Awake, so all plugins are registered by now.
        private void Start()
        {
            // Creative is built in since 0.1.1; the old companion bridge rewrites the handshake
            // and breaks Creative joins, so tell the player to remove it.
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("christianreese.drmultiplayer.creativebridge"))
                Session.Info("Creative Coop Bridge detected: Creative is built in now, please uninstall the bridge.");
        }

        private void Update()
        {
            if (!s_updateAlive) { s_updateAlive = true; Log.LogInfo("Update loop alive"); }

            if (Input.GetKeyDown(_overlayKey.Value))
            {
                ToggleOverlay(_overlayKey.Value, false);
            }
            // Alt key is a letter, so ignore it while an IMGUI text field has focus (keyboardControl != 0 = typing).
            else if (Input.GetKeyDown(_overlayKeyAlt.Value) && GUIUtility.keyboardControl == 0)
            {
                ToggleOverlay(_overlayKeyAlt.Value, false);
            }

            // Drain socket-thread events and run per-frame session housekeeping —
            // everything that touches the game happens here, on the main thread.
            try
            {
                // During a Classic -> Creative reload, packets wait until the new world is up.
                while (!Session.HoldingForModeSwitch && NetHub.Events.TryDequeue(out var ev))
                    Session.HandleEvent(ev);
                Session.Poll();
            }
            catch (Exception e)
            {
                Log.LogError($"session update failed: {e}");
            }
        }

        // Toggles the overlay, deduping the same physical keypress that can arrive
        // via both the legacy Input path (viaImguiEvent=false) and the IMGUI KeyDown
        // event (viaImguiEvent=true). Returns true if the overlay actually toggled
        // (so the OnGUI caller knows whether to Event.current.Use()).
        private bool ToggleOverlay(KeyCode key, bool viaImguiEvent)
        {
            string path = viaImguiEvent ? "IMGUI event path" : "legacy Input path";
            // If the OTHER source toggled less than 0.3s ago, this is the same press
            // arriving via the second path — suppress it. Same-source repeats always
            // pass, so a user toggling rapidly still works.
            if (viaImguiEvent != _lastToggleViaImgui
                && Time.unscaledTime - _lastToggleAt < 0.3f)
            {
                if (_debugInput.Value)
                    Log.LogInfo($"Ignored duplicate {key} toggle via {path} (same press already handled)");
                return false;
            }
            _overlayVisible = !_overlayVisible;
            _lastToggleAt = Time.unscaledTime;
            _lastToggleViaImgui = viaImguiEvent;
            Log.LogInfo($"Overlay toggled by {key} via {path}");
            return true;
        }

        private void OnDestroy()
        {
            Session.Stop("game closed");
            _harmony?.UnpatchSelf();
        }

        // ---------------- discovery callbacks (main thread, via Session.HandleEvent) ----------------

        internal static void OnHostDiscovered(HostFoundEvent host)
        {
            _foundHosts.Add(host);
        }

        internal static void OnScanFinished()
        {
            _scanning = false;
        }

        // ---------------- overlay ----------------

        // The game ships a STRIPPED UnityEngine.IMGUIModule: no GUI.Window/DragWindow,
        // no GUILayout.BeginVertical/Space, no Event.Use. The panel is built from the
        // survivors (GUI.Box + BeginArea/Label/Button/TextField/Begin|EndHorizontal)
        // and dragged by hand.
        private void OnGUI()
        {
            if (!s_onGuiAlive) { s_onGuiAlive = true; Log.LogInfo("OnGUI alive"); }

            // IMGUI event fallback: on some platforms (notably Apple Silicon under
            // Rosetta) the legacy Input manager never delivers these keys, but the
            // IMGUI event path still does. Guard against a double-toggle on platforms
            // where the Update()/legacy path already fired this frame.
            var ev = Event.current;
            if (ev != null && ev.type == EventType.KeyDown)
            {
                if (_debugInput.Value) Log.LogInfo($"IMGUI KeyDown: {ev.keyCode}");
                if (ev.keyCode == _overlayKey.Value)
                {
                    if (ToggleOverlay(_overlayKey.Value, true)) ev.Use();
                }
                else if (ev.keyCode == _overlayKeyAlt.Value && GUIUtility.keyboardControl == 0)
                {
                    if (ToggleOverlay(_overlayKeyAlt.Value, true)) ev.Use();
                }
            }

            s_overlayVisible = _overlayVisible;
            if (!_overlayVisible)
            {
                DrawMiniStatus();
                return;
            }
            EnsureStyles();

            _overlayRect.width = OverlayWidth;
            _overlayRect.height = OverlayHeight;
            s_overlayRect = _overlayRect;

            string modeLabel = Session.Role == SessionRole.Off ? "Multiplayer"
                : Coop.IsCreative ? "Creative Coop"
                : Session.Mode == GameModeKind.Versus ? "Versus" : "Coop";
            GUI.Box(_overlayRect, $"Dorfromantik Multiplayer v{Version} — {modeLabel}");
            GUILayout.BeginArea(new Rect(_overlayRect.x + 12, _overlayRect.y + 34,
                                         _overlayRect.width - 24, _overlayRect.height - 44));
            try { DrawContents(); }
            catch (Exception e) { GUILayout.Label($"UI error: {e.Message}", _smallStyle); }
            GUILayout.EndArea();

            HandleDrag();
        }

        private void EnsureStyles()
        {
            GUI.skin.button.fontSize = 20;
            GUI.skin.button.richText = true;   // color recipient names in the Send controls
            GUI.skin.box.fontSize = 20;
            GUI.skin.textField.fontSize = 20;
            if (_labelStyle != null) return;
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, wordWrap = true, richText = true };
            _smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true, richText = true };
        }

        private void DrawContents()
        {
            if (!Coop.InGame)
            {
                GUILayout.Label("Start a Classic or Creative game first, then host or join from here.", _labelStyle);
                DrawMessages();
                return;
            }

            switch (Session.Role)
            {
                case SessionRole.Off:
                    DrawIdle();
                    break;
                case SessionRole.Guest when !Session.Active:
                    GUILayout.Label("Connecting…", _labelStyle);
                    if (GUILayout.Button("Cancel")) Session.Stop("cancelled");
                    break;
                default:
                    DrawActive();
                    break;
            }

            DrawMessages();
        }

        private void DrawIdle()
        {
            var hostBlocker = Session.HostBlocker();
            var joinBlocker = Session.JoinBlocker();

            GUILayout.Label("<b>Host a game</b>", _labelStyle);
            if (Coop.IsCreative)
            {
                GUILayout.Label("<color=#cccccc>Creative: one shared world, unlimited tiles, everyone builds at once. Guests must be in Creative too.</color>", _smallStyle);
            }
            else
            {
                // Mode toggle — chosen BEFORE hosting. Coop = shared world + shared score, everyone plays
                // at once. Versus = alternating turns, individual scores, highest score wins.
                GUILayout.BeginHorizontal();
                GUILayout.Label("Mode:", _labelStyle, GUILayout.Width(70));
                if (GUILayout.Button(_hostMode == GameModeKind.Versus ? "Versus  ▸" : "Coop  ▸", GUILayout.Width(180)))
                    _hostMode = _hostMode == GameModeKind.Coop ? GameModeKind.Versus : GameModeKind.Coop;
                GUILayout.EndHorizontal();
                GUILayout.Label(_hostMode == GameModeKind.Versus
                    ? "<color=#cccccc>Versus: players take turns; each scores individually; highest score wins.</color>"
                    : "<color=#cccccc>Coop: one shared world and score; everyone plays at once.</color>", _smallStyle);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port:", _labelStyle, GUILayout.Width(70));
            _hostPortText = GUILayout.TextField(_hostPortText, 5, GUILayout.Width(110));
            if (GUILayout.Button("Host Game", GUILayout.Width(180)))
            {
                if (int.TryParse(_hostPortText, out var port) && port > 0 && port < 65536)
                    Session.StartHost(port, _discoveryPort.Value, PlayerName, _hostMode);
                else
                    Session.Info("Invalid port.");
            }
            GUILayout.EndHorizontal();
            if (hostBlocker != null)
                GUILayout.Label($"<color=#ffaa55>Hosting unavailable: {hostBlocker}</color>", _smallStyle);

            // Tell the host which local IP to hand to joiners — otherwise on a multi-homed machine
            // (e.g. LAN + Tailscale) they might give out an address the joiner can't reach.
            var lanAddrs = NetHub.LocalLanAddresses();
            if (lanAddrs.Count > 0)
                GUILayout.Label($"<color=#cccccc>Your LAN address(es): {string.Join(", ", lanAddrs.ToArray())} — give this + the port to joiners.</color>", _smallStyle);
            else
                GUILayout.Label("<color=#cccccc>No LAN address detected — you can still host, but joiners must reach this machine by IP.</color>", _smallStyle);

            GUILayout.Label(" ", _smallStyle);
            GUILayout.Label("<b>Join a game</b>", _labelStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("IP:", _labelStyle, GUILayout.Width(70));
            _joinAddressText = GUILayout.TextField(_joinAddressText, 45, GUILayout.Width(220));
            GUILayout.Label("Port:", _labelStyle, GUILayout.Width(70));
            _joinPortText = GUILayout.TextField(_joinPortText, 5, GUILayout.Width(110));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Join", GUILayout.Width(180)))
            {
                if (int.TryParse(_joinPortText, out var port) && port > 0 && port < 65536 && !string.IsNullOrWhiteSpace(_joinAddressText))
                    Session.Join(_joinAddressText.Trim(), port);
                else
                    Session.Info("Enter a valid IP and port.");
            }
            if (GUILayout.Button(_scanning ? "Scanning…" : "Scan LAN", GUILayout.Width(180)) && !_scanning)
            {
                _scanning = true;
                _foundHosts.Clear();
                NetHub.Scan(_discoveryPort.Value);
            }
            GUILayout.EndHorizontal();
            if (joinBlocker != null)
                GUILayout.Label($"<color=#ffaa55>Joining unavailable: {joinBlocker}</color>", _smallStyle);

            if (_foundHosts.Count > 0)
            {
                GUILayout.Label("<b>Games on your network:</b>", _labelStyle);
                foreach (var host in _foundHosts)
                {
                    if (GUILayout.Button($"{host.Name}  —  {host.Address}:{host.Port}"))
                        Session.Join(host.Address, host.Port);
                }
            }
            else if (!_scanning)
            {
                GUILayout.Label("No games found yet — hit Scan LAN while your friend is hosting.", _smallStyle);
            }
        }

        private void DrawActive()
        {
            var players = Session.Players();
            bool versus = Session.Mode == GameModeKind.Versus;
            string role = Session.Role == SessionRole.Host ? "hosting" : "joined";

            if (versus)
            {
                string thex = Coop.PlayerColorHex(Session.ActivePlayerId);
                string turnName = $"<color=#{thex}><b>{Session.PlayerName(Session.ActivePlayerId)}</b></color>";
                GUILayout.Label($"<b>Versus</b> — {turnName}'s turn  ({players.Count} player{(players.Count == 1 ? "" : "s")}, {role})", _labelStyle);
            }
            else
            {
                GUILayout.Label($"<b>{(Coop.IsCreative ? "Creative Coop" : "Coop")}</b> — {players.Count} player{(players.Count == 1 ? "" : "s")}  ({role})", _labelStyle);
            }
            if (Session.Role == SessionRole.Host && Session.ContinuingSaved)
                GUILayout.Label("<color=#88ddaa>Continuing a saved multiplayer game — returning players get their saved tiles back.</color>", _smallStyle);

            // The full roster: name in the player's color, their tile count, self highlighted.
            // In Versus we also show each player's individual score + a marker on whose turn it is.
            foreach (var p in players)
            {
                string hex = Coop.PlayerColorHex(p.ColorIndex);
                string you = p.Self ? "  <b>(you)</b>" : "";
                if (versus)
                {
                    string turn = p.Id == Session.ActivePlayerId ? "  <color=#ffe08a>● turn</color>" : "";
                    GUILayout.Label($"<color=#{hex}>■</color> <color=#{hex}><b>{p.Name}</b></color>{you}   score: {p.VersusScore}   tiles: {Num(p.Tiles)}{turn}", _labelStyle);
                }
                else
                {
                    GUILayout.Label($"<color=#{hex}>■</color> <color=#{hex}><b>{p.Name}</b></color>{you}   tiles: {Num(p.Tiles)}", _labelStyle);
                }
            }

            var refs = Coop.Refs;
            if (refs != null)
                GUILayout.Label($"<b>{(versus ? "Total score" : "Shared score")}:</b> {refs.Rewards.Score}      <b>Tiles placed:</b> {Coop.PlacedTileCount()}"
                                + (Session.ResyncCount > 0 ? $"      resyncs: {Session.ResyncCount}" : ""), _smallStyle);
            if (!versus && !Coop.IsCreative && Session.MyTiles == 0 && !Session.AllStacksEmpty())
                GUILayout.Label("<color=#ffaa55>You're out of tiles! Ask another player to send you some.</color>", _labelStyle);

            // Gifting is Coop-only (it doesn't fit alternating individual play), and pointless with
            // Creative's unlimited stacks.
            if (versus || Coop.IsCreative)
            {
                GUILayout.Label(" ", _smallStyle);
                if (GUILayout.Button(Session.Role == SessionRole.Host ? "Stop hosting" : "Disconnect"))
                    Session.Stop("disconnected by player");
                return;
            }

            GUILayout.Label(" ", _smallStyle);
            var others = Session.OtherPlayers();
            if (others.Count == 0)
            {
                GUILayout.Label("<b>Send tiles</b> — waiting for others to join…", _smallStyle);
            }
            else
            {
                if (others.All(o => o.Id != _sendRecipientId)) _sendRecipientId = others[0].Id;
                var rec = others.First(o => o.Id == _sendRecipientId);
                string rhex = Coop.PlayerColorHex(rec.ColorIndex);

                GUILayout.Label("<b>Send tiles</b> (taken from your stack)", _labelStyle);
                GUILayout.BeginHorizontal();
                GUILayout.Label("To:", _labelStyle, GUILayout.Width(50));
                // One button cycles through the other players (no dropdown in stripped IMGUI).
                if (GUILayout.Button($"<color=#{rhex}>{rec.Name}</color>  ▸", GUILayout.Width(220)))
                    _sendRecipientId = NextRecipient(others, _sendRecipientId);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(" − ", GUILayout.Width(60))) BumpSendCount(-1);
                _sendCountText = GUILayout.TextField(_sendCountText, 4, GUILayout.Width(80));
                if (GUILayout.Button(" + ", GUILayout.Width(60))) BumpSendCount(+1);
                if (GUILayout.Button("Send", GUILayout.Width(140)))
                {
                    if (int.TryParse(_sendCountText, out var n) && n > 0)
                        Session.SendTilesTo(_sendRecipientId, n);
                    else
                        Session.Info("Enter how many tiles to send.");
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Label(" ", _smallStyle);
            if (GUILayout.Button(Session.Role == SessionRole.Host ? "Stop hosting" : "Disconnect"))
                Session.Stop("disconnected by player");
        }

        private static int NextRecipient(List<Session.PlayerView> others, int currentId)
        {
            int idx = others.FindIndex(o => o.Id == currentId);
            return others[(idx + 1) % others.Count].Id;
        }

        private void DrawMessages()
        {
            if (Session.Messages.Count == 0) return;
            GUILayout.Label(" ", _smallStyle);
            GUILayout.Label("<b>Log</b>", _smallStyle);
            foreach (var msg in Session.Messages)
                GUILayout.Label(msg, _smallStyle);
        }

        /// <summary>Tiny read-only status box (top-right) while playing with the window closed.
        /// Shows every player's tile count compactly, each name in the player's color.</summary>
        private void DrawMiniStatus()
        {
            if (Session.Role == SessionRole.Off || !Session.Active) return;
            EnsureStyles();
            var players = Session.Players();
            bool versus = Session.Mode == GameModeKind.Versus;
            var sb = new StringBuilder();
            foreach (var p in players)
            {
                if (sb.Length > 0) sb.Append("    ");
                // Versus: show each player's score + a turn marker; Coop: show tile counts.
                if (versus)
                {
                    string turn = p.Id == Session.ActivePlayerId ? " ●" : "";
                    sb.Append($"<color=#{Coop.PlayerColorHex(p.ColorIndex)}>{p.Name}</color>: {p.VersusScore}{turn}");
                }
                else
                {
                    sb.Append($"<color=#{Coop.PlayerColorHex(p.ColorIndex)}>{p.Name}</color>: {Num(p.Tiles)}");
                }
            }
            string header = versus
                ? $"Versus — {Session.PlayerName(Session.ActivePlayerId)}'s turn  ({_overlayKey.Value} = menu)"
                : $"{(Coop.IsCreative ? "Creative Coop" : "Coop")} — {players.Count} players  ({_overlayKey.Value} = menu)";
            var rect = new Rect(Screen.width - 420, 8, 412, 54);
            GUI.Box(rect, "");
            GUI.Label(new Rect(rect.x + 10, rect.y + 4, rect.width - 20, 24),
                new GUIContent(header), _smallStyle);
            GUI.Label(new Rect(rect.x + 10, rect.y + 26, rect.width - 20, 24),
                new GUIContent(sb.ToString()), _smallStyle);
        }

        private static string Num(int v) => Coop.IsCreative ? "∞" : v < 0 ? "?" : v.ToString();

        private void BumpSendCount(int delta)
        {
            int.TryParse(_sendCountText, out var n);
            _sendCountText = Mathf.Max(1, n + delta).ToString();
        }

        private void HandleDrag()
        {
            var e = Event.current;
            if (e == null) return;
            var titleBar = new Rect(_overlayRect.x, _overlayRect.y, _overlayRect.width, 30);
            if (e.type == EventType.MouseDown && e.button == 0 && titleBar.Contains(e.mousePosition))
                _dragging = true;
            else if (e.type == EventType.MouseUp)
                _dragging = false;
            else if (e.type == EventType.MouseDrag && _dragging)
            {
                _overlayRect.x += e.delta.x;
                _overlayRect.y += e.delta.y;
            }
        }

        /// <summary>True if the cursor is over the overlay (GUI coords) — read by input patches.</summary>
        internal static bool CursorOverOverlay()
        {
            if (!s_overlayVisible) return false;
            var m = Input.mousePosition;
            return s_overlayRect.Contains(new Vector2(m.x, Screen.height - m.y));
        }
    }
}
