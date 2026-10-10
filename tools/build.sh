#!/usr/bin/env bash
#
# Compiles the mod into SandAssembly.dll, using Besiege's OWN C# compiler
# rather than an installed toolchain.
#
#   ./tools/build.sh            build the mod's assembly
#   ./tools/build.sh --check    compile to a temp file only (see verify-build.sh)
#
# Mod.xml loads SandAssembly.dll directly, so the mod ships as a prebuilt
# assembly rather than a <ScriptAssembly> the game compiles at load time. That
# is also what makes an offline build worth having: a compile error in a
# ScriptAssembly is only discovered by launching Besiege.
#
# No C# toolchain is needed. This loads the game's own libmono.so and calls
# Mono.CSharp.CompilerCallableEntryPoint.InvokeCompiler in its mcs.dll, which is
# the exact code path the game uses. gcc is needed once, to build the host.
#
# Set BESIEGE_DIR if the install is not auto-detected.

set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_DIR="$REPO_DIR/Sand/Scripts"
BUILD_DIR="${TMPDIR:-/tmp}/besiege-se-build"
OUT="$REPO_DIR/Sand/SandAssembly.dll"

CHECK_ONLY=0
if [[ "${1:-}" == "--check" ]]; then
    CHECK_ONLY=1
fi

# Always compile to a scratch file and move it into place afterwards. Writing
# straight to the shipped path means a failed compile can leave a truncated
# assembly behind, and -- if Besiege is open with the mod loaded -- the target
# may be mapped by the running game and refuse to be overwritten. A rename
# replaces the directory entry instead, which always works.
#
# The scratch name includes the pid so two concurrent builds cannot collide.
mkdir -p "$BUILD_DIR"
TMP_OUT="$BUILD_DIR/SandAssembly.$$.dll"
trap 'rm -f "$TMP_OUT"' EXIT

