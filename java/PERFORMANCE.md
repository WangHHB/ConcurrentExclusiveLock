# Performance Benchmarking

The standard benchmark compares lock strategies under the same workload, thread count, lock-instance count, and read/write ratio:

- `synchronized`;
- non-fair `ReentrantLock`;
- non-fair `ReentrantReadWriteLock`;
- `StampedLock`;
- `ConcurrentExclusiveLock` (`CEL`);
- `CEL(ExclusiveOnly)`.

Each strategy and ratio receives independent lock instances and independent Work data. Dedicated threads are created before the timed interval and released through a common start gate.

The benchmark automatically runs these read/write ratios:

```text
100 / 0
99.5 / 0.5
90 / 10
50 / 50
30 / 70
0 / 100
```

## Parameters

| Parameter | Meaning |
|---|---|
| `--lock-instances` | Independent lock + Work instances. |
| `--threads` | Dedicated threads per lock instance. |
| `--operations` | Lock and Work operations per thread. |
| `--workload` | `cpu`, `memory`, `dictionary`, `ledger`, or `payload`. |
| `--work` | Sets both read and write business-step counts. |
| `--read-work` | Read business steps, overriding `--work`. |
| `--write-work` | Write business steps, overriding `--work`. |
| `--memory-mb` | Memory-work MiB per lock instance. |
| `--dictionary-size` | Data size per lock instance. |

```text
total threads    = lock-instances × threads
total operations = lock-instances × threads × operations
```

## Output

- `elapsed`: measured execution time;
- `cpu%`: process CPU time normalized by elapsed time and the JVM-visible processor count;
- `works/s`: completed lock + Work operations per second;
- `works/s/lock`: average throughput per lock instance;
- `work/cpu%`: auxiliary throughput-per-sampled-CPU indicator;
- `reads` / `writes`: actual operation counts;
- `avg write ns`: average elapsed nanoseconds of write operations, including lock acquisition, Work, and release;
- `state`: deterministic final Work state used to compare strategies.

`cpu%` is an auxiliary process-level sample, not hardware-precise measurement. Very short scenarios may show 0 or transiently abnormal values, including values slightly above 100% because process CPU time is sampled at a coarser granularity than very short wall-clock intervals. Formal comparisons should prioritize elapsed time, throughput, final-state consistency, and repeated stable ranges.

## Commands

```powershell
# Quick smoke test
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 1 --threads 4 --workload cpu --operations 100 --work 4

# Short critical sections
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 1 --threads 64 --workload cpu --operations 100000 --work 0

# Memory comparison matching the C# benchmark shape
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 1 --threads 64 --workload memory --operations 10000 `
  --memory-mb 64 --read-work 64 --write-work 64

# Dictionary workload
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 1 --threads 64 --workload dictionary --operations 10000 `
  --dictionary-size 65536 --read-work 64 --write-work 128

# Many independent locks
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 100 --threads 16 --workload dictionary --operations 1000 `
  --dictionary-size 1280 --read-work 64 --write-work 64

# CEL advanced permission paths
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --advanced-perf --threads 64 --operations 100000 --work 64
```


## Included validation result

`TestResults/benchmark-memory-64threads.txt` contains one complete run of the command above from the build sandbox. It is included to verify that every comparison strategy executes all six ratios and produces the same final Work state. The numbers are environment-specific and are not publication-grade cross-machine claims.
