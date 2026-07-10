#!/usr/bin/env bash
#
# package-windows.sh — build the mod and bundle a ready-to-run Windows zip.
#
# Produces dist/DRMultiplayer-windows.zip containing:
#   DRMultiplayer.dll     the pre-built plugin (Release)
#   install-windows.bat   double-clickable installer entry point
#   install-windows.ps1   the actual installer (BepInEx + corlib + the DLL)
#   INSTALL.txt           three-line instructions for the player
#
# A Windows user just extracts the zip anywhere and double-clicks
# install-windows.bat — no .NET SDK, no manual steps, no launch options.
#
# Upload the zip (or at least DRMultiplayer.dll) to a GitHub release:
# install-windows.ps1 also knows how to fetch a release asset named
# DRMultiplayer.dll by itself, so the standalone .bat/.ps1 works too once a
# release carries the DLL.
#
# Usage:  ./package-windows.sh
#   GAME_DIR=/path ./package-windows.sh   override the game folder (needed for
#                                         the game assembly references at build)
#
set -euo pipefail

bold() { printf '\033[1m%s\033[0m\n' "$1"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
die()  { printf '  \033[31m✗ %s\033[0m\n' "$1" >&2; exit 1; }

cd "$(dirname "$0")"

command -v dotnet >/dev/null 2>&1 || die "'dotnet' not found. Install it with:  sudo pacman -S dotnet-sdk"
command -v zip    >/dev/null 2>&1 || die "'zip' not found. Install it with:  sudo pacman -S zip"

# Locate the game — compiling references the game's own Managed/ assemblies.
GAME_DIR="${GAME_DIR:-}"
if [[ -z "$GAME_DIR" ]]; then
  for candidate in \
    "$HOME/.local/share/Steam/steamapps/common/Dorfromantik" \
    "$HOME/.steam/steam/steamapps/common/Dorfromantik"; do
    [[ -d "$candidate" ]] && { GAME_DIR="$candidate"; break; }
  done
fi
[[ -n "$GAME_DIR" && -d "$GAME_DIR/Dorfromantik_Data/Managed" ]] \
  || die "Game folder (with Dorfromantik_Data/Managed) not found — set GAME_DIR=/path"

bold "Building DRMultiplayer (Release)…"
dotnet build -c Release \
  -p:GameDir="$GAME_DIR" \
  -p:Managed="$GAME_DIR/Dorfromantik_Data/Managed"

DLL="bin/Release/netstandard2.0/DRMultiplayer.dll"
[[ -f "$DLL" ]] || die "Build output not found: $DLL"

bold "Packaging…"
STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT
cp "$DLL" install-windows.ps1 install-windows.bat "$STAGING/"
cat > "$STAGING/INSTALL.txt" <<'EOF'
DRMultiplayer for Dorfromantik - Windows install

1. Extract this whole zip anywhere (e.g. your Downloads folder).
2. Double-click install-windows.bat. It finds your Steam install of
   Dorfromantik and sets up everything (BepInEx + the mod).
3. Start the game, begin or continue a classic game, press F6 (or M).

If Windows SmartScreen warns about the script, click "More info" ->
"Run anyway". When you host a game, allow the firewall prompt or
nobody can join you.
EOF

mkdir -p dist
rm -f dist/DRMultiplayer-windows.zip
zip -j -q dist/DRMultiplayer-windows.zip "$STAGING"/*
ok "Wrote dist/DRMultiplayer-windows.zip"
ok "Upload it to a GitHub release; also attach DRMultiplayer.dll on its own so"
ok "the standalone install-windows.ps1 can fetch it."
