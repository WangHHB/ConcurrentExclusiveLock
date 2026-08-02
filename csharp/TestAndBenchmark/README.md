# ConcurrentExclusiveLock Test and Benchmark

`TestAndBenchmark` is the C# reference test program for ConcurrentExclusiveLock (CEL). It contains correctness validation, long-running semantic stress, diagnostics, throughput measurements, acquisition-latency measurements, Exclusive-progress evaluation, Pipeline ablation, and upgrade-contention evaluation.

The command line is intentionally strict so the same topology can be reproduced in other languages:

- exactly one mode must be selected;
- parameters are executed literally and are never scaled from CPU count or machine speed;
- duplicate, unknown, or mode-inapplicable parameters are rejected;
- measured workers use dedicated `Thread` instances and start from explicit gates;
- performance modes perform only a small unreported code-path warmup, not a hidden replay of the requested workload.

## Build and launch

Build the complete solution in Release mode:

```text
dotnet build ../ConcurrentExclusivePack.sln -c Release
```

Windows:

```text
TestAndBenchmark.exe --help
```

Linux or macOS:

```text
dotnet TestAndBenchmark.dll --help
```

All examples below are one physical command line. Replace only paths, `--machine-id`, and `--output` when moving between machines; keep experimental parameters unchanged when results are intended to be compared.

## Common parameters

These parameters are shared by the modes that can use them. A mode rejects parameters that have no meaning for that mode.

| Parameter | Meaning | Default |
|---|---|---:|
| `--lock-instances N` | Number of independent lock groups active in parallel. Every group owns its own lock and workload object. | `1` |
| `--threads N` | Worker threads per lock group. Its exact role is described under each mode. | `32` |
| `--operations N` | Operations per measured worker. Its exact role is described under each mode. | `10000` |
| `--work N` | Sets both `--concurrent-work` and `--exclusive-work` to `N`. It cannot be combined with either specific work parameter. | — |
| `--concurrent-work N` | Work steps executed while Concurrent permission is held. | `32` |
| `--exclusive-work N` | Work steps executed while Exclusive permission is held. | `32` |
| `--output PATH` | Appends environment, invocation, and result records to a JSON Lines file. | no file |
| `--machine-id ID` | Stable label for one hardware/OS/SMT/affinity configuration. | `unlabeled` |
| `--experiment-id ID` | Stable label for one matrix row or experiment. | `manual` |

The JSONL file is append-only. Each invocation writes an environment record and an invocation record before mode-specific results. Keep the console log together with the JSONL file.

## Workload parameters

`--throughput`, `--latency`, and `--exclusive-progress` support the following workloads:

| Workload | Parameters | Meaning |
|---|---|---|
| `--workload cpu` | work-step parameters only | Deterministic CPU work without a large shared data object. |
| `--workload memory` | `--memory-mb N` | Random access to a lock-owned shared memory region of exactly `N` MiB. Default: `64`. |
| `--workload dictionary` | `--dictionary-size N` | Shared dictionary/cache workload with exactly `N` entries. Default: `1280`. |
| `--workload ledger` | `--dictionary-size N` | Shared account-ledger workload with exactly `N` accounts. Default: `1280`. |
| `--workload payload` | `--payload-frames N` | Shared binary-payload workload with exactly `N` frames. Default: `1024`. |

Each lock instance owns a separate workload object. For example, `--lock-instances 8 --workload memory --memory-mb 8` allocates eight independent 8 MiB working sets.

## Performance commands

### Throughput — `--throughput`

**Purpose**

Measures complete Concurrent and Exclusive operations: acquisition, protected work, release, loop overhead, and final-state validation.

**Design overview**

Every lock group has `--threads` dedicated workers. Each worker executes exactly `--operations` operations. A deterministic per-worker operation stream chooses Concurrent or Exclusive permission, and every compared strategy receives the same choice at every worker/operation coordinate.

If `--concurrent-permille` is omitted, the command runs the standard six scenarios:

```text
1000, 995, 900, 500, 300, 0
```

These correspond to Concurrent/Exclusive ratios of `100/0`, `99.5/0.5`, `90/10`, `50/50`, `30/70`, and `0/100`.

Compared implementations:

