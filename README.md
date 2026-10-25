# DRMultiplayer

LAN/online multiplayer for Dorfromantik, for up to 4 players (a host and 3 guests), as a BepInEx 5 plugin.

- **Coop** (Classic): one shared world and score. Each player has their own tile stack, and everyone places at the same time.
- **Versus** (Classic): one shared world, but players take turns and score individually. The highest score wins.
- **Creative Coop**: one shared world with unlimited tiles. Placing and deleting tiles is synced.

Steam and GOG on Windows, Steam/Proton on Linux, and macOS are supported.

## Install

Every player needs the mod.

- **Windows:** download `DRMultiplayer-windows.zip` from [Releases](https://github.com/FabricatedMatter/DRMultiplayer/releases), extract it, and double-click `install-windows.bat`. It finds Steam and GOG installs automatically. If Windows SmartScreen warns you, click "More info", then "Run anyway".
- **Linux (Steam/Proton):** clone the repo and run `./build-linux.sh`. In Steam, force Proton and set the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- **macOS:** clone the repo and run `./build-mac.sh`. Then set the Steam launch option the script prints.

For script flags and building from source, see [BUILDING.md](BUILDING.md).

## Play

1. **Host:** start or continue a Classic or Creative game. Press **F6** (or **M**), pick Coop or Versus (Classic only), and click **Host Game**. You can start playing right away.
2. **Guests:** start any game in the **same mode** as the host. Press **F6**, then either click **Scan LAN** or enter the host's IP and **Join**. Your board is replaced by the host's world.
3. Play. Everyone sees the other players' tile previews in their colors.

- **Coop:** quest and perfect-placement rewards go to the player who placed the tile. You can send tiles to another player from the window. The game ends when every player's stack is empty.
- **Versus:** the overlay shows whose turn it is. Players without tiles are skipped, and gifting is off.
- **Creative:** when a Classic game ends, everyone can choose "continue in Creative" and stay connected. A guest who does this gets a fresh Creative game that is filled with the shared world, so the guest's own saves are never touched.
- **Saving:** the host's normal autosave keeps the shared world. When the host continues the save and hosts again, returning players (matched by name) get their tiles back.
- Dropped guests can rejoin. Undo is disabled, and multiplayer scores stay off the leaderboards.

## Playing over the internet

Scan LAN only works on a local network, so guests have to type the host's IP.

- **Easiest:** everyone joins the same [Tailscale](https://tailscale.com) or [ZeroTier](https://www.zerotier.com) network, and guests use the host's VPN IP.
- **Or:** the host forwards **TCP 24680** on their router to their PC, and guests use the host's public IP. This does not work behind CG-NAT.

When you first host, allow the game through the firewall.

## Notes

- All players need the **same mod version**. Mismatched versions refuse to connect.
- If you installed the old *Creative Coop Bridge* add-on, uninstall it. Creative is now built in.
- Settings live in `BepInEx/config/fabricatedmatter.drmultiplayer.cfg`: the toggle keys, ports (24680 TCP, 24681 UDP discovery) and `PlayerName`. If `PlayerName` is blank, the mod uses your Steam name or your OS user name.
- If F6/M does nothing, set `DebugInputLog=true` in the config and check the BepInEx log.
