#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"

MARKER="vendor/parking_lot/Cargo.toml"
ARCHIVE="parking_lot-vendor.zip"

if [ -f "$MARKER" ]; then
    exit 0
fi

if [ ! -f "$ARCHIVE" ]; then
    printf '%s\n' "Missing $ARCHIVE" >&2
    exit 1
fi

rm -rf vendor
if command -v unzip >/dev/null 2>&1; then
    unzip -q "$ARCHIVE" -d .
elif command -v python3 >/dev/null 2>&1; then
    python3 -m zipfile -e "$ARCHIVE" .
elif command -v python >/dev/null 2>&1; then
    python -m zipfile -e "$ARCHIVE" .
else
    printf '%s\n' "Extract $ARCHIVE into this directory before building (unzip or Python is required)." >&2
    exit 1
fi

if [ ! -f "$MARKER" ]; then
    printf '%s\n' "Vendor extraction failed: $MARKER was not created." >&2
    exit 1
fi