- `lock` / `Monitor`;
- `ReaderWriterLockSlim`;
- CEL.

**Mode parameters**

| Parameter | Meaning |
|---|---|
| `--lock-instances N` | Independent lock groups. |
| `--threads N` | Workers per lock group. |
| `--operations N` | Complete operations per worker. |
| `--concurrent-permille N` | Runs one mix in `[0,1000]`; `900` means 90% Concurrent. Omit it to run all six standard mixes. |
| workload parameters | Select protected work and data size. |

**Main output**

`elapsed`, normalized process `cpu%`, total and per-lock throughput, Concurrent/Exclusive operation counts, average complete Exclusive-operation duration, and final state hash. The average Exclusive value includes acquire + work + release; it is not pure acquisition latency.

**One-line example**

```text
TestAndBenchmark.exe --throughput --lock-instances 1 --threads 64 --operations 100000 --workload memory --memory-mb 8 --concurrent-work 64 --exclusive-work 64 --machine-id workstation-win-smt-on --experiment-id throughput-1x64-memory --output results.jsonl
```

### Acquisition latency — `--latency`

**Purpose**

Measures only the blocking acquisition call for Concurrent and Exclusive permission while still executing the complete requested workload.

**Design overview**

The topology and deterministic permission stream match throughput mode. Every acquisition is timed. Work and release are outside the measured interval. To control retained data size, every worker-local block of `N` operations contributes exactly one deterministic pseudo-random sample selected from a sampling stream independent of the permission-choice stream.

Compared implementations:

- `lock` / `Monitor`;
- `ReaderWriterLockSlim`;
- CEL.

**Mode parameters**

| Parameter | Meaning |
|---|---|
| `--lock-instances N` | Independent lock groups. |
| `--threads N` | Workers per lock group. |
| `--operations N` | Timed acquisitions and complete operations per worker. |
| `--concurrent-permille N` | One Concurrent/Exclusive mix in `[0,1000]`. Omit it to run all six standard mixes. |
| `--latency-sample-every N` | Retains one sample from each worker-local block of `N` operations. All acquisitions are still timed and executed. Default: `1`. |
| workload parameters | Select work performed after acquisition. |

**Main output**

For each permission: sample count, mean, p50, p95, p99, p99.9, and maximum. Percentiles use linear interpolation at `p × (count - 1)` over sorted samples.

**One-line example**

```text
TestAndBenchmark.exe --latency --lock-instances 1 --threads 64 --operations 50000 --concurrent-permille 900 --workload memory --memory-mb 8 --concurrent-work 64 --exclusive-work 64 --latency-sample-every 10 --machine-id workstation-win-smt-on --experiment-id latency-1x64-memory-90-10 --output results.jsonl
```

### Exclusive progress — `--exclusive-progress`

**Purpose**

Counts how many Exclusive operations can complete while every implementation processes the same fixed amount of Concurrent work.

**Design overview**

For each lock group:

1. `--threads` Concurrent workers acquire once and wait at a common flood gate.
2. One Exclusive writer begins its first acquisition and receives one unmeasured 10 ms arm interval.
3. All lock groups open their flood gates at the same measurement start.
4. Every Concurrent worker executes exactly `--operations` Concurrent operations.
5. The writer performs Exclusive operations while at least one Concurrent worker for that lock remains active.
6. After every completed Exclusive operation, the writer must wait for at least one new Concurrent completion on the same lock before requesting Exclusive again.

The per-lock reentry gate is part of the benchmark definition. It prevents a fast writer from repeatedly reacquiring, extending the flood, and creating extra time in which to count itself again. One initial Exclusive completion is possible per lock; every later completion requires new same-lock Concurrent progress.

The test uses only acquire/release operations and external counters/gates. It does not inspect `ObservedState`, `WaitingWriteCount`, or another implementation-specific pending-writer property.

Compared implementations:

- `ReaderWriterLockSlim`;
- CEL.

Serialized `lock` is excluded because it cannot establish the required initial topology in which all Concurrent workers hold permission simultaneously.

**Mode parameters**

| Parameter | Meaning |
|---|---|
| `--lock-instances N` | Independent lock groups and therefore Exclusive writers. |
| `--threads N` | Concurrent flood workers per lock group. |
| `--operations N` | Fixed Concurrent operations per Concurrent worker. It is not the number of writer attempts. |
| workload parameters | Concurrent and Exclusive protected work. |

