# Performance Benchmark Guide

## 1. What the Performance Benchmark Measures

The performance benchmark compares the throughput, CPU efficiency, and scalability of different locking strategies under the same workload, thread count, and read/write ratio.

The standard benchmark compares:

- `lock`;
- `ReaderWriterLockSlim`;
- `ConcurrentExclusiveLock`.

The performance benchmark answers:

> Under the same test conditions, how much business Work can each lock complete, and how much time and CPU does that throughput require?

Performance results do not replace semantic correctness testing. Performance data is meaningful only after the semantic tests have passed.

---

## 2. Measurement Method

Each locking strategy and each read/write scenario receives its own independent lock instances and Work data. Business objects previously used by another locking strategy are not reused.

A warm-up is performed before formal timing begins. Dedicated `Thread` instances are created and wait at a ready barrier. The main thread starts timing and opens the start gate only after all workers are ready, so thread creation and preparation are excluded from the measured interval.

Each program run loads only the Work type selected by `--workload`, but automatically tests every locking strategy with the following read/write ratios:

```text
100 / 0
99.5 / 0.5
90 / 10
50 / 50
30 / 70
0 / 100
```

After access is successfully acquired:

- read operations execute `TickRead`;
- write operations execute `TickWrite`;
- `--read-work` and `--write-work` control the number of business steps for their respective operation types.

The benchmark uses dedicated `Thread` instances. Different locking strategies run in separate rounds and are never mixed within the same timing interval.

---

## 3. Core Runtime Parameters

| Parameter | Meaning |
|---|---|
| `--lock-instances` | Number of independent “lock + Work” instances running concurrently for the same strategy. The default is 1. |
| `--threads` | Number of dedicated `Thread` instances assigned to each lock instance. |
| `--operations` | Number of lock acquisitions and business Work operations executed by each thread. |
| `--workload` | Work type: `cpu`, `memory`, `dictionary`, `ledger`, or `payload`. |
| `--work` | Sets the business step count for both reads and writes. |
| `--read-work` | Business step count for each read operation. Overrides the read side of `--work`. |
| `--write-work` | Business step count for each write operation. Overrides the write side of `--work`. |
| `--memory-mb` | Memory size, in MiB, used by each lock instance for the `memory` workload. |
| `--dictionary-size` | Data size used by each lock instance for the `dictionary`, `ledger`, and `payload` workloads. |

The total thread count and total number of lock operations are:

```text
Total threads         = lock-instances × threads
Total lock operations = lock-instances × threads × operations
```

For example:

```text
--lock-instances 4 --threads 32 --operations 10000
```

means:

- 4 independent locks and 4 independent Work instances are created;
- each lock has 32 dedicated threads;
- the total thread count is 128;
- the total number of lock operations is 1,280,000.

Both `--memory-mb` and `--dictionary-size` are interpreted per lock instance. Increasing `--lock-instances` also increases the total data size proportionally.

---

## 4. Work Types

### `cpu`

Primarily performs integer calculations and rarely accesses large data structures.

Useful for observing:

- the normal overhead of the lock itself;
- short critical-section throughput;
- contention cost as the thread count increases;
- performance crossover points at different business step counts.

`--work 0` still retains required interface calls and minimal state access. It is not a completely empty method.

### `memory`

Performs random accesses against a large shared array.

Useful for observing:

- CPU cache effects;
- memory bandwidth;
- NUMA effects;
- changes caused by expanding the total working set with multiple lock instances.

### `dictionary`

Performs string-key dictionary lookups, object access, and cache updates.

Useful for simulating object caches, index tables, and shared dictionary workloads.

### `ledger`

Performs account lookups, balance validation, transfers, and audit logging.

Useful for simulating entity workloads containing many branches and a mixture of reads and state changes.

### `payload`

Performs binary message-header parsing, conditional checks, and in-place field updates.

Useful for simulating network protocols, message processing, and binary state updates.

A single operation has a different cost in each Work type. Do not directly rank different Work types by `works/s`.

---

## 5. Result Metrics

Common output metrics include:

| Metric | Meaning |
|---|---|
| `elapsed` | Time required by the current locking strategy and read/write scenario to complete all operations. |
| `cpu%` | CPU usage estimated from process CPU time during the measured interval. It is intended only as an auxiliary indicator of resource-consumption trends. |
| `works/s` | Total number of `TickRead` and `TickWrite` operations completed per second across all lock instances. |
| `works/s/lock` | `works/s` divided by the number of lock instances, representing average throughput per lock. |
| `work/cpu%` | `works/s` divided by CPU percentage, used to observe throughput per unit of CPU usage. |
| `reads` / `writes` | Actual numbers of completed read and write Work operations. |
| `state` | Final shared-state summary for the Work, used to verify that different locking strategies produced equivalent business results. |

### About `cpu%`

`cpu%` is derived from process CPU-time sampling during the measured interval. It is not a hardware-level precision measurement.

