#!/usr/bin/env bash
#
# Runs the sand bed's tests headlessly, on Besiege's own Mono, with no game and no display:
#
#   ./tools/test-bed.sh
#
# Seven programs. BedUnitTest exercises SandBed alone: conserving volume and slumping to the
# angle of repose. BedPressTest does the bed as ground for machines: pressing ruts,
# ploughing, blasting, undo, saving and loading. BedLayerTest paints several media on and
# round one another and checks the layers, volumes and packing. BedThreadTest reads the bed
# from six threads at once and compares with one. ChunkMesherTest checks that the light does
# not change across a chunk's seam and that the mesher gives the same on several threads as
# on one. BrushShapeTest checks every brush style's row evaluator against its one-point
# weight, the brush sweep, pouring on several threads, and piles grown in place and settled.
# GuardTest checks the one thing every entry point that takes numbers from outside -- a
# message from another player, a typed value, a settings file -- has to ask first.
# None need the engine, which is why SandBed reaches the ground and the shapes through
# small interfaces.
#
# Set BESIEGE_DIR if the install is not auto-detected.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$REPO_DIR/Sand/Scripts"
WORK="${TMPDIR:-/tmp}/besiege-se-build"
mkdir -p "$WORK"

find_besiege() {
    if [[ -n "${BESIEGE_DIR:-}" ]]; then echo "$BESIEGE_DIR"; return; fi
    local c
    for c in "$HOME/.steam/steam/steamapps/common/Besiege" \
             "$HOME/.local/share/Steam/steamapps/common/Besiege"; do
        [[ -f "$c/Besiege_Data/Managed/mcs.dll" ]] && { echo "$c"; return; }
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

REFS=(-lib:"$MANAGED" -r:UnityEngine.dll -r:UnityEngine.UI.dll -r:Assembly-CSharp.dll
      -r:Assembly-CSharp-firstpass.dll -r:System.dll -r:System.Core.dll)

run() {   # $1 = name, rest = sources
    local name="$1"; shift
    "$WORK/besiegecc" -target:exe -out:"$WORK/$name.exe" "${REFS[@]}" "$@" >/dev/null
    TARGET_ASM="$WORK/$name.exe" timeout 600 "$WORK/monohost"
}

echo "== SandBed on its own"
run BedUnitTest "$REPO_DIR/tools/tests/BedUnitTest.cs" \
    "$SRC/SandBed.cs" "$SRC/Workers.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

echo
echo "== SandBed as ground for machines: pressing and ploughing"
run BedPressTest "$REPO_DIR/tools/tests/BedPressTest.cs" \
    "$SRC/SandBed.cs" "$SRC/Workers.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

echo
echo "== Several media in layers"
run BedLayerTest "$REPO_DIR/tools/tests/BedLayerTest.cs" \
    "$SRC/SandBed.cs" "$SRC/Workers.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

echo
echo "== The bed read from several threads at once"
run BedThreadTest "$REPO_DIR/tools/tests/BedThreadTest.cs" \
    "$SRC/SandBed.cs" "$SRC/Workers.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

echo
echo "== The chunk mesher: seams and threads"
run ChunkMesherTest "$REPO_DIR/tools/tests/ChunkMesherTest.cs" \
    "$SRC/ChunkMesher.cs" "$SRC/SandBed.cs" "$SRC/Workers.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

echo
echo "== Numbers that came from outside"
run GuardTest "$REPO_DIR/tools/tests/GuardTest.cs" \
    "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

echo
echo "== Brush shapes and the budgeted sweep"
run BrushShapeTest "$REPO_DIR/tools/tests/BrushShapeTest.cs" \
    "$SRC/BrushShape.cs" "$SRC/SandBed.cs" "$SRC/Workers.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

# Not part of the suites: it measures, it does not check. Run it with
#   ./tools/test-bed.sh --bench
# to see where the simulation's time goes, before and after a change to it.
if [[ "${1:-}" == "--bench" ]]; then
    echo
    echo "== Where the time goes: a machine parked and driving on sand"
    run PerfBench "$REPO_DIR/tools/tests/PerfBench.cs" \
        "$SRC/SandBed.cs" "$SRC/Workers.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"
fi