**Main output**

Concurrent throughput, `Exclusive entries`, entries per one million Concurrent operations, Exclusive operations per second, and minimum/maximum Exclusive entries among lock instances. `min-lock entries` is important in multi-lock runs because aggregate totals can hide one starving writer.

**One-line example**

```text
TestAndBenchmark.exe --exclusive-progress --lock-instances 8 --threads 8 --operations 100000 --workload cpu --concurrent-work 64 --exclusive-work 8 --machine-id workstation-win-smt-on --experiment-id exclusive-progress-8x8-cpu --output results.jsonl
```

### Pipeline performance — `--pipeline-perf`

**Purpose**

Evaluates the CEL staged-operation model:

```text
Concurrent prepare -> Exclusive commit -> Concurrent post
```

**Design overview**

Each worker executes exactly `--operations` complete logical operations. Every logical operation performs the same deterministic stage work and exactly one validated commit. Each lock instance owns independent synchronization and state.

CEL internal ablations:

- `CEL Core converge`: continuous permission with direct in-place upgrade/downgrade;
- `CEL Scope converge`: the same transition semantics through Scope cleanup;
- `CEL Pipeline converge`: the same transition semantics declared as Pipeline segments;
- `CEL Core handoff`: releases and reacquires between stages.

Portable baselines:

- `RWLS handoff`: releases and reacquires between read/write stages;
- `Monitor serialized`: executes all stages under one monitor.

Handoff baselines have weaker semantics because another operation may intervene between stages.

**Mode parameters**

| Parameter | Meaning |
|---|---|
| `--lock-instances N` | Independent staged-operation groups. |
| `--threads N` | Staged-operation workers per lock group. |
| `--operations N` | Complete prepare/commit/post operations per worker. |
| `--prepare-work N` | Work steps in the first Concurrent stage. Default: `64`. |
| `--commit-work N` | Work steps in the Exclusive commit stage. Default: `8`. |
| `--post-work N` | Work steps in the final Concurrent stage. Default: `64`. |

**Main output**

Elapsed time, CPU percentage, total and per-lock logical operations per second, nanoseconds per logical operation, and validated commit count.

**One-line example**

```text
TestAndBenchmark.exe --pipeline-perf --lock-instances 1 --threads 64 --operations 100000 --prepare-work 128 --commit-work 16 --post-work 128 --machine-id workstation-win-smt-on --experiment-id pipeline-1x64-128-16-128 --output results.jsonl
```

### Upgrade contention — `--upgrade-contention N M`

**Purpose**

Measures CEL's undeclared direct in-place Concurrent-to-Exclusive upgrade under simultaneous upgrade and ordinary Exclusive competition.

**Design overview**

For every lock instance:

1. `N` workers acquire Concurrent permission and wait at the upgrade gate.
2. `M` ordinary Exclusive contenders become ready.
3. All independent lock groups open from one global gate.
4. All `N` holders request in-place upgrade together.
5. The test verifies that no ordinary Exclusive request enters before its own lock's upgrade chain drains.

`N` and `M` are literal per-lock populations. `M` may be zero. Platform comparison locks are not included because they do not expose CEL's undeclared direct in-place upgrade semantics.

**Mode parameters**

| Parameter | Meaning |
|---|---|
| first positional value `N` | Simultaneous upgrading Concurrent holders per lock; must be at least `1`. |
| second positional value `M` | Ordinary Exclusive contenders per lock; may be `0`. |
| `--lock-instances N` | Independent lock groups released together. |

`--threads`, `--operations`, and workload parameters are intentionally not used by this mode.

**Main output**

Time to first upgrade, full upgrade-chain drain time, upgrade throughput, acquisition/release distributions, ordinary Exclusive acquisition distribution, worst-lock p99, worst-lock drain, and count of ordinary Exclusive entries before upgrade drain. A valid run ends with that count equal to zero.

**One-line example**

```text
TestAndBenchmark.exe --upgrade-contention 8 4 --lock-instances 8 --machine-id workstation-win-smt-on --experiment-id upgrade-8x8-plus4 --output results.jsonl
```

