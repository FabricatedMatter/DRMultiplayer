#!/usr/bin/env bash
#
# build-linux.sh — one-stop BepInEx installer + build/deploy for DRMultiplayer on Linux (Arch).
#
# On Linux, Dorfromantik runs as the WINDOWS build under Steam Proton (the game
# folder contains Dorfromantik.exe, UnityPlayer.dll and Dorfromantik_Data/), so
# this installs the WINDOWS BepInEx loader (winhttp.dll injection) — exactly how
# the Game Theory Studio base works. Dorfromantik ships STRIPPED Unity
# assemblies, so plain BepInEx crashes the mod at runtime; we also install an
# unstripped .NET base class library (the full net_4_x Mono corlib), configure
# doorstop, then build and deploy. The game path is passed to the build as
# MSBuild properties, so you never have to edit DRMultiplayer.csproj.
#
# The installer also sets BepInEx.cfg's HideManagerGameObject = true under
# [Chainloader] (stock BepInEx defaults it to false, and doesn't even ship the
# file — it's generated on first run). Together with the unstripped corlib this
# is what makes the mod actually work: with the default false, Dorfromantik
# disables BepInEx's manager GameObject right after chainloader startup, so
# plugins' Update/OnGUI never run and the mod UI never appears (the mod loads —
# its Awake prints "ready" — but looks like BepInEx didn't load at all). Setting
# it true hides the manager from the game and lets the mod UI survive.
#
# Three install modes exist (see --help). In short: the default STANDALONE mode
# assembles a fully self-contained base from the official BepInEx release plus
# the full net_4_x Mono corlib fetched from THIS project's own GitHub release —
# no third-party download required. Since the game's 1.1.5.3 update the reduced
# "unityjit" corlib (used by the deprecated --official mode) is missing stripped
# members mods need; the full net_4_x profile has the complete API surface and
# is byte-for-byte the same base Game Theory Studio ships in their itch.io zip.
#
# Usage:  ./build-linux.sh [--standalone|--gts[=zip]|--official] [--clean] [--no-build] [-h|--help]
#   --standalone (DEFAULT) official BepInEx + the full net_4_x corlib from this
#               project's GitHub release; fully self-contained, recommended
#   --gts[=zip] optional fallback: install Game Theory Studio's "bepinex-base.zip"
#               (requires the itch.io download in ~/Downloads or a passed path)
#   --official  DEPRECATED debug path: from-scratch BepInEx + unity.bepinex.dev
#               unityjit corlibs (known-insufficient / gray assets since v1.1.5.3)
#   --clean     remove any previous BepInEx install first (also removes any
#               plugins/configs you installed under BepInEx/)
#   --no-build  install/configure the modding base only, don't build
#   GAME_DIR=/path ./build-linux.sh   override the game folder location
#
set -euo pipefail

# --- pinned downloads -----------------------------------------------------
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
BEPINEX_VERSION="5.4.23.5"
# STANDALONE corlib: the FULL net_4_x desktop Mono profile (146 top-level DLLs +
# a Facades/ subfolder), redistributed from this project's own GitHub release and
# pinned to the game's Unity 2021.3.45f2. This is the complete-API-surface corlib
# that works since game v1.1.5.3 — no third-party (GTS) download needed. The zip
# root holds the DLLs and Facades/ directly, so it unzips straight into unstripped_libs.
CORLIB_URL="https://github.com/FabricatedMatter/DRMultiplayer/releases/download/bepinex-corlib-2021.3.45/net_4_x_corlib_unity-2021.3.45.zip"
CORLIB_SHA256="23993802ff6a0cffa9cccf7fd0a880f7f983a9524208ba95859b277878e720a3"
# DEPRECATED (--official mode only): the REDUCED unityjit corlib, matched to the
# detected Unity version. Missing members the game strips since v1.1.5.3 → gray assets.
CORLIBS_BASE="https://unity.bepinex.dev/corlibs"
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
MODE=""            # "" (default → standalone), "standalone", "gts", or "official"
GTS_ZIP=""         # path to bepinex-base.zip (defaults to ~/Downloads)
UNITY_VER=""       # detected in official mode only (kept set for set -u safety)
for arg in "$@"; do
  case "$arg" in
    --clean)      DO_CLEAN=1 ;;
    --no-build)   DO_BUILD=0 ;;
    --standalone) MODE=standalone ;;
    --official)   MODE=official ;;
    --gts)        MODE=gts ;;
    --gts=*)      MODE=gts; GTS_ZIP="${arg#--gts=}"; GTS_ZIP="${GTS_ZIP/#\~/$HOME}" ;;
    -h|--help)
      cat <<'EOF'
