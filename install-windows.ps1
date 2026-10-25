# install-windows.ps1 - one-stop BepInEx + DRMultiplayer installer for Windows.
#
# For players: no .NET SDK, no build step, nothing manual. The script
#   1. finds your Dorfromantik install (Steam registry + library folders, GOG registry),
#   2. installs the official BepInEx 5.4.23.5 (win_x64) loader,
#   3. installs the full net_4_x unstripped Mono corlib (sha256-verified from
#      this project's GitHub release) into unstripped_libs\ and points
#      doorstop_config.ini at it (the game ships stripped assemblies, so plain
#      BepInEx is not enough),
#   4. adds a full Newtonsoft.Json (the game's own copy may be stripped),
#   5. sets HideManagerGameObject = true in BepInEx\config\BepInEx.cfg
#      (without it the mod loads but its UI never appears),
#   6. installs DRMultiplayer.dll into BepInEx\plugins\DRMultiplayer\
#      (from a DRMultiplayer.dll sitting next to this script if present,
#      otherwise downloaded from this project's GitHub releases).
#
# Unlike Linux/macOS there are NO Steam launch options to set: on native
# Windows the winhttp.dll loader injects by itself.
#
# Usage (or just double-click install-windows.bat):
#   powershell -ExecutionPolicy Bypass -File install-windows.ps1
#   ... -GameDir "D:\SteamLibrary\steamapps\common\Dorfromantik"   custom path
#   ... -Clean      remove a previous BepInEx install first
#   ... -NoMod      set up the modding base only, skip the plugin DLL

param(
    [string]$GameDir = "",
    [switch]$Clean,
    [switch]$NoMod
)

$ErrorActionPreference = "Stop"
# PowerShell 5.1 defaults to TLS 1.0, which GitHub rejects.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# --- pinned downloads -------------------------------------------------------
$BepInExUrl   = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip"
# Full net_4_x desktop Mono profile (146 DLLs + Facades\), Windows flavor,
# pinned to the game's Unity 2021.3.45f2. Same asset the Linux installer uses
# (on Linux the game is this same Windows build, run under Proton).
$CorlibUrl    = "https://github.com/FabricatedMatter/DRMultiplayer/releases/download/bepinex-corlib-2021.3.45/net_4_x_corlib_unity-2021.3.45.zip"
$CorlibSha256 = "23993802ff6a0cffa9cccf7fd0a880f7f983a9524208ba95859b277878e720a3"
$NewtonsoftUrl = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"
$Repo          = "FabricatedMatter/DRMultiplayer"

function Info($m) { Write-Host "  * $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  + $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  ! $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "  x $m" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "DRMultiplayer - Windows install" -ForegroundColor White
Write-Host ""

# --- 1. locate the game -----------------------------------------------------
function Find-GameDir {
    $candidates = @()
    try {
        $steam = (Get-ItemProperty -Path "HKCU:\Software\Valve\Steam").SteamPath
        if ($steam) {
            $steam = $steam -replace '/', '\'
            $candidates += Join-Path $steam "steamapps\common\Dorfromantik"
            # Extra Steam library folders live in libraryfolders.vdf.
            $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
            if (Test-Path $vdf) {
                foreach ($line in Get-Content $vdf) {
                    if ($line -match '"path"\s+"([^"]+)"') {
                        $lib = $Matches[1] -replace '\\\\', '\'
                        $candidates += Join-Path $lib "steamapps\common\Dorfromantik"
                    }
                }
            }
        }
    } catch { }
    # GOG installs register themselves under GOG.com\Games\<id> with their install path.
    foreach ($root in "HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games", "HKLM:\SOFTWARE\GOG.com\Games") {
        try {
            Get-ChildItem $root -ErrorAction Stop | ForEach-Object {
                $g = Get-ItemProperty $_.PSPath
                if ($g.gameName -like "*Dorfromantik*" -and $g.path) { $candidates += $g.path }
            }
        } catch { }
    }
    $candidates += "C:\Program Files (x86)\Steam\steamapps\common\Dorfromantik"
    $candidates += "C:\Program Files (x86)\GOG Galaxy\Games\Dorfromantik"
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "Dorfromantik.exe")) { return $c }
    }
    return $null
}

if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir) {
    Warn "Could not find Dorfromantik in your Steam or GOG libraries."
    $GameDir = Read-Host "  Enter the path to your Dorfromantik folder"
}
if (-not (Test-Path (Join-Path $GameDir "Dorfromantik.exe"))) {
    Fail "Dorfromantik.exe not found in: $GameDir"
}
$Managed = Join-Path $GameDir "Dorfromantik_Data\Managed"
if (-not (Test-Path $Managed)) {
    Fail "Managed folder not found at: $Managed (is this really the game folder?)"
}
Ok "Game folder: $GameDir"

