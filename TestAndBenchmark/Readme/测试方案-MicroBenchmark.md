# Micro Benchmark 标准微基准

日期：2026-07-22

## 测试方案

使用 BenchmarkDotNet 测单路径成本、低竞争路径、分配、线程诊断和关键路径反汇编。

当前覆盖：

- 无竞争上限：NoLock / AtomicOnly。
- BCL 对照：Monitor / lock、ReaderWriterLockSlim。
- Scope 基础路径：Concurrent / Exclusive。
- Scope 高级路径：原地升级、原地降级、Try 失败路径。
- Pipeline：单段和基础转换路径。
- 低竞争路径：16 个工作线程统一开始门，CPU work 16/16，Concurrent-only 与 99.5/0.5 mixed，对照 Scope / ReaderWriterLockSlim / Monitor。

无竞争和低竞争 micro 均使用公共 `BenchmarkWork` CPU payload。

## 通过标准

- 输出 Mean / Error / StdDev / Ratio / Allocated 等 BenchmarkDotNet 指标。
- 无竞争类启用 MemoryDiagnoser、ThreadingDiagnoser、DisassemblyDiagnoser。
- Try 失败类启用 MemoryDiagnoser、ThreadingDiagnoser、DisassemblyDiagnoser。
- 低竞争类启用 MemoryDiagnoser、ThreadingDiagnoser。
- Scope 基础路径与对照组处于稳定、可解释区间。
- 原地升级 / 降级和 Pipeline 路径没有异常分配。
- 多次运行的 Mean / Ratio 波动较小，不使用单次极值下结论。

## 命令

标准运行：

```powershell
TestAndBenchmark.exe micro
```

快速冒烟：

```powershell
TestAndBenchmark.exe micro --filter *ScopeConcurrent* --job short --warmupCount 1 --iterationCount 1
```

低竞争冒烟：

```powershell
TestAndBenchmark.exe micro --filter *LowContention* --job short --warmupCount 1 --iterationCount 1
```
