# ConcurrentExclusiveLock

**ConcurrentExclusiveLock (CEL) is a Concurrent / Exclusive synchronization protocol designed for fine-grained state objects. All implemented language versions use the C# implementation as their semantic baseline and provide the complete set of capabilities: preemptive Exclusive acquisition, in-place Concurrent-to-Exclusive upgrade, in-place Exclusive-to-Concurrent downgrade, and three abstraction layers consisting of the core lock, Scope, and Pipeline. The Pipeline layer can coordinate multi-stage reads and writes, upgrades and downgrades, conditional convergence, and exception-safe release within a continuous synchronization context, preventing complex concurrency-control logic from being scattered throughout business code.

Among publicly available implementations known to date, CEL is the only reader-writer synchronization implementation that supports direct in-place upgrade and downgrade without requiring special read or write modes, advance declaration of upgrade intent, or prior acquisition of upgrade permission. Its upgrade and downgrade paths are both remarkably simple and elegant, achieving continuous, symmetric, and efficient permission transitions with minimal state changes.

All language versions have undergone performance benchmarking and extensive stress testing. The C# reference implementation has also completed a unified formal matrix spanning a single-core configuration, SMT enabled and disabled, a 4-vCPU virtual machine, and dual-socket 52-core / 104-thread systems running both Windows and Linux. The results show that CEL consistently outperforms traditional implementations when real Concurrent parallelism, timely Exclusive progress, or frequent permission convergence is required. In non-target scenarios such as write-heavy and purely Exclusive workloads, it generally remains at mutex-level performance without structural degradation.**

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

## Project Positioning

ConcurrentExclusiveLock is not intended to be a universal lock for every problem, nor is it a simple reproduction of a traditional Reader/Writer Lock.

Its primary goal is:

> To express Concurrent / Exclusive permissions at low normal-path cost across large numbers of fine-grained state objects, while combining preemption, upgrade, downgrade, business-ID convergence, and continuous workflow orchestration into one complete protocol.

The project currently includes:

- `ConcurrentExclusiveLock`
- `ConcurrentExclusiveLockScope`
- `ConcurrentExclusiveLockPipeline`
- complete XML API documentation
- protection against misuse of synchronous Segments
- BenchmarkDotNet performance tests
- long-running randomized call stress tests

---

## Test Project

The test project in this repository validates the core synchronization protocol under different contention conditions, permission-transition paths, and hardware topologies. It mainly covers:

- basic Concurrent / Exclusive acquisition, release, and state-consistency tests;
- preemptive Exclusive behavior and progress under a fixed Concurrent flood;
- in-place Concurrent → Exclusive upgrades and in-place Exclusive → Concurrent downgrades;
- ordering between ordinary Exclusive requests and a batch upgrade chain;
- ContextID / EpochID protocol behavior;
- Pipeline Segment combinations, exception-safe release, and randomized semantic tests;
- single-lock / multi-lock throughput and pure permission-acquisition latency;
- staged performance comparisons among Pipeline convergence, Core handoff, RWLS handoff, and a serialized baseline;
- a unified cross-machine matrix covering one core, SMT enabled and disabled, virtual machines, Windows / Linux, and dual-socket NUMA systems;
- long-running randomized call stress tests and BenchmarkDotNet performance tests.

All command-line topology and workload parameters are executed literally. The benchmark records detected hardware and runtime information but never changes the thread count, lock-instance count, operation count, or workload according to the detected core count.

**The test project was written by AI.**

The test code is intended to assist with validating the current implementation, expand path coverage, and provide performance-observation data. The core synchronization protocol, API design, and semantic definitions are governed by the C# / .NET main project implementation.


## Performance

This section now uses a unified formal cross-machine matrix instead of the earlier historical snapshot from a single Windows 11 workstation.

The formal matrix jointly evaluates:

- semantic correctness;
- mixed Concurrent / Exclusive throughput;
- pure permission-acquisition latency and tail latency;
- Exclusive progress under a fixed amount of Concurrent work;
- staged `Concurrent → Exclusive → Concurrent` workflows;
- contention ordering between large-scale in-place upgrades and ordinary Exclusive requests.

