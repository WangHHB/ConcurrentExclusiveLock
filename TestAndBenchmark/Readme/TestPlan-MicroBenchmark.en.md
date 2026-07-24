# Micro Benchmark Test Plan

Date: 2026-07-22

## Plan

Use BenchmarkDotNet to measure single-path cost, low-contention paths, allocation, threading diagnostics, and critical-path disassembly.

Current coverage:

- Uncontended upper bounds: NoLock / AtomicOnly.
- BCL comparisons: Monitor / lock and ReaderWriterLockSlim.
- Scope base paths: Concurrent / Exclusive.
- Scope advanced paths: in-place upgrade, in-place downgrade, and Try failure paths.
- Pipeline: single-stage and basic conversion paths.
- Low-contention paths: 16 worker threads with a common start gate, CPU work 16/16, Concurrent-only and 99.5/0.5 mixed cases, comparing Scope / ReaderWriterLockSlim / Monitor.

Uncontended and low-contention microbenchmarks both use the shared `BenchmarkWork` CPU payload.

Try failure benchmarks keep the Try call at the outermost call site. The conflicting permission is held by a background worker, so the benchmark measures the failed Try path itself rather than a nested call inside an already-held region.

## Pass Criteria

- BenchmarkDotNet reports Mean / Error / StdDev / Ratio / Allocated.
- Uncontended benchmarks enable MemoryDiagnoser, ThreadingDiagnoser, and DisassemblyDiagnoser.
- Try failure benchmarks enable MemoryDiagnoser, ThreadingDiagnoser, and DisassemblyDiagnoser.
- Low-contention benchmarks enable MemoryDiagnoser and ThreadingDiagnoser.
- Scope base paths and comparison paths stay in stable, explainable ranges.
- In-place upgrade / downgrade and Pipeline paths do not introduce unexpected allocation.
- Conclusions are based on stable Mean / Ratio values, not a single extreme run.

## Commands

Standard run:

```powershell
TestAndBenchmark.exe micro
```

Quick smoke:

```powershell
TestAndBenchmark.exe micro --filter *ScopeConcurrent* --job short --warmupCount 1 --iterationCount 1
```

Low-contention smoke:

```powershell
TestAndBenchmark.exe micro --filter *LowContention* --job short --warmupCount 1 --iterationCount 1
```

BenchmarkDotNet reports are written under:

```text
BenchmarkDotNet.Artifacts\results
```