## Correctness, stress, and diagnostic commands

### Combined correctness — `--correctness`

**Purpose**

Runs the deterministic advanced-lock cases and the full CEL semantic contract, including randomized valid semantic paths.

**Design overview**

The suite covers direct acquisition, Scope binding/lifecycle, ContextID/EpochID nesting, state/contention snapshots, upgrade/downgrade cycles, unconditional and conditional conversion, Pipeline behavior, exception cleanup, independent locks, and randomized contract-valid paths. Timeouts are deadlock detectors; they do not reduce requested work.

**Mode parameters**

| Parameter | Meaning | Default |
|---|---|---:|
| `--lock-instances N` | Independent locks used by randomized and mass-independent cases. | `1` |
| `--semantic-workers N` | Random semantic workers per lock; must be at least `2`. | `4` |
| `--semantic-operations N` | Random semantic rounds per lock. | `256` |
| `--semantic-seed N` | Reproducible seed for semantic paths. Omit for a generated seed. | generated |
| `--pipeline-exception-permille N` | Executed random Pipeline segments marked for injected failure per 1,000. Range `[0,1000]`. | `10` |
| `--advanced-operations N` | Operations per participant in the many-independent-lock advanced case. | `1` |
| `--advanced-seed N` | Reproducible seed for advanced randomized work. Omit for a generated seed. | generated |

**One-line example**

```text
TestAndBenchmark.exe --correctness --lock-instances 8 --semantic-workers 8 --semantic-operations 1000 --semantic-seed 12345 --advanced-operations 4 --advanced-seed 23456 --machine-id workstation-win-smt-on --experiment-id correctness --output results.jsonl
```

### Pipeline semantic stress — `--pipeline-stress DURATION`

**Purpose**

Repeatedly runs the complete Pipeline semantic suite for a requested duration. This is correctness stress, not a performance ranking.

**Design overview**

Every batch runs fixed semantic cases and random 3–7 segment Pipelines over all requested lock groups and workers. Generated segments cover `None`, Concurrent, Exclusive, converge, try, ContextID, and EpochID paths. Injected exceptions may occur before or after business work while the declared permission is active. The test validates exact propagation, tracker cleanup, permission release, lock reuse, and unrelated-worker progress.

Accepted duration forms include `30s`, `15m`, `24h`, `1d`, and `hh:mm:ss`.

**Mode parameters**

| Parameter | Meaning |
|---|---|
| positional `DURATION` | Minimum requested wall-clock stress duration. |
| `--lock-instances N` | Persistent lock groups used in each batch. |
| `--semantic-workers N` | Workers per lock group. |
| `--semantic-operations N` | Rounds per lock per batch. |
| `--semantic-seed N` | Base seed from which deterministic batch seeds are derived. |
| `--pipeline-exception-permille N` | Random executed segments marked for injected failure per 1,000. |

**One-line example**

```text
TestAndBenchmark.exe --pipeline-stress 10m --lock-instances 8 --semantic-workers 8 --semantic-operations 1000 --semantic-seed 34567 --pipeline-exception-permille 10 --machine-id workstation-win-smt-on --experiment-id pipeline-stress-10m --output results.jsonl
```

**24-hour release validation**

```text
TestAndBenchmark.exe --pipeline-stress 24h --lock-instances 8 --semantic-workers 128 --semantic-operations 2000
```

### Persistent-lock endurance — `--endurance DURATION`

**Purpose**

Runs randomized semantic validation while reusing the same lock objects for the complete duration.

**Design overview**

Unlike batch-local correctness runs, the lock set persists across every batch. This is intended to expose accumulated lifecycle, epoch, state, and cleanup defects. The printed base seed deterministically derives every batch seed.

**Mode parameters**

| Parameter | Meaning |
|---|---|
| positional `DURATION` | Minimum requested wall-clock endurance duration. |
| `--lock-instances N` | Persistent lock objects. |
| `--semantic-workers N` | Workers per lock. |
| `--semantic-operations N` | Rounds per lock per batch. |
| `--semantic-seed N` | Reproducible base seed; omit for a generated seed. |

**One-line example**