build-linux.sh — install BepInEx + build/deploy DRMultiplayer on Linux (Steam/Proton).

Usage:
  ./build-linux.sh [--standalone|--gts[=zip]|--official] [--clean] [--no-build] [-h|--help]

Install modes:
  --standalone (DEFAULT, recommended) Fully self-contained install: official
              BepInEx win_x64 5.4.23.5 + the FULL net_4_x Mono corlib fetched
              (and sha256-verified) from THIS project's own GitHub release, plus
              a full Newtonsoft.Json. Needs no third-party download. This full
              corlib has the complete API surface the game strips since v1.1.5.3,
              so it is the known-good base — equivalent to what GTS ships.
  --gts[=zip] Optional fallback. Install Game Theory Studio's "bepinex-base.zip"
              (BepInEx + unstripped_corlib, doorstop preconfigured), then add a
              full Newtonsoft.Json. Download the zip from
              https://game-theory-studio.itch.io/dorfromantik-mods into
              ~/Downloads (or pass its path). Always does a clean install. This
              mode is NOT auto-selected — you must pass --gts explicitly.
  --official  DEPRECATED debug fallback. From-scratch install: official BepInEx
              win_x64 + matched unity.bepinex.dev "unityjit" corlibs. That corlib
              is reduced and misses members the game strips since v1.1.5.3, which
              causes gray/missing assets — use --standalone instead.

Options:
  --clean     Remove any previous BepInEx install before installing (this also
              deletes any plugins/configs you added under BepInEx/).
  --no-build  Install and configure the modding base only; skip the build.
  -h, --help  Show this help.

Environment:
  GAME_DIR    Override the Dorfromantik folder location.
              Default: ~/.local/share/Steam/steamapps/common/Dorfromantik
EOF
      exit 0 ;;
    *) die "Unknown option: $arg (try --help)" ;;
  esac
done

bold "DRMultiplayer — Linux (Steam/Proton) install & build"
echo

# --- 0a. resolve install mode --------------------------------------------
# STANDALONE is the unconditional default: a self-contained base from official
# BepInEx + the full net_4_x corlib pulled from this project's GitHub release, so
# no third-party download is required. GTS mode (bepinex-base.zip) is an explicit
# optional fallback only — it is never auto-selected. We still hint when the GTS
# zip happens to be sitting in ~/Downloads, but standalone stays the default.
DOWNLOADS_ZIP="$HOME/Downloads/bepinex-base.zip"
if [[ -z "$MODE" ]]; then
  MODE=standalone
  if [[ -f "$DOWNLOADS_ZIP" ]]; then
    info "Found $DOWNLOADS_ZIP — pass --gts to use the Game Theory Studio base instead."
  fi
fi
[[ "$MODE" == gts && -z "$GTS_ZIP" ]] && GTS_ZIP="$DOWNLOADS_ZIP"

# --- 1. prerequisites -----------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
  die "'dotnet' not found. Install it with:  sudo pacman -S dotnet-sdk"
fi
ok ".NET SDK $(dotnet --version)"
command -v curl  >/dev/null 2>&1 || die "'curl' not found. Install it with:  sudo pacman -S curl"
command -v unzip >/dev/null 2>&1 || die "'unzip' not found. Install it with:  sudo pacman -S unzip"
ok "curl and unzip present"

# --- 2. locate the game ---------------------------------------------------
# Allow override:  GAME_DIR=/path/to/Dorfromantik ./build-linux.sh
GAME_DIR="${GAME_DIR:-}"
if [[ -z "$GAME_DIR" ]]; then
  for candidate in \
    "$HOME/.local/share/Steam/steamapps/common/Dorfromantik" \
    "$HOME/.steam/steam/steamapps/common/Dorfromantik"; do
    if [[ -d "$candidate" ]]; then
      GAME_DIR="$candidate"
      break
    fi
  done
fi

