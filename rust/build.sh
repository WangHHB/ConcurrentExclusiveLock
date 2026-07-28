#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"

cargo fmt --all
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --offline --no-deps -- -D warnings
cargo build --release --workspace --offline
cargo test --release --workspace --offline

mkdir -p Artifacts/linux-x64
cp target/release/cel-test-and-benchmark Artifacts/linux-x64/
printf '%s\n' "Build completed: Artifacts/linux-x64/cel-test-and-benchmark"
