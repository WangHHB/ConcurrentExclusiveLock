# Local Verification Record

This file records the validation performed while generating the initial C/C++ port artifact.

## Environment

```text
Operating system: Linux container
CMake:           3.31.6
C compiler:      GCC 14.2.0
C++ compiler:    G++ 14.2.0
Generator:       Ninja 1.12.1
```

## Release Build

```shell
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build -j 4
```

Result: PASS.

## Full Semantics

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --full-semantics \
  --lock-instances 8 \
  --semantic-workers 4 \
  --semantic-operations 256
```

Result: PASS.

Covered C API, snapshots, IDs, exclusion, preemption, upgrade/downgrade, multiple upgrades, ContextID/EpochID, Scope, timeouts, Pipeline, and randomized legal paths.

## Pipeline Stress

Final-source run:

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --pipeline-stress 5s \
  --lock-instances 8 \
  --semantic-workers 4 \
  --semantic-operations 64
```

Observed result:

```text
batches=480853
pipelines=15627871
PIPELINE STRESS: PASS
```

## Exclusive Contention

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --contention-stress 2s \
  --semantic-workers 16
```

Observed result in one run:

```text
total=23980977
min/thread=1060158
max/thread=1916969
CONTENTION STRESS: PASS
```

These values are environment-specific and are recorded only as a smoke/stability reference.

## Reference Memory Benchmark

A release run using 4 locks, 4 threads per lock, 200,000 operations per thread, 64 MiB per lock, and 32/32 read/write steps completed successfully. Every strategy produced matching read/write counts and final state hashes.

The raw output is stored in `TestResults/benchmark-memory-long-linux.txt`.

## AddressSanitizer + UndefinedBehaviorSanitizer

Debug build with:

```text
-fsanitize=address,undefined -fno-omit-frame-pointer
```

Full semantic regression result: PASS.

## ThreadSanitizer

Debug build with:

```text
-fsanitize=thread -fno-omit-frame-pointer
```

Full semantic regression result: PASS.

## CMake Installation and Consumer Test

The static libraries were installed through `cmake --install`. Separate C and C++ consumer projects then located the package through `find_package(ConcurrentExclusiveLock)` and linked `ConcurrentExclusiveLock::C` and `ConcurrentExclusiveLock::Cpp`.

Result: PASS.

## Shared-Library Build

A release build with `-DCEL_BUILD_SHARED=ON` produced and installed the C and C++ shared libraries in addition to the static libraries. Separate consumers linked `ConcurrentExclusiveLock::CShared` and `ConcurrentExclusiveLock::CppShared` and executed successfully.

Result: PASS.

## Platform Scope

The POSIX backend was executed in this environment.

The Windows `SRWLOCK` + `Interlocked` backend is included in source but was not executable in the Linux container. It requires compilation and semantic/stress validation on Windows before a Windows binary release.
