# Semantic and Stress Test Guide

## 1. What These Tests Validate

The semantic and stress tests validate the **protocol correctness and long-term stability** of `ConcurrentExclusiveLock`, `ConcurrentExclusiveLockScope`, and `ConcurrentExclusiveLockPipeline`.

They answer:

> After contention, upgrade, downgrade, failure, exceptions, and long-running execution, does the lock still comply with its defined access-permission protocol?

These tests do not compare the performance of different locks, and throughput is not a pass criterion.

The tests primarily perform black-box calls through the public API and use dedicated `Thread` instances to create real contention. Assertions run continuously throughout each test. Any access-permission overlap, release error, thread exception, or timeout causes the test to fail.

`ObservedState` and `ObservedContention` are observational snapshots and are not used to establish synchronization correctness.

---

## 2. Main Test Modes

### `--advanced-correctness`

Specialized tests for advanced access conversions.

The tests focus on:

- continuous-access semantics after `ExclusiveToConcurrent` downgrade;
- the convergence process of `ConvergeConcurrent`;
- conditional Concurrent-to-Exclusive upgrade;
- automatic release of Concurrent access for failed contenders;
- scheduling relationships between the upgrade winner and ordinary Exclusive requests;
- isolation between locks when many independent locks run concurrently.

This mode allows the lock count, operation count per lock, and random seed to be specified explicitly.

### `--full-semantics`

Full semantic regression testing.

The test first runs deterministic access-contract checks and then executes model-driven random legal paths covering:

- basic Concurrent / Exclusive acquisition and release;
- preemptive Exclusive access;
- Exclusive-to-Concurrent downgrade;
- Concurrent-to-Exclusive upgrade;
- conditional upgrade using ContextID / EpochID;
- Scope lifetime and exception-path release;
- Pipeline access-state transitions;
- repeated upgrade and downgrade cycles within the same held-access chain.

The random state machine generates only call paths that comply with the public protocol. Its purpose is to verify the implementation, not to deliberately exercise undefined or illegal usage.

### `--pipeline-semantics`

Deterministic Pipeline semantic testing in fixed batches.

It verifies permission continuation, segmentation, upgrade, downgrade, and failure handling across different Segment combinations, including:

- independent Concurrent / Exclusive segments;
- Try-type segments;
- `ConvergeConcurrent`;
- `ConvergeExclusive`;
- convergence to Exclusive conditioned on ContextID / EpochID;
- continuing from None after a Try condition fails;
- final release when the Pipeline completes or exits through an exception.

### `--pipeline-stress <duration>`

Time-based randomized Pipeline stress testing.

Each batch randomly generates:

- the number of independent locks;
- the number of dedicated threads per lock;
- the number of Pipeline rounds per lock;
- the number and combination of Segments;
- empty business segments and extremely short critical sections;
- a random seed.

This mode is intended to discover low-probability problems in permission conversion, state transitions, and exception-path release. When a failure occurs, retain the reported seed and reproduce the batch with the same parameters.

The Pipeline sits above the core lock and Scope layers. Random Segment combinations exercise ordinary Concurrent, ordinary Exclusive, Try acquisition, upgrade, downgrade, business-ID-conditioned convergence, and failure-release paths. Therefore, in the current test system, **running `--pipeline-stress` for an extended period generally covers the main access-permission protocols and the combined behavior of all three API layers**.

Routine validation does not require every long-running mode to be executed. After completing one deterministic semantic test, it is normally sufficient to run the randomized Pipeline stress test for several hours. Before a release or after a major change, extend the run to 24 hours or longer when appropriate.

Here, “coverage” refers to the main access-permission and conversion protocols. Specialized concerns such as timeout overloads, observational snapshots, extreme single-lock contention, and long-term resource trends of persistent lock objects are still covered by their corresponding dedicated test modes.

### `--endurance <duration>`

Long-running endurance testing.

A set of lock objects is created when the test starts and is reused by every subsequent batch. Locks are not repeatedly recreated in a way that could hide long-term state problems.

The lock count and thread topology are selected automatically according to the number of logical processors. The test continuously exercises several categories of legal call paths and periodically reports:

- accumulated batch and operation progress;
- normalized CPU usage;
- working set and private memory;
- managed memory;
- thread count;
- GC counts;
- distribution of access paths.

This mode accepts only the duration. It does not accept lock-count, thread-count, or operation-count parameters. Pressing `Ctrl+C` requests a graceful stop after the current batch completes and prints the final result.

### `--contention-stress <duration>`

High-contention diagnostics for a single lock.

This mode uses many dedicated threads contending for the same lock to observe contention pressure and long-running behavior. It is a specialized stress test and is not used to rank the performance of different locks.

---

## 3. Core Validation Rules

Depending on the selected mode, the tests validate one or more of the following rules:

