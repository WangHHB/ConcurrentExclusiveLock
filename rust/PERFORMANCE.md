# Rust Performance Test Guide

## 1. Purpose

The benchmark compares the same shared-memory Work under:

- `std::sync::Mutex`;
- `std::sync::RwLock`;
- `ConcurrentExclusiveLock` (CEL);
- `CEL(ExclusiveOnly)`, which routes reads and writes through Exclusive to expose the monitor slow path.

It is not designed to prove universal superiority. It measures the throughput/write-completion trade-off under read-dominant, balanced, and write-heavy workloads, including multi-entity lock topologies and nontrivial critical-section Work.

## 2. Work model

Each lock owns an independent `MemoryWork` with a configurable `i64` array. Concurrent operations use worker-local random state to read random locations and mix 64-bit values. Exclusive operations use shared random state to update random locations and advance a final state hash. Every strategy receives the same operation counts and Work parameters, and fresh lock/Work instances are created for each strategy and scenario.

This is based on the C# project's random shared-memory workload. It is intentionally more representative than an empty region or a single integer increment.

## 3. Scenarios

The standard run executes `100/0`, `99.5/0.5`, `90/10`, `50/50`, `30/70`, and `0/100` read/write ratios.

The 99.5/0.5 and 90/10 scenarios are especially useful for examining read-dominant throughput together with writer completion. The write-heavy scenarios show convergence toward mutex-like execution rather than a Concurrent advantage.

## 4. Release build

Always use Release:

```powershell
cargo build --release --workspace
```

Record CPU, OS, Rust version/target, power policy, NUMA topology, complete command, and raw output. Avoid drawing conclusions from Debug builds.

## 5. Commands

Single hot lock:

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 1 `
  --threads 32 `
  --operations 100000 `
  --workload memory `
  --memory-mb 64 `
  --read-work 256 `
  --write-work 256
```

Eight entity locks with eight workers per lock:

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 8 `
  --threads 8 `
  --operations 100000 `
  --workload memory `
  --memory-mb 64 `
  --read-work 256 `
  --write-work 256
```

`--memory-mb` is per lock instance.

## 6. Work sizes

Very short critical regions mostly measure synchronization overhead and may not expose the benefit of Concurrent execution. Keep complete results for at least:

```text
Work 64
Work 256
Work 640
```

Example heavy Work:

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 8 `
  --threads 8 `
  --operations 100000 `
  --memory-mb 64 `
  --read-work 640 `
  --write-work 640
```

## 7. Metrics

- `elapsed`: wall-clock time for the strategy/scenario.
- `works/s`: aggregate throughput.
- `works/s/lock`: average throughput per entity lock.
- `avg write ns`: average end-to-end write operation time, including permission waiting and Exclusive Work.
- `reads` / `writes`: actual operation counts.
- `state`: final business-state hash; all strategies must match.

## 8. Interpretation boundary

Conclusions should remain tied to the tested machine and parameters. Platform `RwLock` policy, CPU/NUMA topology, scheduler behavior, cache state, critical-section size, and write ratio can materially change results. High write ratios naturally converge toward mutually exclusive execution, while very short regions may favor simpler primitives.

The Rust RawMonitor also has different implementation details from .NET Monitor and Java ReentrantLock, so cross-language numbers should be reported as separate platform results rather than direct language rankings.
