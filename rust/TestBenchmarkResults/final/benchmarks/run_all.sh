#!/usr/bin/env bash
set -euo pipefail
P="$(cd "$(dirname "$0")/../../.." && pwd)"
B="$P/target/release/cel-test-and-benchmark"
D="$P/TestResults/final/benchmarks"
run() {
  local name="$1"; shift
  echo "START $name $(date -u +%FT%TZ)" | tee -a "$D/progress.log"
  "$B" "$@" > "$D/$name.log" 2>&1
  echo "PASS  $name $(date -u +%FT%TZ)" | tee -a "$D/progress.log"
}
: > "$D/progress.log"
run single_1t_w64 --lock-instances 1 --threads 1 --workload memory --operations 100000 --memory-mb 64 --read-work 64 --write-work 64
run single_4t_w64 --lock-instances 1 --threads 4 --workload memory --operations 30000 --memory-mb 64 --read-work 64 --write-work 64
run single_16t_w64_r1 --lock-instances 1 --threads 16 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 64
run single_16t_w64_r2 --lock-instances 1 --threads 16 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 64
run single_16t_w64_r3 --lock-instances 1 --threads 16 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 64
run single_64t_w64 --lock-instances 1 --threads 64 --workload memory --operations 3000 --memory-mb 64 --read-work 64 --write-work 64
run single_16t_w1 --lock-instances 1 --threads 16 --workload memory --operations 50000 --memory-mb 64 --read-work 1 --write-work 1
run single_16t_w256 --lock-instances 1 --threads 16 --workload memory --operations 3000 --memory-mb 64 --read-work 256 --write-work 256
run multi_8x4_w64 --lock-instances 8 --threads 4 --workload memory --operations 5000 --memory-mb 16 --read-work 64 --write-work 64
run multi_64x2_w64 --lock-instances 64 --threads 2 --workload memory --operations 2000 --memory-mb 4 --read-work 64 --write-work 64
echo "ALL_PASS $(date -u +%FT%TZ)" | tee -a "$D/progress.log"
