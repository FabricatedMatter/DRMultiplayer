#!/usr/bin/env bash
#
# build-mac.sh — one-stop BepInEx installer + build/deploy for DRMultiplayer on macOS.
#
# CONFIRMED WORKING on native macOS (Apple Silicon, arm64) — the universal game
# app is run through its x86_64 slice under Rosetta (ARCHPREFERENCE=x86_64) so
# BepInEx's doorstop can bootstrap Mono. BepInEx loads, the mod menu appears, and
# there are no BCrypt errors.
#
# Dorfromantik ships STRIPPED Unity assemblies, so plain BepInEx crashes the mod
# at runtime. This script installs the OFFICIAL macOS BepInEx loader (5.4.23.5
# universal) AND the FULL macOS corlib (net_4_x breadth — 146 DLLs + 134 Facades,
# fetched from this repo's GitHub release asset, sha256-pinned) plus a full
# Newtonsoft.Json, configures run_bepinex.sh, seeds BepInEx.cfg with
# HideManagerGameObject = true, clears the Gatekeeper quarantine, then builds and
# deploys. The game path is passed to the build as MSBuild properties, so you
# never have to edit DRMultiplayer.csproj.
#
# IMPORTANT: macOS needs the FULL corlib breadth, but with the CORE assemblies in
# the macOS "unityjit" flavor. This is a hybrid: the full net_4_x set with the
# macOS unityjit core DLLs (mscorlib, System, System.Core, System.Security,
# System.Xml, System.Data, etc. + netstandard.dll) overlaid on top. The reduced
# 15-DLL macOS "unityjit" set is NOT sufficient — with it BepInEx loads and the
# mod menu appears, but the game's TEXTURES/ASSETS go missing (the corlib lacks
# the breadth the game needs). A Windows net_4_x corlib is fatal on macOS: it
# P/Invokes BCrypt.dll (a Windows-only crypto DLL → DllNotFoundException floods)
# and its metadata mismatch makes MonoMod/Harmony throw a NullReferenceException
# in BepInEx.Preloader (ConsoleSetOutFix), so BepInEx never loads. The hybrid
# gives the full breadth with the macOS core, and is confirmed working live on
# Apple Silicon (textures restored, mod menu works, no BCrypt errors).
#
# The installer also sets BepInEx.cfg's HideManagerGameObject = true under
# [Chainloader] (stock BepInEx defaults it to false and doesn't even ship the
# file — it's generated on first run). With the default false, Dorfromantik
# disables BepInEx's manager GameObject right after chainloader startup, so
# plugins' Update/OnGUI never run and the mod UI never appears; setting it true
# hides the manager from the game and lets the mod UI survive.
#
# Usage:  ./build-mac.sh [--clean] [--no-build] [-h|--help]
#   --clean     remove any previous BepInEx install first (also removes any
#               plugins/configs you installed under BepInEx/)
#   --no-build  install/configure the modding base only, don't build
#   GAME_DIR=/path ./build-mac.sh   override the game folder location
#
set -euo pipefail

