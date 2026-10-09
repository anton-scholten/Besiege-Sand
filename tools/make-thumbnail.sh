#!/usr/bin/env bash
#
# Copies the mod's Workshop thumbnail into the mod folder: Sand/Resources/Thumbnail.png.
#
#   ./tools/make-thumbnail.sh            # -> Sand/Resources/Thumbnail.png, 768px square
#   ./tools/make-thumbnail.sh 512        # ... at another size
#
# The source is Thumbnail_2.png in the repository root -- the artwork, next to the Thumbnail_2.xcf
# it is made in -- and the copy is what the mod ships and what Steam is sent. This is the same
# arrangement the Node Editor mod uses: artwork at the top of the repository, and a copy under
# <Mod>/Resources declared in Mod.xml as the mod's Icon.
#
# It has to be a copy because the game cannot open an image from disk: the mod loader refuses
# System.IO, and a mod's pictures reach it as declared Resources instead.
#
# The copy is smaller than the artwork, and not only to save space. Steam caps a Workshop
# preview at 1024 KiB and refuses an item over it with nothing said about which file, which
# would break every upload of the mod while the game itself loads it perfectly well; the
# artwork in the repository root is over that cap as it stands, so copying it verbatim would
# do exactly that. 768 is what the Node Editor ships, and it leaves plenty of room under the
# cap. The size is checked below rather than left to be discovered on an upload.
#
# Needs ImageMagick's `convert`, which the icon generator does not.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

SRC="$REPO_DIR/Thumbnail_2.png"
OUT="$REPO_DIR/Sand/Resources/Thumbnail.png"
PX="${1:-768}"

[[ -f "$SRC" ]] || { echo "No $SRC to copy." >&2; exit 1; }
command -v convert >/dev/null 2>&1 || { echo "Needs ImageMagick's 'convert'." >&2; exit 1; }

mkdir -p "$(dirname "$OUT")"
convert "$SRC" -auto-orient -resize "${PX}x${PX}" -strip "$OUT"

CAP=$((1024 * 1024))
SIZE=$(stat -c%s "$OUT")
if (( SIZE > CAP )); then
    echo "$OUT is ${SIZE} bytes, over Steam's ${CAP} byte Workshop preview cap." >&2
    echo "Every upload of the mod will be refused until it is smaller. Try a lower size." >&2
    exit 1
fi
echo "Wrote $OUT (${PX}x${PX}, ${SIZE} bytes, under Steam's ${CAP} byte cap)."