For the complete commands, metric definitions, output fields, and historical snapshots, see the [C# Testing and Benchmark Guide](./csharp/TestAndBenchmark/README.md).

### Test Matrix

| Environment | Operating system / runtime | Visible processors | Primary purpose |
|---|---|---:|---|
| AMD Ryzen 7 5700X, BIOS configured for one core | Windows 11 / .NET 8.0.22 | 1 | Single-core degradation and fixed-cost baseline |
| AMD Ryzen 7 5700X, SMT disabled, fixed at 4.5 GHz | Windows 11 / .NET 8.0.22 | 8 | Physical-core contention |
| AMD Ryzen 7 5700X, SMT enabled, fixed at 4.5 GHz | Windows 11 / .NET 8.0.22 | 16 | Main workstation reference result |
| AMD EPYC 9V74 cloud virtual machine | Debian 13 / .NET 8.0.29 | 4 vCPU | Oversubscription and Linux virtualization |
| Intel Platinum 8269CY, 2 sockets, 52 cores / 104 threads | Ubuntu 26.04 / .NET 8.0.29 | 104 | Dual-socket NUMA / Linux |
| Intel Platinum 8269CY, 2 sockets, 52 cores / 104 threads | Windows Server 2025 / .NET 8.0.29 | 104 | Same-hardware cross-OS comparison |

Each environment produces one JSONL file containing **94 records across 14 experiments**. The correctness mode completed with `exitCode = 0` in every formal matrix.

### Unified Parameters and Measurement Boundaries

The throughput tests use two fixed topologies:

- `1×64`: one hot lock with 64 worker threads;
- `8×8`: eight independent locks with eight worker threads per lock.

Both topologies execute a total of 6,400,000 operations, using 8 MiB of shared memory per lock, 64 Concurrent work steps, and 64 Exclusive work steps. Thread counts, lock-instance counts, workload sizes, and operation counts are executed exactly as supplied on the command line and are never scaled according to the machine's core count.

In `throughput` mode, `averageExclusiveOperationNs` measures one complete Exclusive operation, including acquisition, protected work, release, and timing overhead. It is not pure acquisition latency.

The `latency` mode measures permission-acquisition time specifically. Every acquisition is timed. `--latency-sample-every 10` only controls deterministic sample retention; sample selection uses random state independent from the Concurrent / Exclusive permission choice and is performed after permission release, so it does not alter the measured contention process.

The results below are each taken from one formal matrix run on the corresponding environment. They demonstrate repeatable performance shapes rather than absolute guarantees for every machine and workload. When comparing different hardware, trends and relative multipliers are more meaningful than raw throughput.

### 1. 5700X with SMT Enabled: One Hot Lock

`1×64` most directly exposes Concurrent parallelism within a single lock instance.

| Concurrent / Exclusive | `lock` works/s | `ReaderWriterLockSlim` works/s | CEL works/s | CEL / `lock` | CEL / RWLS |
|---:|---:|---:|---:|---:|---:|
| 100 / 0 | 2,589,515 | 6,660,960 | **11,083,470** | **4.28×** | **1.66×** |
| 99.5 / 0.5 | 2,682,341 | 5,094,422 | **12,337,668** | **4.60×** | **2.42×** |
| 90 / 10 | 2,392,583 | 2,107,590 | **5,392,722** | **2.25×** | **2.56×** |
| 50 / 50 | 1,982,155 | 1,050,757 | **2,178,160** | **1.10×** | **2.07×** |
| 30 / 70 | 1,855,687 | 892,264 | **1,923,724** | **1.04×** | **2.16×** |
| 0 / 100 | 1,688,922 | 1,082,263 | **1,704,085** | **1.01×** | **1.57×** |

These results show a clear degradation curve:

- At high Concurrent ratios, CEL outperforms both ordinary `lock` and `ReaderWriterLockSlim`.
- At 90/10, CEL reaches **2.25×** the throughput of `lock` and **2.56×** that of RWLS.
- Even at 50/50 and 30/70, CEL retains mutex-level throughput while delivering approximately twice the throughput of RWLS.
- At 100% Exclusive, CEL reaches **1.01×** the throughput of `lock`, showing that its richer Concurrent, preemption, upgrade, and downgrade semantics do not impose a meaningful pure-serialization-path tax.

### 2. 5700X with SMT Enabled: Eight Independent Locks

In `8×8`, an ordinary mutex can also execute across independent lock instances in parallel, so CEL's multiplier relative to `lock` naturally becomes smaller.

| Concurrent / Exclusive | `lock` works/s | `ReaderWriterLockSlim` works/s | CEL works/s | CEL / `lock` | CEL / RWLS |
|---:|---:|---:|---:|---:|---:|
| 100 / 0 | 3,563,840 | 10,388,661 | **12,432,672** | **3.49×** | **1.20×** |
| 99.5 / 0.5 | 3,642,434 | 7,004,578 | **14,775,721** | **4.06×** | **2.11×** |
| 90 / 10 | 3,528,619 | 4,423,317 | **5,494,419** | **1.56×** | **1.24×** |
| 50 / 50 | 3,257,390 | 3,250,963 | **3,294,069** | **1.01×** | **1.01×** |
| 30 / 70 | 3,118,793 | 3,025,585 | **3,270,777** | **1.05×** | **1.08×** |
| 0 / 100 | 3,076,909 | 2,752,932 | **3,048,696** | **0.99×** | **1.11×** |

The multi-lock results show that:

- CEL's single-lock advantage is not produced by global spinning or global mutual exclusion.
- At 99.5/0.5, CEL still reaches **2.11×** the throughput of RWLS.
- From 50/50 through 100% Exclusive, CEL remains within approximately 1% to 5% of `lock`.
- Multiple locks reduce the relative throughput multiplier, not CEL's per-instance permission-convergence capability.

### 3. Cross-Topology Results: The Advantage Appears Only When Real Parallelism and Contention Exist

The following table uses the 90/10 mixed workload to show CEL's scalability relative to RWLS and the 100% Exclusive workload to show CEL's serialized-path cost relative to ordinary `lock`.

| Environment | 1×64, 90/10 CEL / RWLS | 8×8, 90/10 CEL / RWLS | 1×64, 0/100 CEL / `lock` |
|---|---:|---:|---:|
| 5700X single core | **0.99×** | **1.05×** | 0.97× |
| 5700X, SMT disabled | **2.34×** | **1.49×** | 1.01× |
| 5700X, SMT enabled | **2.56×** | **1.24×** | 1.01× |
| EPYC 4 vCPU / Debian | **12.20×** | **2.69×** | 1.04× |
| 8269CY / Ubuntu | **9.89×** | **11.82×** | 0.99× |
| 8269CY / Windows Server | **2.88×** | **1.76×** | 0.82× |

In the single-core configuration, CEL and RWLS are effectively tied. With no parallel resources to exploit, CEL does not manufacture a throughput advantage.

Once the workload moves to real multicore, virtualized, or dual-socket NUMA environments, the gap under mixed contention becomes visible:

- In the 4-vCPU Debian VM, CEL reaches **12.20×** RWLS throughput in the `1×64` 90/10 workload.
- On dual-socket Ubuntu, CEL reaches **9.89×** and **11.82×** RWLS throughput in `1×64` and `8×8` 90/10 respectively.
- Windows Server 2025 substantially improves RWLS behavior, but CEL still leads by **2.88×** and **1.76×** on the same dual-socket hardware.

In `1×64` at 100% Exclusive, CEL ranges from **0.82× to 1.04×** ordinary `lock`. This shows that CEL's advantages are not purchased through structural collapse under write-heavy workloads. The weakest result occurs on dual-socket Windows Server; on the other multicore environments, CEL remains close to or equal with the ordinary mutex.

### 4. Ubuntu / Windows Server Comparison on the Same Dual-Socket Machine

The two 8269CY results use the same hardware, the same .NET runtime version, and the same benchmark parameters, allowing the effect of operating-system synchronization and scheduling paths to be observed directly.

Taking the geometric mean of `Windows / Ubuntu` throughput across all 12 throughput scenarios gives:

| Implementation | Windows / Ubuntu geometric mean | Minimum | Maximum |
|---|---:|---:|---:|
| `lock` | **1.458×** | 0.771× | 2.632× |
| RWLS | **2.227×** | 0.699× | 6.985× |
| **CEL** | **1.019×** | 0.637× | 1.939× |

Representative absolute throughput values are:

| Scenario | Ubuntu RWLS | Ubuntu CEL | Windows RWLS | Windows CEL |
|---|---:|---:|---:|---:|
| 1×64, 100/0 | 1.500 M/s | **7.959 M/s** | 1.233 M/s | **7.957 M/s** |
| 1×64, 90/10 | 0.203 M/s | **2.002 M/s** | 0.559 M/s | **1.613 M/s** |
| 8×8, 90/10 | 0.667 M/s | **7.882 M/s** | 4.659 M/s | **8.184 M/s** |
| 8×8, 50/50 | 0.420 M/s | **3.152 M/s** | 2.095 M/s | **3.235 M/s** |

The most notable observations are:

- Under `1×64` pure Concurrent load, CEL throughput differs by only about **0.02%** between the two operating systems.
- Under `8×8` 90/10, CEL differs by only about **3.8%**.
- In that same scenario, RWLS differs by approximately **6.99×** between Windows and Ubuntu.
- On Ubuntu, RWLS falls from 10.560 M/s at `8×8` 100/0 to 0.667 M/s at 90/10, a throughput reduction of approximately **93.7%** after writers are introduced.

This comparison does not imply that CEL must have identical absolute speed on every operating system. It does show that its key performance shape is driven primarily by the protocol itself, while RWLS is more sensitive to operating-system waiting, wake-up, scheduling, and cross-NUMA coordination paths.

### 5. Exclusive Acquisition Latency

The `latency` test uses a 90/10 mixed workload. Values in the table are the mean **pure acquisition time** for Exclusive permission and do not include Exclusive work or release.

| Environment | 1×64 RWLS | 1×64 CEL | Improvement | 8×8 RWLS | 8×8 CEL | Improvement |
|---|---:|---:|---:|---:|---:|---:|
| 5700X, SMT disabled | 228.0 μs | **12.4 μs** | **18.41×** | 70.4 μs | **8.1 μs** | **8.73×** |
| 5700X, SMT enabled | 177.4 μs | **11.9 μs** | **14.97×** | 121.3 μs | **14.9 μs** | **8.12×** |
| EPYC 4 vCPU / Debian | 342.5 μs | **18.8 μs** | **18.25×** | 173.0 μs | **39.3 μs** | **4.40×** |
| 8269CY / Ubuntu | 160.0 μs | **33.0 μs** | **4.84×** | 151.7 μs | **7.6 μs** | **20.03×** |
| 8269CY / Windows Server | 60.6 μs | **51.9 μs** | **1.17×** | 25.9 μs | **7.6 μs** | **3.40×** |

Except for the single-core degradation baseline, CEL reduces mean Exclusive acquisition time in every multicore environment and both topologies.

Representative p99 values include:

- 5700X with SMT disabled, `1×64`: RWLS 1,240.9 μs, CEL **10.0 μs**;
- 4-vCPU Debian, `1×64`: RWLS 2,583.4 μs, CEL **3.81 μs**;
- dual-socket Ubuntu, `8×8`: RWLS 1,568.8 μs, CEL **144.0 μs**;
- dual-socket Windows Server, `8×8`: RWLS 102.1 μs, CEL **80.2 μs**.

The latency distributions may contain a small number of extreme scheduler-driven tails, so p99 should not be interpreted independently from mean, p99.9, and max. The complete percentiles are retained in the raw JSONL files.

### 6. Exclusive Progress Under a Fixed Amount of Concurrent Work

`exclusive-progress` does not measure which implementation can keep looping for a longer wall-clock duration. Instead, each implementation completes the same fixed number of Concurrent operations while the benchmark counts the number of Exclusive completions.

Each lock has one writer that continuously requests Exclusive permission. After every Exclusive completion, that writer must wait until at least one new Concurrent completion occurs on the same lock before requesting Exclusive again. This prevents the Exclusive-entry count from inflating itself merely by extending the benchmark duration.

| Environment | 1×64: RWLS → CEL | CEL / RWLS | 8×8: RWLS → CEL | CEL / RWLS |
|---|---:|---:|---:|---:|
| 5700X, SMT disabled | 39 → **23,640** | **606.15×** | 2,513 → **68,519** | **27.27×** |
| 5700X, SMT enabled | 709 → **80,506** | **113.55×** | 1,803 → **95,370** | **52.90×** |
| EPYC 4 vCPU / Debian | 1,524 → **51,890** | **34.05×** | 9,054 → **274,774** | **30.35×** |
| 8269CY / Ubuntu | 14,774 → **519,254** | **35.15×** | 245,361 → **874,909** | **3.57×** |
| 8269CY / Windows Server | 12,424 → **527,689** | **42.47×** | 181,427 → **654,287** | **3.61×** |

Except for the single-core configuration, where no real parallelism exists, CEL substantially increases the number of completed Exclusive entries in every multicore environment.

This mode observes progress under a fixed Concurrent flood; it is not a proof of strict FIFO fairness. Per-lock min / max values in short runs remain sensitive to thread scheduling, so the homepage uses total Exclusive entries as its primary metric.

### 7. Pipeline: In-Place Convergence Versus Release and Reacquisition

The Pipeline benchmark uses a fixed three-stage workflow:

```text
Concurrent prepare(128)
    → Exclusive commit(16)
    → Concurrent post(128)
```

`CEL Pipeline converge` preserves the same synchronization context and performs in-place upgrade / downgrade. `CEL Core handoff` and `RWLS handoff` release and reacquire permission between stages.

| Environment | 1×64 Pipeline / Core handoff | 1×64 Pipeline / RWLS | 8×8 Pipeline / Core handoff | 8×8 Pipeline / RWLS |
|---|---:|---:|---:|---:|
| 5700X single core | 0.90× | **1.00×** | 0.86× | **0.94×** |
| 5700X, SMT disabled | 1.08× | **2.15×** | 1.41× | **1.51×** |
| 5700X, SMT enabled | 1.07× | **2.54×** | 1.22× | **1.72×** |
| EPYC 4 vCPU / Debian | 0.91× | **4.00×** | 0.76× | **1.47×** |
| 8269CY / Ubuntu | 1.75× | **7.09×** | 1.89× | **3.48×** |
| 8269CY / Windows Server | 1.72× | **4.41×** | 1.64× | **2.15×** |

The results show that:

- On the 5700X and dual-socket 8269CY real multicore environments, Pipeline convergence outperforms both CEL Core handoff and RWLS handoff.
- On dual-socket Ubuntu at `1×64`, Pipeline reaches **1.75×** Core handoff and **7.09×** RWLS handoff.
- On dual-socket Windows Server at `1×64`, the corresponding multipliers are **1.72×** and **4.41×**.
- In the single-core and oversubscribed 4-vCPU / 64-worker environments, preserving the permission context in place is not always faster than releasing and allowing the scheduler to redistribute work. This establishes a clear applicability boundary.
- `Monitor serialized` serves as a serialized upper-bound baseline, but it does not provide inter-stage Concurrent parallelism or continuous permission semantics and is therefore not an equivalent Pipeline replacement.

### 8. Correctness and Upgrade Contention

The correctness mode completed successfully in all six formal matrices.

Upgrade-contention testing covers:

- one lock with 64 upgrading threads and 0 ordinary Exclusive threads;
- one lock with 64 upgrading threads and 16 ordinary Exclusive threads;
- eight locks with eight upgrading threads per lock and 0 ordinary Exclusive threads;
- eight locks with eight upgrading threads and four ordinary Exclusive threads per lock.

All 24 upgrade-contention results across the six environments satisfy:

```text
ordinaryEnteredBeforeUpgradeDrain = 0
```

In these tests, no ordinary Exclusive request entered before the pending upgrade chain had drained. This validates the current implementation's upgrade-priority ordering, but it must not be generalized into a strict FIFO guarantee over all operating-system scheduling orders.

### Complete Benchmark Results

The homepage retains only the tables needed to explain the main performance shapes instead of expanding dozens of raw outputs inline.

The complete material includes:

- every formal matrix command;
- precise definitions of all metrics;
- JSONL field documentation;
- latency mean / p50 / p95 / p99 / p99.9 / max;
- per-lock Exclusive Progress details;
- absolute throughput for every Pipeline strategy;
- historical single-machine benchmark snapshots.

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