# --- pinned downloads -----------------------------------------------------
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_macos_universal_5.4.23.5.zip"
BEPINEX_VERSION="5.4.23.5"
# FULL macOS corlib (net_4_x breadth — 146 DLLs + 134 Facades) for Unity
# 2021.3.45, hosted as this repo's GitHub release asset and sha256-pinned. This is
# a hybrid: the full net_4_x set with the macOS "unityjit" core DLLs (mscorlib,
# System, System.Core, System.Security, System.Xml, System.Data, etc. +
# netstandard.dll) overlaid on top. Do NOT swap in the reduced 15-DLL macOS
# unityjit set: BepInEx loads and the mod menu appears, but the game's
# TEXTURES/ASSETS go missing — macOS needs the full corlib breadth. And do NOT use
# a Windows net_4_x corlib: it P/Invokes BCrypt.dll (a Windows-only crypto DLL →
# DllNotFoundException floods) and its metadata mismatch makes MonoMod/Harmony
# throw a NullReferenceException in BepInEx.Preloader (ConsoleSetOutFix), so
# BepInEx never loads. The hybrid was tested live on Apple Silicon and works fully
# (textures restored, mod menu works, no BCrypt errors). The zip root holds the
# DLLs plus a Facades/ subfolder, so it unzips straight into unstripped_libs.
CORLIB_URL="https://github.com/FabricatedMatter/DRMultiplayer/releases/download/bepinex-corlib-2021.3.45/net_4_x_corlib_macos-2021.3.45.zip"
CORLIB_SHA256="2626683c15ed2d9196f7c223716c1816ffa14625735184e8e4f6a9c2f34d0d49"
# NOTE: we deliberately do NOT fetch unity.bepinex.dev/libraries here — overriding
# the game's UnityEngine modules breaks asset loading (gray world / missing assets).
# The game must use its own shipped UnityEngine assemblies; only the BCL (corlibs)
# needs unstripping.
# Full Newtonsoft.Json — the game's shipped copy may be stripped (missing
# JsonConvert). A .nupkg is a plain zip; the DLL is at lib/netstandard2.0/.
NEWTONSOFT_URL="https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"

# --- pretty output --------------------------------------------------------
bold() { printf '\033[1m%s\033[0m\n' "$1"; }
info() { printf '  \033[36m•\033[0m %s\n' "$1"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$1"; }
die()  { printf '  \033[31m✗ %s\033[0m\n' "$1" >&2; exit 1; }

cd "$(dirname "$0")"

# --- 0. args --------------------------------------------------------------
DO_CLEAN=0
DO_BUILD=1
for arg in "$@"; do
  case "$arg" in
    --clean)    DO_CLEAN=1 ;;
    --no-build) DO_BUILD=0 ;;
    -h|--help)
      cat <<'EOF'
build-mac.sh — install BepInEx + build/deploy DRMultiplayer on macOS.

CONFIRMED WORKING on native macOS (Apple Silicon, arm64) — the universal game app
runs through its x86_64 slice under Rosetta (ARCHPREFERENCE=x86_64) so BepInEx can
bootstrap. Installs the official macOS BepInEx (5.4.23.5 universal) plus the FULL
macOS corlib (net_4_x breadth — 146 DLLs + Facades, with macOS-flavor core; hosted
as this repo's GitHub release asset, sha256-pinned) and a full Newtonsoft.Json, and
sets HideManagerGameObject = true so the mod UI appears.

Note: the reduced 15-DLL macOS unityjit corlib is NOT used — with it the mod loads
but the game's textures/assets go missing; macOS needs the full corlib breadth,
just with macOS-platform core assemblies. A Windows net_4_x corlib is fatal on the
macOS Mono runtime (BCrypt.dll P/Invokes + MonoMod preloader crash).

Usage:
  ./build-mac.sh [--clean] [--no-build] [-h|--help]

Options:
  --clean     Remove any previous BepInEx install before installing (this also
              deletes any plugins/configs you added under BepInEx/).
  --no-build  Install and configure the modding base only; skip the build.
  -h, --help  Show this help.

Environment:
  GAME_DIR    Override the Dorfromantik folder location.
              Default: ~/Library/Application Support/Steam/steamapps/common/Dorfromantik
EOF
      exit 0 ;;
    *) die "Unknown option: $arg (try --help)" ;;
  esac
done

bold "DRMultiplayer — macOS install & build"
echo

# --- 1. prerequisites -----------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
  die "'dotnet' not found. Install it with:  brew install --cask dotnet-sdk"
fi
ok ".NET SDK $(dotnet --version)"
command -v curl  >/dev/null 2>&1 || die "'curl' not found (should ship with macOS)."
command -v unzip >/dev/null 2>&1 || die "'unzip' not found (should ship with macOS)."
ok "curl and unzip present"

