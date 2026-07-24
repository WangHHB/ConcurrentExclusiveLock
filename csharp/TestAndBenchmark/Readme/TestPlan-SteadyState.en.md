# Steady-State Benchmark Test Plan

Date: 2026-07-22

## Plan

Run a fixed number of independent lock instances at the same time. Each lock instance owns a fixed number of dedicated worker threads. Each worker performs a fixed number of Concurrent / Exclusive permission acquisitions, business-work execution, and releases.

Parameters:

- `--lock-instances`: number of independent single-lock scenarios running at the same time.
- `--threads`: worker thread count per lock instance.
- `--operations`: operations per worker thread.
- `--concurrent-percent`: Concurrent operation ratio. Decimal values are supported. If omitted, the benchmark runs six scenarios: 100/0, 99.5/0.5, 90/10, 50/50, 30/70, and 0/100.
- `--work`: sets both ConcurrentWork and ExclusiveWork.
- `--read-work`: legacy-compatible parameter name for ConcurrentWork.
- `--write-work`: legacy-compatible parameter name for ExclusiveWork.

Targets:

- `scope`: ConcurrentExclusiveLockScope.
- `rwls`: ReaderWriterLockSlim.
- `monitor`: Monitor / lock.

For the same parameter set, every target uses the same lock-instance count, thread count, operation count, and deterministic request sequence. Use `--lock-instances 1` for single-lock contention conclusions. Multi-lock cases represent many independent single-lock scenarios running together and should not be mixed with single-lock contention conclusions.

The output format is aligned with the older LockBenchmark project and adds `avg excl ns`.

## Pass Criteria

- Every target reports matching total / concurrent / exclusive counts.
- `state` is printed so final business state can be compared across targets.
- Higher `works/s` means higher total throughput for the full run.
- `works/s/lock` shows average per-lock throughput in multi-lock cases.
- `work/cpu%` is an observation metric for completed work per CPU percentage point.
- `avg excl ns` is the average duration for Exclusive operations, measured from before requesting permission through business work and completed release.

## Commands

Quick smoke:

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 4 --workload cpu --operations 100 --work 4 --target all
```

Single-lock overhead:

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload cpu --operations 100000 --work 0 --target all
```

Single-lock thread scaling scan:

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 1,2,4,8,16,32 --workload cpu --operations 100000 --work 256 --target all --concurrent-percent 100
```

Single-lock CPU workload:

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload cpu --operations 100000 --read-work 256 --write-work 256 --target all
```

Single-lock large random memory access:

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 128 --target all
```

Single-lock dictionary cache:

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload dictionary --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all
```

Single-lock account ledger:

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload ledger --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all
```

Single-lock binary payload:

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload payload --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all
```

Four independent single-lock cases at the same time, 32 threads per lock:

```powershell
TestAndBenchmark.exe steady --lock-instances 4 --threads 32 --workload ledger --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all
```

Eight short-critical-section single-lock cases at the same time, 16 threads per lock:

```powershell
TestAndBenchmark.exe steady --lock-instances 8 --threads 16 --workload cpu --operations 100000 --work 4 --target all
```

One thousand lock cases at the same time, dictionary cache:

```powershell
TestAndBenchmark.exe steady --lock-instances 1000 --threads 8 --workload dictionary --operations 100 --dictionary-size 1280 --read-work 64 --write-work 128 --target all
```

One thousand lock cases at the same time, CPU:

```powershell
TestAndBenchmark.exe steady --lock-instances 1000 --threads 8 --workload cpu --operations 100 --read-work 64 --write-work 128 --target all
```