if [[ -z "$GAME_DIR" || ! -d "$GAME_DIR" ]]; then
  warn "Game folder not found in the usual Steam library locations."
  read -r -p "  Enter the path to your Dorfromantik folder: " GAME_DIR
  GAME_DIR="${GAME_DIR/#\~/$HOME}"
fi
[[ -d "$GAME_DIR" ]] || die "Game folder does not exist: $GAME_DIR"
ok "Game folder: $GAME_DIR"

# On Linux the game is the Windows Proton build: Dorfromantik.exe at the root
# and the Unity Managed/ folder under Dorfromantik_Data/.
DATA_DIR="$GAME_DIR/Dorfromantik_Data"
MANAGED="$DATA_DIR/Managed"
if [[ ! -f "$GAME_DIR/Dorfromantik.exe" ]]; then
  die "Dorfromantik.exe not found at:
    $GAME_DIR/Dorfromantik.exe
  On Linux the game must be the Windows build run under Proton. If there is no
  .exe this may be a native Linux build, which this script does not support."
fi
if [[ ! -d "$MANAGED" ]]; then
  die "Managed folder not found at:
    $MANAGED
  Is this really the game folder?"
fi
ok "Managed assemblies found"

UNSTRIPPED_DIR="$GAME_DIR/unstripped_libs"
DOORSTOP_CONFIG="$GAME_DIR/doorstop_config.ini"

# --- 3. previous-install handling ----------------------------------------
has_previous=0
for p in \
  "$GAME_DIR/BepInEx" \
  "$GAME_DIR/winhttp.dll" \
  "$DOORSTOP_CONFIG" \
  "$GAME_DIR/changelog.txt" \
  "$GAME_DIR/.doorstop_version" \
  "$GAME_DIR/unstripped_corlib" \
  "$UNSTRIPPED_DIR"; do
  if [[ -e "$p" ]]; then has_previous=1; break; fi
done

clean_install() {
  warn "Removing previous BepInEx install (this also removes any plugins and"
  warn "configs you added under BepInEx/)…"
  rm -rf "$GAME_DIR/BepInEx"
  rm -f  "$GAME_DIR/winhttp.dll"
  rm -f  "$DOORSTOP_CONFIG"
  rm -f  "$GAME_DIR/changelog.txt"
  rm -f  "$GAME_DIR/.doorstop_version"
  rm -rf "$GAME_DIR/unstripped_corlib"
  rm -rf "$UNSTRIPPED_DIR"
  rm -f  "$GAME_DIR/output_log.txt"
  ok "Previous install removed"
}

if [[ "$MODE" == gts ]]; then
  # GTS mode is always a clean install. Verify the zip exists *before* wiping.
  [[ -f "$GTS_ZIP" ]] || die "Game Theory Studio base zip not found:
    $GTS_ZIP
  Download bepinex-base.zip from
    https://game-theory-studio.itch.io/dorfromantik-mods
  into ~/Downloads (or pass --gts=/path/to/bepinex-base.zip)."
  DO_CLEAN=1
  clean_install   # prints the plugins/configs-removed warning; no prompt in GTS mode
elif [[ "$has_previous" -eq 1 ]]; then
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

# --- 4. shared download helper -------------------------------------------
WORKDIR="$(mktemp -d)"
cleanup() { rm -rf "$WORKDIR"; }
trap cleanup EXIT

fetch() {
  # fetch <url> <dest-file>
  local url="$1" dest="$2"
  info "Downloading $url"
  curl -fL --retry 3 -o "$dest" "$url" \
    || die "Download failed: $url
  Check your connection. If this is the unstripped corlibs/libraries archive,
  unity.bepinex.dev may not have version $UNITY_VER — verify the URL in a browser."
}

