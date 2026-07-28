# ConcurrentExclusiveLock for Java

This Java implementation is a port based on the original C# implementation of ConcurrentExclusiveLock. The C# version remains the reference implementation for the design and synchronization semantics: [`../csharp`](../csharp).

The Java port keeps the same overall Concurrent/Exclusive access model while adapting the implementation to the Java memory model and standard synchronization primitives.


## Installation

### Maven

```xml
<dependency>
    <groupId>io.github.wanghhb</groupId>
    <artifactId>concurrent-exclusive-lock</artifactId>
    <version>1.1.3</version>
</dependency>
```

### Gradle

```gradle
implementation 'io.github.wanghhb:concurrent-exclusive-lock:1.1.3'
```

[Maven Central](https://central.sonatype.com/artifact/io.github.wanghhb/concurrent-exclusive-lock)


## Usage

The Java API follows the same three-layer model as the C# reference implementation:

1. `ConcurrentExclusiveLock` — low-level permission protocol;
2. `ConcurrentExclusiveLockScope` — `AutoCloseable` lifecycle wrapper;
3. `ConcurrentExclusiveLockPipeline` — sequential permission-workflow orchestration.

Create one lock for each independently synchronized entity, room, player, session, aggregate, or task context:

```java
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLock;

private final ConcurrentExclusiveLock locker =
        ConcurrentExclusiveLock.create();
```

### Low-level API

`ConcurrentExclusiveLock` provides direct control over acquisition, release, upgrade, downgrade, and business IDs:

```java
locker.acquireConcurrent();
locker.tryAcquireConcurrent();

locker.acquireExclusive();
locker.tryAcquireExclusive();

locker.releaseConcurrent();
locker.releaseExclusive();

locker.concurrentToExclusive();
locker.exclusiveToConcurrent();

locker.switchContextID(newContextID);
locker.raiseEpochID(newEpochID);

locker.tryConcurrentToExclusiveWithSwitchContextID(newContextID);
locker.tryConcurrentToExclusiveWithRaiseEpochID(newEpochID);
```

Timeout overloads use `java.time.Duration`.

Exclusive permission is thread-affine: it must be released or downgraded by the same thread that acquired it.

### Scope

`ConcurrentExclusiveLockScope` is the recommended form for most business code. It implements `AutoCloseable`, so try-with-resources releases the final permission still held by the scope, including exception and early-return paths.

The scope is mutable, not thread-safe, and must not be shared across threads.

#### Concurrent

```java
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockScope;

public void readState() {
    try (ConcurrentExclusiveLockScope scope =
                 new ConcurrentExclusiveLockScope(locker)) {

        scope.acquireConcurrent();

        readEntityState();

        // Optional. close() releases it automatically when omitted.
        // scope.releaseConcurrent();
    }
}
```

#### Exclusive

```java
public void modifyState() {
    try (ConcurrentExclusiveLockScope scope =
                 new ConcurrentExclusiveLockScope(locker)) {

        scope.acquireExclusive();

        modifyEntityState();

        // Optional. close() releases it automatically when omitted.
        // scope.releaseExclusive();
    }
}
```

#### Inspect under Concurrent, then upgrade

```java
public void applyEpoch(int targetEpoch) {
    try (ConcurrentExclusiveLockScope scope =
                 new ConcurrentExclusiveLockScope(locker)) {

        scope.acquireConcurrent();

        inspectCurrentState();

        if (!scope.tryConcurrentToExclusiveWithRaiseEpochID(targetEpoch)) {
            // On failure, the previous Concurrent permission has already
            // been released by the protocol.
            return;
        }

        applyEpochUpdate();

        // The scope now holds Exclusive permission.
    }
}
```

#### Downgrade Exclusive to Concurrent

```java
public void rebuildAndPublish() {
    try (ConcurrentExclusiveLockScope scope =
                 new ConcurrentExclusiveLockScope(locker)) {

        scope.acquireExclusive();

        rebuildEntityState();

        scope.exclusiveToConcurrent();

        publishSnapshot();

        // The scope now holds Concurrent permission.
    }
}
```

### Pipeline

`ConcurrentExclusiveLockPipeline` describes a complete permission workflow as a sequence of synchronous `Runnable` segments.

```java
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockPipeline;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockSegment;

ConcurrentExclusiveLockPipeline pipeline =
        new ConcurrentExclusiveLockPipeline(locker);

pipeline.doPipeline(
        ConcurrentExclusiveLockSegment.concurrent(
                this::readCurrentState),

        ConcurrentExclusiveLockSegment.tryApplyIDConvergeExclusive(
                this::applyNewEpoch,
                targetEpoch,
                ConcurrentExclusiveLockSegment.IDType.EPOCH_ID),

        ConcurrentExclusiveLockSegment.convergeConcurrent(
                this::publishNewSnapshot),

        ConcurrentExclusiveLockSegment.none(
                this::notifyOtherSystems)
);
```

Available segment factories:

| Segment factory | Meaning |
|---|---|
| `none(...)` | Runs without access permission. |
| `concurrent(...)` | Acquires an independent Concurrent segment. Consecutive independent segments release and reacquire permission. |
| `tryConcurrent(...)` | Attempts an independent Concurrent segment and skips it on failure. |
| `exclusive(...)` | Acquires an independent Exclusive segment. Consecutive independent segments release and reacquire permission. |
| `testExclusive(...)` | Attempts Exclusive only while the lock is idle; it does not preempt existing Concurrent holders. |
| `tryExclusive(...)` | Attempts preemptive Exclusive permission. |
| `convergeConcurrent(...)` | Continues Concurrent, downgrades Exclusive to Concurrent, or acquires Concurrent. |
| `convergeExclusive(...)` | Continues Exclusive, upgrades Concurrent to Exclusive, or acquires Exclusive. |
| `tryApplyIDConvergeExclusive(...)` | Applies a ContextID or EpochID condition and converges to Exclusive only on success. |

When a try-type segment does not satisfy its condition:

- the current segment is skipped;
- no exception is thrown;
- the current permission becomes `NONE`;
- later segments continue to run.

### Synchronous boundary

Pipeline segments are synchronous `Runnable` instances. A segment must finish all protected work before `Runnable.run()` returns.

Do not start asynchronous work inside a segment and return before it completes:

```java
ConcurrentExclusiveLockSegment.exclusive(() -> {
    // Unsupported: the pipeline cannot keep permission for work that
    // continues after this Runnable returns.
    java.util.concurrent.CompletableFuture.runAsync(this::modifyEntityState);
});
```

`doPipelineAsync(...)` only schedules the complete synchronous pipeline on the common pool or a supplied `Executor`. It does not make individual segments asynchronous.

The directory layout mirrors the C# implementation:

```text
java/
├─ ConcurrentExclusiveLock/   # Core library
├─ TestAndBenchmark/          # Semantic tests, stress tests, and benchmarks
└─ pom.xml                    # Maven multi-module build
```

## Requirements

- JDK 17 or later
- Maven 3.9 or later

JDK 21 is recommended for development and testing while the produced bytecode targets Java 17.

## Build

From the `java` directory:

```powershell
mvn clean package
```

Generated files:

```text
ConcurrentExclusiveLock\target\concurrent-exclusive-lock-1.1.1.jar
TestAndBenchmark\target\TestAndBenchmark.jar
```

A JDK-only Windows build script is also included:

```powershell
.\build-jdk.ps1
```

It uses `javac` and `jar` directly and does not require Maven.

## Run tests

```powershell
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar --help

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --full-semantics `
  --lock-instances 8 `
  --semantic-workers 4 `
  --semantic-operations 256

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 64 `
  --semantic-operations 1000
```

## Run the standard performance comparison

```powershell
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 1 `
  --threads 64 `
  --workload memory `
  --operations 10000 `
  --memory-mb 64 `
  --read-work 64 `
  --write-work 64
```

The standard comparison runs:

- `synchronized`;
- non-fair `ReentrantLock`;
- non-fair `ReentrantReadWriteLock`;
- `StampedLock`;
- `ConcurrentExclusiveLock` (`CEL`);
- `CEL(ExclusiveOnly)`, which routes both reads and writes through Exclusive permission.

See [`TESTING.md`](TESTING.md) and [`PERFORMANCE.md`](PERFORMANCE.md) for the supported modes and parameters.

## Observed benchmark result

The following result is one local run of the included benchmark harness. It is provided as a reference point, not as a universal performance claim.

Environment:

```text
Java:             OpenJDK 21.0.12, 64-bit Server VM
OS:               Windows 11
Logical CPUs:     16
Lock instances:   8
Threads per lock: 8
Total threads:    64
Workload:         memory, 64 MiB shared per lock
Operations:       500,000 per thread
Total operations: 32,000,000 per strategy and scenario
Read/write work:  32 / 32 steps
```

Command:

```powershell
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 8 `
  --threads 8 `
  --workload memory `
  --operations 500000 `
  --memory-mb 64 `
  --read-work 32 `
  --write-work 32
```

CEL results:

| Read/write | Throughput | Work/CPU% | Average write time | Brief observation |
|---:|---:|---:|---:|---|
| 100/0 | 15,857,525 works/s | 166,928 | — | Slightly ahead of `StampedLock`; the difference was small. |
| 99.5/0.5 | 13,888,372 works/s | 155,005 | 14.52 μs | Highest throughput and Work/CPU%, with much lower write time than the tested read/write locks. |
| 90/10 | 10,048,027 works/s | 113,071 | 7.61 μs | Highest result in all three reported metrics in this run. |
| 50/50 | 5,347,318 works/s | 72,256 | 12.63 μs | Best throughput and write time among the tested explicit locks; `synchronized` had higher total throughput. |
| 30/70 | 4,594,175 works/s | 65,628 | 15.02 μs | Close to the other explicit locks; no clear overall lead. |
| 0/100 | 3,792,801 works/s | 57,347 | 16.60 μs | Similar to the other explicit locks, but slightly behind the fastest ones. |

In this run, CEL showed its strongest results from read-dominant workloads through the 90/10 scenario. At 50/50 it remained competitive and led the tested explicit locks in throughput, while write-heavy scenarios converged toward ordinary exclusive-lock performance. `synchronized` was stronger in the high-write scenarios.

The result suggests that CEL offers a strong overall balance of throughput, CPU efficiency, and write latency across mixed workloads. It does not imply that CEL is the fastest choice for every workload, JVM, machine, or application. Application-specific benchmarking is still recommended.

## License

ConcurrentExclusiveLock is dual-licensed under the MIT License or the Apache License 2.0, at your option.

See the repository-root `LICENSE-MIT` and `LICENSE-APACHE-2.0` files.
