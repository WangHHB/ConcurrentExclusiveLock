#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")"
./prepare-vendor.sh
B=./target/release/cel-test-and-benchmark

cargo test --release --workspace --offline
"$B" --full-semantics --lock-instances 2 --semantic-workers 4 --semantic-operations 512 --semantic-seed 0x6A09E667F3BCC909
"$B" --pipeline-semantics
"$B" --pipeline-stress 30s --lock-instances 1 --semantic-workers 4 --semantic-seed 0x6A09E667F3BCC909
"$B" --contention-stress 30s --semantic-workers 16