$UnstrippedDir  = Join-Path $GameDir "unstripped_libs"
$DoorstopConfig = Join-Path $GameDir "doorstop_config.ini"
$PluginsDir     = Join-Path $GameDir "BepInEx\plugins"
$CfgDir         = Join-Path $GameDir "BepInEx\config"
$Cfg            = Join-Path $CfgDir "BepInEx.cfg"

# --- 2. previous-install handling --------------------------------------------
function Remove-PreviousInstall {
    Warn "Removing previous BepInEx install (this also removes any plugins and"
    Warn "configs you added under BepInEx\)..."
    foreach ($p in @("BepInEx", "winhttp.dll", "doorstop_config.ini",
                     "changelog.txt", ".doorstop_version",
                     "unstripped_corlib", "unstripped_libs", "output_log.txt")) {
        $full = Join-Path $GameDir $p
        if (Test-Path $full) { Remove-Item -Recurse -Force $full }
    }
    Ok "Previous install removed"
}

$hasPrevious = @("BepInEx", "winhttp.dll", "doorstop_config.ini",
                 "unstripped_corlib", "unstripped_libs") |
    Where-Object { Test-Path (Join-Path $GameDir $_) }

if ($Clean -and $hasPrevious) {
    Remove-PreviousInstall
} elseif ($hasPrevious) {
    Info "A previous BepInEx install was detected; keeping it (run with -Clean to reinstall)."
}

# --- 3. heal broken installs -------------------------------------------------
# Overriding the game's own UnityEngine modules breaks asset loading (gray
# world / missing assets); remove such overrides if an older setup left them.
if (Test-Path $UnstrippedDir) {
    $ue = Get-ChildItem -Path $UnstrippedDir -Filter "UnityEngine*.dll" -ErrorAction SilentlyContinue
    if ($ue) {
        Warn "Removing UnityEngine module overrides (they cause missing assets)"
        $ue | Remove-Item -Force
        Ok "Removed UnityEngine module overrides"
    }
}

