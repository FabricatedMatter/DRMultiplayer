# DRMultiplayer: local-coop multiplayer for Dorfromantik

A BepInEx 5 (Mono) plugin that adds **LAN multiplayer for up to 4 players** to
Dorfromantik (**host + up to 3 guests**), in two modes:

- **Coop**: one shared world, one shared score, an independent tile stack per
  player; everyone places at once.
- **Versus**: one shared world but **turn-based** play and an **individual
  score per player**; the highest score at the end wins.

Built for trusted local play: the host validates only *legality* (slot free +
rail/river rules), never whether another player "really had" a tile.

## Installing

Every player needs the game plus this mod.

**Windows (easiest, no tech skills needed):** download
`DRMultiplayer-windows.zip` from the
[releases page](https://github.com/FabricatedMatter/DRMultiplayer/releases), extract it
anywhere (your Downloads folder is fine), and **double-click
`install-windows.bat`**. The script finds your Steam install of Dorfromantik
automatically, sets up the whole modding base (BepInEx + the unstripped corlib
the game needs) and installs the mod. There are **no Steam launch options** and
no other manual steps on Windows; just start the game afterwards. If Windows
SmartScreen warns about the script, click "More info" and then "Run anyway".
Details and flags are in [Building](#building) below.

**Linux (Steam/Proton):** clone this repo and run `./build-linux.sh` (see
[Building](#building)).

**macOS:** clone this repo and run `./build-mac.sh` (see
[Building](#building)).

## Playing

All machines need the game + this plugin (see [Installing](#installing)).

1. **Host**: start a classic game (**fresh or continued**), press **F6 (or M)**,
   pick the **Mode** (toggle **Coop / Versus**; this is chosen *before* hosting),
   optionally change the port (default `24680`), hit **Host Game**. You can start
   playing immediately; friends join whenever they like (up to 3 of them). If
   you're **continuing a saved multiplayer game** (see below), the overlay says so,
   the saved mode is restored, and returning players get their saved tiles (and
   Versus scores) back.
2. **Guests**: press **F6 (or M)** from **any classic game** (fresh or mid-game),
   then either **Scan LAN** and click the discovered game, or type the host's
   IP/port and **Join**. The host's world (seeds, preplaced challenge tiles, any
   tiles already placed) is transferred automatically; **your own board is wiped
   and replaced by the host's** when you join, so you don't need an empty game.
   Guests join **incrementally**; there's no need to wait for everyone before the
   host begins.
3. Play! Every player places tiles into the shared world at any time. The
   overlay lists all players, each in their own color, with their tile counts;
   you also see each other player's **ghost preview** (a translucent tile in
   their color) of what they're about to place.
   - Quest rewards, flag quests and perfect placements grant bonus tiles **only
     to the player whose placement earned them**. Score and level are shared.
   - **Send tiles**: in the multiplayer window, choose a **recipient** (cycle
     through the other players), pick an amount, and hit **Send**; the tiles are
     deducted from your stack and added to theirs. The host relays guest→guest
     gifts.
   - Running out of tiles does **not** end the game; you spectate (or beg for
     tiles) until **everyone's** stacks are empty, and then the game-over screen
     shows on all machines.

A dropped guest can rejoin the same session (the host keeps listening); the
world is rebuilt from the host on reconnect.

### Versus mode

Toggle **Versus** in the host window before hitting **Host Game**. Same shared
world, but:

- **Turn-based.** Only the **active player** may place a tile; everyone else
  waits. The overlay and the mini-HUD show whose turn it is (`● turn`). Turns
  rotate in ascending player order, and the host advances the turn automatically
  after each placement.
- **Individual scores.** Each player has their **own** score. Quest completions
  and perfect placements count **only for the placer**: the host measures the
  score gained by each placement and credits it to whoever made it, then
  broadcasts everyone's scores so all machines agree. (The game's own on-screen
  score widget still shows the global running total; the **per-player** breakdown
  lives in the multiplayer overlay + mini-HUD.)
- **Out of tiles?** A player with no tiles is **skipped** in the turn order; the
  others keep playing. If only one player still has tiles, they keep taking
  consecutive turns until they're out too.
- **Winner.** When **everyone's** stacks are empty the game ends and the **highest
  score wins** (ties are reported); the result is shown in the log on every
  machine, and the normal game-over screen still appears.
- **Gifting is disabled** in Versus (it doesn't fit alternating individual play);
  the "Send tiles" control is hidden.

### Saving & re-hosting a multiplayer game

You can quit a multiplayer game and pick it up later:

- The **shared world** and the **host's own stack** live in the game's normal
  classic autosave; the host just **Continues** that save as usual.
- The mod additionally writes a small **companion file** next to it
  (`BepInEx/config/DRMultiplayer/session-<biomeSeed>_<preplacedSeed>.json`, keyed
  to the world's seeds so it matches the exact classic save being continued). It
  remembers the game mode and, **per player**, their remaining-tile count and
  top-of-stack. It's refreshed on the host's autosave and whenever a player
  joins/leaves.
- When the host **Continues + re-hosts** that world, each returning player is
  matched **by name** to their remembered slot and handed back their saved
  playerId, remaining-tile count and top-of-stack. **New** players (names not in
  the file) simply get the next free slot with a fresh stack.
- **Top-of-stack limitation:** a player's *next* tile face is restored exactly
  (seed + segments + quest/special). The tiles **deeper** in a returning guest's
  stack are freshly regenerated; only the count and the very next tile are
  guaranteed to match. The host's full stack comes back intact from the game's
  own save.

### Playing over the internet (VPN / port forwarding)

The mod speaks plain TCP, so it works over the internet too, not just on a home
network. One thing changes: the **Scan LAN** button cannot discover games across
the internet (it uses a local UDP broadcast), so guests always **type the host's
IP and port** into the Join fields. Two ways to get connected, easiest first.

**Option A: a free VPN tool (recommended, no router setup).** Tools like
[Tailscale](https://tailscale.com), [ZeroTier](https://www.zerotier.com) or
Radmin VPN make your computers behave as if they were plugged into the same home
network, even across the internet:

1. Everyone (host and guests) installs the same tool and joins the same
   network. With Tailscale you log in and share access with your friends (or
   they log into the same account); with ZeroTier or Radmin VPN one person
   creates a network and the others join it by its ID/name.
2. The host looks up their **VPN IP address** in the tool's window (Tailscale
   addresses look like `100.x.y.z`, ZeroTier like `192.168.19x.x` by default).
3. The host starts hosting as usual; guests press **F6 (or M)**, type that IP
   and the port (default `24680`), and hit **Join**.

This needs no router changes and works through almost any firewall or shared
connection. When the host first hosts, allow the game/mod through the OS
firewall prompt (on Windows, click "Allow access").

**Option B: port forwarding on the host's router.** No extra software, but you
need access to the host's router settings:

1. Find the host PC's **local** IP address (Windows: `ipconfig` in a command
   prompt, look for "IPv4 Address", usually `192.168.x.y`). Ideally give the PC
   a fixed address ("DHCP reservation" in the router) so the forward keeps
   working after a reboot.
2. Open the router's admin page (commonly `http://192.168.0.1` or
   `http://192.168.1.1`; the address and login are printed on the router).
   Look for **"Port Forwarding"** (sometimes called "Virtual Server" or "NAT").
3. Add a rule: forward external **TCP port `24680`** to the host PC's local IP,
   internal port `24680` (or whatever port you picked in the Host window).
4. The host finds their **public** IP (search "what is my IP" in a browser)
   and gives it to the guests.
5. Guests type that public IP and the port into the Join fields.

Caveats for option B: the host must also allow the game through their OS
firewall (Windows asks on first host; click "Allow access"). Some ISPs put
customers behind **CG-NAT** (shared public addresses); then port forwarding is
impossible and you should use option A instead. Public IPs also change from
time to time on most home connections, so re-check it before each session.

In both options only the **host** needs any setup; guests just connect
outward, which needs no forwarding or firewall changes.

### Notes / limitations

- The window toggles with **F6 or M**; both are rebindable in
  `BepInEx/config/` after the first run. **M** exists because macOS F-keys are
  unreliable (even with Fn). If neither key opens the window, set
  `DebugInputLog=true` in the config to log every key the game receives.
- Classic mode only, up to 4 players (host + 3 guests). Both **Coop** and
  **Versus** are supported (host picks before hosting).
- All players must run the **same mod version**: the wire protocol is versioned
  and mismatched peers refuse to pair (this release bumped it to **protocol 5**
  for Versus mode, so it is **not** compatible with older builds).
- **Undo is disabled** during multiplayer (it would fork the shared world).
- Coop scores are kept **off the solo leaderboards/highscores**.
- The **guest never autosaves**; your solo autosave is untouched by joining.
  The **host autosaves normally**, so the shared world lives on in the host's
  classic save and can be **continued and re-hosted** (per-player stacks are
  restored from the companion file; see "Saving & re-hosting" above).
- LAN discovery uses a UDP broadcast on `DiscoveryPort` (default `24681`,
  must match on both machines). If broadcast doesn't reach (VPNs, containers,
  same-machine testing), join by IP directly (`127.0.0.1` works).

## Building

### Install the modding base

Dorfromantik ships **stripped** Unity assemblies, so plain BepInEx isn't
enough: the mod needs the **unstripped** BCL (corlib) at runtime. The game's
own **UnityEngine** assemblies must **not** be overridden (doing so causes
missing/gray assets), so only the BCL is unstripped.

The Windows, Linux and macOS installers are all **standalone**: they assemble
the whole modding base themselves from the official BepInEx release plus a
**platform-matched** unstripped corlib, so you **no longer need to download
anything from itch.io**. See [Why the full corlib?](#why-the-full-corlib) below
for the reasoning (including why the corlib differs by platform).

**Windows (native):** run **`install-windows.bat`** (double-click) or
`install-windows.ps1` from PowerShell. It installs the official **BepInEx
5.4.23.5 (win_x64)** loader plus the same **full `net_4_x` corlib** the Linux
install uses (the Linux game *is* this Windows build, just run under Proton),
downloaded and sha256-verified from this project's GitHub release, adds a full
`Newtonsoft.Json`, points `doorstop_config.ini` at `unstripped_libs`, and sets
**`HideManagerGameObject = true`** in `BepInEx/config/BepInEx.cfg` (see [Why
the full corlib?](#why-the-full-corlib) below). It then installs
`DRMultiplayer.dll`: a copy sitting next to the script wins (that's how the
release zip is laid out), otherwise it downloads the DLL from this repo's
GitHub releases. Unlike Linux/macOS there are **no manual steps at all**: no
launch options, the `winhttp.dll` loader injects by itself on native Windows.
Flags: `-Clean` to wipe a previous BepInEx install first, `-NoMod` to set up
the modding base only, and `-GameDir "D:\...\Dorfromantik"` to override the
game folder.

To **bundle a ready-made package for less tech-savvy Windows users**, run
**`./package-windows.sh`** (on Linux): it builds the plugin in Release and
writes `dist/DRMultiplayer-windows.zip` containing `DRMultiplayer.dll`, both
installer scripts and a three-line `INSTALL.txt`. Upload that zip to a GitHub
release; also attach the bare `DRMultiplayer.dll` as its own release asset so
that the standalone `install-windows.ps1` can fetch it when no DLL sits next
to it.

**Linux (Steam/Proton):** just run **`./build-linux.sh`**. By default it does a
**standalone** install: the official **BepInEx 5.4.23.5 (win_x64)** loader plus
the **full `net_4_x` corlib** downloaded and sha256-verified from this project's
own GitHub release, with `doorstop_config.ini` configured and a full
`Newtonsoft.Json` added, then it builds and deploys. It also pre-seeds
`BepInEx/config/BepInEx.cfg` with **`HideManagerGameObject = true`** under
`[Chainloader]`, the second half of the fix (see [Why the full
corlib?](#why-the-full-corlib) below): without it the mod loads but its UI never
appears on Dorfromantik. This is **confirmed fully working on Linux/Proton.** On
Linux the game runs as the **Windows build under Steam Proton**, so this installs
the `winhttp.dll` loader. Modes: `--standalone` (default, recommended);
`--gts[=zip]`, an **optional fallback** that installs Game Theory Studio's
`bepinex-base.zip` instead (download it from
<https://game-theory-studio.itch.io/dorfromantik-mods> into `~/Downloads`, or
pass its path; never auto-selected); `--official`, a **deprecated** debug path
using the reduced `unityjit` corlib from <https://unity.bepinex.dev>, known to
produce gray/missing assets since **v1.1.5.3**. Other flags: `--clean` to wipe a
previous install first, `--no-build` to set up the modding base only, and
`GAME_DIR=/path` to override the game folder. The manual steps are both in Steam:
force Proton (Properties → Compatibility → *Force the use of a specific Steam
Play compatibility tool*) and set the launch option
`WINEDLLOVERRIDES="winhttp=n,b" %command%`.

**macOS (native):** just run **`./build-mac.sh`**. This is now **confirmed fully
working**, tested live on an Apple Silicon MacBook Air. The script does the whole
thing natively: it downloads the official **BepInEx 5.4.23.5 (macOS universal)**
loader plus the **full macOS `net_4_x` corlib** (the same `net_4_x` breadth as
Linux, 146 DLLs + 134 Facades, but with the **macOS-flavor core assemblies**
overlaid; GitHub release asset `net_4_x_corlib_macos-2021.3.45.zip` from this repo,
pinned and sha256-verified) and a full
`Newtonsoft.Json` (the game's own UnityEngine assemblies are **not** overridden),
installs them, patches and enables `run_bepinex.sh`, and (like Linux) sets
**`HideManagerGameObject = true`** in `BepInEx/config/BepInEx.cfg` so the mod's UI
actually shows (see [Why the full corlib?](#why-the-full-corlib) below). It forces
**x86_64** via `ARCHPREFERENCE` so BepInEx can bootstrap even on **Apple Silicon**
(the game app is a universal binary and its x86_64 slice runs under **Rosetta**),
clears the Gatekeeper quarantine, then builds and deploys the mod. Note that macOS
needs its **own-platform corlib**: the same full `net_4_x` breadth Linux uses,
just with the **macOS-flavor core assemblies** (the reduced 15-DLL `unityjit` set
loads the mod menu but leaves the game's textures/assets missing); see [Why the
full corlib?](#why-the-full-corlib) for why a Windows corlib on the
Mac Mono runtime is fatal. There is **no `--gts` mode** on macOS: GTS's zip is
Windows-only. Flags: `--clean` to wipe a previous BepInEx install first,
`--no-build` to set up the modding base without building, and `GAME_DIR=/path` to
override the game folder.

The **only manual step** on macOS is the Steam launch option: in
Steam → Dorfromantik → Properties → Launch Options, set
`"<GameDir>/run_bepinex.sh" %command%` (the script prints the exact line for
your machine).

#### Why the full corlib?

GTS's working `bepinex-base.zip` turned out to be just the official **BepInEx
5.4.23.5** (its core DLLs / `winhttp.dll` / changelog are byte-for-byte identical
to the official release); it differs from a stock install in exactly **two**
non-library ways:

1. **The full `net_4_x` unstripped Mono class library** (146 DLLs + 134 Facades,
   from the Unity 2021.3.45f2 editor) instead of the reduced `unityjit` corlib
   (15 DLLs) that unity.bepinex.dev serves. Since Dorfromantik **1.1.5.3** the
   game strips against the `unityjit` profile, so only the full `net_4_x` profile
   still carries the BCL members that BepInEx and mods need; the reduced corlib
   gives gray/missing assets. That full profile is a Unity redistributable, so
   this project now **hosts it itself** (release tag `bepinex-corlib-2021.3.45`),
   which is why the installers are independent of GTS's package.

   **The corlib is per-platform, though.** The `net_4_x` profile above (GitHub
   release asset `net_4_x_corlib_unity-2021.3.45.zip`) is for the **Windows**
   build, which is what runs both natively on Windows and on Linux under Proton.
   The **native macOS** build needs the **full macOS `net_4_x` corlib**: the
   *same* `net_4_x` breadth (146
   DLLs + 134 Facades), just with the **macOS-flavor core assemblies** overlaid,
   provided as a hybrid (GitHub release asset `net_4_x_corlib_macos-2021.3.45.zip`,
   also hosted on this repo, sha256-pinned). macOS does **not** get away with the
   reduced 15-DLL `unityjit` set: that loads the mod menu, but the game's
   **textures/assets go missing**; it needs the full breadth too, only with
   macOS-flavor core assemblies. You must give each OS its **own-platform**
   corlib: dropping the **Windows** `net_4_x` corlib onto the **macOS** Mono
   runtime is fatal. It P/Invokes `BCrypt.dll` (Windows-only, so a
   `DllNotFoundException`) and its metadata mismatch makes MonoMod/Harmony throw
   inside BepInEx's preloader, so BepInEx never loads at all; textures go missing
   and you get exactly the "gray world" symptom.
2. **`HideManagerGameObject = true`** under `[Chainloader]` in
   `BepInEx/config/BepInEx.cfg`. Stock BepInEx defaults this to `false` and
   doesn't even ship the file (it's generated on the game's first run). This is
   the decisive fix for the "**mod loads but no UI**" symptom: with the default
   `false`, Dorfromantik disables BepInEx's manager GameObject right after
   chainloader startup, so plugins' `Update`/`OnGUI` never run; the mod's `Awake`
   logs "ready", yet its menu never appears, looking exactly like BepInEx didn't
   load. Setting it `true` keeps the manager alive and the mod menu shows up
   (confirmed live on Linux/Proton).

### Compile

```bash
dotnet build     # auto-deploys to <GameDir>/BepInEx/plugins/DRMultiplayer/
```

**On Windows**, run `install-windows.bat` once to set up the modding base, then
build with the .NET SDK from a clone of this repo:
`dotnet build -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Dorfromantik"`
(adjust the path to your Steam library; the build auto-deploys into
`BepInEx\plugins\DRMultiplayer\`).

**On macOS** (**confirmed working**; see the macOS note above), run
`./build-mac.sh`; it's a one-stop installer + builder. It installs BepInEx and
the full macOS `net_4_x` unstripped corlib (macOS-flavor core, see above), finds the game (the `Managed/`
folder lives inside `Dorfromantik.app` on Mac), clears the Gatekeeper quarantine
flag (`xattr -dr com.apple.quarantine`), and passes the paths to the build so you
don't have to edit the `.csproj`. Override the game location with
`GAME_DIR=/path ./build-mac.sh`. Use `--no-build` to only set up the modding
base, or `--clean` to reinstall it from scratch.

**On Linux (Steam/Proton)**, run `./build-linux.sh` instead, the equivalent
one-stop installer + builder. It detects the game, installs the Windows BepInEx
loader plus the full `net_4_x` unstripped corlib (standalone by default; see
above), patches `doorstop_config.ini`, and passes the paths to the build. Same
`--clean` / `--no-build` flags and `GAME_DIR=/path` override, plus the
`--standalone` / `--gts` / `--official` mode flags.

> **macOS note:** freshly downloaded BepInEx files and the game folder must have
> the quarantine attribute cleared or the loader is blocked:
> `xattr -dr com.apple.quarantine "<GameDir>"`. The build script does this for
> you.

Game path is set in `DRMultiplayer.csproj` (`<GameDir>`). Game references are
taken straight from the game's `Managed/` folder, so compile-time success
implies the members exist in the shipped (stripped) assemblies. **`Newtonsoft.Json`
is the exception**: it now comes from NuGet at compile time, because some game
installs ship it stripped (missing `JsonConvert`). The install scripts place a
full copy of `Newtonsoft.Json.dll` into the unstripped-libs override, so the
complete assembly is bound at runtime for both the game and the mod.

## How it works

### Authority & packet flow

TCP, length-prefixed JSON frames (`Protocol.cs`, `Net.cs`). The host owns a
stable **player id** for everyone (host = 0, guests 1..3) and an authoritative
**roster** (id, name, color, stack height) that it broadcasts to all players
whenever it changes. The **host orders all placements**:

- The host's own placements apply instantly and are broadcast to every guest
  (`TilePlacedPacket`, tagged with `origin` = 0).
- A guest's placement input is intercepted (Harmony prefix on
  `TilePlacer.PlaceCurrentTile`) and sent as a `PlaceRequestPacket`. The host
  checks the slot is free and `TileFitter.TileFitsInSlot` passes, applies the
  tile, replies to the requester (`PlaceResultPacket`) **and** broadcasts the
  tile to all *other* guests (`TilePlacedPacket`, `origin` = the placer's id).
  Only on the accept does the requesting guest replay its placement through the
  normal game path, advancing *its* stack and earning *its* rewards; it ignores
  the broadcast it originated (matched by `origin`), so placements never
  double-apply. One round-trip on LAN ≈ imperceptible.

Since every placement is host-ordered and TCP is in-order, all worlds apply the
same placements in the same sequence. A `SyncStamp` (non-initial tile count +
score) rides on every placement message; any mismatch makes a guest request a
**full world resync** (host snapshot → destroy + rebuild) to which the host
replies to just that guest, so desyncs self-heal instead of compounding.

### Tile transfer format

Tiles travel as a mirror of the game's own save format (`TileData_003`:
segments, quest state, special-tile id) and are rebuilt with
`TileGenerator.CreateTileFromSaveData` + `TilePlacer.PlaceTileDirectly`, the
exact code path the game's save-load uses, including quest-tile
reconstruction (`questId`, `targetValue`, `questActive`).

### Keeping the simulations in lockstep

Remote placements use `PlaceTileDirectly` (fires all placement broadcasts with
`isPlacedByPlayer=false` → no local stack advance, no undo turn) plus a manual
`RewardSystem.AddTilePlacementScore` so score, perfect-placement counting and
quest progress match the placing machine. Key patches:

| Patch | Why |
|---|---|
| `RewardSystem.GainTiles` prefix | swallows tile rewards while applying a remote placement; rewards belong to whoever placed |
| `RewardSystem.GameOver` prefix | defers game over until the host declares both stacks empty |
| `Dorfromantik.UndoTracker.Undo` prefix | undo would fork the shared world |
| `SaveLoadSystem.SaveActiveGame` prefix/postfix | guest prefix: don't overwrite its solo autosave; host postfix: write the mod's companion file alongside the autosave |
| `RewardSystem.UpdateHighscore` prefix | coop score stays off solo leaderboards |
| `TileVisual.Rotate` prefix | forces instant mesh rotation during programmatic placement (the auto-fit tween otherwise leaves the mesh visually desynced) |

Two subtle desync sources are handled explicitly:

- **Hovering previews live in the world** (they merge element groups), so a
  floating preview could complete a quest during a remote placement that never
  completed on the other machine. The preview is lifted out of the world
  before any remote placement is applied and restored afterwards.
- **Snapshot tiles arrive in arbitrary order**, so intermediate board states
  never existed on the host. Quest fulfillment is gated during bulk
  application via `LoadingProgressRouter.IsLoading`, the same mechanism the
  game's own save-load uses.

### Files

- `Plugin.cs`: BepInEx entry, config, F6 IMGUI overlay (host/join/scan/send
  tiles/status), main-thread event pump. The game ships a *stripped*
  IMGUIModule; the overlay only uses surviving members.
- `Session.cs`: the multiplayer state machine (player ids + roster, handshake,
  placement relay, per-player preview relay, desync recovery, gifting, mutual
  game over when every stack is empty).
- `Coop.cs`: all game interaction (tile ⇄ wire conversion, snapshot
  build/apply, remote placement, stack manipulation, stat sync).
- `Net.cs`: TCP framing on background threads, UDP LAN discovery.
- `Protocol.cs`: packet DTOs.
- `SaveFile.cs`: the companion save (JSON under `BepInEx/config/DRMultiplayer/`)
  that remembers per-player stacks so a saved multiplayer game can be re-hosted.
- `Patches.cs`: Harmony patches (game hooks + overlay input blocking).
- `install-windows.ps1` / `install-windows.bat`: the Windows end-user installer
  (BepInEx base + pre-built plugin, no SDK required).
- `package-windows.sh`: builds and bundles `dist/DRMultiplayer-windows.zip` for
  distribution to Windows players.