if [[ "$MODE" == gts ]]; then
  # --- 5G. install Game Theory Studio's bepinex-base.zip -------------------
  # The zip roots BepInEx/, winhttp.dll, doorstop_config.ini (already pointing
  # at unstripped_corlib) and the unstripped_corlib/ folder — a known-good base
  # for the game's stronger stripping since v1.1.5.3. Unity version detection is
  # not needed here. We already ran a clean install above.
  echo
  bold "Installing the Game Theory Studio base…"
  info "Source: $GTS_ZIP"
  unzip -oq "$GTS_ZIP" -d "$GAME_DIR" || die "Failed to unzip the GTS base into $GAME_DIR"

  [[ -f "$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll" ]] \
    || die "BepInEx/core/BepInEx.Preloader.dll missing after unzip — is this really bepinex-base.zip?"
  [[ -f "$GAME_DIR/winhttp.dll" ]] \
    || die "winhttp.dll missing after unzip — unexpected archive layout."
  [[ -f "$DOORSTOP_CONFIG" ]] \
    || die "doorstop_config.ini missing after unzip — unexpected archive layout."
  [[ -f "$GAME_DIR/unstripped_corlib/mscorlib.dll" ]] \
    || die "unstripped_corlib/mscorlib.dll missing after unzip — unexpected archive layout."
  # The zip ships doorstop preconfigured (dll_search_path_override = unstripped_corlib);
  # just verify. Don't anchor with $ — the file may carry CRLF line endings.
  grep -q 'dll_search_path_override = unstripped_corlib' "$DOORSTOP_CONFIG" \
    || die "doorstop_config.ini is not configured for unstripped_corlib (unexpected GTS base)."
  ok "GTS base installed (BepInEx + unstripped_corlib, doorstop preconfigured)"

  # Full Newtonsoft.Json — the GTS base ships none, and the game's Managed copy
  # is stripped, so add the full NuGet build into the corlib override.
  GTS_CORLIB="$GAME_DIR/unstripped_corlib"
  NUPKG="$WORKDIR/newtonsoft.nupkg"
  fetch "$NEWTONSOFT_URL" "$NUPKG"
  unzip -oj "$NUPKG" "lib/netstandard2.0/Newtonsoft.Json.dll" -d "$GTS_CORLIB" \
    || die "Failed to extract Newtonsoft.Json.dll from the nupkg."
  [[ -f "$GTS_CORLIB/Newtonsoft.Json.dll" ]] || die "Newtonsoft.Json.dll missing after extraction."
  ok "Full Newtonsoft.Json installed into unstripped_corlib"
else
# --- shared install flow: standalone (default) and official (deprecated) --
# Both modes install the official BepInEx win_x64 release and configure doorstop
# to resolve the game-root RELATIVE unstripped_libs/ dir (an absolute Linux path
# would not resolve under Wine). They differ ONLY in the corlib source (below):
#   standalone → the full net_4_x corlib from this project's GitHub release,
#   official   → the reduced unity.bepinex.dev unityjit corlib for the detected
#                Unity version (deprecated / insufficient since game v1.1.5.3).

# --- 4o. detect Unity version (official mode only) -----------------------
# Only --official needs this, to pick the matching unity.bepinex.dev corlib zip.
# Standalone's corlib asset is pinned to Unity 2021.3.45, so no detection needed.
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

if [[ "$MODE" == official ]]; then
  UNITY_FULL="$(detect_unity_version || true)"
  [[ -n "$UNITY_FULL" ]] || die "Could not detect the game's Unity version (checked
    globalgamemanagers, level0 and data.unity3d in $DATA_DIR).
  The game files may be in an unexpected layout."
  # Strip the a/b/f/p suffix: 2021.3.45f2 -> 2021.3.45
  UNITY_VER="$(printf '%s' "$UNITY_FULL" | sed -E 's/[abfp][0-9]+$//')"
  ok "Unity version: $UNITY_FULL (using $UNITY_VER for unstripped libs)"
fi

