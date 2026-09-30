#!/usr/bin/env bash
#
# Compiles the mod without writing over the shipped assembly, as a check that the
# sources are still good. Run it after editing any .cs file.
#
#   ./tools/verify-build.sh
#
# This is tools/build.sh --check; see that script for how and why the mod is
# compiled with Besiege's own compiler.

exec "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/build.sh" --check
