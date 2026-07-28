# Rust semantic and stress testing

## Scope

The suite covers Concurrent ID uniqueness, Concurrent overlap, Exclusive isolation, preemptive Exclusive behavior, upgrade/downgrade, multiple upgrade serialization, ContextID/EpochID conditional conversion, Try/timeouts, Scope and Pipeline unwind release, deterministic Segment semantics, randomized Pipeline composition, Exclusive contention progress, and final Idle-state validation.

## Quick verification

```bash
./build.sh
./run-tests.sh
```

On Windows:

```powershell
.\build.ps1
.\run-tests.ps1
```

The workspace builds with `--offline`; parking_lot and its required dependencies are under `vendor/`.

## Cargo tests

```bash
cargo test --release --workspace --offline
```

## Full semantics

```bash
./target/release/cel-test-and-benchmark \
  --full-semantics --lock-instances 2 \
  --semantic-workers 4 --semantic-operations 512 \
  --semantic-seed 0x6A09E667F3BCC909
```

The unwind checks intentionally panic inside Scope/Pipeline callbacks and catch the panic. The default panic hook may print the intentional message; the final `PASS` line is authoritative.

## Pipeline semantics

```bash
./target/release/cel-test-and-benchmark --pipeline-semantics
```

## Randomized Pipeline stress

```bash
./target/release/cel-test-and-benchmark \
  --pipeline-stress 30m --lock-instances 1 \
  --semantic-workers 4 --semantic-seed 0x6A09E667F3BCC909
```

Each round builds 3–10 random segments. The validator rejects overlapping Exclusive callbacks and any Exclusive/Concurrent overlap. The long run reports progress at least once per minute and must finish with both validator and lock Idle.

The formal 30-minute run passed with `2,732,232,429` randomized rounds and `14,775,380,351` validated callbacks.

## Exclusive contention progress

```bash
./target/release/cel-test-and-benchmark \
  --contention-stress 60s --semantic-workers 32
```

Every worker must make progress, Exclusive regions must not overlap, and the lock must finish Idle. The final 60-second run completed `401,719,852` acquisitions across 32 workers; the minimum per worker was `11,457,115`.

## Endurance

```bash
./target/release/cel-test-and-benchmark \
  --endurance 30m --lock-instances 2 --semantic-workers 4
```

The final 60-second Endurance run completed 58 deterministic batches.

## Release checklist

```text
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --offline --no-deps -- -D warnings
cargo test --release --workspace --offline
full semantics
Pipeline semantics
30-minute Pipeline stress
60-second Exclusive contention stress
benchmark state-hash equality
```

Raw logs are retained under `TestResults/final/`.
