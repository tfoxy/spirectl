#!/usr/bin/env bash
set -euo pipefail

root_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
build_dir="${SPI_NATIVE_OBSERVER_BUILD_DIR:-/tmp/spirectl-native-observer-build}"
cmake -S "$root_dir" -B "$build_dir" -DCMAKE_BUILD_TYPE=RelWithDebInfo
cmake --build "$build_dir" --parallel
ctest --test-dir "$build_dir" --output-on-failure

if [[ -n "${SPI_NATIVE_OBSERVER_PREFLIGHT_EXE:-}" ]]; then
  "$build_dir/native_observer_preflight" "$SPI_NATIVE_OBSERVER_PREFLIGHT_EXE" \
    "${SPI_NATIVE_OBSERVER_PREFLIGHT_SHA256:?missing expected SHA256}" \
    "${SPI_NATIVE_OBSERVER_PREFLIGHT_BUILD_ID:?missing expected build ID}"
fi

if [[ -n "${GODOT_BIN:-}" ]]; then
  descriptor="$build_dir/spirectl_native_observer.gdextension"
  marker="$build_dir/gdextension-loaded.txt"
  library="$build_dir/libspirectl_native_observer.so"
  cat >"$descriptor" <<EOF
[configuration]
entry_symbol = "spirectl_native_observer_init"
compatibility_minimum = 4.5

[libraries]
linux.debug.x86_64 = "$library"
linux.release.x86_64 = "$library"
EOF
  rm -f "$marker"
  SPI_NATIVE_OBSERVER_DESCRIPTOR="$descriptor" \
    SPI_NATIVE_OBSERVER_LOAD_MARKER="$marker" \
    "$GODOT_BIN" --headless --path "$root_dir/probe" --script "$root_dir/probe/load_extension.gd"
  test "$(cat "$marker")" = loaded
fi
