#!/usr/bin/env bash
#
# Writes the sand panel's folded button: Sand/Resources/Icon.png, the little pile of sand the
# panel becomes when it is folded away.
#
#   ./tools/make-icon.sh                 # -> Sand/Resources/Icon.png, 1024px
#   ./tools/make-icon.sh out.png         # ... somewhere else
#   ./tools/make-icon.sh out.png 2048    # ... at another size (a multiple of 64)
#
# It is a file rather than something the game draws at startup because the mod loader refuses
# System.IO: the mod cannot open an image from disk, and a mod's pictures reach it as declared
# Resources instead. Mod.xml declares this as SandIcon and SandHud loads it back for the button,
# falling back to drawing it if the file is missing.
#
# This is not the mod's Workshop thumbnail. That is Thumbnail_2.png, the artwork, which
# ./tools/make-thumbnail.sh copies into the mod folder.
#
# Runs on Besiege's own Mono, with no game and no display, like tools/test-bed.sh.
# Set BESIEGE_DIR if the install is not auto-detected.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$REPO_DIR/Sand/Scripts"
WORK="${TMPDIR:-/tmp}/besiege-se-build"
mkdir -p "$WORK"

OUT="${1:-$REPO_DIR/Sand/Resources/Icon.png}"
PX="${2:-1024}"

find_besiege() {
    if [[ -n "${BESIEGE_DIR:-}" ]]; then echo "$BESIEGE_DIR"; return; fi
    local c
    for c in "$HOME/.steam/steam/steamapps/common/Besiege" \
             "$HOME/.local/share/Steam/steamapps/common/Besiege"; do
        [[ -f "$c/Besiege_Data/Managed/mcs.dll" ]] && { echo "$c"; return; }
    done
    # Also follow Steam's library folders, the way tools/build.sh does: the game is often
    # not on the drive Steam itself is.
    local vdf
    for vdf in "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
               "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"; do
        [[ -f "$vdf" ]] || continue
        while read -r lib; do
            [[ -f "$lib/steamapps/common/Besiege/Besiege_Data/Managed/mcs.dll" ]] &&
                { echo "$lib/steamapps/common/Besiege"; return; }
        done < <(grep -oE '"path"[[:space:]]+"[^"]+"' "$vdf" | sed -E 's/.*"([^"]+)"$/\1/')
    done
    return 1
}

BESIEGE="$(find_besiege)" || { echo "Could not find Besiege. Set BESIEGE_DIR." >&2; exit 1; }
DATA="$BESIEGE/Besiege_Data"
export LIBMONO="$DATA/Mono/x86_64/libmono.so" MANAGED="$DATA/Managed" MONOETC="$DATA/Mono/etc"

# The compiler and host are built by build.sh; make sure they exist.
if [[ ! -x "$WORK/besiegecc" || ! -x "$WORK/monohost" ]]; then
    "$REPO_DIR/tools/build.sh" --check >/dev/null
fi

EXE="$WORK/MakeIcon.exe"
"$WORK/besiegecc" -target:exe -out:"$EXE" \
    -lib:"$MANAGED" -r:UnityEngine.dll -r:System.dll -r:System.Core.dll \
    "$REPO_DIR/tools/make-icon/MakeIcon.cs" \
    "$SRC/SandIcon.cs" >/dev/null

TARGET_ASM="$EXE" "$WORK/monohost" "$OUT" "$PX"