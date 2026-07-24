# ConcurrentExclusiveLock

[![C# Build and Test](https://github.com/WangHHB/ConcurrentExclusiveLock/actions/workflows/dotnet.yml/badge.svg)](https://github.com/WangHHB/ConcurrentExclusiveLock/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/ConcurrentExclusiveLock.svg)](https://www.nuget.org/packages/ConcurrentExclusiveLock/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ConcurrentExclusiveLock.svg)](https://www.nuget.org/packages/ConcurrentExclusiveLock/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**ConcurrentExclusiveLock (CEL)** is a Concurrent/Exclusive synchronization protocol designed for fine-grained state objects.

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

The current **C# / .NET implementation** is the original and authoritative version. The project targets **.NET Standard 2.1** and also takes into account Unity3D hot-path requirements for strict allocation and GC control.

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

### Inspect Under Concurrent, Then Upgrade

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
| `ConvergeConcurrent` | Continues an existing Concurrent context or downgrades Exclusive to Concurrent in place |
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

`ConvergeConcurrent` represents continuing or establishing a continuous Concurrent context.

`TryApplyIDConvergeExclusive` represents entering or continuing an Exclusive context after a business ID has been applied successfully.

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

---

## Project Status

The Pipeline has completed approximately **240 hours of randomized call stress testing**.

The current C# / .NET implementation is the semantic reference. Implementations in other languages should follow its protocol semantics rather than merely translating its syntax mechanically.

---

## Project Information

- **Project**: ConcurrentExclusiveLock
- **Abbreviation**: CEL
- **Author**: 王弈博 (YiBoWang)
- **Version**: 1.0.0
- **Original implementation**: C# / .NET
- **Compatibility target**: .NET 8.0, .NET Standard 2.1
- **Intended environments**: .NET, Unity3D, game servers, and other fine-grained state systems
- **GitHub**: <https://github.com/WangHHB/ConcurrentExclusiveLock>

---

> A compact, high-performance Concurrent/Exclusive synchronization protocol for entity-level state objects, featuring preemptive Exclusive access, in-place upgrade/downgrade, and ContextID/EpochID support.
