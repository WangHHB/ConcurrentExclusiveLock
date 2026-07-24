# Exclusive Preemption Test Plan

Date: 2026-07-22

## Plan

Run a fixed number of Concurrent workers that continuously acquire Concurrent permission while Exclusive requests arrive periodically.

Recommended parameter names use kebab-case: `--threads` for the Concurrent flood worker count, plus `--exclusive-hold-ms`, `--exclusive-pause-ms`, and `--exclusive-timeout-ms`.

Targets under the same parameters:

- `scope`: ConcurrentExclusiveLockScope.
- `rwls`: ReaderWriterLockSlim.
- `monitor`: Monitor / lock.

The recommended workload is `dictionary`, so both Concurrent and Exclusive regions touch a state-table-like payload.

The workload parameters are shared with the steady-state benchmark:

- `--workload`: `cpu`, `memory`, `dictionary`, `ledger`, or `payload`.
- `--work`: sets both ConcurrentWork and ExclusiveWork.
- `--read-work`: legacy-compatible parameter name for ConcurrentWork.
- `--write-work`: legacy-compatible parameter name for ExclusiveWork.
- `--memory-mb`: working-set size for the memory workload.
- `--dictionary-size`: size parameter for dictionary / ledger / payload workloads.

`--concurrent-spin` only increases time spent inside the Concurrent region. It is not a business workload.

## Good Result

- Scope has `Exclusive failed = 0`.
- Scope Exclusive wait p95 / p99 / max remain stable.
- Scope shows a clear Exclusive preemption latency advantage over ReaderWriterLockSlim under a Concurrent flood.
- All targets write to the same CSV format for side-by-side comparison.

## Commands

Standard comparison:

```powershell
TestAndBenchmark.exe exclusive-preemption --profile standard --target all --workload dictionary --dictionary-size 65536 --read-work 64 --write-work 128
```

Quick smoke:

```powershell
TestAndBenchmark.exe exclusive-preemption --profile quick --target all --workload dictionary --dictionary-size 1280 --read-work 8 --write-work 16
```

Specified worker count:

```powershell
TestAndBenchmark.exe exclusive-preemption --profile standard --target all --workload dictionary --threads 64 --dictionary-size 65536 --read-work 64 --write-work 128
```
