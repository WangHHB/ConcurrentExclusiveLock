# Integrated Suite Test Plan

Date: 2026-07-22

## Plan

`suite` combines the common test plans behind one entry point so routine verification does not require memorizing many commands.

Default coverage:

- correctness: black-box correctness.
- steady: standard cross-target performance benchmark.
- advanced-perf: advanced semantic performance such as in-place upgrade / downgrade.
- exclusive-preemption: Exclusive preemption latency.
- stress: Pipeline-focused random stress.

BenchmarkDotNet microbenchmarks are not included in the suite by default. They build separate benchmark processes, produce detailed reports, and are better analyzed on their own. Add `--include-micro` when a short microbenchmark smoke run is needed.

`--group` selects the test shape:

- `smoke`: default integrated smoke test covering correctness, representative performance, preemption, and random stress.
- `empty`: empty-lock / no-work boundary, focused on pure permission acquisition and release cost.
- `short`: very short critical region, using CPU `--work 16`.
- `long`: long CPU-work boundary, checking whether lock cost is diluted and preemption remains stable.
- `workloads`: runs `cpu`, `memory`, `dictionary`, `ledger`, and `payload`.
- `instances100`: runs 100 independent lock instances and scales operations per thread so total works match the single-lock workload group.
- `all`: correctness + empty + short + long + workloads + instances100 + stress.

## Pass Criteria

- If any child command exits non-zero, the suite stops and prints the failing command.
- If every child command exits zero, it prints `Suite result: PASS`.
- `--seed` is forwarded to stress for reproduction.

## Commands

Quick integrated suite:

```powershell
TestAndBenchmark.exe suite --profile quick --group smoke
```

Standard integrated suite:

```powershell
TestAndBenchmark.exe suite --profile standard --group smoke
```

Empty-lock boundary:

```powershell
TestAndBenchmark.exe suite --profile quick --group empty
```

Very short critical region:

```powershell
TestAndBenchmark.exe suite --profile quick --group short
```

Long work boundary:

```powershell
TestAndBenchmark.exe suite --profile quick --group long
```

Workload group:

```powershell
TestAndBenchmark.exe suite --profile quick --group workloads
```

100 independent lock instances, total works aligned with the workload group:

```powershell
TestAndBenchmark.exe suite --profile quick --group instances100
```

Full group:

```powershell
TestAndBenchmark.exe suite --profile standard --group all
```

With BenchmarkDotNet low-contention smoke:

```powershell
TestAndBenchmark.exe suite --profile quick --include-micro
```

Specified stress seed:

```powershell
TestAndBenchmark.exe suite --profile quick --seed 123456
```
