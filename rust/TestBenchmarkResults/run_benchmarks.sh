#!/usr/bin/env bash
set -u
ROOT=/mnt/data/rust_validation/rust_std_mutex_aligned
OUT=/mnt/data/rust_validation/extended_validation_20260728/benchmarks
BIN="$ROOT/target/release/cel-test-and-benchmark"
mkdir -p "$OUT"
run_one() {
  local name="$1"; shift
  echo "[$(date -Is)] START $name $*" | tee -a "$OUT/progress.log"
  if timeout 900s "$BIN" "$@" > "$OUT/$name.log" 2>&1; then
    echo "[$(date -Is)] PASS $name" | tee -a "$OUT/progress.log"
  else
    rc=$?
    echo "[$(date -Is)] FAIL rc=$rc $name" | tee -a "$OUT/progress.log"
  fi
}
run_one single_1t_w64 --lock-instances 1 --threads 1 --workload memory --operations 30000 --memory-mb 64 --read-work 64 --write-work 64
run_one single_4t_w64 --lock-instances 1 --threads 4 --workload memory --operations 20000 --memory-mb 64 --read-work 64 --write-work 64
run_one single_16t_w64_r1 --lock-instances 1 --threads 16 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 64
run_one single_16t_w64_r2 --lock-instances 1 --threads 16 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 64
run_one single_16t_w64_r3 --lock-instances 1 --threads 16 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 64
run_one single_64t_w64 --lock-instances 1 --threads 64 --workload memory --operations 3000 --memory-mb 64 --read-work 64 --write-work 64
run_one single_16t_w1 --lock-instances 1 --threads 16 --workload memory --operations 20000 --memory-mb 64 --read-work 1 --write-work 1
run_one single_16t_w256 --lock-instances 1 --threads 16 --workload memory --operations 3000 --memory-mb 64 --read-work 256 --write-work 256
run_one multi_8x4_w64 --lock-instances 8 --threads 4 --workload memory --operations 5000 --memory-mb 16 --read-work 64 --write-work 64
run_one multi_64x2_w64 --lock-instances 64 --threads 2 --workload memory --operations 1500 --memory-mb 2 --read-work 64 --write-work 64
echo "[$(date -Is)] ALL DONE" | tee -a "$OUT/progress.log"