```text
TestAndBenchmark.exe --endurance 10m --lock-instances 8 --semantic-workers 8 --semantic-operations 1000 --semantic-seed 45678 --machine-id workstation-win-smt-on --experiment-id endurance-10m --output results.jsonl
```

### Contention diagnostic — `--contention-diagnostic DURATION`

**Purpose**

Pressures one CEL instance and samples the weak diagnostic `Contention` snapshot.

**Design overview**

The mode is not a cross-lock performance comparison and does not treat `ObservedState` or `ObservedContention` as synchronization proof. It reports diagnostic maximum, average, nonzero percentage, operation count, elapsed time, and CPU use.

**Mode parameters**

| Parameter | Meaning |
|---|---|
| positional `DURATION` | Diagnostic run duration. |
| `--threads N` | Total pressure workers on the single CEL instance. |

**One-line example**

```text
TestAndBenchmark.exe --contention-diagnostic 30s --threads 64 --machine-id workstation-win-smt-on --experiment-id contention-diagnostic-30s --output results.jsonl
```

## Interpreting results

- `cpu%` is process CPU time divided by wall time and the runtime-visible logical processor count. Very short runs may reflect operating-system CPU-time resolution.
- Throughput's average Exclusive-operation duration is acquire + held work + release + timing overhead. It is not an acquisition percentile.
- Use `--latency` for acquisition-tail distributions.
- Use `--exclusive-progress` to compare writer progress during a fixed amount of Concurrent work; do not interpret it as unrestricted writer throughput.
- Use `min-lock entries` and worst-lock tails in multi-lock runs so aggregate results do not hide one poorly progressing lock.
- Pipeline converge and handoff strategies do not provide identical semantics. Handoff permits interference between stages.
- Upgrade contention is CEL-specific and should not be presented as a conventional reader/writer-lock comparison.
- Keep correctness/stress evidence separate from performance rankings.
- Retain all runs rather than selecting only the best result.

## Matrix runners

`PaperMatrix/common-core.tsv`, `run-matrix.ps1`, and `run-matrix.sh` provide a transparent larger exploratory matrix. They do not inspect the machine to alter topology or operation counts.

Windows PowerShell:

```text
.\PaperMatrix\run-matrix.ps1 -DotNet dotnet -BenchmarkDll .\TestAndBenchmark.dll -Matrix .\PaperMatrix\common-core.tsv -MachineId workstation-smt-on -OutputDirectory .\results\workstation-smt-on
```

Linux or macOS:

```text
./PaperMatrix/run-matrix.sh /path/to/dotnet /path/to/TestAndBenchmark.dll ./PaperMatrix/common-core.tsv workstation-linux ./results/workstation-linux
```

The runner creates exact command logs, append-only JSONL results, and complete per-experiment console logs.

## Cross-language porting requirements

A port should preserve behavior, not merely translate syntax:

1. Use one dedicated OS thread per requested worker; do not replace measured workers with a thread pool, tasks, async jobs, goroutines, or scheduler-managed jobs.
2. Create locks, workloads, threads, and gates before timing, then release workers from the documented common gate.
3. Preserve literal topology: `lockIndex = globalWorker / threadsPerLock`; every lock owns independent synchronization and state.
4. Never scale user parameters from CPU count, memory, NUMA topology, runtime, or observed speed.
5. Preserve the deterministic operation-mix generator and independent sample-retention stream.
6. Time every acquisition in latency mode; retain one deterministic sample per worker-local block.
7. Use a monotonic high-resolution clock and the same linear-interpolation percentile definition.
8. Preserve the Exclusive-progress initial-holder topology and same-lock post-Exclusive Concurrent-completion gate.
9. Preserve continuous-permission versus handoff semantic labels in Pipeline mode.
10. Preserve upgrade-contention ordering and the rule that ordinary Exclusive cannot enter before its own upgrade chain drains.
11. Preserve exception injection, cleanup checks, and reproducible seed reporting in semantic stress.
12. Preserve JSONL field meanings and increment the schema version whenever a field is removed, renamed, or changes meaning.

## Historical benchmark snapshot

The main library README contains the retained Windows 11 / Ryzen 7 5700X / .NET 8.0.22 historical throughput tables and their interpretation. Those historical `avg write ns` values measured complete Exclusive operations, not pure acquisition latency. New formal measurements should use the current explicit commands and archive both JSONL and console output.