1. An Exclusive business region must not overlap with any Concurrent region or another Exclusive region.
2. Concurrent IDs must be within the valid range, and IDs held simultaneously in the same round must be unique.
3. Conditional Concurrent-to-Exclusive upgrade must comply with the single-winner or business-ID advancement rules.
4. After a conditional upgrade fails, the original Concurrent access must be released automatically according to the protocol.
5. Exclusive-to-Concurrent downgrade must comply with the continuous-access and upgrade-scheduling rules.
6. Scope must end its held state correctly after normal release, early return, and exception paths.
7. Pipeline must switch, continue, or release access correctly according to the Segment mode.
8. When a Try-type segment does not meet its execution condition, the segment must be skipped and processing must continue from the None state.
9. At the end of every round or batch, the lock must remain reusable.
10. After any test failure, the output must contain enough batch and random-seed information to reproduce the problem.

---

## 4. Runtime Parameters

### General Semantic-Test Parameters

| Parameter | Meaning |
|---|---|
| `--lock-instances` | Number of independent locks. Different locks do not share state. |
| `--semantic-workers` | Maximum number of dedicated `Thread` instances per lock. Must be at least 2. |
| `--semantic-operations` | Maximum number of semantic-path or Pipeline rounds executed per lock. |
| `--semantic-seed` | Optional random seed. Specifying it reproduces the same randomized call shape. |

In randomized stress modes, the actual number of locks, workers per lock, and rounds per lock may be selected randomly within `1..N` or another valid range defined by the test. These parameters therefore usually define the upper limits of the randomized shape rather than fixed values used by every batch.

### Advanced-Test Parameters

| Parameter | Meaning |
|---|---|
| `--advanced-operations` | Baseline number of tasks per lock for each advanced operation type. |
| `--advanced-seed` | Optional random seed for the advanced tests. |

The many-independent-locks mode creates a fixed number of dedicated threads for each lock and reuses those threads across multiple task rounds.

### Duration Parameters

The following duration formats are supported:

```text
30s
10m
24h
1d
hh:mm:ss
```

`--pipeline-stress` must be followed by a duration.

`--endurance` is followed only by a duration; all topology parameters are selected automatically by the program.

---

## 5. Failure and Reproduction

Any assertion failure, thread exception, or timeout immediately terminates the current test and returns a nonzero exit code.

When a randomized test fails, the output normally includes:

- the batch number;
- the random seed;
- the number of locks, workers per lock, and rounds used by the batch;
- the failing path or exception chain.

To reproduce the failure, keep the test mode and main parameters unchanged and pass the reported seed through the corresponding `--semantic-seed` or `--advanced-seed` option.

---

## 6. Recommended Execution Order

When validating a release package for the first time, use the following sequence:

1. Run the full semantic regression once and confirm that every deterministic contract passes.
2. Run the deterministic Pipeline semantic test.
3. Run the randomized Pipeline stress test for 10 minutes to confirm that the command and randomized batches execute reliably.
4. For routine final validation, run the randomized Pipeline stress test for several hours.
5. Before a release or after a major change, extend the Pipeline stress test to 24 hours or longer.

In the current test system, the randomized Pipeline stress test calls the core lock through Scope and covers the main acquisition, release, upgrade, downgrade, Try-failure, and business-ID convergence paths. Therefore, **it is generally unnecessary to run every stress mode for a long duration; several hours of `--pipeline-stress` can serve as the primary stress validation**.

`--endurance`, `--contention-stress`, and the many-independent-locks tests supplement the main test with persistent-lock-object behavior, resource trends, peak single-lock contention, and specific topologies. They are not required after every change.

A quick test passing only shows that the main paths did not fail immediately. Long-running randomized stress testing expands coverage across scheduling orders, Segment combinations, and execution time, but does not replace deterministic semantic testing.

---

## 7. Commands Ready to Run

```powershell
# Show all command-line options
.\TestAndBenchmark.exe --help

# Specialized advanced access-conversion tests
.\TestAndBenchmark.exe --advanced-correctness

# Advanced access-conversion tests with many independent locks
.\TestAndBenchmark.exe --advanced-correctness --lock-instances 1000 --advanced-operations 4 --advanced-seed 12345

# Full semantic regression
.\TestAndBenchmark.exe --full-semantics --lock-instances 64 --semantic-workers 4 --semantic-operations 256

# Deterministic Pipeline semantic test
.\TestAndBenchmark.exe --pipeline-semantics --lock-instances 1 --semantic-workers 64 --semantic-operations 1000 --semantic-seed 12345

# Randomized Pipeline stress test: 10-minute smoke test
.\TestAndBenchmark.exe --pipeline-stress 10m --lock-instances 8 --semantic-workers 64 --semantic-operations 1000 --semantic-seed 12345

# Randomized Pipeline stress test: 3-hour routine validation
.\TestAndBenchmark.exe --pipeline-stress 3h --lock-instances 8 --semantic-workers 128 --semantic-operations 2000

# Randomized Pipeline stress test: 24-hour release validation
.\TestAndBenchmark.exe --pipeline-stress 24h --lock-instances 8 --semantic-workers 128 --semantic-operations 2000

# Optional: long-running endurance test for persistent lock objects and resource trends
.\TestAndBenchmark.exe --endurance 24h

# Single-lock high-contention diagnostics: 128 dedicated threads for 10 seconds
.\TestAndBenchmark.exe --contention-stress 10s --threads 128
```
