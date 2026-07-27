# ConcurrentExclusiveLock for C and C++

[中文说明](README_CN.md)

ConcurrentExclusiveLock (CEL) is a high-performance, non-recursive synchronization protocol based on **Concurrent / Exclusive access permissions**.

This project is a port of the [original C# implementation](https://github.com/WangHHB/ConcurrentExclusiveLock/tree/main/csharp). The **C# version remains the reference implementation** for synchronization semantics, state transitions, upgrade/downgrade behavior, ContextID/EpochID rules, Scope behavior, and Pipeline behavior.

The port is divided into two layers:

- a caller-owned **C core** with a direct lock-object API and no acquisition token;
- **C++17 wrappers** providing RAII Scope management, synchronous Pipeline orchestration, and exception-based error reporting.

The project is dual-licensed under the MIT License or the Apache License 2.0, at your option.

---

## Table of Contents

- [Design Position](#design-position)
- [Core Concepts](#core-concepts)
- [Why This Is Not a ReadWriteLock](#why-this-is-not-a-readwritelock)
- [Preemptive Exclusive](#preemptive-exclusive)
- [Practical Ordering, Not Strict FIFO](#practical-ordering-not-strict-fifo)
- [In-Place Upgrade and Downgrade](#in-place-upgrade-and-downgrade)
- [ContextID and EpochID](#contextid-and-epochid)
- [Architecture](#architecture)
- [Supported Platforms](#supported-platforms)
- [Build](#build)
- [Install with CMake](#install-with-cmake)
- [C API](#c-api)
- [C++ API](#c-api-1)
- [C++ Scope](#c-scope)
- [C++ Pipeline](#c-pipeline)
- [Synchronous and Asynchronous Boundaries](#synchronous-and-asynchronous-boundaries)
- [State Observation](#state-observation)
- [Low-Allocation Design](#low-allocation-design)
- [Use Cases](#use-cases)
- [Design Boundaries](#design-boundaries)
- [Tests and Stress Tests](#tests-and-stress-tests)
- [Performance Benchmark](#performance-benchmark)
- [Observed Local Benchmark Result](#observed-local-benchmark-result)
- [Project Layout](#project-layout)
- [Project Status](#project-status)
- [License](#license)

---

## Design Position

CEL expresses **whether concurrent business access is permitted**, not whether the protected code reads or writes memory.

A good unit of ownership is usually one independently synchronized business object:

- player;
- room;
- entity;
- session;
- Actor;
- aggregate root;
- cache entry;
- order;
- task context.

The intended model is therefore:

```text
one independently synchronized entity
                ↓
one ConcurrentExclusiveLock instance
```

CEL is especially useful when many fine-grained lock instances coexist and each instance normally has a small number of contenders.

---

## Core Concepts

### Concurrent

Concurrent means that the current operation may execute together with other Concurrent operations.

A Concurrent region is **not necessarily read-only**. It may modify state when the business model guarantees that the simultaneous modifications do not conflict.

Examples:

- updating independent slots in the same entity;
- adding events to independently owned channels;
- reading state while updating thread-local or partition-owned data;
- performing validation that may run in parallel with equivalent validations.

### Exclusive

Exclusive means that the current operation must execute alone and may not overlap any Concurrent or other Exclusive operation.

An Exclusive region is **not necessarily write-only**. It may contain substantial reading, validation, aggregation, serialization, or decision logic.

The question CEL answers is:

> May this business operation execute concurrently with other business operations?

It does not answer:

> Does this code read or write data?

---

## Why This Is Not a ReadWriteLock

A traditional Reader/Writer Lock is organized around shared readers and exclusive writers.

CEL targets a broader entity-level permission protocol:

- multiple non-conflicting modifications may run concurrently;
- a read-oriented operation may still need Exclusive permission;
- an operation may inspect state under Concurrent and then become the unique committer;
- an Exclusive operation may downgrade while preserving a continuous access context;
- permission changes may be coupled to a ContextID or EpochID transition;
- one workflow may contain multiple independent and converged permission segments.

For this reason, the API deliberately uses **Concurrent / Exclusive**, not Read / Write.

---

## Preemptive Exclusive

The defining characteristic of CEL is **preemptive Exclusive acquisition**.

Ordinary Concurrent acquisition and release use the atomic counter fast path and normally do not enter the platform monitor queue.

When an Exclusive request enters the contention window:

1. new Concurrent acquisitions are prevented from entering;
2. existing Concurrent holders leave naturally;
3. the Exclusive request enters after the existing Concurrent holders drain;
4. normal contention resumes when Exclusive is released or downgraded.

This avoids requiring a continuously busy Concurrent workload to accidentally become fully idle before an Exclusive operation can proceed.

The C/C++ port preserves the reference algorithm:

```text
Concurrent fast path
    atomic state check and increment/decrement

Exclusive and upgrade slow path
    platform monitor mutex
    + atomic contention state
    + spin/yield drain waiting
```

The original C# implementation uses `Monitor.Enter`, `Monitor.TryEnter`, and `Monitor.Exit` as the Exclusive scheduling mechanism. It does not use `Monitor.Wait` or `Monitor.Pulse`. Accordingly, this port requires a platform mutex backend, not a condition-variable queue.

---

## Practical Ordering, Not Strict FIFO

CEL intentionally does **not** implement a ticket lock or a strict FIFO waiter queue.

Strict FIFO would add queue state, cancellation holes, timeout bookkeeping, head-of-line blocking, and additional contention to every Exclusive request. It would also complicate the relationship between ordinary Exclusive requests and Concurrent-to-Exclusive upgrades.

Instead, CEL relies on one serialized platform-monitor slow path. This provides practical ordering under contention while leaving actual execution order subject to:

- operating-system scheduling;
- CPU topology;
- cache state;
- thread suspension;
- process load;
- business-region duration;
- the fairness characteristics of the platform mutex.

The guarantee is therefore:

> Exclusive requests are coordinated through a serialized blocking slow path, but strict FIFO ordering is not guaranteed.

This matches the C# reference implementation.

---

## In-Place Upgrade and Downgrade

### Concurrent → Exclusive

A common workflow is:

1. inspect or validate state under Concurrent permission;
2. determine that a modification is required;
3. converge to Exclusive without releasing the current access context;
4. perform the unique commit.

The unconditional conversion is:

```text
ConcurrentToExclusive
```

The business-ID-conditioned conversions are:

```text
TryConcurrentToExclusiveWithSwitchContextID
TryConcurrentToExclusiveWithRaiseEpochID
```

When multiple Concurrent holders request an unconditional upgrade, their Exclusive regions execute serially. Upgrade requests take priority over ordinary Exclusive requests until the active upgrade group drains, matching the C# algorithm.

After a successful upgrade, the caller holds Exclusive permission.

After a failed conditional upgrade, the original Concurrent permission has already been released by the protocol. The caller must not release Concurrent again.

### Exclusive → Concurrent

After Exclusive work, the caller may downgrade directly:

```text
ExclusiveToConcurrent
```

After the downgrade:

- Exclusive is no longer held;
- Concurrent remains held;
- follow-up Concurrent work may continue;
- the caller later releases Concurrent;
- no ordinary release/reacquire window is introduced when no competing upgrade requires the context to be split.

Under upgrade contention, the reference protocol may cut the current context and reacquire Concurrent so that remaining upgrades can continue. The C/C++ implementation preserves this behavior.

---

## ContextID and EpochID

The lock stores two atomic business identifiers outside the core permission protocol.

### ContextID

ContextID identifies a business context, such as:

- room instance;
- battle context;
- player session;
- data-loading batch;
- logical transaction;
- task owner.

`SwitchContextID` atomically replaces the current value and returns whether the value changed.

When several Concurrent holders attempt a conditional upgrade using the same new ContextID, only the holder that actually changes ContextID succeeds. Failed holders automatically lose their previous Concurrent permission.

ContextID is not an ownership token and is not automatically cleared by release or Scope destruction.

### EpochID

EpochID represents a monotonically advancing lifecycle, version, or phase, such as:

- entity version;
- room tick;
- battle phase;
- snapshot version;
- generation;
- processing batch.

`RaiseEpochID` succeeds only when the new value is greater than the current value.

EpochID can also select which Concurrent callers are allowed to converge to Exclusive. Failed conditional upgrades automatically release their original Concurrent permission.

ContextID and EpochID are business state. Their allocation, meaning, reset, persistence, and validation are the caller's responsibility.

---

## Architecture

```text
C API
└─ cel_lock
   ├─ atomic 64-bit permission counter
   ├─ atomic ContextID
   ├─ atomic EpochID
   └─ platform monitor mutex

C++ API
├─ ConcurrentExclusiveLock
├─ ConcurrentExclusiveLockScope
├─ ConcurrentExclusiveLockSegment
└─ ConcurrentExclusiveLockPipeline
```

The permission-state fields remain the same 128-bit logical state used by the C# reference design:

```text
Counter     64 bits
ContextID   32 bits
EpochID     32 bits
```

The platform monitor object is additional runtime synchronization storage.

### No acquisition Token

The C API does not return or require an ownership token.

Concurrent acquisition returns a **Concurrent ID** in `[1, maxConcurrent]`. This value describes the ID assigned within the current uninterrupted Concurrent round; it is not supplied to release.

Release remains explicit:

```c
cel_lock_release_concurrent(&lock);
cel_lock_release_exclusive(&lock);
```

This follows the direct usage style of the Java port while preserving the C# protocol semantics.

---

## Supported Platforms

The source contains two platform monitor backends:

| Platform family | Monitor backend | Atomic backend |
|---|---|---|
| Windows | `SRWLOCK` | Windows `Interlocked` operations |
| POSIX | `pthread_mutex_t` | GCC/Clang-compatible `__atomic` operations |

The POSIX path is intended for Linux, macOS, Android, iOS, and other pthread-based systems. CMake detects whether the target requires a separate `libatomic` for 64-bit atomic operations and links it when necessary.

The included package was compiled and tested in the supplied Linux build environment. The Windows backend is included in the source and is designed for MSVC/clang-cl/MinGW-compatible Windows toolchains, but it was not executed inside this Linux sandbox.

Unsupported platforms can provide another internal monitor/atomic backend without changing the public C or C++ APIs.

---

## Build directly with Visual Studio 2026 (no CMake required)

Open the solution in the project root:

```text
ConcurrentExclusiveLock.sln
```

Select `Release | x64`, set `TestAndBenchmark` as the startup project, and build the solution. The executable is written to:

```text
bin\x64\Release\TestAndBenchmark.exe
```

The Release configuration includes a memory-workload benchmark command line with Work 640. You can also run:

```powershell
.\build-vs.ps1
.\run-benchmark-vs.ps1
```

## Build

Requirements:

- C11-capable compiler for the C API;
- on POSIX, a GCC/Clang-compatible compiler providing `__atomic` built-ins;
- C++17 compiler for the C++ wrappers and TestAndBenchmark;
- CMake 3.20 or later;
- a supported Windows or pthread platform.

### Configure and build

```shell
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

On multi-configuration generators, `CMAKE_BUILD_TYPE` may be omitted and `--config Release` selects the configuration.

### Run CTest

```shell
ctest --test-dir build -C Release --output-on-failure
```

### Build only the libraries

```shell
cmake -S . -B build -DCEL_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

### Optional shared libraries

```shell
cmake -S . -B build -DCEL_BUILD_SHARED=ON -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

Static libraries are always built. `CEL_BUILD_SHARED=ON` additionally builds `ConcurrentExclusiveLock::CShared` and `ConcurrentExclusiveLock::CppShared`. Their output names use the `-shared` suffix so Windows import libraries do not collide with the static `.lib` files.

Convenience scripts are included:

```powershell
.\build.ps1
```

```shell
./build.sh
```

---

## Install with CMake

Install to a prefix:

```shell
cmake --install build --config Release --prefix ./install
```

Consume the installed package:

```cmake
find_package(ConcurrentExclusiveLock CONFIG REQUIRED)

add_executable(MyApplication main.cpp)

target_link_libraries(MyApplication PRIVATE
    ConcurrentExclusiveLock::Cpp)
```

For a pure C consumer:

```cmake
target_link_libraries(MyCApplication PRIVATE
    ConcurrentExclusiveLock::C)
```

When the project is included with `add_subdirectory`, the same aliases are available:

```cmake
add_subdirectory(cpp)
target_link_libraries(MyApplication PRIVATE ConcurrentExclusiveLock::Cpp)
```

---

## C API

Include:

```c
#include <ConcurrentExclusiveLock.h>
```

### Lifecycle

`cel_lock` is caller-owned and does not allocate a separate lock object.

```c
cel_lock lock;

if (cel_lock_init(&lock) != CEL_RESULT_SUCCESS) {
    /* handle initialization failure */
}

/* use lock */

if (cel_lock_destroy(&lock) != CEL_RESULT_SUCCESS) {
    /* destroy requires no remaining holder or waiter */
}
```

Rules:

- call `cel_lock_init` exactly once before use;
- do not copy or move the bytes of an initialized `cel_lock`;
- stop all users and waiters before `cel_lock_destroy`;
- the lock object must outlive every operation using it.

### Concurrent

```c
int32_t concurrent_id = 0;
cel_result result = cel_lock_acquire_concurrent(
    &lock,
    CEL_MAX_CONCURRENT,
    &concurrent_id);

if (result == CEL_RESULT_SUCCESS) {
    /* Concurrent business work */

    cel_lock_release_concurrent(&lock);
}
```

Immediate attempt:

```c
int32_t concurrent_id = 0;
cel_result result = cel_lock_try_acquire_concurrent(
    &lock,
    CEL_MAX_CONCURRENT,
    &concurrent_id);

if (result == CEL_RESULT_SUCCESS) {
    /* acquired */
    cel_lock_release_concurrent(&lock);
} else if (result == CEL_RESULT_NOT_ACQUIRED) {
    /* skipped */
}
```

Timed attempt:

```c
int32_t concurrent_id = 0;
cel_result result = cel_lock_try_acquire_concurrent_for(
    &lock,
    250,
    CEL_MAX_CONCURRENT,
    &concurrent_id);
```

A negative timeout means an infinite wait. `0` performs an immediate attempt.

### Exclusive

```c
if (cel_lock_acquire_exclusive(&lock) == CEL_RESULT_SUCCESS) {
    /* Exclusive business work */

    cel_lock_release_exclusive(&lock);
}
```

Immediate/conditional form:

```c
cel_result result = cel_lock_try_acquire_exclusive(&lock, true);
```

When `preempt_concurrent` is `true`, this follows the C# `TryAcquireExclusive(true)` semantics: it may wait for existing Concurrent holders, but it fails when an existing Exclusive pressure window already prevents entry or when an upgrade request takes priority.

When `preempt_concurrent` is `false`, it attempts Exclusive only when the lock is immediately Idle.

Timed preemptive form:

```c
cel_result result = cel_lock_try_acquire_exclusive_for(&lock, 250);
```

### Upgrade

```c
int32_t concurrent_id;
cel_lock_acquire_concurrent(
    &lock,
    CEL_MAX_CONCURRENT,
    &concurrent_id);

/* inspect under Concurrent */

cel_lock_concurrent_to_exclusive(&lock);

/* now Exclusive */

cel_lock_release_exclusive(&lock);
```

### Conditional upgrade with ContextID

```c
int32_t concurrent_id;
cel_lock_acquire_concurrent(
    &lock,
    CEL_MAX_CONCURRENT,
    &concurrent_id);

cel_result result =
    cel_lock_try_concurrent_to_exclusive_with_switch_context_id(
        &lock,
        new_context_id);

if (result == CEL_RESULT_SUCCESS) {
    /* now Exclusive */
    cel_lock_release_exclusive(&lock);
} else if (result == CEL_RESULT_NOT_ACQUIRED) {
    /* original Concurrent was already released */
}
```

### Conditional upgrade with EpochID

```c
cel_result result =
    cel_lock_try_concurrent_to_exclusive_with_raise_epoch_id(
        &lock,
        new_epoch_id);
```

Failure again means that the previous Concurrent permission has already been released.

### Downgrade

```c
cel_lock_acquire_exclusive(&lock);

/* Exclusive modification */

cel_lock_exclusive_to_concurrent(&lock);

/* now Concurrent */

cel_lock_release_concurrent(&lock);
```

### Result codes

| Result | Meaning |
|---|---|
| `CEL_RESULT_SUCCESS` | Operation succeeded. |
| `CEL_RESULT_NOT_ACQUIRED` | A Try operation did not acquire permission. |
| `CEL_RESULT_TIMEOUT` | Timed operation expired. |
| `CEL_RESULT_INVALID_ARGUMENT` | Invalid pointer, limit, or output argument. |
| `CEL_RESULT_NOT_INITIALIZED` | Lock was not initialized. |
| `CEL_RESULT_BUSY` | Destruction was attempted while the lock remained active/busy. |
| `CEL_RESULT_CAPACITY_EXCEEDED` | The 31-bit Concurrent count boundary was exceeded. |
| `CEL_RESULT_PLATFORM_ERROR` | A platform synchronization operation failed. |

`cel_result_string` returns a stable diagnostic string for these result values.

---

## C++ API

Include:

```cpp
#include <ConcurrentExclusiveLock.hpp>
```

Use namespace:

```cpp
using intomic::ConcurrentExclusiveLock;
```

The C++ lock owns an embedded C lock and initializes/destroys it automatically.

```cpp
class Entity {
private:
    ConcurrentExclusiveLock locker_;
};
```

The C++ lock is deliberately non-copyable and non-movable because relocating an active platform mutex or duplicating its state would be invalid.

### Concurrent

```cpp
void ReadState() {
    int concurrentID = locker_.AcquireConcurrent();

    ReadEntityState();

    locker_.ReleaseConcurrent();
}
```

### Exclusive

```cpp
void ModifyState() {
    locker_.AcquireExclusive();

    ModifyEntityState();

    locker_.ReleaseExclusive();
}
```

### Timed Try

```cpp
using namespace std::chrono_literals;

if (locker_.TryAcquireExclusive(250ms)) {
    ModifyEntityState();
    locker_.ReleaseExclusive();
}
```

C++ Try methods return `0` or `false` when permission is not acquired. Invalid arguments, capacity errors, initialization errors, and platform errors are reported as exceptions.

---

## C++ Scope

`ConcurrentExclusiveLockScope` is the recommended C++ form for business code with early returns, exceptions, or permission conversions.

It tracks the final permission held by the scope and releases it in the destructor.

```cpp
using intomic::ConcurrentExclusiveLockScope;

void ReadState() {
    ConcurrentExclusiveLockScope scope(locker_);
    scope.AcquireConcurrent();

    ReadEntityState();

    // Explicit release is optional.
}
```

### Exclusive exception safety

```cpp
void ModifyState() {
    ConcurrentExclusiveLockScope scope(locker_);
    scope.AcquireExclusive();

    ModifyEntityState(); // may throw

    // Scope destruction releases Exclusive during stack unwinding.
}
```

### Inspect, then conditional upgrade

```cpp
void ApplyEpoch(std::int32_t targetEpoch) {
    ConcurrentExclusiveLockScope scope(locker_);
    scope.AcquireConcurrent();

    InspectCurrentState();

    if (!scope.TryConcurrentToExclusiveWithRaiseEpochID(targetEpoch)) {
        // Concurrent was already released by the failed protocol operation.
        return;
    }

    ApplyEpochUpdate();
    // Scope now holds Exclusive.
}
```

### Downgrade

```cpp
void RebuildAndPublish() {
    ConcurrentExclusiveLockScope scope(locker_);
    scope.AcquireExclusive();

    RebuildEntityState();

    scope.ExclusiveToConcurrent();

    PublishSnapshot();
    // Scope now holds Concurrent.
}
```

Scope rules:

- one calling context only;
- not thread-safe as an object;
- not copyable or movable;
- does not restore or clear ContextID/EpochID;
- explicit release updates the tracked final state;
- `Dispose()` releases the final held permission at most once, and the destructor calls it automatically;
- destructor never throws.

---

## C++ Pipeline

`ConcurrentExclusiveLockPipeline` executes a sequence of synchronous business segments. Each segment declares the permission required for its execution.

```cpp
using intomic::ConcurrentExclusiveLockPipeline;
using intomic::ConcurrentExclusiveLockSegment;

ConcurrentExclusiveLockPipeline pipeline(locker_);

pipeline.DoPipeline(
    ConcurrentExclusiveLockSegment::Concurrent([&] {
        ReadCurrentState();
    }),

    ConcurrentExclusiveLockSegment::TryApplyIDConvergeExclusive(
        [&] {
            ApplyNewEpoch();
        },
        targetEpoch,
        ConcurrentExclusiveLockSegment::IDType::EpochID),

    ConcurrentExclusiveLockSegment::ConvergeConcurrent([&] {
        PublishNewSnapshot();
    }),

    ConcurrentExclusiveLockSegment::None([&] {
        NotifyOtherSystems();
    }));
```

The Pipeline uses the permission successfully held by the preceding segment to release, reacquire, continue, upgrade, or downgrade permission.

### Segment types

| Factory | Semantics |
|---|---|
| `None` | Release any held permission and run without CEL permission. |
| `Concurrent` | Acquire an independent Concurrent segment. Even a preceding Concurrent is released and reacquired. |
| `TryConcurrent` | Attempt an independent Concurrent segment; skip on failure. |
| `Exclusive` | Acquire an independent Exclusive segment. Even a preceding Exclusive is released and reacquired. |
| `TestExclusive` | Attempt Exclusive only while Idle; do not preempt Concurrent. |
| `TryExclusive` | Attempt preemptive Exclusive; may yield to an upgrade request. |
| `ConvergeConcurrent` | Continue Concurrent, downgrade Exclusive, or acquire Concurrent. |
| `ConvergeExclusive` | Continue Exclusive, upgrade Concurrent, or acquire Exclusive. |
| `TryApplyIDConvergeExclusive` | Apply ContextID/EpochID and converge to Exclusive only on success. |

### Try Segment behavior

When a Try-type segment does not meet its condition:

- the current segment is not executed;
- no exception is thrown for ordinary failure;
- any permission still associated with the failed transition is released according to the protocol;
- the Pipeline continues from the None state;
- later segments continue to execute.

### Exceptions

If a segment throws:

1. subsequent segments are not executed;
2. the Scope releases the final held permission during stack unwinding;
3. the original exception propagates to the caller.

---

## Synchronous and Asynchronous Boundaries

CEL is a synchronous permission protocol.

Exclusive permission is thread-affine and must be released or downgraded by the thread that acquired it.

A Pipeline segment is a synchronous `std::function<void()>`. All protected work must finish before the function returns.

Unsupported pattern:

```cpp
ConcurrentExclusiveLockSegment::Exclusive([&] {
    std::thread([&] {
        ModifyEntityState();
    }).detach();
    // The segment returns while detached work is still running.
});
```

`DoPipelineAsync` schedules the **entire synchronous Pipeline** using `std::async`. It does not allow an individual segment to outlive its synchronous callback.

The lock object and every object captured by the segments must outlive the asynchronous Pipeline operation.

---

## State Observation

### ObservedState

Possible values:

```text
Idle
Concurrent
Exclusive
```

This is an observational snapshot only.

After a preemptive Exclusive request enters the contention window, `ObservedState` may already report Exclusive while existing Concurrent holders are still draining. Therefore, it represents the observed access tendency/transition state, not proof that Exclusive business code is already executing.

### ObservedContention

This is an observational contention-pressure indicator.

- pure Concurrent operation normally reports `0`;
- once Exclusive pressure exists, it reports the observed combined Concurrent and Exclusive pressure;
- it is intended for diagnostics, monitoring, or scheduling hints;
- it must not be used as an authoritative synchronization predicate.

---

## Low-Allocation Design

### C lock

`cel_lock` is embedded directly in caller-owned storage. Initialization does not allocate a separate lock token.

### C++ lock and Scope

`ConcurrentExclusiveLock` embeds `cel_lock` directly. `ConcurrentExclusiveLockScope` stores only a pointer and its final permission accounting state.

### Hot paths

Ordinary Concurrent acquisition and release operate on the atomic counter fast path. They do not allocate and normally do not enter the platform monitor.

Exclusive acquisition and upgrades enter the serialized monitor slow path.

### Pipeline allocation

A variadic `DoPipeline` call constructs a fixed `std::array` of Segment values at the call site. Each Segment contains a `std::function<void()>`; whether the callable allocates depends on the standard-library small-object optimization and the size of the captured object.

The lock itself does not allocate business objects, callbacks, or task state.

---

## Use Cases

### Entity-level state

Assign one lock to each player, room, entity, order, session, or aggregate.

### Cache loading

1. inspect under Concurrent;
2. if loaded, continue Concurrent;
3. otherwise use ContextID/EpochID to select one loader;
4. selected caller upgrades to Exclusive;
5. publish the loaded state;
6. downgrade or release.

### Order/risk workflow

1. read order state under Concurrent;
2. perform external validation without CEL permission;
3. use EpochID to select the current commit phase;
4. converge to Exclusive for the unique mutation;
5. downgrade for follow-up reads;
6. acquire an independent final Exclusive segment for settlement if required.

### Versioned state publication

Concurrent readers use the current snapshot while an EpochID-selected caller becomes the unique publisher of the next version.

---

## Design Boundaries

CEL deliberately does not provide:

- recursive Concurrent or Exclusive nesting;
- strict FIFO ordering;
- ownership tokens;
- automatic deadlock detection;
- automatic ContextID/EpochID lifecycle management;
- process-shared synchronization;
- coroutine-aware permission transfer;
- safe destruction while users or waiters remain;
- automatic protection of object lifetime.

Rules:

- do not acquire ordinary Exclusive while holding Concurrent; upgrade instead;
- do not acquire ordinary Concurrent while holding Exclusive; downgrade instead;
- release according to the final converted permission;
- do not copy an initialized C lock;
- do not move/copy the C++ lock or Scope;
- do not cross an asynchronous/thread boundary while relying on Exclusive permission;
- do not use observed snapshots as lock-state predicates.

Misuse of release/conversion functions is outside the defined protocol and may corrupt the state counter or violate platform mutex ownership rules.

---

## Tests and Stress Tests

Build the TestAndBenchmark target, then run:

```shell
./build/TestAndBenchmark/TestAndBenchmark --help
```

Full semantic regression:

```shell
./build/TestAndBenchmark/TestAndBenchmark --full-semantics --lock-instances 8 --semantic-workers 4 --semantic-operations 256
```

Deterministic Pipeline contracts:

```shell
./build/TestAndBenchmark/TestAndBenchmark --pipeline-semantics
```

Randomized Pipeline stress:

```shell
./build/TestAndBenchmark/TestAndBenchmark --pipeline-stress 10m --lock-instances 8 --semantic-workers 8 --semantic-operations 256
```

The three semantic parameters are maxima in this mode. Every finite batch chooses a reproducible random shape within those limits, prints a heartbeat every 10 seconds, and reports a failure if one batch makes no worker progress for 10 minutes.

Single-lock Exclusive contention diagnostics:

```shell
./build/TestAndBenchmark/TestAndBenchmark --contention-stress 10m --semantic-workers 64
```

The semantic suite covers:

- C API compilation and direct use;
- Concurrent/Exclusive exclusion;
- Concurrent IDs and maxConcurrent;
- preemptive Exclusive;
- upgrade/downgrade;
- multiple upgrade serialization;
- ContextID single-winner conditional upgrade;
- EpochID conditional upgrade;
- timeout paths;
- Scope release on normal, converted, and exception paths;
- Pipeline transitions, Try failure, and exception release;
- randomized legal paths across many independent locks.

See [TESTING.md](TESTING.md) for details.

---

## Performance Benchmark

Default benchmark:

```shell
./build/TestAndBenchmark/TestAndBenchmark
```

A larger memory-workload run:

```shell
./build/TestAndBenchmark/TestAndBenchmark --lock-instances 8 --threads 8 --workload memory --operations 500000 --memory-mb 64 --read-work 32 --write-work 32
```

The standard comparison includes:

- `std::mutex`;
- `std::shared_mutex`;
- CEL;
- `CEL(ExclusiveOnly)`.

Every strategy receives a fresh Work instance. Read/write decisions are deterministic per thread, and the benchmark verifies that all strategies complete the same read count, write count, and final state hash.

The memory Work follows the C# benchmark model: each lock owns a shared memory region, reads perform random indexed loads and mixing, and writes update random positions plus a serialized state hash.

Benchmark results depend on the compiler, standard library, operating system, mutex implementation, CPU topology, NUMA placement, workload size, thread count, and business work. They are not universal claims.

See [PERFORMANCE.md](PERFORMANCE.md).

---

## Observed Local Benchmark Results

The following results come from two independent Release-build memory-workload runs. They are included as reproducible observations, not as universal cross-platform performance claims. The Linux and Windows runs use different hardware and workload parameters, so the absolute values must not be compared directly across the two machines.

### Linux: multiple fine-grained locks

```text
Compiler:          GCC/G++ 14.2.0
Container CPUs:    5 reported hardware threads
Lock instances:   4
Threads per lock: 4
Total threads:    16
Workload:         memory, 64 MiB shared per lock
Operations:       200,000 per thread
Total operations: 3,200,000 per strategy and scenario
Read/write work:  32 / 32 steps
```

CEL compared with `std::shared_mutex`:

| Read/write | `std::shared_mutex` works/s | CEL works/s | CEL throughput difference | `std::shared_mutex` avg write | CEL avg write | Write-latency comparison |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 4,902,155 | 4,771,451 | -2.67% | — | — | — |
| 99.5/0.5 | 3,282,886 | 3,421,835 | +4.23% | 497.30 μs | 89.85 μs | 5.54× lower |
| 90/10 | 1,629,067 | 3,108,759 | +90.83% | 59.95 μs | 7.97 μs | 7.52× lower |
| 50/50 | 2,663,306 | 2,687,139 | +0.89% | 8.14 μs | 5.11 μs | 1.59× lower |
| 30/70 | 2,872,152 | 2,734,470 | -4.79% | 6.16 μs | 4.82 μs | 1.28× lower |
| 0/100 | 2,334,148 | 2,701,963 | +15.76% | 5.71 μs | 5.06 μs | 1.13× lower |

### Windows: one lock under 64-thread contention

```text
Compiler/runtime:  MSVC Release build on Windows
Reported CPUs:     16
Lock instances:   1
Threads per lock: 64
Total threads:    64
Workload:         memory, 64 MiB shared by the lock
Operations:       10,000 per thread
Total operations: 640,000 per strategy and scenario
Read/write work:  64 / 128 steps
```

CEL compared with `std::shared_mutex`:

| Read/write | `std::shared_mutex` works/s | CEL works/s | CEL throughput difference | `std::shared_mutex` avg write | CEL avg write | Write-latency comparison |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 6,867,130 | 7,138,491 | +3.95% | — | — | — |
| 99.5/0.5 | 3,608,730 | 3,552,412 | -1.56% | 253.19 μs | 30.28 μs | 8.36× lower |
| 90/10 | 916,086 | 961,376 | +4.94% | 179.91 μs | 75.39 μs | 2.39× lower |
| 50/50 | 334,891 | 375,224 | +12.04% | 175.04 μs | 173.40 μs | approximately equal |
| 30/70 | 270,018 | 299,836 | +11.04% | 182.01 μs | 214.06 μs | 17.61% higher |
| 0/100 | 235,043 | 200,049 | -14.89% | 270.48 μs | 316.45 μs | 17.00% higher |

### Interpretation

These measurements do not show a universal throughput winner. They show a more useful and more reproducible pattern:

- In the pure-Concurrent case, CEL remained close to `std::shared_mutex` in both environments: -2.67% in the Linux run and +3.95% in the Windows run.
- With only 0.5% writes, total throughput stayed within approximately 4.3% of `std::shared_mutex`, while CEL reduced average write time by 5.54× on Linux and 8.36× on Windows. This is the clearest result for CEL's preemptive-Exclusive design goal.
- At 90/10, CEL had higher throughput and substantially lower average write time in both runs. The unusually large Linux throughput gap should still be treated as platform- and scheduler-specific rather than a universal ratio.
- At 50/50, CEL was slightly faster on Linux and 12.04% faster on Windows; write latency was lower on Linux and effectively equal on Windows.
- Write-heavy and pure-Exclusive results were mixed. CEL was not consistently faster: it lost throughput in the Linux 30/70 case and the Windows 0/100 case, while winning the other corresponding runs. This is why the project does not claim universal superiority over `std::shared_mutex`.

`std::shared_mutex` is a mature, highly optimized standard-library primitive and therefore a strong baseline. It also has a narrower semantic surface. Within CEL's intended **non-recursive** permission model, the same implementation supports preemptive Exclusive acquisition, in-place Concurrent-to-Exclusive upgrade, in-place Exclusive-to-Concurrent downgrade, Try and timed acquisition, ContextID/EpochID-conditioned convergence, RAII Scope management, and synchronous Pipeline orchestration.

The significant result is therefore not that CEL wins every throughput row. It is that CEL remains close to or exceeds a highly optimized baseline in several important workloads—especially rare-write and mixed-access workloads—while retaining the complete acquisition, conversion, conditional-convergence, Scope, and Pipeline semantics of its design. Applications should still benchmark their own lock topology, contention level, critical-region duration, compiler, standard library, operating system, and CPU architecture.

Complete raw outputs:

- [`TestResults/benchmark-memory-long-linux.txt`](TestResults/benchmark-memory-long-linux.txt)
- [`TestResults/benchmark-memory-long-windows.txt`](TestResults/benchmark-memory-long-windows.txt)

---

## Project Layout

```text
ConcurrentExclusiveLock-C-Cpp/
├─ include/
│  ├─ ConcurrentExclusiveLock.h       # Public C API
│  └─ ConcurrentExclusiveLock.hpp     # Inline C++ Lock/Scope/Segment wrappers
├─ src/
│  ├─ ConcurrentExclusiveLock.c       # C core + platform backends
│  ├─ ConcurrentExclusiveLock.cpp     # C++ lifecycle/error bridge + Pipeline execution
│  └─ ConcurrentExclusiveLockInternal.h
├─ TestAndBenchmark/
│  ├─ c_api_smoke.c
│  ├─ SemanticTests.cpp
│  ├─ Benchmark.cpp
│  └─ main.cpp
├─ cmake/
├─ CMakeLists.txt
├─ README.md
├─ README_CN.md
├─ TESTING.md
├─ TESTING_CN.md
├─ PERFORMANCE.md
├─ PERFORMANCE_CN.md
├─ VERIFICATION.md
├─ LICENSE-MIT
└─ LICENSE-APACHE-2.0
```

---

## Project Status

Version: **1.0.0 initial port**

Implemented:

- complete C core API;
- Windows and POSIX monitor/atomic backends;
- C++ Lock wrapper;
- C++ RAII Scope;
- complete C++ Segment/Pipeline state machine;
- timed acquisition overloads;
- semantic, randomized stress, contention, and benchmark executable;
- CMake build/install/package integration;
- English and Chinese documentation.

The package has passed the included semantic suite, randomized Pipeline stress, AddressSanitizer/UndefinedBehaviorSanitizer, and ThreadSanitizer in the Linux sandbox used to create this artifact. Platform-specific validation on Windows and other POSIX targets should still be performed before publishing binaries for those systems.

Author of the original design and reference implementation: **YiBoWang (王弈博)**.

Repository: `https://github.com/WangHHB/ConcurrentExclusiveLock`

---

## License

ConcurrentExclusiveLock is dual-licensed under either:

- MIT License; or
- Apache License 2.0.

You may choose either license.

See [LICENSE-MIT](LICENSE-MIT) and [LICENSE-APACHE-2.0](LICENSE-APACHE-2.0).