# --- 4. download helpers -----------------------------------------------------
$WorkDir = Join-Path $env:TEMP ("drmultiplayer-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $WorkDir | Out-Null

function Fetch($url, $dest) {
    Info "Downloading $url"
    try {
        Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
    } catch {
        Fail "Download failed: $url`n  $($_.Exception.Message)"
    }
}

function Test-FullyInstalled {
    if (-not (Test-Path (Join-Path $GameDir "winhttp.dll"))) { return $false }
    if (-not (Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.Preloader.dll"))) { return $false }
    if (-not (Test-Path $DoorstopConfig)) { return $false }
    if (-not (Select-String -Path $DoorstopConfig -Pattern 'dll_search_path_override = unstripped_libs' -Quiet)) { return $false }
    if (-not (Test-Path (Join-Path $UnstrippedDir "mscorlib.dll"))) { return $false }
    if (-not (Test-Path (Join-Path $UnstrippedDir "Newtonsoft.Json.dll"))) { return $false }
    return $true
}

try {
    # --- 5. modding base -----------------------------------------------------
    if (Test-FullyInstalled) {
        Ok "BepInEx already installed and configured; skipping base downloads."
    } else {
        Write-Host ""
        Write-Host "Installing BepInEx 5.4.23.5 (win_x64)..." -ForegroundColor White

        # 5a. BepInEx loader, unzipped directly into the game folder.
        $bepZip = Join-Path $WorkDir "bepinex.zip"
        Fetch $BepInExUrl $bepZip
        Expand-Archive -Path $bepZip -DestinationPath $GameDir -Force
        if (-not (Test-Path (Join-Path $GameDir "winhttp.dll"))) {
            Fail "winhttp.dll missing after unzip (unexpected archive layout)."
        }
        if (-not (Test-Path $DoorstopConfig)) {
            Fail "doorstop_config.ini missing after unzip (unexpected archive layout)."
        }
        Ok "BepInEx loader installed"

        # 5b. full net_4_x corlib into unstripped_libs\ at the game root.
        $corlibZip = Join-Path $WorkDir "corlib.zip"
        Fetch $CorlibUrl $corlibZip
        $actual = (Get-FileHash -Path $corlibZip -Algorithm SHA256).Hash
        if ($actual -ne $CorlibSha256.ToUpperInvariant() -and $actual.ToLowerInvariant() -ne $CorlibSha256) {
            Fail "Corlib checksum mismatch; refusing to install.`n  expected: $CorlibSha256`n  got:      $actual"
        }
        Ok "Corlib checksum verified (sha256)"
        New-Item -ItemType Directory -Path $UnstrippedDir -Force | Out-Null
        Expand-Archive -Path $corlibZip -DestinationPath $UnstrippedDir -Force
        if (-not (Test-Path (Join-Path $UnstrippedDir "mscorlib.dll"))) {
            Fail "mscorlib.dll missing after extraction (unexpected corlib layout)."
        }
        Ok "Full net_4_x corlib installed (Unity 2021.3.45)"

        # 5c. full Newtonsoft.Json into the override (a .nupkg is a plain zip).
        $nupkg = Join-Path $WorkDir "newtonsoft.zip"
        Fetch $NewtonsoftUrl $nupkg
        $nupkgDir = Join-Path $WorkDir "newtonsoft"
        Expand-Archive -Path $nupkg -DestinationPath $nupkgDir -Force
        Copy-Item (Join-Path $nupkgDir "lib\netstandard2.0\Newtonsoft.Json.dll") $UnstrippedDir -Force
        Ok "Full Newtonsoft.Json installed into unstripped_libs"

        # 5d. point doorstop at the corlib override (relative path).
        $lines = Get-Content $DoorstopConfig
        $lines = $lines | ForEach-Object {
            if ($_ -match '^dll_search_path_override\s*=\s*$') { 'dll_search_path_override = unstripped_libs' } else { $_ }
        }
        Set-Content -Path $DoorstopConfig -Value $lines
        if (-not (Select-String -Path $DoorstopConfig -Pattern 'dll_search_path_override = unstripped_libs' -Quiet)) {
            Fail "Failed to patch dll_search_path_override in doorstop_config.ini (unexpected format)."
        }
        Ok "Configured doorstop_config.ini (dll_search_path_override = unstripped_libs)"
    }

    # --- 6. HideManagerGameObject = true (every run; heals old installs) -----
    # Stock BepInEx defaults this to false and doesn't ship the file at all.
    # With false, Dorfromantik disables BepInEx's manager GameObject right after
    # chainloader startup, so plugins' Update/OnGUI never run and the mod UI
    # never appears, even though the mod itself loads.
    New-Item -ItemType Directory -Path $CfgDir -Force | Out-Null
    if (Test-Path $Cfg) {
        $lines = Get-Content $Cfg
        if ($lines -match '^HideManagerGameObject') {
            $lines = $lines | ForEach-Object {
                if ($_ -match '^HideManagerGameObject\s*=') { 'HideManagerGameObject = true' } else { $_ }
            }
        } elseif ($lines -match '^\[Chainloader\]') {
            $out = @()
            foreach ($l in $lines) {
                $out += $l
                if ($l -match '^\[Chainloader\]') { $out += 'HideManagerGameObject = true' }
            }
            $lines = $out
        } else {
            $lines += ''
            $lines += '[Chainloader]'
            $lines += 'HideManagerGameObject = true'
        }
        Set-Content -Path $Cfg -Value $lines
    } else {
        Set-Content -Path $Cfg -Value @('[Chainloader]', 'HideManagerGameObject = true')
    }
    Ok "BepInEx configured (HideManagerGameObject = true)"

    # --- 7. the plugin DLL ----------------------------------------------------
    New-Item -ItemType Directory -Path (Join-Path $PluginsDir "DRMultiplayer") -Force | Out-Null
    if ($NoMod) {
        Ok "Base-only run (-NoMod); modding base is ready."
    } else {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
        $localDll = Join-Path $scriptDir "DRMultiplayer.dll"
        $destDll  = Join-Path $PluginsDir "DRMultiplayer\DRMultiplayer.dll"
        if (Test-Path $localDll) {
            Copy-Item $localDll $destDll -Force
            Ok "Installed DRMultiplayer.dll (bundled copy next to the script)"
        } else {
            Info "No DRMultiplayer.dll next to the script; checking GitHub releases..."
            $assetUrl = $null
            try {
                $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases" -UseBasicParsing
                foreach ($rel in $releases) {
                    $asset = $rel.assets | Where-Object { $_.name -eq "DRMultiplayer.dll" } | Select-Object -First 1
                    if ($asset) { $assetUrl = $asset.browser_download_url; break }
                }
            } catch { }
            if ($assetUrl) {
                Fetch $assetUrl $destDll
                Ok "Installed DRMultiplayer.dll (downloaded from GitHub releases)"
            } else {
                Warn "No pre-built DRMultiplayer.dll found (neither next to the script nor"
                Warn "on GitHub releases). The modding base is installed; drop the DLL into:"
                Warn "  $PluginsDir\DRMultiplayer\"
            }
        }
    }
} finally {
    Remove-Item -Recurse -Force $WorkDir -ErrorAction SilentlyContinue
}

# --- 8. done ------------------------------------------------------------------
Write-Host ""
Ok "Done. No Steam launch options are needed on Windows."
Info "Launch Dorfromantik, start or continue a classic game, press F6 (or M)."
Info "When you host for the first time, Windows Firewall will ask for permission;"
Info "click 'Allow access' or nobody can join you."
