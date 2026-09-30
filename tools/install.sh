#!/usr/bin/env bash
#
# Installs the mod into Besiege's Mods folder.
#
#   ./tools/install.sh              build, then symlink the mod (best for
#                                   development -- a rebuild is picked up by the
#                                   next game start, with no reinstall)
#   ./tools/install.sh --copy       build, then copy instead (for handing someone
#                                   a folder, or if symlinks are awkward)
#   ./tools/install.sh --uninstall  remove it again
#   ./tools/install.sh --no-build   skip the build step
#
# Besiege reads mods once at startup, so restart the game afterwards.
# Set BESIEGE_DIR if the install is not auto-detected, e.g.
#   BESIEGE_DIR="$HOME/.steam/steam/steamapps/common/Besiege" ./tools/install.sh
#
# The folder Besiege loads is Sand/, not the repository root: that
# subfolder is the whole of what gets uploaded to the Workshop, and everything
# beside it -- sources, tools, docs, working files -- is not part of the mod.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MOD_NAME="Sand"
SRC="$REPO_DIR/$MOD_NAME"

MODE="link"
BUILD=1
for arg in "$@"; do
    case "$arg" in
        --uninstall) MODE="uninstall"; BUILD=0 ;;
        --copy)      MODE="copy" ;;
        --no-build)  BUILD=0 ;;
        *) echo "Unknown option: $arg" >&2; exit 1 ;;
    esac
done

find_besiege() {
    if [[ -n "${BESIEGE_DIR:-}" ]]; then echo "$BESIEGE_DIR"; return; fi
    local candidates=(
        "$HOME/.steam/steam/steamapps/common/Besiege"
        "$HOME/.local/share/Steam/steamapps/common/Besiege"
        "$HOME/Library/Application Support/Steam/steamapps/common/Besiege"
    )
    local vdf
    for vdf in "$HOME/.steam/steam/steamapps/libraryfolders.vdf" \
               "$HOME/.local/share/Steam/steamapps/libraryfolders.vdf"; do
        [[ -f "$vdf" ]] || continue
        while read -r lib; do candidates+=("$lib/steamapps/common/Besiege"); done \
            < <(grep -oE '"path"[[:space:]]+"[^"]+"' "$vdf" | sed -E 's/.*"([^"]+)"$/\1/')
    done
    local dir
    for dir in "${candidates[@]}"; do
        [[ -d "$dir/Besiege_Data" ]] && { echo "$dir"; return; }
    done
    return 1
}

if ! BESIEGE="$(find_besiege)"; then
    echo "Could not find Besiege. Set BESIEGE_DIR to your install directory." >&2
    exit 1
fi

MODS="$BESIEGE/Besiege_Data/Mods"
DEST="$MODS/$MOD_NAME"

if [[ "$MODE" == "uninstall" ]]; then
    if [[ -L "$DEST" ]]; then
        rm "$DEST"
        echo "Removed symlink $DEST"
    elif [[ -d "$DEST" ]]; then
        rm -rf "$DEST"
        echo "Removed $DEST"
    else
        echo "Nothing installed at $DEST"
    fi
    exit 0
fi

if [[ $BUILD -eq 1 ]]; then
    BESIEGE_DIR="$BESIEGE" "$REPO_DIR/tools/build.sh"
    echo
fi

if [[ ! -f "$SRC/SandAssembly.dll" ]]; then
    echo "Sand/SandAssembly.dll is missing; run ./tools/build.sh first." >&2
    exit 1
fi

# Check the manifest before installing anything. A malformed Mod.xml does not
# produce an error in game: the mod simply never appears in the list, which is
# indistinguishable from not having installed it.
if command -v python3 >/dev/null 2>&1; then
    if ! python3 - "$SRC" <<'PY'
import os, sys, xml.dom.minidom
src = sys.argv[1]
try:
    root = xml.dom.minidom.parse(os.path.join(src, "Mod.xml")).documentElement
except Exception as e:
    sys.exit("Mod.xml is not valid XML: %s" % e)
# Resource paths in Mod.xml are written Windows-style; the game normalises the
# separator, but not the case, so this also catches a texture that would only
# resolve on a case-insensitive filesystem.
kinds = (("Assembly", ""), ("Block", ""), ("Entity", ""),
         ("Texture", "Resources"), ("Mesh", "Resources"),
         ("AudioClip", "Resources"), ("AssetBundle", "Resources"))
missing = []
for tag, base in kinds:
    for node in root.getElementsByTagName(tag):
        rel = node.getAttribute("path")
        if rel and not os.path.exists(os.path.join(src, base, rel.replace(chr(92), "/"))):
            missing.append(rel)
if missing:
    sys.exit("Mod.xml names files that are not there: %s" % ", ".join(missing))
PY
    then
        echo "Refusing to install: the manifest is broken (see above)." >&2
        exit 1
    fi
fi

mkdir -p "$MODS"
# Replace whatever is there, whichever kind it is, then install.
[[ -L "$DEST" ]] && rm "$DEST"
[[ -d "$DEST" ]] && rm -rf "$DEST"

if [[ "$MODE" == "copy" ]]; then
    cp -r "$SRC" "$DEST"
    rm -rf "$DEST/Scripts"
    echo "Copied mod to $DEST"
else
    ln -s "$SRC" "$DEST"
    echo "Linked $DEST -> $SRC"
fi

if pgrep -x Besiege >/dev/null 2>&1 || pgrep -f 'Besiege\.x86' >/dev/null 2>&1; then
    echo "Besiege is running; restart it to pick this up."
fi

cat <<'EOF2'

Done. Next:
  1. Start Besiege and enable "Sand Probe" in the mods menu. It adds no blocks and
     changes nothing you can see -- everything it finds goes to Player.log.
  2. Read the report:
       grep -a '\[Sand\]' ~/.config/unity3d/Spiderling\ Games/Besiege/Player.log
  3. A scene report is taken automatically on every level load, and F9 forces one.
     To take one without touching the menus, start the game with
       -sandprobe-level 30,45
     which loads those scenes in turn and reports on each.

Note: the game writes the generated mod ID into Mod.xml the first time it loads
the mod. With a symlink that write lands in your working copy, which is what you
want -- <ID> is meant to stay stable for the life of the mod, so commit it.
EOF2
