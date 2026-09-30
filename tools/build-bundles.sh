#!/usr/bin/env bash
#
# Compiles shaders/ into the mod's asset bundles, one per graphics API, using the
# Unity 5.4.0f3 editor that tools/fetch-unity.sh put in vendor/wineprefix.
#
#   ./tools/build-bundles.sh
#
# Runs the editor in batch mode with no graphics device. Nothing appears on
# screen; the editor's own log is copied out at the end, because a Unity batch
# failure says nothing on stdout.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export WINEARCH=win64
export WINEDEBUG=-all
export WINEDLLOVERRIDES="mscoree,mshtml="

# A Unity licence is bound to the machine as the editor sees it, and two Wine
# prefixes do not report the same bindings -- the repo's own prefix reports Wine's
# default ProductId, a prefix made by another Wine run may not, and a licence
# activated in one is rejected in the other with
#   LICENSE SYSTEM 12345-oem-0000001-54321 != 00330-50000-00000-AAOEM
# So the prefix to build in is whichever one actually holds a licence, not
# whichever one this repo created. Set UNITY_WINEPREFIX and UNITY_EDITOR to
# override.
licensed() {   # $1 = prefix
    compgen -G "$1/drive_c/ProgramData/Unity/*.ulf" >/dev/null 2>&1
}

LOG="$REPO_DIR/vendor/unity-build.log"
mkdir -p "$REPO_DIR/vendor"
rm -f "$LOG"

# A native Linux editor is preferred when one is installed: no Wine in the way, and
# the bundles come out of the same graphics API the player runs. Unity's
# experimental Linux editor for 5.4.0f3 installs to /opt/Unity.
NATIVE="${UNITY_NATIVE:-/opt/Unity/Editor/Unity}"

# Candidate Wine installs, in order: an explicit override, this repo's prefix,
# then the default prefix. The first that has an editor AND a licence wins.
WINE_EDITOR=""
if [[ -n "${UNITY_EDITOR:-}" ]]; then
    WINE_EDITOR="$UNITY_EDITOR"
    export WINEPREFIX="${UNITY_WINEPREFIX:-$REPO_DIR/vendor/wineprefix}"
else
    for cand in "$REPO_DIR/vendor/wineprefix:drive_c/Unity/Editor/Unity.exe" \
                "$HOME/.wine:drive_c/Program Files/Unity/Editor/Unity.exe"; do
        pfx="${cand%%:*}"; rel="${cand#*:}"
        [[ -f "$pfx/$rel" ]] || continue
        if licensed "$pfx"; then
            export WINEPREFIX="$pfx"; WINE_EDITOR="$pfx/$rel"; break
        fi
        # remember an unlicensed one only as a last resort
        [[ -z "$WINE_EDITOR" ]] && { export WINEPREFIX="$pfx"; WINE_EDITOR="$pfx/$rel"; }
    done
fi

echo "Building shader bundles (this opens no window; give it a few minutes)..."
set +e
if [[ -x "$NATIVE" ]]; then
    echo "editor: $NATIVE (native)"
    "$NATIVE" -batchmode -quit -nographics \
        -projectPath "$REPO_DIR/shaders" \
        -executeMethod SandShaderBuild.BuildAll \
        -logFile "$LOG"
elif [[ -n "$WINE_EDITOR" && -f "$WINE_EDITOR" ]]; then
    echo "editor: $WINE_EDITOR"
    echo "prefix: $WINEPREFIX$(licensed "$WINEPREFIX" && echo ' (licensed)' || echo ' (NO LICENCE -- expect a refusal)')"
    wine "$WINE_EDITOR" -batchmode -quit -nographics \
        -projectPath 'Z:'"${REPO_DIR//\//\\}"'\shaders' \
        -executeMethod SandShaderBuild.BuildAll \
        -logFile 'Z:'"${LOG//\//\\}"
else
    echo "No editor found. Either install Unity 5.4.0f3 natively --" >&2
    echo "  https://download.unity3d.com/download_unity/linux/unity-editor-5.4.0f3+20160727_amd64.deb" >&2
    echo "-- or run ./tools/fetch-unity.sh for the Wine route." >&2
    exit 1
fi
rc=$?
set -e

if [[ -f "$LOG" ]]; then
    echo "--- editor log, last 40 lines ---"
    tail -40 "$LOG"
fi

if [[ $rc -ne 0 ]]; then
    echo "Editor exited $rc. Full log: $LOG" >&2
    exit $rc
fi

echo
echo "Bundles:"
find "$REPO_DIR/Sand/Resources/SandCompute" -type f 2>/dev/null || echo "  (none produced)"
