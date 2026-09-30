#!/usr/bin/env bash
#
# Activates the Unity 5.4.0f3 editor in this repository's Wine prefix.
#
#   ./tools/activate-unity.sh
#
# Run this yourself, in your own terminal. It asks for a Unity account and never
# writes the answers anywhere: they go straight to the editor as arguments and the
# only thing left behind is the licence file Unity writes into the prefix.
#
# Why it is needed at all, since Unity Personal is free: free is not the same as
# licence-free. Unity 5.4 refuses to start -- batch mode included -- until a
# Unity_lic.ulf exists, and every route to one goes through a Unity account.
#
# Why not the editor's own activation window: in 5.4 that window is a Chromium
# view, and under Wine it renders black. The editor's log also shows its login web
# packages (unity-editor-home, unityeditor-cloud-hub) are missing from the install.
# Activating from the command line skips that UI completely.
#
# A Personal licence needs no serial. For Plus or Pro, set UNITY_SERIAL first.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export WINEPREFIX="$REPO_DIR/vendor/wineprefix"
export WINEARCH=win64
export WINEDEBUG=-all
export WINEDLLOVERRIDES="mscoree,mshtml="

EDITOR="$WINEPREFIX/drive_c/Unity/Editor/Unity.exe"
[[ -f "$EDITOR" ]] || { echo "No editor. Run ./tools/fetch-unity.sh first." >&2; exit 1; }

LIC="$WINEPREFIX/drive_c/ProgramData/Unity/Unity_lic.ulf"
if [[ -f "$LIC" ]]; then
    echo "Already activated: $LIC"
    exit 0
fi

read -r -p  "Unity account e-mail: " UNITY_USER
read -r -s -p "Unity account password: " UNITY_PASS; echo

ARGS=(-batchmode -nographics -quit
      -username "$UNITY_USER" -password "$UNITY_PASS")
[[ -n "${UNITY_SERIAL:-}" ]] && ARGS+=(-serial "$UNITY_SERIAL")

LOG="$REPO_DIR/vendor/unity-activate.log"
rm -f "$LOG"

set +e
wine "$EDITOR" "${ARGS[@]}" -logFile 'Z:'"${LOG//\//\\}"
rc=$?
set -e
unset UNITY_PASS

if [[ -f "$LIC" ]]; then
    echo "Activated. Licence at $LIC"
    echo "Now run ./tools/build-bundles.sh"
    exit 0
fi

echo "Activation did not produce a licence file (editor exited $rc)." >&2
[[ -f "$LOG" ]] && { echo "--- last 20 log lines ---" >&2; tail -20 "$LOG" >&2; }
exit 1