It can be affected by the duration of the timing interval, operating-system scheduling, background load, thread migration, dynamic CPU frequency, and timer resolution. In very short tests, the value may fluctuate significantly and may even appear as 0 or temporarily unusually high. Small differences of only a few percentage points should therefore not be treated as meaningful.

However, when tests are repeated on the same machine with identical parameters and a sufficiently long timing interval, `cpu%` still provides useful reference information. It can help indicate:

- whether one locking strategy consumes substantially more CPU;
- whether higher throughput is accompanied by higher CPU usage;
- resource-consumption trends under multi-lock or high-thread configurations;
- the throughput-per-CPU trend represented by `work/cpu%`.

Because `work/cpu%` is calculated from `works/s` and `cpu%`, it is also only an auxiliary metric. Formal comparisons should primarily consider `elapsed`, `works/s`, result consistency, and the stable range observed across repeated runs.

The results should satisfy:

```text
reads + writes = lock-instances × threads × operations
```

For the same Work type, read/write ratio, and parameter set, different locking strategies should produce the same `state`. If a warning such as `final work state differs` appears, the performance data from that run should not be treated as valid.

---

## 6. How to Compare Results Correctly

1. Compare different locking strategies only when the Work type, read/write ratio, and all parameters are identical.
2. Different Work types have different business complexity and should not be ranked directly by `works/s`.
3. Single-lock results are useful for observing contention on one shared object. Multi-lock results are useful for observing aggregate throughput across many independent entities.
4. `--threads` is the thread count per lock, not the total process thread count.
5. With multiple lock instances, consider both `works/s` and `works/s/lock`.
6. Do not attach a debugger during formal testing. Use a prepared Release build.
7. Close other high-load applications whenever possible to reduce interference from scheduling and background work.
8. Repeat formal tests and evaluate the stable range rather than using a single peak result.
9. Each scenario should preferably run for at least 1 second. Formal comparisons should preferably run for at least 3 seconds.
10. Use `cpu%` and `work/cpu%` only to observe clear trends; do not compare small differences.
11. Millisecond-scale smoke-test configurations verify only the program flow and must not be used to evaluate CPU efficiency or lock performance.

---

## 7. Advanced Access-Path Performance Comparison

`--advanced-perf` tests only the advanced access paths of `ConcurrentExclusiveLock`. It does not participate in the standard comparison against `lock` and `ReaderWriterLockSlim`.

It compares the cost of different CEL access paths at the same scale, including:

- ordinary Concurrent access;
- Exclusive downgraded to Concurrent;
- a mixed path combining ordinary Concurrent with Concurrent obtained by downgrade;
- ordinary Exclusive access;
- conditional Concurrent-to-Exclusive upgrade;
- a mixed path combining ordinary Exclusive with Concurrent-to-Exclusive upgrade.

All output uses the same thread count, operation count per thread, and business step count, making it suitable for observing the relative cost of different access flows.

---

## 8. Commands Ready to Run

```powershell
# Show all command-line options
.\TestAndBenchmark.exe --help

# Quick smoke test: verifies only that the program completes successfully
.\TestAndBenchmark.exe --lock-instances 1 --threads 4 --workload cpu --operations 100 --work 4

# Approximate lock overhead with a very short critical section
.\TestAndBenchmark.exe --lock-instances 1 --threads 64 --workload cpu --operations 100000 --work 0

# Very short CPU workload
.\TestAndBenchmark.exe --lock-instances 1 --threads 16 --workload cpu --operations 100000 --read-work 64 --write-work 128

# Heavier work inside the lock
.\TestAndBenchmark.exe --lock-instances 1 --threads 64 --workload cpu --operations 100000 --read-work 256 --write-work 256

# Short random-memory-access workload
.\TestAndBenchmark.exe --lock-instances 1 --threads 32 --workload memory --operations 10000 --memory-mb 64 --read-work 32 --write-work 32

# Large-working-set random-memory-access workload
.\TestAndBenchmark.exe --lock-instances 1 --threads 64 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 128

# Dictionary-cache workload
.\TestAndBenchmark.exe --lock-instances 1 --threads 64 --workload dictionary --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128

# Account, transfer, and audit workload
.\TestAndBenchmark.exe --lock-instances 1 --threads 64 --workload ledger --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128

# Multiple lock instances: 4 locks with 32 dedicated threads per lock
.\TestAndBenchmark.exe --lock-instances 4 --threads 32 --workload ledger --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128

# Random-memory-access workload with many locks
.\TestAndBenchmark.exe --lock-instances 100 --threads 8 --workload memory --operations 1000 --memory-mb 16 --read-work 128 --write-work 128

# Dictionary workload with many locks
TestAndBenchmark.exe --lock-instances 100 --threads 16 --workload dictionary --operations 1000 --dictionary-size 1280 --read-work 64 --write-work 64

# CEL advanced access-path performance comparison
.\TestAndBenchmark.exe --advanced-perf --threads 64 --operations 100000 --work 64
```
