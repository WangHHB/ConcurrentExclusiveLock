#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "Usage: $0 <dotnet> <TestAndBenchmark.dll> <matrix.tsv> <machine-id> <output-dir>" >&2
  exit 2
fi

DOTNET=$1
DLL=$2
MATRIX=$3
MACHINE_ID=$4
OUTPUT_DIR=$5
mkdir -p "$OUTPUT_DIR/logs"
JSONL="$OUTPUT_DIR/results.jsonl"
COMMANDS="$OUTPUT_DIR/commands.txt"
: > "$COMMANDS"

required() {
  local name=$1 value=$2
  if [[ -z "$value" || "$value" == "-" ]]; then
    echo "Matrix row '$ID' requires column '$name'." >&2
    exit 2
  fi
}

line_no=0
while IFS=$'\t' read -r ENABLED ID MODE LOCKS THREADS OPERATIONS CONCURRENT_PERMILLE WORKLOAD CONCURRENT_WORK EXCLUSIVE_WORK MEMORY_MB DICTIONARY_SIZE PAYLOAD_FRAMES LATENCY_SAMPLE_EVERY PREPARE_WORK COMMIT_WORK POST_WORK UPGRADE_N UPGRADE_M DURATION SEMANTIC_WORKERS SEMANTIC_OPERATIONS SEMANTIC_SEED ADVANCED_OPERATIONS ADVANCED_SEED PIPELINE_EXCEPTION_PERMILLE; do
  line_no=$((line_no + 1))
  [[ $line_no -eq 1 ]] && continue
  [[ -z "${ENABLED:-}" || "$ENABLED" == \#* || "$ENABLED" == "0" ]] && continue
  required id "$ID"
  required mode "$MODE"

  ARGS=()
  case "$MODE" in
    throughput|latency)
      required lock_instances "$LOCKS"; required threads "$THREADS"; required operations "$OPERATIONS"
      required concurrent_permille "$CONCURRENT_PERMILLE"; required workload "$WORKLOAD"
      required concurrent_work "$CONCURRENT_WORK"; required exclusive_work "$EXCLUSIVE_WORK"
      ARGS+=(--"$MODE" --lock-instances "$LOCKS" --threads "$THREADS" --operations "$OPERATIONS")
      ARGS+=(--concurrent-permille "$CONCURRENT_PERMILLE" --workload "$WORKLOAD")
      ARGS+=(--concurrent-work "$CONCURRENT_WORK" --exclusive-work "$EXCLUSIVE_WORK")
      case "$WORKLOAD" in
        cpu) ;;
        memory) required memory_mb "$MEMORY_MB"; ARGS+=(--memory-mb "$MEMORY_MB") ;;
        dictionary|ledger) required dictionary_size "$DICTIONARY_SIZE"; ARGS+=(--dictionary-size "$DICTIONARY_SIZE") ;;
        payload) required payload_frames "$PAYLOAD_FRAMES"; ARGS+=(--payload-frames "$PAYLOAD_FRAMES") ;;
        *) echo "Unknown workload '$WORKLOAD' in matrix row '$ID'." >&2; exit 2 ;;
      esac
      [[ "$MODE" == "latency" ]] && { required latency_sample_every "$LATENCY_SAMPLE_EVERY"; ARGS+=(--latency-sample-every "$LATENCY_SAMPLE_EVERY"); }
      ;;
    exclusive-progress)
      required lock_instances "$LOCKS"; required threads "$THREADS"; required operations "$OPERATIONS"
      required workload "$WORKLOAD"; required concurrent_work "$CONCURRENT_WORK"; required exclusive_work "$EXCLUSIVE_WORK"
      ARGS+=(--exclusive-progress --lock-instances "$LOCKS" --threads "$THREADS" --operations "$OPERATIONS")
      ARGS+=(--workload "$WORKLOAD" --concurrent-work "$CONCURRENT_WORK" --exclusive-work "$EXCLUSIVE_WORK")
      case "$WORKLOAD" in
        cpu) ;;
        memory) required memory_mb "$MEMORY_MB"; ARGS+=(--memory-mb "$MEMORY_MB") ;;
        dictionary|ledger) required dictionary_size "$DICTIONARY_SIZE"; ARGS+=(--dictionary-size "$DICTIONARY_SIZE") ;;
        payload) required payload_frames "$PAYLOAD_FRAMES"; ARGS+=(--payload-frames "$PAYLOAD_FRAMES") ;;
        *) echo "Unknown workload '$WORKLOAD' in matrix row '$ID'." >&2; exit 2 ;;
      esac
      ;;
    pipeline-perf)
      required lock_instances "$LOCKS"; required threads "$THREADS"; required operations "$OPERATIONS"
      required prepare_work "$PREPARE_WORK"; required commit_work "$COMMIT_WORK"; required post_work "$POST_WORK"
      ARGS+=(--pipeline-perf --lock-instances "$LOCKS" --threads "$THREADS" --operations "$OPERATIONS")
      ARGS+=(--prepare-work "$PREPARE_WORK" --commit-work "$COMMIT_WORK" --post-work "$POST_WORK")
      ;;
    upgrade-contention)
      required lock_instances "$LOCKS"; required upgrade_n "$UPGRADE_N"; required upgrade_m "$UPGRADE_M"
      ARGS+=(--upgrade-contention "$UPGRADE_N" "$UPGRADE_M" --lock-instances "$LOCKS")
      ;;
    correctness)
      required lock_instances "$LOCKS"; required semantic_workers "$SEMANTIC_WORKERS"; required semantic_operations "$SEMANTIC_OPERATIONS"
      required advanced_operations "$ADVANCED_OPERATIONS"
      ARGS+=(--correctness --lock-instances "$LOCKS" --semantic-workers "$SEMANTIC_WORKERS")
      ARGS+=(--semantic-operations "$SEMANTIC_OPERATIONS" --advanced-operations "$ADVANCED_OPERATIONS")
      [[ "$SEMANTIC_SEED" != "-" ]] && ARGS+=(--semantic-seed "$SEMANTIC_SEED")
      [[ "$ADVANCED_SEED" != "-" ]] && ARGS+=(--advanced-seed "$ADVANCED_SEED")
      [[ "$PIPELINE_EXCEPTION_PERMILLE" != "-" ]] && ARGS+=(--pipeline-exception-permille "$PIPELINE_EXCEPTION_PERMILLE")
      ;;
    pipeline-stress|endurance)
      required duration "$DURATION"; required lock_instances "$LOCKS"; required semantic_workers "$SEMANTIC_WORKERS"; required semantic_operations "$SEMANTIC_OPERATIONS"
      ARGS+=(--"$MODE" "$DURATION" --lock-instances "$LOCKS" --semantic-workers "$SEMANTIC_WORKERS" --semantic-operations "$SEMANTIC_OPERATIONS")
      [[ "$SEMANTIC_SEED" != "-" ]] && ARGS+=(--semantic-seed "$SEMANTIC_SEED")
      if [[ "$MODE" == "pipeline-stress" ]]; then
        required pipeline_exception_permille "$PIPELINE_EXCEPTION_PERMILLE"
        ARGS+=(--pipeline-exception-permille "$PIPELINE_EXCEPTION_PERMILLE")
      fi
      ;;
    contention-diagnostic)
      required duration "$DURATION"; required threads "$THREADS"
      ARGS+=(--contention-diagnostic "$DURATION" --threads "$THREADS")
      ;;
    *) echo "Unknown mode '$MODE' in matrix row '$ID'." >&2; exit 2 ;;
  esac

  CMD=("$DOTNET" "$DLL" "${ARGS[@]}" --machine-id "$MACHINE_ID" --experiment-id "$ID" --output "$JSONL")
  printf '%q ' "${CMD[@]}" | tee -a "$COMMANDS"
  printf '\n' | tee -a "$COMMANDS"
  "${CMD[@]}" 2>&1 | tee "$OUTPUT_DIR/logs/$ID.log"
done < "$MATRIX"
