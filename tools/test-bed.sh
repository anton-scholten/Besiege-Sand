#!/usr/bin/env bash
#
# Runs the sleeping-sand bed's tests headlessly, on Besiege's own Mono, with no game
# and no display:
#
#   ./tools/test-bed.sh            # the quick suites
#   ./tools/test-bed.sh --load     # ... and a 40 second closed-loop pour (about a minute)
#
# Five programs. BedUnitTest exercises SandBed alone: absorbing grains, conserving
# volume, slumping to the angle of repose, releasing sand around a shape and stopping
# when the field is full. BedSolverTest drives the real CpuSolver together with the bed:
# a few thousand grains dropped on flat ground must settle into the bed without losing
# volume, and a sphere pushed into the finished pile must wake a patch that then settles
# back. SolverShapeTest drops sand on a movable box and checks that it loads it with its
# own weight, spins a wheel in sand and checks it grips, and drags a box through sand and
# checks it is held back. SolverPhysicsTest checks the sand as a material: it holds a
# stack, stops sliding, makes a heap, collapses a block into a heap without throwing it,
# settles without hopping, and does not sink into the ground or through a thin plate.
# GovernorTest checks the time-budget governor and the coarse grain sizes.
# None need the engine, which is why SandBed reaches the ground, the shapes
# and the field through small interfaces.
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
    "$SRC/SandBed.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

echo
echo "== SandBed as ground for machines: digging and pressing"
run BedPressTest "$REPO_DIR/tools/tests/BedPressTest.cs" \
    "$SRC/SandBed.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

echo
echo "== SandBed with the real CPU solver"
run BedSolverTest "$REPO_DIR/tools/tests/BedSolverTest.cs" \
    "$SRC/SandBed.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs" "$SRC/CpuSolver.cs" \
    "$SRC/ISandSolver.cs" "$SRC/GroundField.cs" "$SRC/WaterLink.cs" "$SRC/ShapeMath.cs"

echo
echo "== CPU solver against a movable collider"
run SolverShapeTest "$REPO_DIR/tools/tests/SolverShapeTest.cs" \
    "$SRC/SandBed.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs" "$SRC/CpuSolver.cs" \
    "$SRC/ISandSolver.cs" "$SRC/GroundField.cs" "$SRC/WaterLink.cs" "$SRC/ShapeMath.cs"

echo
echo "== Sand as a material"
run SolverPhysicsTest "$REPO_DIR/tools/tests/SolverPhysicsTest.cs" \
    "$SRC/SandBed.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs" "$SRC/CpuSolver.cs" \
    "$SRC/ISandSolver.cs" "$SRC/GroundField.cs" "$SRC/WaterLink.cs" "$SRC/ShapeMath.cs"

echo
echo "== Governor and coarse grains"
run GovernorTest "$REPO_DIR/tools/tests/GovernorTest.cs" \
    "$SRC/SandGovernor.cs" "$SRC/SandBed.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs"

if [[ "${1:-}" == "--load" ]]; then
    echo
    echo "== Closed-loop pour: solver, bed and governor together"
    run LoadTest "$REPO_DIR/tools/tests/LoadTest.cs" \
        "$SRC/SandGovernor.cs" "$SRC/SandBed.cs" "$SRC/BedCodec.cs" "$SRC/Bytes.cs" "$SRC/SandTypes.cs" "$SRC/SandMaterials.cs" \
        "$SRC/CpuSolver.cs" "$SRC/ISandSolver.cs" "$SRC/GroundField.cs" "$SRC/WaterLink.cs" \
        "$SRC/ShapeMath.cs"
fi