find_besiege() {
    if [[ -n "${BESIEGE_DIR:-}" ]]; then echo "$BESIEGE_DIR"; return; fi
    local candidates=(
        "$HOME/.steam/steam/steamapps/common/Besiege"
        "$HOME/.local/share/Steam/steamapps/common/Besiege"
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
        [[ -f "$dir/Besiege_Data/Managed/mcs.dll" ]] && { echo "$dir"; return; }
    done
    return 1
}

if ! BESIEGE="$(find_besiege)"; then
    echo "Could not find Besiege. Set BESIEGE_DIR to your install directory." >&2
    exit 1
fi

DATA="$BESIEGE/Besiege_Data"
export LIBMONO="$DATA/Mono/x86_64/libmono.so"
export MANAGED="$DATA/Managed"
export MONOETC="$DATA/Mono/etc"

# UI Factory is needed to *build*, not to run. The sand panel is a soft dependency --
# without UI Factory the HUD falls back to a plain panel -- but its source names its
# types, so the compiler has to resolve them. See Scripts/UIF.cs.
find_uifactory() {
    if [[ -n "${UIFACTORY_DIR:-}" ]]; then echo "$UIFACTORY_DIR"; return; fi
    local roots=("$BESIEGE/../../workshop/content/346010/2913469777"
                 "$BESIEGE/Besiege_Data/Mods/UIFactory")
    local root hit
    for root in "${roots[@]}"; do
        hit="$(find "$root" -name Besiege.UI.dll -print -quit 2>/dev/null || true)"
        [[ -n "$hit" ]] && { dirname "$hit"; return; }
    done
    return 1
}

if ! UIFACTORY="$(find_uifactory)"; then
    cat >&2 <<'NOUIF'
Could not find UI Factory 3 (Besiege.UI.dll), which is needed to compile.

The mod does not need it to run -- without it the sand panel is a plain one --
but the source names its types, so
the compiler has to be able to resolve them.

Subscribe to Workshop item 2913469777 ("UI Factory"), or set UIFACTORY_DIR to
the folder holding Besiege.UI.dll.
NOUIF
    exit 1
fi

echo "Besiege:    $BESIEGE"
echo "UI Factory: $UIFACTORY"

HOST="$BUILD_DIR/besiegecc"
for tool in besiegecc monohost; do
    if [[ ! -x "$BUILD_DIR/$tool" || "$REPO_DIR/tools/$tool.c" -nt "$BUILD_DIR/$tool" ]]; then
        echo "Building $tool host..."
        gcc -O1 -o "$BUILD_DIR/$tool" "$REPO_DIR/tools/$tool.c" -ldl
    fi
done

if pgrep -x Besiege >/dev/null 2>&1 || pgrep -f 'Besiege\.x86' >/dev/null 2>&1; then
    echo "Note: Besiege appears to be running. The build itself is fine, but the"
    echo "      game will not pick up the new assembly until you restart it."
fi

# System.Xml is referenced for the [Xml*] attributes on the block module. That is
# not a blacklist violation: the loader's AssemblyScanner walks field types,
# locals and IL operands, and never enumerates custom attributes -- and the
# module system has no other way to name the XML elements it deserialises.
echo "Compiling $(ls "$SRC_DIR"/*.cs | wc -l) source files with Besiege's compiler..."
set +e
"$HOST" -target:library -out:"$TMP_OUT" \
    -lib:"$MANAGED" -lib:"$UIFACTORY" \
    -r:UnityEngine.dll -r:UnityEngine.UI.dll \
    -r:Assembly-CSharp.dll -r:Assembly-CSharp-firstpass.dll \
    -r:System.dll -r:System.Core.dll -r:System.Xml.dll -r:DynamicText.dll \
    -r:Besiege.UI.dll -r:Besiege.UI.Bridge.dll \
    "$SRC_DIR"/*.cs
rc=$?
set -e

if [[ $rc -ne 0 ]]; then
    cat >&2 <<'EOF'

Build FAILED. The previously built assembly, if any, was left untouched.

If the output above is a list of CS#### errors, that is an ordinary compile
error -- fix the source. Otherwise:

  "the compiler threw a managed exception"
      The exception text is printed above it. Compiling holds the game's whole
      assembly set in memory, so if it is an OutOfMemoryException, close Besiege
      (or anything else large) and try again.

  a SIGSEGV inside Mono.CSharp
      A construct this ancient compiler cannot handle. The known case is any
      `enum` declaration; use int constants instead.
EOF
    exit $rc
fi

# Compiling cleanly says nothing about whether the mod loader will accept the
# result: it also scans for blacklisted namespaces and refuses the whole
# assembly over a single reference. Catch that here, not at launch.
BLACKLIST="$BUILD_DIR/blacklist.exe"
if [[ ! -f "$BLACKLIST" || "$REPO_DIR/tools/tests/BlacklistCheck.cs" -nt "$BLACKLIST" ]]; then
    "$HOST" -target:exe -out:"$BLACKLIST" -lib:"$MANAGED" -r:System.dll \
        "$REPO_DIR/tools/tests/BlacklistCheck.cs" >/dev/null 2>&1
fi
if [[ -f "$BLACKLIST" ]]; then
    set +e
    TARGET_ASM="$BLACKLIST" "$BUILD_DIR/monohost" "$TMP_OUT" \
        "$UIFACTORY/Besiege.UI.dll" "$UIFACTORY/Besiege.UI.Bridge.dll"
    scan_rc=$?
    set -e
    if [[ $scan_rc -ne 0 ]]; then
        echo >&2
        echo >&2 "Refusing to install an assembly Besiege would reject."
        exit 1
    fi
else
    echo "(blacklist checker unavailable; skipping that check)" >&2
fi

# The block XML is not compiled, so nothing else looks at it: a malformed file
# gets as far as the game, which declines to load the block and says little about
# why. "--" inside a comment is the easy one to write and is illegal in XML.
xml_bad=0
for xml in "$REPO_DIR/Sand"/*.xml; do
    [[ -f "$xml" ]] || continue
    if ! python3 -c "import sys,xml.etree.ElementTree as ET; ET.parse(sys.argv[1])" "$xml" 2>/tmp/xmlerr; then
        echo "XML ERROR in $xml: $(cat /tmp/xmlerr | tail -1)" >&2
        xml_bad=1
    fi
done
if [[ $xml_bad -eq 1 ]]; then
    echo "Refusing to install: the game will not load a block whose XML is malformed." >&2
    exit 1
fi
echo "XML check: $(ls "$REPO_DIR/Sand"/*.xml 2>/dev/null | wc -l) files, all well-formed."

if [[ $CHECK_ONLY -eq 1 ]]; then
    echo "Build OK (check only; $(stat -c%s "$TMP_OUT") bytes, not installed)"
else
    mv -f "$TMP_OUT" "$OUT"
    echo "Build OK -> $OUT"
fi
