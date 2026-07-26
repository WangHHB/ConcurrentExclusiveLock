# Semantic and Stress Testing

The test project validates the protocol correctness and long-running stability of:

- `ConcurrentExclusiveLock`;
- `ConcurrentExclusiveLockScope`;
- `ConcurrentExclusiveLockPipeline`.

It uses public APIs and dedicated Java threads. Any permission overlap, assertion failure, worker exception, or timeout returns a nonzero process exit code.

## Modes

### `--advanced-correctness`

Validates upgrade serialization, ContextID unique-winner behavior, downgrade behavior, and random legal permission paths across independent locks.

### `--full-semantics`

Runs deterministic core, Scope, and Pipeline contracts followed by model-driven random legal paths. It covers:

- Concurrent / Exclusive acquisition and release;
- preemptive Exclusive behavior;
- Exclusive → Concurrent downgrade;
- Concurrent → Exclusive upgrade;
- ContextID / EpochID conditional upgrade;
- Scope close and exception release;
- Pipeline transitions and failure continuation.

### `--pipeline-semantics`

Runs fixed Pipeline transition contracts and concurrent fixed Pipeline batches.

### `--pipeline-stress <duration>`

Randomly chooses the number of locks, workers per lock, rounds per lock, Segment combinations, and a reproducible batch seed. A failed batch prints its exact shape and seed.

### `--endurance <duration>`

Creates persistent lock objects and repeatedly executes legal permission paths until the requested duration expires. It periodically reports operation rate, process CPU sampling, heap usage, thread count, and GC count.

### `--contention-stress <duration>`

Runs many dedicated threads against one lock using a Concurrent / Exclusive mix.

## Semantic parameters

| Parameter | Meaning |
|---|---|
| `--lock-instances` | Number or maximum number of independent locks. |
| `--semantic-workers` | Dedicated workers per lock; minimum 2. |
| `--semantic-operations` | Legal paths or Pipeline rounds per lock. |
| `--semantic-seed` | Reproducible random seed. |
| `--advanced-operations` | Operations per lock in advanced correctness mode. |
| `--advanced-seed` | Reproducible advanced-test seed. |

Supported durations:

```text
30s
10m
24h
1d
hh:mm:ss
```

## Suggested order

```powershell
# Help
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar --help

# Full deterministic and random semantic regression
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --full-semantics `
  --lock-instances 64 `
  --semantic-workers 4 `
  --semantic-operations 256

# Fixed Pipeline semantics
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-semantics `
  --lock-instances 1 `
  --semantic-workers 64 `
  --semantic-operations 1000 `
  --semantic-seed 12345

# Ten-minute random Pipeline smoke test
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 64 `
  --semantic-operations 1000 `
  --semantic-seed 12345

# General multi-hour validation
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-stress 3h `
  --lock-instances 8 `
  --semantic-workers 128 `
  --semantic-operations 2000

# Release validation
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-stress 24h `
  --lock-instances 8 `
  --semantic-workers 128 `
  --semantic-operations 2000
```
