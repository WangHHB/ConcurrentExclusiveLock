# Advanced Perf Test Plan

Date: 2026-07-22

## Plan

Run dedicated worker threads against the advanced semantic paths of ConcurrentExclusiveLockScope.

Covered cases:

- Concurrent enter / release.
- Exclusive enter / release.
- ExclusiveToConcurrent in-place downgrade.
- ConcurrentToExclusive in-place upgrade.
- Concurrent plus downgrade path, split 50/50.
- Exclusive plus upgrade path, split 50/50.

Each semantic loop executes exactly one shared `BenchmarkWork` payload. Each printed case uses a fresh lock instance.

The workload parameters are shared with the steady-state benchmark:

- `--workload`: `cpu`, `memory`, `dictionary`, `ledger`, or `payload`.
- `--work`: sets both ConcurrentWork and ExclusiveWork.
- `--read-work`: legacy-compatible parameter name for ConcurrentWork.
- `--write-work`: legacy-compatible parameter name for ExclusiveWork.
- `--memory-mb`: working-set size for the memory workload.
- `--dictionary-size`: size parameter for dictionary / ledger / payload workloads.

Scope has true in-place permission conversion:

- `ExclusiveToConcurrent` downgrades in place without opening a release-and-reacquire window.
- `ConcurrentToExclusive` upgrades in place without requiring a separate upgradeable mode.
- In-place upgrade obtains Exclusive permission with stronger preemption than normal Exclusive acquisition.

Targets:

- `scope`: ConcurrentExclusiveLockScope.
- `rwls`: nearest comparable ReaderWriterLockSlim path.
- `monitor`: nearest comparable Monitor / lock path.

ReaderWriterLockSlim and Monitor do not provide CEL's true in-place upgrade / downgrade semantics. In the advanced semantic cases, they are only simulation-style performance comparisons, not semantic equivalents.

## Good Result

- Every case ends in `Idle`.
- All workers complete the requested loop count.
- In-place upgrade / downgrade paths have stable `ns/op` and throughput.
- Mixed paths are explainable and do not show state leaks or abnormal tails.
- `cpu%` is an observation metric only, not a pass/fail condition.

## Commands

Quick run:

```powershell
TestAndBenchmark.exe advanced-perf --threads 16 --operations 10000 --workload cpu --work 64
```

Standard comparison:

```powershell
TestAndBenchmark.exe advanced-perf --target all --threads 64 --operations 100000 --workload dictionary --dictionary-size 65536 --read-work 64 --write-work 128
```

.NET built-in lock comparison:

```powershell
TestAndBenchmark.exe advanced-perf --target all --threads 64 --operations 100000 --workload dictionary --dictionary-size 65536 --read-work 64 --write-work 128
```
