# Random Stress Test Plan

Date: 2026-07-22

## Plan

Focus the random stress test on Pipeline random combinations, while keeping a small amount of Scope coverage as a baseline.

Pipeline uses Scope internally for permission entry, release, upgrade, downgrade, and exception cleanup. If Pipeline survives long random stress, the combined semantics of Pipeline, Scope, and the underlying lock are exercised together.

Long-running stress can run indefinitely. Stop it by closing the window or pressing Ctrl+C.

The recommended public knobs are intentionally small:

- `--profile`: selects stress intensity.
- `--seed`: reproduces a failing run.
- `--workers`: sets worker thread count.
- `--lock-instances`: sets the number of independent lock instances.

Other details are fixed by the selected profile.

## Pass Criteria

- No unexpected exception during the run.
- No observed overlap between Concurrent and Exclusive regions.
- No observed state leak at the end.
- Pipeline can reacquire Exclusive after a segment throws an expected random exception.
- Any failure must print the seed and a full reproduction command.

## Commands

Quick smoke:

```powershell
TestAndBenchmark.exe stress --profile quick
```

Standard stress:

```powershell
TestAndBenchmark.exe stress --profile standard
```

Long-running stress:

```powershell
TestAndBenchmark.exe stress --profile forever
```

Saturate the machine:

```powershell
TestAndBenchmark.exe stress --profile max
```

Reproduce by seed:

```powershell
TestAndBenchmark.exe stress --profile standard --seed 123456
```

Specified machine pressure:

```powershell
TestAndBenchmark.exe stress --profile max --workers 128 --lock-instances 1
```
