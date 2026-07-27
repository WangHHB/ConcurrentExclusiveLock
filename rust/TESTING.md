# Rust Semantic and Stress Test Guide

## 1. Purpose

The test suite validates protocol correctness and long-running stability for `ConcurrentExclusiveLock`, `ConcurrentExclusiveLockScope`, and `ConcurrentExclusiveLockPipeline`.

It answers:

> After real multithreaded contention, upgrade, downgrade, Try failure, panic unwinding, and long execution, does the lock still obey the Concurrent/Exclusive permission protocol and remain reusable?

Throughput is not a pass condition for semantic tests. `observed_state()` and `observed_contention()` are observational snapshots only.

## 2. Quick run

From the `rust` directory:

```powershell
.\run-tests.ps1
```

Or run Cargo directly:

```powershell
cargo test --release --workspace
cargo run --release -p cel-test-and-benchmark -- --full-semantics
cargo run --release -p cel-test-and-benchmark -- --pipeline-semantics
```

## 3. Modes

### `cargo test --release --workspace`

Runs the core crate integration tests, including Concurrent/Exclusive isolation, conditional-upgrade single-winner behavior, automatic release on conditional failure, and Pipeline continuation after an ID condition fails.

### `--full-semantics`

Runs deterministic protocol checks followed by randomized legal call paths. It covers ordinary acquisition/release, preemptive Exclusive, upgrade, downgrade, ContextID/EpochID conditions, Scope cleanup, timeout APIs, snapshots, Pipeline transitions, and independent-lock isolation.

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --full-semantics `
  --lock-instances 8 `
  --semantic-workers 4 `
  --semantic-operations 1000 `
  --semantic-seed 0x6A09E667F3BCC909
```

### `--pipeline-semantics`

Runs fixed Pipeline combinations and verifies independent segments, convergence, business-ID conditions, Try failure, None continuation, and panic-path release.

### `--pipeline-stress <duration>`

Continuously generates randomized synchronous Segment combinations covering ordinary acquisition, Try, upgrade, downgrade, and business-ID convergence.

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 8 `
  --semantic-seed 0x123456789ABCDEF0
```

Supported durations include `500ms`, `30s`, `10m`, `24h`, `1d`, and `01:30:00`.

### `--contention-stress <duration>`

Uses many threads contending for Exclusive on one lock. It checks non-overlap, practical progress for every waiter during the test window, final Idle state, and reports min/max acquisitions per worker. It does not require strict FIFO.

### `--endurance <duration>`

Repeatedly runs deterministic contracts and short randomized Pipeline batches while reusing lock objects.

## 4. Core rules

Depending on the selected mode, the tests verify:

1. Exclusive never overlaps Concurrent or another Exclusive region.
2. Concurrent IDs stay in range.
3. New Concurrent entries stop after preemptive Exclusive pressure enters the window.
4. upgraded Exclusive regions remain serialized.
5. failed conditional upgrade automatically releases the original Concurrent permission.
6. downgrade leaves the caller holding Concurrent, not Exclusive.
7. Scope releases according to its final state on normal exit and panic unwind.
8. failed Try segments are skipped and later Pipeline segments continue from None.
9. the lock is reusable after every round.
10. independent locks do not share protocol state.

## 5. Reproduction

Preserve the mode, full command, seed, lock count, worker count, operation count, and panic output. Re-run with the same `--semantic-seed` to reproduce the same randomized call shape.

## 6. Recommended order

Routine changes:

```text
cargo test --release --workspace
→ --full-semantics
→ --pipeline-semantics
→ --pipeline-stress 10m
```

Before release, also run formatting, Clippy with warnings denied, larger semantic parameters, several hours of Pipeline stress, and contention stress. Major synchronization changes should receive 24-hour runs on Windows, Linux, and macOS.
