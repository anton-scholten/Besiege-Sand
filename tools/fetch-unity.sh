#!/usr/bin/env bash
#
# Fetches the Unity 5.4.0f3 editor and installs it into a Wine prefix inside this
# repository, so building the mod's shader bundles needs nothing from outside.
#
#   ./tools/fetch-unity.sh            fetch and install (idempotent)
#   ./tools/fetch-unity.sh --clean    delete the prefix and the installers
#
# Why this version and no other: Besiege is Unity 5.4.0f3, and an asset bundle
# records the version it was built with. A bundle from any later editor loads as
# nothing at all, with no error worth reading. See the notes repo,
# 18-vendoring-unity-code.md.
#
# Why Wine: Unity never shipped a Linux editor for 5.4.0f3 -- the archive answers
# 404 for it -- but the Windows editor and, importantly, the *Linux build target*
# support for that version are both still served. The editor runs under Wine well
# enough to build asset bundles in batch mode, which is all that is wanted here.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENDOR="$REPO_DIR/vendor"
HASH=a6d8d714de6f
BASE="https://download.unity3d.com/download_unity/$HASH"

export WINEPREFIX="$VENDOR/wineprefix"
export WINEARCH=win64
export WINEDEBUG=-all
export WINEDLLOVERRIDES="mscoree,mshtml="

if [[ "${1:-}" == "--clean" ]]; then
    rm -rf "$VENDOR/wineprefix" "$VENDOR/unity"
    echo "Removed the Wine prefix and the installers."
    exit 0
fi

command -v wine >/dev/null || { echo "wine is not installed." >&2; exit 1; }

mkdir -p "$VENDOR/unity"

fetch() {
    local url="$1" out="$VENDOR/unity/$2"
    if [[ -s "$out" ]]; then echo "have $(basename "$out")"; return; fi
    echo "fetching $(basename "$out")..."
    curl -fL --retry 3 -o "$out" "$url"
}

fetch "$BASE/Windows64EditorInstaller/UnitySetup64-5.4.0f3.exe" UnitySetup64-5.4.0f3.exe
fetch "$BASE/TargetSupportInstaller/UnitySetup-Linux-Support-for-Editor-5.4.0f3.exe" \
      UnitySetup-Linux-Support-for-Editor-5.4.0f3.exe

if [[ ! -d "$WINEPREFIX" ]]; then
    echo "creating the Wine prefix..."
    wineboot -i >/dev/null 2>&1
fi

EDITOR="$WINEPREFIX/drive_c/Unity/Editor/Unity.exe"
if [[ ! -f "$EDITOR" ]]; then
    echo "installing the editor (several minutes)..."
    wine "$VENDOR/unity/UnitySetup64-5.4.0f3.exe" /S '/D=C:\Unity'
fi
[[ -f "$EDITOR" ]] || { echo "Editor did not install; see $WINEPREFIX." >&2; exit 1; }

# The target support installer writes into the editor's own folder, so it has to
# run after the editor and point at the same place.
if [[ ! -d "$WINEPREFIX/drive_c/Unity/Editor/Data/PlaybackEngines/LinuxStandaloneSupport" ]]; then
    echo "installing Linux build target support..."
    wine "$VENDOR/unity/UnitySetup-Linux-Support-for-Editor-5.4.0f3.exe" /S '/D=C:\Unity'
fi

echo
echo "Editor:  $EDITOR"
ls "$WINEPREFIX/drive_c/Unity/Editor/Data/PlaybackEngines" 2>/dev/null || true
