# Correctness Test Plan

Date: 2026-07-22

## Plan

Run fast deterministic behavior tests covering base lock semantics, Scope automatic release, exception cleanup, upgrade, downgrade, and basic Pipeline segment conversion.

This test plan checks functional contract regressions. It is not used for performance conclusions.

## Pass Criteria

- Every test prints `PASS`.
- Process exit code is `0`.
- Any failed test, timeout, or unexpected exception is a failure.

## Commands

```powershell
TestAndBenchmark.exe correctness
```

```powershell
TestAndBenchmark.exe test
```

Show available entry points:

```powershell
TestAndBenchmark.exe help
```