## Formal cross-machine command set

The following commands are the fixed primary set for the current cross-machine evaluation. Replace `workstation-win-smt-on` with the machine/configuration label and replace `results.jsonl` with the desired output path. Treat SMT on/off, operating-system changes, and materially different affinity/NUMA placement as different machine IDs. Do not otherwise change the parameters when comparing machines.

```text
TestAndBenchmark.exe --correctness --lock-instances 8 --semantic-workers 8 --semantic-operations 1000 --semantic-seed 12345 --advanced-operations 4 --advanced-seed 23456 --machine-id workstation-win-smt-on --experiment-id correctness --output results.jsonl

TestAndBenchmark.exe --throughput --lock-instances 1 --threads 64 --operations 100000 --workload memory --memory-mb 8 --concurrent-work 64 --exclusive-work 64 --machine-id workstation-win-smt-on --experiment-id throughput-1x64-memory --output results.jsonl
TestAndBenchmark.exe --throughput --lock-instances 8 --threads 8 --operations 100000 --workload memory --memory-mb 8 --concurrent-work 64 --exclusive-work 64 --machine-id workstation-win-smt-on --experiment-id throughput-8x8-memory --output results.jsonl

TestAndBenchmark.exe --latency --lock-instances 1 --threads 8 --operations 50000 --concurrent-permille 900 --workload memory --memory-mb 8 --concurrent-work 64 --exclusive-work 64 --latency-sample-every 10 --machine-id workstation-win-smt-on --experiment-id latency-1x8-memory-90-10 --output results.jsonl
TestAndBenchmark.exe --latency --lock-instances 1 --threads 64 --operations 50000 --concurrent-permille 900 --workload memory --memory-mb 8 --concurrent-work 64 --exclusive-work 64 --latency-sample-every 10 --machine-id workstation-win-smt-on --experiment-id latency-1x64-memory-90-10 --output results.jsonl
TestAndBenchmark.exe --latency --lock-instances 8 --threads 8 --operations 50000 --concurrent-permille 900 --workload memory --memory-mb 8 --concurrent-work 64 --exclusive-work 64 --latency-sample-every 10 --machine-id workstation-win-smt-on --experiment-id latency-8x8-memory-90-10 --output results.jsonl

TestAndBenchmark.exe --exclusive-progress --lock-instances 1 --threads 64 --operations 100000 --workload cpu --concurrent-work 64 --exclusive-work 8 --machine-id workstation-win-smt-on --experiment-id exclusive-progress-1x64-cpu --output results.jsonl
TestAndBenchmark.exe --exclusive-progress --lock-instances 8 --threads 8 --operations 100000 --workload cpu --concurrent-work 64 --exclusive-work 8 --machine-id workstation-win-smt-on --experiment-id exclusive-progress-8x8-cpu --output results.jsonl

TestAndBenchmark.exe --pipeline-perf --lock-instances 1 --threads 64 --operations 100000 --prepare-work 128 --commit-work 16 --post-work 128 --machine-id workstation-win-smt-on --experiment-id pipeline-1x64-128-16-128 --output results.jsonl
TestAndBenchmark.exe --pipeline-perf --lock-instances 8 --threads 8 --operations 100000 --prepare-work 128 --commit-work 16 --post-work 128 --machine-id workstation-win-smt-on --experiment-id pipeline-8x8-128-16-128 --output results.jsonl

TestAndBenchmark.exe --upgrade-contention 64 0 --lock-instances 1 --machine-id workstation-win-smt-on --experiment-id upgrade-1x64-plus0 --output results.jsonl
TestAndBenchmark.exe --upgrade-contention 64 16 --lock-instances 1 --machine-id workstation-win-smt-on --experiment-id upgrade-1x64-plus16 --output results.jsonl
TestAndBenchmark.exe --upgrade-contention 8 0 --lock-instances 8 --machine-id workstation-win-smt-on --experiment-id upgrade-8x8-plus0 --output results.jsonl
TestAndBenchmark.exe --upgrade-contention 8 4 --lock-instances 8 --machine-id workstation-win-smt-on --experiment-id upgrade-8x8-plus4 --output results.jsonl
```
