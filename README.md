<p align="center">
  <strong>English</strong> ｜ <a href="README_CN.md">简体中文</a>
</p>

# ConcurrentExclusiveLock

[![C# Build and Test](https://github.com/WangHHB/ConcurrentExclusiveLock/actions/workflows/dotnet.yml/badge.svg)](https://github.com/WangHHB/ConcurrentExclusiveLock/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/ConcurrentExclusiveLock.svg)](https://www.nuget.org/packages/ConcurrentExclusiveLock/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ConcurrentExclusiveLock.svg)](https://www.nuget.org/packages/ConcurrentExclusiveLock/)
[![License](https://img.shields.io/badge/License-MIT%20OR%20Apache--2.0-blue.svg)](#license)

**ConcurrentExclusiveLock (CEL)** is a Concurrent/Exclusive synchronization protocol designed for fine-grained state objects.

## Implementations

- [C#](./csharp) — Reference implementation
- [Java](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/java/README.md) — Java 17+, available on [Maven Central](https://central.sonatype.com/artifact/io.github.wanghhb/concurrent-exclusive-lock)
- C++ — Planned


## Installation

```shell
dotnet add package ConcurrentExclusiveLock
```

It is suitable for assigning an independent lock instance to each player, room, entity, session, Actor, aggregate root, or task context. When a large number of lock objects coexist, CEL coordinates:

- concurrent access;
- exclusive access;
- preemptive Exclusive acquisition;
- in-place Concurrent → Exclusive upgrades;
- in-place Exclusive → Concurrent downgrades;
- ContextID / EpochID business-state coordination;
- permission workflow orchestration;
- automatic release on exceptional paths.

The current **C# / .NET implementation** is the original and authoritative version.

---

## Table of Contents

- [Core Concepts](#core-concepts)
- [Why This Is Not a ReadWriteLock](#why-this-is-not-a-readwritelock)
- [Preemptive Exclusive](#preemptive-exclusive)
- [In-Place Upgrade and Downgrade](#in-place-upgrade-and-downgrade)
- [ContextID and EpochID](#contextid-and-epochid)
- [Three API Layers](#three-api-layers)
- [Quick Start](#quick-start)
- [Pipeline](#pipeline)
- [Synchronous and Asynchronous Boundaries](#synchronous-and-asynchronous-boundaries)
- [Low-Allocation Design](#low-allocation-design)
- [State Observation](#state-observation)
- [Use Cases](#use-cases)
- [Design Boundaries](#design-boundaries)
- [Test Project](#test-project)
- [Performance](#performance)
- [Project Status](#project-status)

---

## Core Concepts

CEL expresses **access permission**, not the read/write intent of the code inside the protected region.

### Concurrent

Concurrent means that the current operation may enter at the same time as other Concurrent operations.

A Concurrent region is not necessarily read-only. It may perform modifications as long as the business rules guarantee that the concurrent operations do not conflict.

### Exclusive

Exclusive means that the current operation must execute alone and may not run concurrently with any Concurrent or Exclusive operation.

An Exclusive region is not necessarily write-only. It may contain substantial reading, validation, and computation logic.

Therefore, the question CEL answers is:

> May this business operation execute concurrently with other business operations?

It does not answer:

> Is this code reading data or writing data?

---

## Why This Is Not a ReadWriteLock

A traditional Reader/Writer Lock is primarily built around the semantics of shared reads and exclusive writes.

CEL targets a broader entity-level permission model:

- multiple non-conflicting state modifications may execute concurrently;
- some purely read-oriented operations may still require exclusive access;
- a business operation may first inspect state concurrently and then upgrade to become the unique committer;
- after an exclusive update, the operation may need to retain a continuous Concurrent context;
- permission acquisition may be coupled with a ContextID or EpochID transition;
- a single workflow may perform multiple permission transitions.

For this reason, CEL uses Concurrent / Exclusive rather than Read / Write.

---

## Preemptive Exclusive

The main characteristic of CEL is **preemptive Exclusive** acquisition.

Normal Concurrent acquisition and release primarily use lightweight atomic counters and do not enter the `Monitor` ordering queue.

When an Exclusive request enters the contention window:

1. new Concurrent operations are prevented from entering;
2. existing Concurrent holders are allowed to leave naturally;
3. the Exclusive request acquires permission after Concurrent holders have drained;
4. normal contention resumes after Exclusive is released.

This means that under continuous Concurrent traffic, an Exclusive request does not have to wait indefinitely for an accidental fully idle window.

Normal Exclusive acquisition and Concurrent → Exclusive transitions use `Monitor` for mutual exclusion, waiting, wake-up behavior, and exclusive ordering.

CEL does not provide an additional strict FIFO guarantee and does not promise stronger fairness than `Monitor`. Actual execution order is still affected by OS scheduling, CPU topology, cache state, system load, and business-operation duration.

---

## In-Place Upgrade and Downgrade

### Concurrent → Exclusive

A typical business workflow is often more complex than simply “lock and modify”:

1. inspect or validate state under Concurrent permission;
2. decide whether a modification is required;
3. attempt to become the unique committer for the relevant business condition;
4. enter Exclusive permission after success;
5. apply the modification.

CEL supports converging directly from the current Concurrent context to Exclusive without first releasing Concurrent and then competing again from outside.

The current business-condition upgrade methods include:

```csharp
ConcurrentToExclusive();
TryConcurrentToExclusiveWithSwitchContextID(int newContextID);
TryConcurrentToExclusiveWithRaiseEpochID(int newEpochID);
```

After a successful upgrade, the current call context holds Exclusive permission.

After a failed upgrade, the original Concurrent permission has already been released by the protocol. The caller must not call `ReleaseConcurrent()` again.

### Exclusive → Concurrent

After the exclusive modification is complete, permission can be downgraded directly:

```csharp
scope.ExclusiveToConcurrent();
```

After the downgrade:

- Exclusive permission is no longer held;
- Concurrent permission remains held;
- follow-up logic that depends on a continuous access context may continue;
- no new contention window is introduced by releasing Exclusive and reacquiring Concurrent.

---

## ContextID and EpochID

CEL can associate two business identifiers with the lock state.

### ContextID

`ContextID` represents the identity of the current business context, for example:

- the current room instance;
- the current battle context;
- the current player session;
- the current data-loading batch;
- the current task owner;
- the current logical transaction context.

```csharp
bool changed = locker.SwitchContextID(newContextID);
```

`SwitchContextID` returns `false` when the new value is equal to the current value.

It can be used to recognize the same business context and avoid repeating initialization, switching, commit, or Exclusive logic within that context.

### EpochID

`EpochID` represents a lifecycle, version, or phase that may only move forward, for example:

- an entity version;
- a room tick;
- a battle phase;
- a snapshot version;
- a lifecycle generation;
- a data-processing batch.

```csharp
bool raised = locker.RaiseEpochID(newEpochID);
```

The update succeeds only when `newEpochID` is greater than the current value.

ContextID and EpochID are business states outside the core locking protocol. Their meaning, allocation, cleanup rules, and lifecycle are defined by the caller.

---

## Three API Layers

The project provides three API layers.

### 1. ConcurrentExclusiveLock

`ConcurrentExclusiveLock` is the low-level synchronization protocol.

```csharp
private readonly ConcurrentExclusiveLock _locker = ConcurrentExclusiveLock.Create();
```

It is a `readonly struct`, while the actual shared state is stored in an internal token.

Copying a `ConcurrentExclusiveLock` value does not copy the lock state. The copied value still refers to the same internal synchronization state.

A default-initialized instance is invalid and must not be used. Instances must be created with:

```csharp
ConcurrentExclusiveLock.Create();
```

Common APIs:

```csharp
AcquireConcurrent();
TryAcquireConcurrent();

AcquireExclusive();
TryAcquireExclusive();

ReleaseConcurrent();
ReleaseExclusive();

ExclusiveToConcurrent();

SwitchContextID(...);
RaiseEpochID(...);

ConcurrentToExclusive();
TryConcurrentToExclusiveWithSwitchContextID(...);
TryConcurrentToExclusiveWithRaiseEpochID(...);
```

This layer is suitable for low-level code that requires precise control over each acquisition, release, and transition.

---

### 2. ConcurrentExclusiveLockScope

`ConcurrentExclusiveLockScope` is a `using`-based permission-lifetime wrapper.

```csharp
using (var scope = new ConcurrentExclusiveLockScope(_locker))
{
    scope.AcquireConcurrent();

    ReadEntityState();
}
```

The caller may release the current permission manually.

When permission has not been released manually, `Dispose()` releases Concurrent or Exclusive according to the final permission state recorded by the Scope.

Scope primarily reduces release errors on paths involving:

- exceptions;
- early returns;
- multiple branch exits;
- Concurrent → Exclusive upgrades;
- Exclusive → Concurrent downgrades;
- state changes after failed Try operations.

`Dispose()` only releases access permission still held by the current Scope. It does not restore or clear ContextID / EpochID.

Scope is a mutable value type with release responsibility and must only be owned and operated by a single call context.

Do not copy a Scope, pass it by value, operate on it across threads, or separately operate on multiple copies of the same Scope.

---

### 3. ConcurrentExclusiveLockPipeline

`ConcurrentExclusiveLockPipeline` describes a complete permission workflow as an ordered sequence of Segments.

Each Segment declares:

- the business code to execute;
- the access permission required by the Segment;
- an optional ContextID or EpochID condition.

Based on the permission successfully held by the previous Segment, the Pipeline automatically decides whether to:

- continue using the current permission;
- release and reacquire permission;
- upgrade in place;
- downgrade in place;
- skip the current Segment when its condition fails;
- continue later Segments from the None state.

The role of the Pipeline can be summarized as:

> Entity Permission Workflow Orchestration

---

## Quick Start

### Concurrent

```csharp
private readonly ConcurrentExclusiveLock _locker = ConcurrentExclusiveLock.Create();

public void ReadState()
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireConcurrent();

        ReadEntityState();

        //You may release manually at last, or let scope.Dispose() release the final held access.
        //scope.ReleaseConcurrent();
    }
}
```

### Exclusive

```csharp
public void ModifyState()
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireExclusive();

        ModifyEntityState();

        //You may release manually at last, or let scope.Dispose() release the final held access.
        //scope.ReleaseExclusive();
    }
}
```

### Upgrade Concurrent To Exclusive

```csharp
public void ExecuteCommand(PlayerCommand command)
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireConcurrent();

        if (!CanPrepareCommand(command))
        {
            scope.ReleaseConcurrent();
            return;
        }

        PreparedCommand prepared = PrepareCommand(command);

        scope.ConcurrentToExclusive();

        if (CanCommitCommand(prepared))
        {
            CommitCommand(prepared);
        }

        //The final held access is Exclusive; release it manually or let scope.Dispose() release it.
        //scope.ReleaseExclusive();
    }
}
```

### Inspect Under Concurrent, Then Upgrade To Exclusive

```csharp
public void ApplyEpoch(int targetEpoch)
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireConcurrent();

        InspectCurrentState();

        if (!scope.TryConcurrentToExclusiveWithRaiseEpochID(targetEpoch))
        {
            // The original Concurrent permission has already been released when the upgrade fails.
            return;
        }

        ApplyEpochUpdate();

        //The final held access is Exclusive; release it manually or let scope.Dispose() release it.
        //scope.ReleaseExclusive();
    }
}
```

### Downgrade After Exclusive Work

```csharp
public void RebuildAndPublish()
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireExclusive();

        RebuildEntityState();

        scope.ExclusiveToConcurrent();

        PublishSnapshot();

        //The final held access is Concurrent; release it manually or let scope.Dispose() release it.
        //scope.ReleaseConcurrent();
    }
}
```

---

## Pipeline

### Example

```csharp
var pipeline = new ConcurrentExclusiveLockPipeline(_locker);

pipeline.DoPipeline(
    ConcurrentExclusiveLockSegment.Concurrent(() =>
    {
        ReadCurrentState();
    }),

    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
    {
        ApplyNewEpoch();
    }, targetEpoch, ConcurrentExclusiveLockSegment.IDType.EpochID),

    ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
    {
        PublishNewSnapshot();
    }),

    ConcurrentExclusiveLockSegment.None(() =>
    {
        NotifyOtherSystems();
    })
);
```

### Segment Types

| Segment | Semantics |
|---|---|
| `None` | Executes without holding access permission |
| `Concurrent` | Acquires an independent Concurrent permission; consecutive Segments of the same type are still separated and reacquired |
| `TryConcurrent` | Attempts to acquire an independent Concurrent permission; skips the Segment on failure |
| `Exclusive` | Acquires an independent Exclusive permission; consecutive Segments of the same type are still released and reacquired |
| `TestExclusive` | Attempts Exclusive only when the lock is Idle and does not preempt existing Concurrent holders |
| `TryExclusive` | Attempts preemptive Exclusive and may block new Concurrent entries |
| `ConvergeConcurrent` | Continues existing Concurrent access, downgrades Exclusive to Concurrent in place when possible, or acquires Concurrent access |
| `ConvergeExclusive` | Continues existing Exclusive access, upgrades Concurrent to Exclusive in place, or acquires Exclusive access |
| `TryApplyIDConvergeExclusive` | Attempts to apply a ContextID / EpochID and converges to Exclusive after success |

### Try Segment Behavior

When a Try Segment does not obtain its execution condition:

- the current Segment is not executed;
- the Pipeline does not throw because of that failure;
- the Pipeline does not terminate early;
- the current permission state is treated as None;
- later Segments continue to be processed.

### Independent Permissions and Converged Permissions

`Concurrent` and `Exclusive` represent independent permission Segments.

Even when the previous Segment already holds the same permission, the Pipeline releases and reacquires it, giving other contenders an opportunity to enter.

`ConvergeConcurrent` represents continuing an existing Concurrent context, attempting to establish one by downgrading an Exclusive context in place, or acquiring a new Concurrent context.

`ConvergeExclusive` represents continuing an existing Exclusive context, establishing one by upgrading a Concurrent context in place, or acquiring a new Exclusive context.

`TryApplyIDConvergeExclusive` represents continuing an existing Exclusive context, establishing one, or acquiring a new Exclusive context after the business ID has been successfully applied.

---

## Synchronous and Asynchronous Boundaries

Pipeline Segments use synchronous delegates:

```csharp
Action Segment;
```

Therefore, the Pipeline is a **synchronous permission workflow orchestrator**.

The following code is not supported:

```csharp
ConcurrentExclusiveLockSegment.Concurrent(async () =>
{
    DoPartA();

    await SomethingAsync();

    DoPartB();
});
```

The project provides disabled `Func<Task>` overloads that reject directly supplied async lambdas at compile time. This prevents an async lambda from being converted to `async void`, which would cause the following problems:

- the Pipeline could not determine when the asynchronous portion of the Segment actually completes;
- the Pipeline could release or transition permission before asynchronous continuation code completes;
- asynchronous exceptions could not propagate normally through the Pipeline;
- Exclusive relies on a thread-owned synchronization mechanism and cannot safely cross an `await`.

Note: Due to C# overload resolution rules, a synchronous lambda that always throws may also match the disabled Func<Task> overload. In this case, explicitly cast the lambda to Action:
```csharp
ConcurrentExclusiveLockSegment.Exclusive((Action)(() =>
{
    throw new Exception();
}));
```

### DoPipelineAsync

```csharp
await pipeline.DoPipelineAsync(segments);
```

`DoPipelineAsync` means:

> Execute one complete synchronous Pipeline on a thread-pool thread by using `Task.Run`.

It does not make Segments awaitable and is not a native asynchronous locking protocol.

When the caller is already running on a worker thread, thread-pool thread, or server request thread, calling the synchronous `DoPipeline()` method directly is usually preferable.

---

## Low-Allocation Design

CEL is designed for large numbers of fine-grained lock objects and frequently executed hot paths.

### Lock Instance

Each call to `ConcurrentExclusiveLock.Create()` creates one internal token.

The token's core state fields include:

- a 64-bit Counter;
- a 32-bit ContextID;
- a 32-bit EpochID.

The core state fields total 128 bits.

This 128-bit figure does not include the CLR object header, references, or alignment overhead.

`Monitor` operates directly on the internal token, so no separate synchronization object is created.

### Hot Paths

After the lock instance has been initialized:

- `ConcurrentExclusiveLock` is a value-type handle;
- `ConcurrentExclusiveLockScope` is a struct;
- `ConcurrentExclusiveLockPipeline` is a readonly struct;
- creating and releasing a Scope does not require allocating a new object for every entry;
- the normal Concurrent path primarily uses atomic operations.

This makes CEL suitable for:

- Unity3D `Update`;
- game-server entity loops;
- high-frequency state updates;
- environments with strict GC control.

### Allocations Still Controlled by the Caller

The library's low-allocation design cannot eliminate allocations introduced by business code, such as:

- lambdas that capture local variables;
- dynamically creating delegates on every call;
- creating a new Segment array through `params` on every call;
- calling `Task.Run`;
- allocating objects inside business logic.

On extremely hot paths, delegates and Segment arrays can be cached, and the synchronous API can be called directly.

---

## State Observation

CEL provides two observation properties:

```csharp
ConcurrentExclusiveLockState ObservedState;
int ObservedContention;
```

### ObservedState

`ObservedState` represents the access tendency or transition state observed at the instant of reading.

It does not mean that a thread is necessarily already executing Exclusive business code at that exact moment.

For example, when a preemptive Exclusive request has entered the contention window but is still waiting for existing Concurrent holders to leave, the observed state may already be Exclusive.

### ObservedContention

`ObservedContention` is an instantaneous contention-pressure observation value intended for:

- diagnostics;
- monitoring;
- scheduling reference;
- performance analysis.

It must not be used as a synchronization correctness condition.

Its value is 0 under purely Concurrent activity. It reflects the observed contention scale only when Exclusive pressure exists.

---

## Use Cases

CEL is particularly suitable for:

- players, rooms, battles, and map entities in game servers;
- Unity3D state access with strict heap-allocation control;
- Actor or Actor-like entities;
- session and connection state;
- cache entries and aggregate roots;
- entity lifecycle progression;
- versioned data updates;
- background-task state machines;
- inspect, upgrade, commit, and fallback workflows on the same entity;
- server systems where large numbers of fine-grained locks coexist for long periods.

A typical workflow is:

```text
Concurrent inspection
    ↓
ContextID / EpochID condition
    ↓
In-place convergence to Exclusive
    ↓
Unique commit
    ↓
Downgrade to Concurrent
    ↓
Publish or read the new state
```

---

## Design Boundaries

CEL is a synchronous, non-recursive access-permission protocol.

The following rules must be observed:

1. Do not use a default-initialized `ConcurrentExclusiveLock`; call `Create()`.
2. Do not call normal `AcquireExclusive()` while already holding Concurrent; use an upgrade protocol.
3. Do not call normal `AcquireConcurrent()` while already holding Exclusive; use a downgrade protocol.
4. Do not treat Exclusive as a recursive lock.
5. Do not copy or concurrently operate on `ConcurrentExclusiveLockScope`.
6. Do not use async lambdas inside Pipeline Segments.
7. Do not let business code that depends on current permission cross an `await`.
8. `ObservedState` and `ObservedContention` are observational only and must not establish synchronization correctness.
9. The business meaning and lifecycle of ContextID / EpochID are the caller's responsibility.
10. CEL does not guarantee strict FIFO fairness.

These constraints preserve clear synchronization semantics, low normal-path overhead, and suitability for high-frequency execution paths.

---

## Test Project

The test project validates the core synchronization protocol under different contention conditions and permission-transition paths. It mainly covers:

- basic Concurrent / Exclusive acquisition and release;
- preemptive Exclusive contention;
- Concurrent → Exclusive upgrades;
- Exclusive → Concurrent downgrades;
- ContextID / EpochID protocol behavior;
- Pipeline Segment combinations and state transitions;
- randomized stress testing;
- BenchmarkDotNet performance testing.

**The test project was written by AI.**

The test code is intended to assist with validating the current implementation, expand path coverage, and provide performance-observation data. The core synchronization protocol, API design, and semantic definitions are governed by the C# / .NET main project implementation.

## Performance

### Test Environment

- **Operating system**: Windows 11
- **CPU**: AMD Ryzen 7 5700X, 8 cores / 16 threads
- **SMT**: Enabled
- **CPU frequency**: Fixed at 4.5 GHz on all cores
- **Runtime**: .NET 8.0.22
- **GC**: No GC occurred during the benchmark
- **Worker threads**: Dedicated `Thread` instances starting from a shared gate
- **Workload**: Random access to shared memory
- **Compared implementations**:
  - `lock`
  - `ReaderWriterLockSlim`
  - `ConcurrentExclusiveLock`
  - `ConcurrentExclusiveLock` used exclusively through its Exclusive path

These results only describe the observed behavior under the hardware, runtime, workload, and benchmark parameters listed above. They are not an absolute performance guarantee for other environments.

`avg write ns` is the average write-operation latency reported by the benchmark. The current results contain averages only; they do not include P95, P99, P99.9, or maximum latency and should not be interpreted as tail-latency guarantees.

### Conclusions

#### 1. A single hot lock best exposes per-instance Concurrent parallelism

With one lock and 64 contending threads, ordinary `lock` serializes the critical section, while CEL allows Concurrent operations to execute simultaneously.

Under this memory workload, CEL achieved the following throughput relative to `lock`:

| Concurrent / Exclusive | `lock` works/s | CEL works/s | CEL / `lock` |
|---:|---:|---:|---:|
| 100 / 0 | 657,517 | 5,928,072 | **9.02×** |
| 99.5 / 0.5 | 728,260 | 4,842,249 | **6.65×** |
| 90 / 10 | 712,098 | 2,109,201 | **2.96×** |
| 50 / 50 | 678,149 | 831,019 | **1.23×** |
| 30 / 70 | 665,893 | 723,968 | **1.09×** |
| 0 / 100 | 658,964 | 655,340 | **0.99×** |

The results show a natural degradation curve:

- When the Concurrent ratio is high, CEL can make substantial use of per-instance parallelism.
- As the Exclusive ratio rises, the available parallel window gradually shrinks.
- At 100% Exclusive, CEL degrades to approximately the throughput of an ordinary mutex.
- When there is no Concurrent work to parallelize, CEL cannot create throughput gains by itself.

CEL is not intended for extremely short critical sections containing almost no useful work. Normal Concurrent acquisition and release still require updates to shared atomic state. When the useful work is smaller than the coordination cost, direct serialization may be more efficient.

#### 2. Multiple locks naturally reduce the relative throughput multiplier

With 8 lock instances and 8 threads per instance, ordinary `lock` can already execute across independent lock instances in parallel:

```text
Lock 1 -> 1 critical section
Lock 2 -> 1 critical section
...
Lock 8 -> 1 critical section
```

As a result, the throughput multiplier of CEL relative to ordinary `lock` naturally becomes smaller in the multiple-lock scenario.

| Concurrent / Exclusive | `lock` works/s | CEL works/s | CEL / `lock` |
|---:|---:|---:|---:|
| 100 / 0 | 4,290,496 | 9,932,028 | **2.31×** |
| 99.5 / 0.5 | 5,374,123 | 9,426,514 | **1.75×** |
| 90 / 10 | 5,075,562 | 6,457,895 | **1.27×** |
| 50 / 50 | 4,763,081 | 4,405,050 | **0.92×** |
| 30 / 70 | 4,589,396 | 4,379,425 | **0.95×** |
| 0 / 100 | 4,357,654 | 4,244,409 | **0.97×** |

This does not mean that CEL loses its per-lock concurrency capability. It means that an ordinary mutex also gains inter-instance parallelism.

The multiple-lock test did not show a structural throughput collapse as the number of lock instances increased. This indicates that the high single-lock throughput was not obtained through global spinning, continuously monopolizing machine resources, or interference between independent lock instances.

Each lock instance owns an independent work object. Therefore, this test uses a 64 MiB working set per instance and a 512 MiB total working set.

#### 3. A reduced throughput multiplier does not imply a reduced write-latency advantage

Throughput measures how much total work the machine completes over a period of time. It is affected by the number of lock instances, CPU core count, memory bandwidth, and the amount of useful work.

Write latency measures how long a particular write request waits for permissions to converge on its target lock. It is determined more directly by that lock's state transitions and contention model.

For a single hot lock with sparse writes and 99.5% Concurrent operations:

| Implementation | Average write latency |
|---|---:|
| `lock` | 1,856,481 ns |
| `ReaderWriterLockSlim` | 1,356,004 ns |
| CEL | **16,300 ns** |

CEL's average write latency was approximately:

- **1/114** of ordinary `lock`;
- **1/83** of `ReaderWriterLockSlim`.

The complete average write-latency comparison for the single-lock test is:

| Concurrent / Exclusive | `lock` | `ReaderWriterLockSlim` | CEL |
|---:|---:|---:|---:|
| 99.5 / 0.5 | 1,856.5 μs | 1,356.0 μs | **16.3 μs** |
| 90 / 10 | 321.2 μs | 263.7 μs | **33.6 μs** |
| 50 / 50 | 117.8 μs | 155.7 μs | **73.1 μs** |
| 30 / 70 | 99.9 μs | 124.1 μs | **75.4 μs** |
| 0 / 100 | 94.1 μs | 105.0 μs | **94.6 μs** |

When the Concurrent ratio is high, CEL's preemptive Exclusive path prevents new Concurrent operations from entering and waits only for already active Concurrent holders to leave naturally.

The writer therefore waits on a closed and continuously shrinking set of existing Concurrent holders instead of waiting for a continuously arriving Concurrent stream to become empty by chance.

As the Exclusive ratio rises, CEL's write latency gradually approaches ordinary mutex behavior. At 100% Exclusive, CEL, `CEL(ExclusiveOnly)`, and ordinary `lock` are at approximately the same latency level.

#### 4. CEL retains low average write latency with multiple locks

With 8 lock instances and 8 threads per lock:

| Concurrent / Exclusive | `lock` | `ReaderWriterLockSlim` | CEL |
|---:|---:|---:|---:|
| 99.5 / 0.5 | 144.9 μs | 949.1 μs | **54.9 μs** |
| 90 / 10 | 35.1 μs | 81.2 μs | **10.6 μs** |
| 50 / 50 | 16.2 μs | 22.6 μs | **6.0 μs** |
| 30 / 70 | 14.5 μs | 17.3 μs | **5.1 μs** |
| 0 / 100 | 14.2 μs | 15.0 μs | **14.6 μs** |

In the 90/10, 50/50, and 30/70 scenarios, CEL retained substantially lower average write latency even after the total throughput of the compared locks had become similar.

This indicates that:

> Multiple locks reduce CEL's relative throughput multiplier, not the efficiency of permission convergence and handoff for an individual request.

The single-lock test primarily exposes CEL's maximum per-instance concurrency. The multiple-lock test verifies that many independent lock instances can operate simultaneously without obvious global degradation. Average write latency more directly demonstrates the effect of preemptive Exclusive acquisition and permission convergence.

### Complete Benchmark Results

<details>
<summary><strong>Single lock: 1 lock instance, 64 threads, 64 MiB shared memory, 64 work steps</strong></summary>

```text
F:\Projects\ConcurrentExclusiveLock\csharp\TestAndBenchmark\bin\Release\net8.0>TestAndBenchmark.exe --lock-instances 1 --threads 64 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 64
Lock benchmark
.NET=8.0.22, OS=Microsoft Windows NT 10.0.26200.0
GC=False, CPU=16

lock-instances=1, threads/lock=64, total-threads=64, works/thread=10,000, read-steps=64, write-steps=64
workload=memory (64 MiB shared, read-steps=64, write-steps=64)
Workers use dedicated Thread instances and start from a common gate.
Each lock instance owns a fresh IWork; all worker groups share one start gate.

Scenario: read/write 100/0
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.973s      10.0%        657517        657517        65536       640,000             0           0.0  0000000000000000
  ReaderWriterLockSlim          0.141s      94.5%       4553332       4553332        48188       640,000             0           0.0  0000000000000000
  CEL                           0.108s      97.7%       5928072       5928072        60681       640,000             0           0.0  0000000000000000
  CEL(ExclusiveOnly)            0.899s      11.3%        711688        711688        63015       640,000             0           0.0  0000000000000000

Scenario: read/write 99.5/0.5
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.879s      11.0%        728260        728260        66198       636,838         3,162     1856481.2  8398C18E7F9AA0EB
  ReaderWriterLockSlim          0.220s      96.6%       2904039       2904039        30062       636,838         3,162     1356004.3  8398C18E7F9AA0EB
  CEL                           0.132s      69.5%       4842249       4842249        69719       636,838         3,162       16299.5  8398C18E7F9AA0EB
  CEL(ExclusiveOnly)            0.901s      10.4%        710583        710583        68267       636,838         3,162     1567402.2  8398C18E7F9AA0EB

Scenario: read/write 90/10
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.899s      11.0%        712098        712098        64887       576,034        63,966      321241.7  4304798A1CB10952
  ReaderWriterLockSlim          0.540s      55.7%       1186134       1186134        21278       576,034        63,966      263729.1  4304798A1CB10952
  CEL                           0.303s      44.4%       2109201       2109201        47490       576,034        63,966       33600.9  4304798A1CB10952
  CEL(ExclusiveOnly)            0.915s      11.0%        699329        699329        63627       576,034        63,966      288658.3  4304798A1CB10952

Scenario: read/write 50/50
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.944s      11.2%        678149        678149        60681       320,007       319,993      117790.0  7F3AA8C4A6F5CFA7
  ReaderWriterLockSlim          1.413s      50.9%        452787        452787         8892       320,007       319,993      155729.2  7F3AA8C4A6F5CFA7
  CEL                           0.770s      18.9%        831019        831019        43984       320,007       319,993       73086.6  7F3AA8C4A6F5CFA7
  CEL(ExclusiveOnly)            0.947s      10.6%        675557        675557        63627       320,007       319,993      114950.5  7F3AA8C4A6F5CFA7

Scenario: read/write 30/70
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.961s      10.8%        665893        665893        61826       191,321       448,679       99941.6  984C4BC0324B2349
  ReaderWriterLockSlim          1.578s      49.8%        405680        405680         8141       191,321       448,679      124113.9  984C4BC0324B2349
  CEL                           0.884s      15.2%        723968        723968        47490       191,321       448,679       75431.8  984C4BC0324B2349
  CEL(ExclusiveOnly)            0.966s      10.8%        662768        662768        61249       191,321       448,679       99725.6  984C4BC0324B2349

Scenario: read/write 0/100
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.971s      11.4%        658964        658964        57996             0       640,000       94123.9  9C619B979129B421
  ReaderWriterLockSlim          1.101s      26.3%        581326        581326        22066             0       640,000      105048.3  9C619B979129B421
  CEL                           0.977s      11.9%        655340        655340        55072             0       640,000       94603.5  9C619B979129B421
  CEL(ExclusiveOnly)            0.971s      11.2%        659281        659281        59041             0       640,000       94419.0  9C619B979129B421

sink=3007092141684130081
```

</details>

<details>
<summary><strong>Multiple locks: 8 lock instances, 8 threads per lock, 64 MiB per instance, 32 work steps</strong></summary>

```text
F:\Projects\ConcurrentExclusiveLock\csharp\TestAndBenchmark\bin\Release\net8.0>TestAndBenchmark.exe --lock-instances 8 --threads 8 --workload memory --operations 10000 --memory-mb 64 --read-work 32 --write-work 32
Lock benchmark
.NET=8.0.22, OS=Microsoft Windows NT 10.0.26200.0
GC=False, CPU=16

lock-instances=8, threads/lock=8, total-threads=64, works/thread=10,000, read-steps=32, write-steps=32
workload=memory (64 MiB shared, read-steps=32, write-steps=32)
Workers use dedicated Thread instances and start from a common gate.
Each lock instance owns a fresh IWork; all worker groups share one start gate.

Scenario: read/write 100/0
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.149s      87.7%       4290496        536312        48907       640,000             0           0.0  A57B1FF0E740A896
  ReaderWriterLockSlim          0.073s      94.1%       8810160       1101270        93623       640,000             0           0.0  A57B1FF0E740A896
  CEL                           0.064s      92.4%       9932028       1241503       107436       640,000             0           0.0  A57B1FF0E740A896
  CEL(ExclusiveOnly)            0.122s      86.8%       5266545        658318        60681       640,000             0           0.0  A57B1FF0E740A896

Scenario: read/write 99.5/0.5
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.119s      94.3%       5374123        671765        56988       636,726         3,274      144936.1  5FFADA82B8F7C3C6
  ReaderWriterLockSlim          0.078s      97.3%       8173388       1021674        84021       636,726         3,274      949142.2  5FFADA82B8F7C3C6
  CEL                           0.068s      86.3%       9426514       1178314       109227       636,726         3,274       54855.1  5FFADA82B8F7C3C6
  CEL(ExclusiveOnly)            0.122s      89.7%       5250588        656324        58514       636,726         3,274       93040.6  5FFADA82B8F7C3C6

Scenario: read/write 90/10
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.126s      81.3%       5075562        634445        62415       575,901        64,099       35149.4  7F8372CDB19E8250
  ReaderWriterLockSlim          0.107s      94.3%       6001664        750208        63627       575,901        64,099       81179.8  7F8372CDB19E8250
  CEL                           0.099s      92.6%       6457895        807237        69719       575,901        64,099       10565.8  7F8372CDB19E8250
  CEL(ExclusiveOnly)            0.128s      78.3%       4985029        623129        63627       575,901        64,099       26110.0  7F8372CDB19E8250

Scenario: read/write 50/50
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.134s      89.4%       4763081        595385        53281       320,069       319,931       16227.2  77DA7C15C44409F7
  ReaderWriterLockSlim          0.135s      95.2%       4726561        590820        49648       320,069       319,931       22625.6  77DA7C15C44409F7
  CEL                           0.145s      94.8%       4405050        550631        46479       320,069       319,931        6043.6  77DA7C15C44409F7
  CEL(ExclusiveOnly)            0.136s      86.5%       4721501        590188        54613       320,069       319,931       14954.3  77DA7C15C44409F7

Scenario: read/write 30/70
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.139s      84.7%       4589396        573675        54162       191,782       448,218       14530.9  E4C312562E36CC29
  ReaderWriterLockSlim          0.144s      93.4%       4433969        554246        47490       191,782       448,218       17349.8  E4C312562E36CC29
  CEL                           0.146s      88.2%       4379425        547428        49648       191,782       448,218        5116.5  E4C312562E36CC29
  CEL(ExclusiveOnly)            0.141s      87.9%       4536514        567064        51603       191,782       448,218       14007.9  E4C312562E36CC29

Scenario: read/write 0/100
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.147s     100.4%       4357654        544707        43401             0       640,000       14154.6  4CD28C3524A9BA6F
  ReaderWriterLockSlim          0.161s      92.2%       3975301        496913        43116             0       640,000       15026.7  4CD28C3524A9BA6F
  CEL                           0.151s      80.3%       4244409        530551        52852             0       640,000       14633.0  4CD28C3524A9BA6F
  CEL(ExclusiveOnly)            0.148s      77.5%       4338768        542346        56014             0       640,000       14312.1  4CD28C3524A9BA6F

sink=4320303262889978983
```

</details>

---

## Project Status

The Pipeline has completed approximately **240 hours of randomized call stress testing**.

The current C# / .NET implementation is the semantic reference. Implementations in other languages should follow its protocol semantics rather than merely translating its syntax mechanically.

---

## Project Information

- **Project**: ConcurrentExclusiveLock
- **Abbreviation**: CEL
- **Author**: 王弈博 (YiBoWang)
- **Original implementation**: C# / .NET
- **Compatibility target**: .NET 8.0, .NET Standard 2.1
- **Intended environments**: .NET, Unity3D, game servers, and other fine-grained state systems
- **GitHub**: <https://github.com/WangHHB/ConcurrentExclusiveLock>

---

## License

ConcurrentExclusiveLock is dual-licensed under the MIT License or the Apache License 2.0, at your option.
See [`LICENSE-MIT`](LICENSE-MIT) and [`LICENSE-APACHE-2.0`](LICENSE-APACHE-2.0) for details.

---

> A compact, high-performance Concurrent/Exclusive synchronization protocol for entity-level state objects, featuring preemptive Exclusive access, in-place upgrade/downgrade, and ContextID/EpochID support.