# --- 5. already installed? -----------------------------------------------
is_fully_installed() {
  [[ -f "$GAME_DIR/winhttp.dll" ]] || return 1
  [[ -f "$GAME_DIR/BepInEx/core/BepInEx.Preloader.dll" ]] || return 1
  [[ -f "$DOORSTOP_CONFIG" ]] || return 1
  grep -q 'dll_search_path_override = unstripped_libs' "$DOORSTOP_CONFIG" 2>/dev/null || return 1
  [[ -d "$UNSTRIPPED_DIR" ]] || return 1
  # non-empty unstripped_libs
  [[ -n "$(ls -A "$UNSTRIPPED_DIR" 2>/dev/null)" ]] || return 1
  # full Newtonsoft.Json must be present in the override (older scripts lacked it)
  [[ -f "$UNSTRIPPED_DIR/Newtonsoft.Json.dll" ]] || return 1
  # HideManagerGameObject = true must be set (older scripts didn't seed it; without
  # it the mod loads but its UI never appears on Dorfromantik). CRLF-tolerant grep.
  grep -Eq '^HideManagerGameObject = true'$'\r''?$' "$GAME_DIR/BepInEx/config/BepInEx.cfg" 2>/dev/null || return 1
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
  echo
  bold "Installing BepInEx $BEPINEX_VERSION (win_x64)…"

  # 6a. BepInEx loader — unzipped directly into the game folder.
  BEPINEX_ZIP="$WORKDIR/bepinex.zip"
  fetch "$BEPINEX_URL" "$BEPINEX_ZIP"
  unzip -oq "$BEPINEX_ZIP" -d "$GAME_DIR" || die "Failed to unzip BepInEx into $GAME_DIR"
  [[ -f "$GAME_DIR/winhttp.dll" ]] || die "winhttp.dll missing after unzip — unexpected archive layout."
  [[ -f "$DOORSTOP_CONFIG" ]] || die "doorstop_config.ini missing after unzip — unexpected archive layout."
  ok "BepInEx loader installed"

  # 6b. unstripped corlib into unstripped_libs/ at the GAME ROOT.
  # NOTE: NOT under BepInEx/ — doorstop resolves the search path relative to the
  # game dir under Wine and we patch it with the RELATIVE path "unstripped_libs";
  # an absolute Linux path would not resolve inside Wine.
  mkdir -p "$UNSTRIPPED_DIR"
  if [[ "$MODE" == standalone ]]; then
    # STANDALONE: the full net_4_x Mono corlib from this project's GitHub release.
    # This is the complete-API-surface profile that works since game v1.1.5.3.
    # Verify the download's sha256 before trusting it, then unzip in place (the
    # zip root already holds the DLLs and the Facades/ subfolder).
    command -v sha256sum >/dev/null 2>&1 \
      || die "'sha256sum' not found. Install it with:  sudo pacman -S coreutils"
    CORLIB_ZIP="$WORKDIR/corlib.zip"
    fetch "$CORLIB_URL" "$CORLIB_ZIP"
    ACTUAL_SHA256="$(sha256sum "$CORLIB_ZIP" | cut -d' ' -f1)"
    [[ "$ACTUAL_SHA256" == "$CORLIB_SHA256" ]] || die "Corlib checksum mismatch — refusing to install.
    expected: $CORLIB_SHA256
    got:      $ACTUAL_SHA256
  The download may be corrupt or tampered with. Re-run, or check the release URL:
    $CORLIB_URL"
    ok "Corlib checksum verified (sha256)"
    unzip -oq "$CORLIB_ZIP" -d "$UNSTRIPPED_DIR" || die "Failed to unzip the corlib into $UNSTRIPPED_DIR"
    [[ -f "$UNSTRIPPED_DIR/mscorlib.dll" ]] || die "mscorlib.dll missing after extraction — unexpected corlib layout."
    ok "Full net_4_x corlib installed (Unity 2021.3.45)"
  else
    # OFFICIAL (deprecated): the reduced unityjit corlib for the detected Unity
    # version. Missing stripped members since v1.1.5.3 → gray/missing assets.
    CORLIBS_ZIP="$WORKDIR/corlibs.zip"
    fetch "$CORLIBS_BASE/$UNITY_VER.zip" "$CORLIBS_ZIP"
    unzip -oq "$CORLIBS_ZIP" -d "$UNSTRIPPED_DIR" || die "Failed to unzip corlibs into $UNSTRIPPED_DIR"
    [[ -n "$(ls -A "$UNSTRIPPED_DIR")" ]] || die "unstripped_libs is empty after extraction."
    ok "Unstripped corlibs installed for Unity $UNITY_VER"
  fi

  # 6c. full Newtonsoft.Json into the override — the game's shipped copy may be
  # stripped (no JsonConvert). Placed here so Mono's dll_search_path_override
  # binds it (by simple name) ahead of Managed/ for both game and mod.
  NUPKG="$WORKDIR/newtonsoft.nupkg"
  fetch "$NEWTONSOFT_URL" "$NUPKG"
  unzip -oj "$NUPKG" "lib/netstandard2.0/Newtonsoft.Json.dll" -d "$UNSTRIPPED_DIR" \
    || die "Failed to extract Newtonsoft.Json.dll from the nupkg."
  [[ -f "$UNSTRIPPED_DIR/Newtonsoft.Json.dll" ]] || die "Newtonsoft.Json.dll missing after extraction."
  ok "Full Newtonsoft.Json installed into unstripped_libs"

  # --- 7. patch doorstop_config.ini --------------------------------------
  # Anchor is the stock empty assignment under [UnityMono]:
  #   dll_search_path_override =
  # The Windows archive ships CRLF line endings, so tolerate an optional
  # trailing CR and preserve it. Point doorstop at the RELATIVE unstripped_libs
  # folder so it resolves under Wine.
  CR="$(printf '\r')"
  sed -i -E "s#^(dll_search_path_override =) *(${CR}?)\$#\\1 unstripped_libs\\2#" "$DOORSTOP_CONFIG"

  grep -q 'dll_search_path_override = unstripped_libs' "$DOORSTOP_CONFIG" \
    || die "Failed to patch dll_search_path_override in doorstop_config.ini (unexpected format)."
  ok "Configured doorstop_config.ini (dll_search_path_override = unstripped_libs)"
fi
fi   # end mode branch (gts / standalone / official)

# --- 8. ensure plugins dir -----------------------------------------------
mkdir -p "$GAME_DIR/BepInEx/plugins"

# --- 8a. force HideManagerGameObject = true ------------------------------
# CRITICAL fix — runs unconditionally on EVERY run and EVERY mode (standalone,
# official, gts), so a plain re-run also heals an existing install. Stock BepInEx
# defaults [Chainloader]/HideManagerGameObject to false AND doesn't even ship
# BepInEx.cfg (it's generated on the game's first run with the default), so we
# must pre-seed/patch the file so the very first launch already has true. With
# false, Dorfromantik disables BepInEx's manager GameObject right after
# chainloader startup, so plugins' Update/OnGUI never run — the mod loads but its
# UI never appears. true hides the manager from the game and lets the mod UI live.
# BepInEx writes this file with CRLF line endings under Wine/Proton, so we stay
# CRLF-tolerant and preserve existing endings.
mkdir -p "$GAME_DIR/BepInEx/config"
CFG="$GAME_DIR/BepInEx/config/BepInEx.cfg"
CR="$(printf '\r')"
if [[ -f "$CFG" ]]; then
  if grep -Eq '^HideManagerGameObject =' "$CFG"; then
    # Line exists — force it to true (whether currently false or already true).
    # Preserve any trailing CR; idempotent when it's already true.
    sed -i -E "s#^(HideManagerGameObject =) *[^${CR}]*(${CR}?)\$#\\1 true\\2#" "$CFG"
  elif grep -Eq "^\[Chainloader\]${CR}?\$" "$CFG"; then
    # [Chainloader] header present but no setting — insert it under the header.
    sed -i -E "/^\[Chainloader\]${CR}?\$/a HideManagerGameObject = true\\${CR}" "$CFG"
  else
    # File exists but no [Chainloader] section — append a fresh block (CRLF).
    printf '\r\n[Chainloader]\r\nHideManagerGameObject = true\r\n' >> "$CFG"
  fi
else
  # No cfg yet (stock BepInEx generates it on first run). Seed the minimum; BepInEx
  # merges the remaining defaults on first run and preserves this value.
  printf '[Chainloader]\r\nHideManagerGameObject = true\r\n' > "$CFG"
fi
grep -Eq '^HideManagerGameObject = true' "$CFG" \
  || die "Failed to set HideManagerGameObject = true in BepInEx.cfg (unexpected format): $CFG"
ok "BepInEx configured (HideManagerGameObject = true — required so the mod UI survives on Dorfromantik)"

# --- 9. the manual steps -------------------------------------------------
echo
bold "Two manual steps remain (both in Steam):"
info "1. Force Proton: Dorfromantik → Properties → Compatibility →"
info "   \"Force the use of a specific Steam Play compatibility tool\"."
info "2. Inject BepInEx: Dorfromantik → Properties → Launch Options, set:"
info "   WINEDLLOVERRIDES=\"winhttp=n,b\" %command%"
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