# --- 2. locate the game ---------------------------------------------------
# Allow override:  GAME_DIR=/path/to/Dorfromantik ./build-mac.sh
DEFAULT_GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Dorfromantik"
GAME_DIR="${GAME_DIR:-$DEFAULT_GAME_DIR}"

if [[ ! -d "$GAME_DIR" ]]; then
  warn "Game folder not found at:"
  warn "  $GAME_DIR"
  read -r -p "  Enter the path to your Dorfromantik folder: " GAME_DIR
  GAME_DIR="${GAME_DIR/#\~/$HOME}"
fi
[[ -d "$GAME_DIR" ]] || die "Game folder does not exist: $GAME_DIR"
ok "Game folder: $GAME_DIR"

# On macOS the Unity Managed/ folder lives INSIDE the .app bundle.
APP_DIR="$GAME_DIR/Dorfromantik.app"
DATA_DIR="$APP_DIR/Contents/Resources/Data"
MANAGED="$DATA_DIR/Managed"
if [[ ! -d "$MANAGED" ]]; then
  die "Managed folder not found at:
    $MANAGED
  (Expected inside the .app bundle on macOS. Is this really the game folder?)"
fi
ok "Managed assemblies found"

RUN_SCRIPT="$GAME_DIR/run_bepinex.sh"
UNSTRIPPED_DIR="$GAME_DIR/BepInEx/unstripped_libs"

# --- 3. previous-install handling ----------------------------------------
has_previous=0
for p in \
  "$RUN_SCRIPT" \
  "$GAME_DIR/BepInEx" \
  "$GAME_DIR/libdoorstop.dylib" \
  "$GAME_DIR/doorstop_config.ini" \
  "$GAME_DIR/unstripped_corlib" \
  "$UNSTRIPPED_DIR"; do
  if [[ -e "$p" ]]; then has_previous=1; break; fi
done

clean_install() {
  warn "Removing previous BepInEx install (this also removes any plugins and"
  warn "configs you added under BepInEx/)…"
  rm -rf "$GAME_DIR/BepInEx"
  rm -f  "$GAME_DIR/libdoorstop.dylib"
  rm -f  "$RUN_SCRIPT"
  rm -f  "$GAME_DIR/changelog.txt"
  rm -f  "$GAME_DIR/.doorstop_version"
  rm -f  "$GAME_DIR/doorstop_config.ini"
  rm -rf "$GAME_DIR/unstripped_corlib"
  rm -f  "$GAME_DIR/winhttp.dll"
  ok "Previous install removed"
}

if [[ "$has_previous" -eq 1 ]]; then
  if [[ "$DO_CLEAN" -eq 1 ]]; then
    clean_install
  else
    warn "A previous BepInEx install was detected in the game folder."
    read -r -p "  Remove previous BepInEx install first? [y/N] " reply
    if [[ "$reply" =~ ^[Yy]$ ]]; then
      DO_CLEAN=1
      clean_install
    fi
  fi
fi

# --- 4. detect Unity version ---------------------------------------------
detect_unity_version() {
  local f raw
  for f in "$DATA_DIR/globalgamemanagers" "$DATA_DIR/level0" "$DATA_DIR/data.unity3d"; do
    [[ -f "$f" ]] || continue
    raw="$(strings "$f" 2>/dev/null \
      | grep -Eo '[0-9]{4}\.[0-9]+\.[0-9]+[abfp][0-9]+' \
      | head -n 1)"
    if [[ -n "$raw" ]]; then
      printf '%s' "$raw"
      return 0
    fi
  done
  return 1
}

UNITY_FULL="$(detect_unity_version || true)"
[[ -n "$UNITY_FULL" ]] || die "Could not detect the game's Unity version (checked
    globalgamemanagers, level0 and data.unity3d in $DATA_DIR).
  The game files may be in an unexpected layout."
