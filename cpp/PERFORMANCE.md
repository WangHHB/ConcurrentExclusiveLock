# Performance Benchmark Guide

## Position

The included benchmark is a reproducible comparison harness, not a universal ranking.

It measures the interaction between synchronization strategy and a configurable business Work. The result depends on the compiler, standard library, operating system, CPU topology, thread count, lock count, memory placement, and Work size.

## Standard Strategies

- `std::mutex`;
- `std::shared_mutex`;
- `CEL` — reads use Concurrent and writes use Exclusive;
- `CEL(ExclusiveOnly)` — both reads and writes use Exclusive.

`CEL(ExclusiveOnly)` isolates the cost of the Exclusive path from the benefit of Concurrent parallelism.

## Workloads

### `memory`

The default Work follows the C# memory benchmark model.

Each lock instance owns a separate shared buffer. A read operation:

1. starts from the shared state hash;
2. advances a thread-local xorshift random cursor;
3. performs random indexed loads;
4. applies a 64-bit mixing function.

A write operation:

1. advances the serialized writer cursor;
2. performs random indexed loads and in-place stores;
3. advances the shared state hash.

Default:

```text
64 MiB shared per lock
32 read steps per completed Work
32 write steps per completed Work
```

### `cpu`

A small scalar integer-mixing baseline with almost no memory pressure. It emphasizes the overhead of very short protected regions and is not a typical business workload.

## Scenarios

The standard run executes:

```text
100/0
99.5/0.5
90/10
50/50
30/70
0/100
```

Each value is the approximate read/write ratio.

## Correctness Controls

For every strategy/scenario:

- each worker starts with the same deterministic decision seed;
- every strategy completes the same number of operations;
- every strategy receives fresh lock and Work instances;
- read counts must match;
- write counts must match;
- final state hashes must match.

A mismatch terminates the benchmark with an error instead of printing incomparable throughput values.

## Metrics

| Metric | Meaning |
|---|---|
| `elapsed` | Wall-clock time for the strategy/scenario. |
| `cpu%` | Process CPU time normalized by reported hardware concurrency. Short runs can be noisy. |
| `works/s` | Completed lock acquisition + Work operations per second. |
| `works/s/lock` | Throughput divided by lock instance count. |
| `work/cpu%` | Throughput divided by normalized CPU percentage. |
| `reads` / `writes` | Completed operation counts. |
| `avg write ns` | Average elapsed nanoseconds around write acquisition, Work, and release. |
| `state` | Final deterministic Work state hash. |

## Commands

Default:

```shell
./build/TestAndBenchmark/TestAndBenchmark
```

Single hot lock:

```shell
./build/TestAndBenchmark/TestAndBenchmark --lock-instances 1 --threads 64 --workload memory --operations 100000 --memory-mb 64 --read-work 32 --write-work 32
```

Many fine-grained locks:

```shell
./build/TestAndBenchmark/TestAndBenchmark --lock-instances 64 --threads 1 --workload memory --operations 100000 --memory-mb 8 --read-work 32 --write-work 32
```

Long reference run:

```shell
./build/TestAndBenchmark/TestAndBenchmark --lock-instances 8 --threads 8 --workload memory --operations 500000 --memory-mb 64 --read-work 32 --write-work 32
```

CPU baseline:

```shell
./build/TestAndBenchmark/TestAndBenchmark --workload cpu --operations 1000000 --read-work 64 --write-work 64
```

## Interpreting Results

### One hot lock

A single highly contended lock most clearly exposes the difference between Concurrent parallelism and exclusive serialization.

### Many independent locks

As lock count grows, ordinary mutex strategies also gain natural parallelism because threads no longer contend on the same instance. The relative throughput multiplier of CEL may shrink even when its per-entity semantics remain useful.

### Write latency

A throughput result does not fully describe how quickly a rare Exclusive operation begins. Read-dominant workloads should also be evaluated using average and tail write latency in the actual application.

### Work size

When Work is extremely small, lock implementation overhead dominates. As Work grows, cache behavior, NUMA, scheduling, and business computation become larger parts of total time.

## Recommended Reporting

Record:

- exact commit/version;
- compiler and version;
- build type and optimization flags;
- operating system;
- CPU model/topology;
- lock instances;
- threads per lock;
- Work type and size;
- operations per thread;
- read/write steps;
- complete raw output.

Do not publish only the best scenario or omit configurations where another strategy is stronger.
