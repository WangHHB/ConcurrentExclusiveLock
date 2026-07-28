#!/usr/bin/env bash
set -euo pipefail
P=$(cd "$(dirname "$0")/../.." && pwd)
F="$P/TestBenchmarkResults/final"
export PATH=/opt/rust-1.75/bin:$PATH
export RUST_BACKTRACE=0
cd "$P"

run_log() {
  local name=$1
  shift
  printf '\n=== %s ===\n' "$name"
  "$@" >"$F/$name.log" 2>&1
  printf 'PASS %s\n' "$name"
}

run_log cargo-fmt cargo fmt --all -- --check
run_log cargo-clippy cargo clippy --workspace --all-targets --offline --no-deps -- -D warnings
run_log cargo-check cargo check --workspace --offline
run_log cargo-build-release cargo build --release --workspace --offline
run_log cargo-test-release cargo test --release --workspace --offline
run_log cargo-package-core cargo package -p concurrent-exclusive-lock --allow-dirty --offline
run_log cargo-metadata cargo metadata --offline --format-version 1

EMPTY_CARGO_HOME=$(mktemp -d)
EMPTY_TARGET=$(mktemp -d)
if env CARGO_HOME="$EMPTY_CARGO_HOME" cargo build --release --workspace --offline --target-dir "$EMPTY_TARGET" >"$F/clean-empty-cargo-home-build.log" 2>&1; then
  printf 'PASS clean-empty-cargo-home-build\n'
else
  status=$?
  rm -rf "$EMPTY_CARGO_HOME" "$EMPTY_TARGET"
  exit "$status"
fi
rm -rf "$EMPTY_CARGO_HOME" "$EMPTY_TARGET"
run_log full-semantics ./target/release/cel-test-and-benchmark \
  --full-semantics --lock-instances 2 --semantic-workers 4 \
  --semantic-operations 512 --semantic-seed 0x6A09E667F3BCC909
run_log pipeline-semantics ./target/release/cel-test-and-benchmark --pipeline-semantics
run_log contention-stress-60s ./target/release/cel-test-and-benchmark \
  --contention-stress 60s --semantic-workers 32
run_log endurance-60s ./target/release/cel-test-and-benchmark \
  --endurance 60s --lock-instances 2 --semantic-workers 4 \
  --semantic-seed 0x6A09E667F3BCC909

mkdir -p Artifacts/linux-x64 Artifacts/windows-x64 Artifacts/crate
cp target/release/cel-test-and-benchmark Artifacts/linux-x64/cel-test-and-benchmark
chmod +x Artifacts/linux-x64/cel-test-and-benchmark
cp target/package/concurrent-exclusive-lock-1.0.0.crate Artifacts/crate/
sha256sum Artifacts/crate/concurrent-exclusive-lock-1.0.0.crate > Artifacts/crate/concurrent-exclusive-lock-1.0.0.crate.sha256
{
  rustc --version
  cargo --version
  file Artifacts/linux-x64/cel-test-and-benchmark
  ldd Artifacts/linux-x64/cel-test-and-benchmark || true
  echo '--- help ---'
  Artifacts/linux-x64/cel-test-and-benchmark --help
} >"$F/linux-artifact-inspection.log" 2>&1
sha256sum Artifacts/linux-x64/cel-test-and-benchmark > Artifacts/linux-x64/cel-test-and-benchmark.sha256
printf 'ALL FINAL VALIDATIONS PASS\n'