# Strip the a/b/f/p suffix: 2021.3.45f2 -> 2021.3.45
UNITY_VER="$(printf '%s' "$UNITY_FULL" | sed -E 's/[abfp][0-9]+$//')"
# Informational only — the unstripped corlib is pinned to Unity 2021.3.45 (see
# CORLIB_URL); we no longer resolve a download by the detected version. We still
# report it so a mismatched game build is easy to spot.
ok "Unity version: $UNITY_FULL (corlib is pinned to Unity 2021.3.45)"
[[ "$UNITY_VER" == "2021.3.45" ]] \
  || warn "Game reports Unity $UNITY_VER but the pinned corlib targets 2021.3.45."

# --- 5. already installed? -----------------------------------------------
is_fully_installed() {
  [[ -f "$RUN_SCRIPT" ]] || return 1
  [[ -f "$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll" ]] || return 1
  grep -q 'executable_name="Dorfromantik.app"' "$RUN_SCRIPT" 2>/dev/null || return 1
  [[ -d "$UNSTRIPPED_DIR" ]] || return 1
  # non-empty unstripped_libs
  ls -A "$UNSTRIPPED_DIR" >/dev/null 2>&1 && [[ -n "$(ls -A "$UNSTRIPPED_DIR")" ]] || return 1
  # full Newtonsoft.Json must be present in the override (older scripts lacked it)
  [[ -f "$UNSTRIPPED_DIR/Newtonsoft.Json.dll" ]] || return 1
  # HideManagerGameObject = true must be set (older scripts didn't seed it; without
  # it the mod loads but its UI never appears on Dorfromantik).
  grep -Eq '^HideManagerGameObject = true$' "$GAME_DIR/BepInEx/config/BepInEx.cfg" 2>/dev/null || return 1
  # ARCHPREFERENCE must force x86_64 — installs from older script versions lack this and
  # need re-patching (returning 1 here triggers the re-download/re-patch branch below).
  grep -q 'export ARCHPREFERENCE="x86_64"' "$RUN_SCRIPT" 2>/dev/null || return 1
  return 0
}

# --- 5a. heal already-broken installs ------------------------------------
# Older script versions dumped the unstripped UnityEngine*.dll modules into the
# override, which breaks asset loading (gray world / missing assets). Remove
# exactly those files on any plain re-run so the game uses its own shipped
# UnityEngine assemblies — this runs even when everything else is installed, so a
# re-run heals a broken install without needing --clean.
if [[ -d "$UNSTRIPPED_DIR" ]]; then
  ue_found=0
  for f in "$UNSTRIPPED_DIR"/UnityEngine*.dll; do
    [[ -e "$f" ]] || continue   # non-matching glob stays literal — skip it
    if [[ "$ue_found" -eq 0 ]]; then
      warn "removing UnityEngine module overrides (they cause missing assets)"
      ue_found=1
    fi
    rm -f "$f"
  done
  [[ "$ue_found" -eq 1 ]] && ok "removed UnityEngine module overrides" || true
fi

if [[ "$DO_CLEAN" -eq 0 ]] && is_fully_installed; then
  ok "BepInEx already installed and configured — skipping downloads."
