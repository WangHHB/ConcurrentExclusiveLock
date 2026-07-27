# Local Verification Record

This file records the validation performed on the packaged C/C++ source after the Pipeline stress runner was corrected.

## Environment

```text
Operating system: Linux container
CMake:           3.31.6
GCC/G++:         14.2.0
Clang/Clang++:   17.0.0
Build generator: Unix Makefiles
```

The POSIX backend was executed. The Windows `SRWLOCK` + `Interlocked` backend remains source-compatible with the included Visual Studio 2026 solution, but could not be executed in this Linux environment.

## Core Lock Integrity

The packaged core lock implementations were not changed while correcting the stress runner.

```text
src/ConcurrentExclusiveLock.c    SHA-256 240c9969f874e42dd88477f9eb153822727366111d66ad8f9eabb1fa72796864
src/ConcurrentExclusiveLock.cpp  SHA-256 1e0ea5f3e6eec8abf13d7ffd2e70b552359bcb4ddf9200b8ec7d9e413a2bc953
```

Both hashes match the supplied project archive.

## Release Build and Full Semantics

```shell
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build -j 2

./build/TestAndBenchmark/TestAndBenchmark --full-semantics --lock-instances 8 --semantic-workers 4 --semantic-operations 256
```

Result: PASS.

Covered C API, snapshots, IDs, exclusion, preemption, upgrade/downgrade, multiple upgrades, ContextID/EpochID, Scope, timeouts, Pipeline transitions/exception release, and randomized legal paths.

Raw output: `TestResults/full-semantics-linux-final.txt`.

## Final 10-Minute Pipeline Stress

The exact command that previously appeared to hang was run against the final source:

```shell
./build/TestAndBenchmark/TestAndBenchmark --pipeline-stress 10m --lock-instances 1 --semantic-workers 16 --semantic-operations 100 --semantic-seed 12345
```

Observed result:

```text
[PASS] pipeline semantic stress completed: elapsed=00:10:00,
batches=11753, pipelines=5348586, segments=26070374, base-seed=12345.
```

Result: PASS. Heartbeats were emitted every 10 seconds and progress remained continuous for the full run.

Raw output: `TestResults/pipeline-stress-10m-linux-final.txt`.

## AddressSanitizer + UndefinedBehaviorSanitizer

Build flags:

```text
-fsanitize=address,undefined -fno-omit-frame-pointer
```

A 20-second, four-lock randomized Pipeline stress run completed successfully with leak detection and halt-on-error enabled.

Result: PASS.

Raw output: `TestResults/asan-ubsan-pipeline-stress-20s-final.txt`.

## ThreadSanitizer

Build flags:

```text
-fsanitize=thread -fno-omit-frame-pointer
```

A 20-second, four-lock randomized Pipeline stress run completed successfully with halt-on-error enabled.

Result: PASS.

Raw output: `TestResults/tsan-pipeline-stress-20s-final.txt`.

## Clang Build

The final source was also built with Clang/Clang++ 17 and a 10-second, four-lock randomized Pipeline stress run completed successfully.

Result: PASS.

Raw output: `TestResults/clang-pipeline-stress-10s-final.txt`.

## Watchdog and Failure-Path Injection

Temporary, non-packaged source copies were used to verify both diagnostic paths:

- a deliberately stalled worker triggered the no-progress watchdog, printed batch/seed/shape/progress diagnostics, and exited with failure;
- a deliberately thrown worker error stopped the batch immediately instead of waiting for the no-progress timeout.

The injected changes were not applied to the packaged source.

## Previous Package Validation

The previously recorded CMake installation/consumer tests and shared-library consumer tests remain applicable because the C and C++ library implementations and build definitions were unchanged. The final change set is limited to the TestAndBenchmark stress runner, command help, and documentation.
