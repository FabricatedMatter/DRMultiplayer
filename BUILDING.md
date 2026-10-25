# Building & installing from source

Dorfromantik ships **stripped** assemblies, so the mod needs BepInEx 5.4.23.5 plus the **full `net_4_x` unstripped corlib**, in `unstripped_libs`, wired up through `doorstop_config.ini`. The game's own UnityEngine DLLs must not be overridden, or assets turn gray. The install scripts also set `HideManagerGameObject = true` in `BepInEx/config/BepInEx.cfg`. Without it, the game disables BepInEx's manager object and the mod's window never appears.

The corlib is hosted on this repo's `bepinex-corlib-2021.3.45` release and is sha256-pinned. Windows and Linux/Proton use `net_4_x_corlib_unity-2021.3.45.zip`. macOS needs its own build, `net_4_x_corlib_macos-2021.3.45.zip`, because the Windows one breaks the Mac Mono runtime.

## Scripts

| Script | What it does | Flags |
|---|---|---|
| `install-windows.bat` / `.ps1` | Installs the modding base and the plugin. The plugin comes from a DLL next to the script, or from Releases. | `-Clean`, `-NoMod`, `-GameDir <path>` |
| `build-linux.sh` | Installs the modding base, then builds and deploys the plugin. | `--clean`, `--no-build`, `--gts[=zip]` (fallback using Game Theory Studio's base), `--official` (deprecated), `GAME_DIR=` |
| `build-mac.sh` | Same as Linux, plus Rosetta/x86_64, quarantine clearing and `run_bepinex.sh`. | `--clean`, `--no-build`, `GAME_DIR=` |
| `package-windows.sh` | Builds a Release and writes `dist/DRMultiplayer-windows.zip`. Upload the zip and the bare `DRMultiplayer.dll` to a release. | |

## Compile

```bash
dotnet build                          # deploys to <GameDir>/BepInEx/plugins/DRMultiplayer/
dotnet build -p:GameDir="/path/to/Dorfromantik"
```

The build references the game's `Managed/` assemblies directly, so the game must be installed. `Newtonsoft.Json` is the exception: it comes from NuGet, and the install scripts put the full DLL in place at runtime.

## Code layout

- `Plugin.cs`: entry point, config, and the F6 IMGUI overlay. The game's IMGUI module is stripped, so the overlay only uses the members that survive.
- `Session.cs`: the multiplayer state machine. The host orders every placement and deletion. Each placement carries a sync stamp, and a guest whose stamp doesn't match resyncs automatically.
- `Coop.cs`: everything that touches the game. Tiles are converted to and from the game's `TileData_003` save format, and the file also handles snapshots, remote placements and stacks.
- `Patches.cs`: the Harmony hooks.
- `Net.cs` / `Protocol.cs`: length-prefixed JSON over TCP, UDP LAN discovery, and the packet types. Change `ProtocolVersion.Current` whenever any packet's shape changes.
- `SaveFile.cs`: the host's companion save (per-player stacks) under `BepInEx/config/DRMultiplayer/`.
