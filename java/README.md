# ConcurrentExclusiveLock for Java

This Java implementation is a port based on the original C# implementation of ConcurrentExclusiveLock. The C# version remains the reference implementation for the design and synchronization semantics: [`../csharp`](../csharp).

The Java port keeps the same overall Concurrent/Exclusive access model while adapting the implementation to the Java memory model and standard synchronization primitives.


## Installation

### Maven

```xml
<dependency>
    <groupId>io.github.wanghhb</groupId>
    <artifactId>concurrent-exclusive-lock</artifactId>
    <version>1.1.1</version>
</dependency>
```

### Gradle

```gradle
implementation 'io.github.wanghhb:concurrent-exclusive-lock:1.1.1'
```

[Maven Central](https://central.sonatype.com/artifact/io.github.wanghhb/concurrent-exclusive-lock)

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
