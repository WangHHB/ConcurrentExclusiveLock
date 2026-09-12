# ConcurrentExclusiveLock

ConcurrentExclusiveLock (CEL) is a high-performance synchronization library for shared and exclusive access, with direct upgrades from ordinary Concurrent holders and three API layers: Core, Scope, and Pipeline.

**Rich permission semantics, strong parallel throughput, and near-`lock` performance as workloads become write-heavy.** In the published C# benchmarks, CEL substantially outperforms `ReaderWriterLockSlim` across most configurations with meaningful parallel work, while generally staying close to `lock` in write-heavy and fully Exclusive workloads. Both a single hot lock and multiple independent locks are covered.

Use CEL for synchronous reader/writer-style access, then use its upgrade, downgrade, and workflow APIs when an operation needs several permission stages.

## Performance at a Glance

The same lock handles the full range from Concurrent-only to Exclusive-only work:

| Concurrent / Exclusive operations | CEL / `lock` throughput | CEL / `ReaderWriterLockSlim` throughput |
|---|---:|---:|
| 100 / 0 | **4.28×** | **1.66×** |
| 99.5 / 0.5 | **4.60×** | **2.42×** |
| 90 / 10 | **2.25×** | **2.56×** |
| 50 / 50 | **1.10×** | **2.07×** |
| 30 / 70 | **1.04×** | **2.16×** |
| 0 / 100 | **1.01×** | **1.57×** |

**Measured configuration:** Ryzen 7 5700X at 4.5 GHz, SMT enabled, Windows 11, .NET 8.0.22; one lock with 64 workers, 6.4 million operations, 8 MiB of shared memory, and 64 work steps per Concurrent or Exclusive operation. These are complete-operation throughput ratios, including protected work, from one recorded matrix run.

As the Exclusive share grows, this configuration approaches the `lock` baseline while retaining CEL's upgrade and workflow capabilities. The [full performance matrix](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#performance) also covers a single core, SMT disabled, a 4-vCPU VM, and dual-socket 52-core / 104-thread Windows and Linux systems, with acquisition latency, CPU usage, and Exclusive progress results. Results vary with workload and topology; the complete tables include the cases where CEL trails a baseline.

[Benchmark commands and measurement definitions](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/csharp/TestAndBenchmark/README.md)

## Quick Start

### Installation

```shell
dotnet add package ConcurrentExclusiveLock
```

The package targets **.NET Standard 2.1 and .NET 8.0**. Its namespace is `IntomicLib`.

Use a Scope to acquire permission and release it automatically on return or exception:

```csharp
using IntomicLib;

public sealed class SharedState
{
    private readonly ConcurrentExclusiveLock _lock = ConcurrentExclusiveLock.Create();
    private int _value;

    public int Read()
    {
        using (var scope = new ConcurrentExclusiveLockScope(_lock))
        {
            scope.AcquireConcurrent();
            return _value;
        }
    }

    public void Set(int value)
    {
        using (var scope = new ConcurrentExclusiveLockScope(_lock))
        {
            scope.AcquireExclusive();
            _value = value;
        }
    }
}
```

Concurrent callers may execute together; an Exclusive caller executes alone. Scope tracks the final permission, including after upgrades and downgrades, so the same `using` pattern also handles more complex operations.

CEL is synchronous and non-recursive. Keep each Scope in one call context, and keep work that depends on its permission on the same thread without crossing an `await`. See [usage examples](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#usage-examples) and [design boundaries](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#design-boundaries).

## What Direct Upgrades Add

An operation can inspect or prepare state under Concurrent permission and request Exclusive only when it needs to commit. **Multiple ordinary Concurrent holders can enter the upgrade sequence together and obtain Exclusive in turn.** They do not need to declare upgrade intent in advance, enter a dedicated upgradeable mode, or acquire a unique upgrade right.

The protocol coordinates that transition with admission and progress:

| Capability | Effect on the workflow |
|---|---|
| Direct Concurrent → Exclusive upgrade | Converts the caller's Concurrent participation into an Exclusive reservation; other Concurrent holders can leave or register their own upgrades. |
| Upgrade priority | New Concurrent entries are blocked while Exclusive is pending, and ordinary Exclusive requests yield to the registered upgrade chain. |
| Exclusive → Concurrent downgrade | Retains Concurrent continuously when no other upgrades are pending; otherwise lets the remaining upgrades proceed before reacquiring Concurrent. |
| ContextID / EpochID conditions | Couples entry into Exclusive with a business-context change or forward-only phase update. |

This supports parallel preparation followed by serialized commits within one permission protocol. Earlier upgraders can change state before a later upgrader commits; validate the relevant business conditions under Exclusive. See [upgrade and downgrade semantics](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#in-place-upgrade-and-downgrade).

## Choose Your API Layer

| Layer | Responsibility | Typical use |
|---|---|---|
| **Core — `ConcurrentExclusiveLock`** | Admission, exclusion, and permission transitions | Precise control over acquisition, release, and conversion in low-level code |
| **Scope — `ConcurrentExclusiveLockScope`** | Tracks the permission still owned by the call context and releases it on exit | Ordinary application code, branches, early returns, and exceptions |
| **Pipeline — `ConcurrentExclusiveLockPipeline`** | Declares each stage's permission and decides where the context continues, converts, or is released and reacquired | Multi-stage preparation, commit, publication, and conditional workflows |

Start with Scope for ordinary access. Use Core when you need direct control, or Pipeline when the permission transitions are part of the business workflow. Pipeline also coordinates ContextID / EpochID conditions and final cleanup. See [API details](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#three-api-layers) and the [Pipeline example](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#pipeline).

## Upgrade with Scope

The following workflow sketch prepares under Concurrent, upgrades only when a commit is needed, and validates the prepared result under Exclusive:

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

Here, `_locker` is a shared lock created once with `ConcurrentExclusiveLock.Create()`. The command types and preparation/commit methods represent application code. Scope releases the final permission on every exit. Conditional ContextID / EpochID upgrades and downgrade examples are documented in the [full usage guide](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#usage-examples).

## Documentation and Validation

- [Full C# guide](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md) · [简体中文](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README_CN.md)
- [Pipeline example and Segment behavior](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#pipeline)
- [Upgrade, downgrade, and interruption semantics](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#in-place-upgrade-and-downgrade)
- [API layers and Scope ownership rules](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#three-api-layers)
- [Complete performance matrix](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/README.md#performance)
- [Test source, benchmark commands, and measurement definitions](https://github.com/WangHHB/ConcurrentExclusiveLock/tree/main/csharp/TestAndBenchmark)

The published C# matrix covers six environments, including single-core, SMT on/off, a 4-vCPU VM, and dual-socket Windows and Linux systems. All six correctness runs and all 24 upgrade-contention cases passed. The Pipeline has also completed approximately 240 hours of randomized call stress testing. The linked guide contains workload parameters, timing definitions, and the full results.

## License

Use either the [MIT License](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/LICENSE-MIT) or the [Apache License 2.0](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/LICENSE-APACHE-2.0).