else
  # --- 6. download workdir ------------------------------------------------
  WORKDIR="$(mktemp -d)"
  cleanup() { rm -rf "$WORKDIR"; }
  trap cleanup EXIT

  fetch() {
    # fetch <url> <dest-file>
    local url="$1" dest="$2"
    info "Downloading $url"
    curl -fL --retry 3 -o "$dest" "$url" \
      || die "Download failed: $url
  Check your connection. If this is the unstripped corlib archive, verify the
  release asset still exists at the URL in a browser."
  }

  echo
  bold "Installing BepInEx $BEPINEX_VERSION (macOS universal)…"

  # 6a. BepInEx loader — unzipped directly into the game folder.
  BEPINEX_ZIP="$WORKDIR/bepinex.zip"
  fetch "$BEPINEX_URL" "$BEPINEX_ZIP"
  unzip -oq "$BEPINEX_ZIP" -d "$GAME_DIR" || die "Failed to unzip BepInEx into $GAME_DIR"
  [[ -f "$RUN_SCRIPT" ]] || die "run_bepinex.sh missing after unzip — unexpected archive layout."
  ok "BepInEx loader installed"

  # 6b. FULL macOS corlib into BepInEx/unstripped_libs/. Fetched from this repo's
  # GitHub release asset and verified against a pinned sha256 — the zip root holds
  # the DLLs plus a Facades/ subfolder, so it extracts in place. This is the full
  # net_4_x breadth (146 DLLs + Facades) with the macOS-flavor core assemblies.
  # The reduced 15-DLL macOS unityjit set must NOT be used here (mod loads but the
  # game's textures/assets go missing), and neither must a Windows net_4_x corlib
  # (BCrypt.dll P/Invokes + MonoMod preloader crash break BepInEx on macOS).
  mkdir -p "$UNSTRIPPED_DIR"
  CORLIB_ZIP="$WORKDIR/corlib.zip"
  fetch "$CORLIB_URL" "$CORLIB_ZIP"
  ACTUAL_SHA="$(shasum -a 256 "$CORLIB_ZIP" | awk '{print $1}')"
  [[ "$ACTUAL_SHA" == "$CORLIB_SHA256" ]] || die "corlib checksum mismatch — refusing to install.
    expected: $CORLIB_SHA256
    actual:   $ACTUAL_SHA
  The download may be corrupt or tampered with; try again or re-verify CORLIB_URL."
  ok "Verified corlib sha256"
  unzip -oq "$CORLIB_ZIP" -d "$UNSTRIPPED_DIR" || die "Failed to unzip corlib into $UNSTRIPPED_DIR"
  [[ -n "$(ls -A "$UNSTRIPPED_DIR")" ]] || die "unstripped_libs is empty after extraction."
  ok "Full macOS corlib installed (net_4_x breadth, 146 DLLs + Facades, macOS core)"

  # 6c. full Newtonsoft.Json into the override — the game's shipped copy may be
  # stripped (no JsonConvert). Placed here so Mono's dll_search_path_override
  # binds it (by simple name) ahead of Managed/ for both game and mod.
  NUPKG="$WORKDIR/newtonsoft.nupkg"
  fetch "$NEWTONSOFT_URL" "$NUPKG"
  unzip -oj "$NUPKG" "lib/netstandard2.0/Newtonsoft.Json.dll" -d "$UNSTRIPPED_DIR" \
    || die "Failed to extract Newtonsoft.Json.dll from the nupkg."
  [[ -f "$UNSTRIPPED_DIR/Newtonsoft.Json.dll" ]] || die "Newtonsoft.Json.dll missing after extraction."
  ok "Full Newtonsoft.Json installed into unstripped_libs"

  # --- 7. patch run_bepinex.sh -------------------------------------------
  # Anchors are the stock empty assignments. Path may contain spaces — it stays
  # inside double quotes in the script. Use '#' as the sed delimiter since the
  # replacement contains '/'.
  # BepInEx 5 / doorstop cannot bootstrap into native arm64 Mono on Apple Silicon — the
  # game must run its x86_64 slice under Rosetta or Mono never starts (doorstop injects but
  # no BepInEx log appears). Force x86_64; harmless no-op on Intel Macs.
  sed -i '' \
    -e 's#^executable_name=""#executable_name="Dorfromantik.app"#' \
    -e "s#^dll_search_path_override=\"\"#dll_search_path_override=\"$UNSTRIPPED_DIR\"#" \
    -e 's#export ARCHPREFERENCE="arm64,x86_64"#export ARCHPREFERENCE="x86_64"#' \
    "$RUN_SCRIPT"

  grep -q '^executable_name="Dorfromantik.app"' "$RUN_SCRIPT" \
    || die "Failed to patch executable_name in run_bepinex.sh (unexpected format)."
  grep -q "^dll_search_path_override=\"$UNSTRIPPED_DIR\"" "$RUN_SCRIPT" \
    || die "Failed to patch dll_search_path_override in run_bepinex.sh (unexpected format)."
  grep -q 'export ARCHPREFERENCE="x86_64"' "$RUN_SCRIPT" \
    || die "Failed to patch ARCHPREFERENCE in run_bepinex.sh (unexpected format)."
  chmod +x "$RUN_SCRIPT"
  ok "Configured and made run_bepinex.sh executable"
