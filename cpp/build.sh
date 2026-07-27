#!/usr/bin/env sh
set -eu

BUILD_DIR="${1:-build}"
BUILD_TYPE="${BUILD_TYPE:-Release}"
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

cd "$SCRIPT_DIR"
cmake -S . -B "$BUILD_DIR" -DCMAKE_BUILD_TYPE="$BUILD_TYPE"
cmake --build "$BUILD_DIR" --config "$BUILD_TYPE"
ctest --test-dir "$BUILD_DIR" -C "$BUILD_TYPE" --output-on-failure
