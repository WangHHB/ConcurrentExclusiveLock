# Semantic and Stress Test Guide

## Purpose

The TestAndBenchmark executable validates the permission protocol implemented by the C core and the C++ Scope/Pipeline layers.

The semantic tests answer:

> After contention, preemption, upgrade, downgrade, business-ID failure, exceptions, timeouts, and repeated reuse, does each lock still obey the defined Concurrent/Exclusive protocol?

Throughput is not a semantic pass criterion.

## Build

```shell
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

Executable location on a single-configuration build:

```text
build/TestAndBenchmark/TestAndBenchmark
```

A multi-configuration generator may place it below a configuration directory.

## Modes

### `--full-semantics`

Runs deterministic contracts followed by randomized legal paths.

Coverage includes:

- C API initialization and direct use;
- initial snapshots and business IDs;
- Concurrent ID assignment and maxConcurrent;
- Concurrent/Exclusive exclusion;
- preemptive Exclusive;
- unconditional upgrade;
- downgrade;
- multiple upgrade serialization;
- ContextID single-winner upgrade;
- EpochID conditional upgrade;
- Scope normal and exception release;
- timeout paths;
- Pipeline permission transitions;
- Pipeline Try failure;
- Pipeline exception propagation/final release;
- randomized legal paths over independent locks.

Command:

```shell
./build/TestAndBenchmark/TestAndBenchmark --full-semantics --lock-instances 8 --semantic-workers 4 --semantic-operations 256
```

`--advanced-correctness` is currently an alias of this mode.

### `--pipeline-semantics`

Runs deterministic Pipeline contracts only.

```shell
./build/TestAndBenchmark/TestAndBenchmark --pipeline-semantics
```

### `--pipeline-stress <duration>`

Runs finite randomized Pipeline batches until the requested duration expires. Each batch independently chooses lock count, workers per lock, and rounds per lock from `1..N` (workers use `2..N`) using a reproducible batch seed. The requested duration is checked between batches, so the final in-flight batch is allowed to finish cleanly.

Each randomized Pipeline combines:

- independent Concurrent;
- independent Exclusive;
- None boundaries;
- ConvergeConcurrent;
- ConvergeExclusive;
- TryConcurrent;
- TryExclusive;
- ContextID/EpochID-conditioned convergence;
- deliberately injected segment exceptions.

Every protected region continuously checks that Concurrent and Exclusive business probes do not overlap. A heartbeat reports elapsed/remaining time, completed batches, Pipelines, Segments, current seed/shape, and active workers every 10 seconds. A batch fails with reproducible diagnostics if no worker completes a round or exits for 10 minutes. The test also verifies that every permission probe and lock is Idle after each batch.

```shell
./build/TestAndBenchmark/TestAndBenchmark --pipeline-stress 10m --lock-instances 8 --semantic-workers 8 --semantic-operations 256
```

### `--contention-stress <duration>`

Runs many dedicated threads repeatedly acquiring ordinary Exclusive on one lock.

It reports total acquisitions and the minimum/maximum acquisitions per thread. It is a diagnostic for practical waiter progress, not a strict-fairness test.

```shell
./build/TestAndBenchmark/TestAndBenchmark --contention-stress 10m --semantic-workers 64
```

## Parameters

| Parameter | Meaning |
|---|---|
| `--lock-instances` | Exact number of independent locks in semantic regression; maximum locks selected per Pipeline stress batch. |
| `--semantic-workers` | Exact workers per lock in semantic regression; maximum workers per lock selected per Pipeline stress batch; total workers in contention stress. |
| `--semantic-operations` | Exact random legal-path rounds per worker in semantic regression; maximum rounds per lock selected per Pipeline stress batch. |
| `--semantic-seed` | Optional reproducible 64-bit seed. In Pipeline stress it is the base seed used to derive every batch seed. Decimal and `0x` forms are accepted. |

Duration examples:

```text
500ms
30s
10m
24h
1d
```

## Validation Rules

The tests enforce one or more of the following rules:

1. An Exclusive business region must not overlap any Concurrent region.
2. Two Exclusive business regions must not overlap.
3. Concurrent IDs must remain within the requested range.
4. New Concurrent operations must stop entering after preemptive Exclusive pressure is visible.
5. Multiple upgrades must execute their Exclusive regions serially.
6. Same-ContextID conditional upgrades must produce exactly one winner.
7. Failed conditional upgrades must automatically release the original Concurrent permission.
8. Scope must release the final permission after normal exit and exceptions.
9. Pipeline Try failure must skip the current segment and continue from None.
10. Pipeline exceptions must release the final permission and propagate.
11. Every completed test must leave each lock reusable and Idle.

## Recommended Release Validation

1. Run the full semantic regression.
2. Run CTest.
3. Run Pipeline stress for 10 minutes as a command check.
4. Run Pipeline stress for several hours before a normal release.
5. Extend to 24 hours or more after major algorithm/platform changes.
6. Run a single-lock contention diagnostic on each supported operating system.
7. Run sanitizer builds where the compiler supports them.

## Sanitizers

AddressSanitizer and UndefinedBehaviorSanitizer example:

```shell
cmake -S . -B build-asan -G Ninja -DCMAKE_BUILD_TYPE=Debug -DCMAKE_C_FLAGS="-fsanitize=address,undefined -fno-omit-frame-pointer" -DCMAKE_CXX_FLAGS="-fsanitize=address,undefined -fno-omit-frame-pointer" -DCMAKE_EXE_LINKER_FLAGS="-fsanitize=address,undefined"

cmake --build build-asan
./build-asan/TestAndBenchmark/TestAndBenchmark --full-semantics
```

ThreadSanitizer example:

```shell
cmake -S . -B build-tsan -G Ninja -DCMAKE_BUILD_TYPE=Debug -DCMAKE_C_FLAGS="-fsanitize=thread -fno-omit-frame-pointer" -DCMAKE_CXX_FLAGS="-fsanitize=thread -fno-omit-frame-pointer" -DCMAKE_EXE_LINKER_FLAGS="-fsanitize=thread"

cmake --build build-tsan
./build-tsan/TestAndBenchmark/TestAndBenchmark --full-semantics
```

Sanitizer support and compatibility vary by operating system and compiler.

## Failure Handling

Any failed assertion or uncaught worker exception terminates the selected test mode with a nonzero exit code and prints an `ERROR:` message.

For randomized failures, rerun with the same mode, topology, operation count, and `--semantic-seed`.