fi

# --- 8. ensure plugins dir + clear quarantine ----------------------------
mkdir -p "$GAME_DIR/BepInEx/plugins"

# --- 8a. force HideManagerGameObject = true ------------------------------
# CRITICAL fix — runs unconditionally on EVERY run, so a plain re-run also heals
# an existing install. Stock BepInEx defaults [Chainloader]/HideManagerGameObject
# to false AND doesn't even ship BepInEx.cfg (it's generated on the game's first
# run with the default), so we must pre-seed/patch the file so the very first
# launch already has true. With false, Dorfromantik disables BepInEx's manager
# GameObject right after chainloader startup, so plugins' Update/OnGUI never run —
# the mod loads but its UI never appears. true hides the manager from the game and
# lets the mod UI live. macOS BepInEx writes this file with LF line endings, so we
# seed with LF (printf \n) and patch existing lines with BSD sed.
mkdir -p "$GAME_DIR/BepInEx/config"
CFG="$GAME_DIR/BepInEx/config/BepInEx.cfg"
if [[ -f "$CFG" ]]; then
  if grep -Eq '^HideManagerGameObject =' "$CFG"; then
    # Line exists — force it to true (whether currently false or already true).
    # Idempotent when it's already true.
    sed -i '' -E 's#^(HideManagerGameObject =).*$#\1 true#' "$CFG"
  elif grep -Eq '^\[Chainloader\]$' "$CFG"; then
    # [Chainloader] header present but no setting — insert it under the header.
    sed -i '' -E '/^\[Chainloader\]$/a\
HideManagerGameObject = true' "$CFG"
  else
    # File exists but no [Chainloader] section — append a fresh block (LF).
    printf '\n[Chainloader]\nHideManagerGameObject = true\n' >> "$CFG"
  fi
else
  # No cfg yet (stock BepInEx generates it on first run). Seed the minimum; BepInEx
  # merges the remaining defaults on first run and preserves this value.
  printf '[Chainloader]\nHideManagerGameObject = true\n' > "$CFG"
fi
grep -Eq '^HideManagerGameObject = true$' "$CFG" \
  || die "Failed to set HideManagerGameObject = true in BepInEx.cfg (unexpected format): $CFG"
ok "BepInEx configured (HideManagerGameObject = true — required so the mod UI survives on Dorfromantik)"

if command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "$GAME_DIR" 2>/dev/null || true
  ok "Cleared macOS quarantine flag on game folder"
fi

# --- 9. the one manual step ----------------------------------------------
echo
bold "One manual step remains:"
info "In Steam → Dorfromantik → Properties → Launch Options, set:"
info "  \"$RUN_SCRIPT\" %command%"
echo

# --- 10. build -----------------------------------------------------------
if [[ "$DO_BUILD" -eq 0 ]]; then
  ok "Install-only run (--no-build) — modding base is ready."
  exit 0
fi

bold "Building…"
dotnet build \
  -p:GameDir="$GAME_DIR" \
  -p:Managed="$MANAGED"

echo
ok "Build complete."
ok "Deployed to: $GAME_DIR/BepInEx/plugins/DRMultiplayer/"
echo
info "Launch Dorfromantik from Steam, start a fresh classic game, press F6 (or M)."
