# Suite 集成测试套件

日期：2026-07-22

## 测试方案

`suite` 用于把常用测试方案整合成一条入口命令，避免手动记多组命令。

默认覆盖：

- correctness：黑盒正确性。
- steady：标准横向性能基准。
- advanced-perf：原地升级 / 降级等高级语义性能。
- exclusive-preemption：Exclusive 抢占延迟。
- stress：Pipeline-focused 随机压力。

Micro BenchmarkDotNet 默认不进入 suite，因为它会单独构建 benchmark 进程、输出大量报告，并且更适合单独分析。需要时加 `--include-micro`。

`--group` 用于按测试形态分组：

- `smoke`：默认集成冒烟，覆盖 correctness、代表性性能、抢占和随机压力。
- `empty`：空锁 / 无 work 边界，主要看纯权限获取与释放成本。
- `short`：极短临界区，使用 `--work 16` 的轻 CPU work，主要看短业务区下锁成本是否过重。
- `long`：长 CPU work 边界，主要看业务区变长后锁成本是否被稀释，以及抢占是否稳定。
- `workloads`：分别跑 `cpu`、`memory`、`dictionary`、`ledger`、`payload` 五类 workload。
- `instances100`：100 份独立锁实例同时运行，按 `lock-instances=100` 折算 operations/thread，使总 works 与 `workloads` 单锁组一致。
- `all`：correctness + empty + short + long + workloads + instances100 + stress。

## 通过标准

- 任一子命令返回非 0，suite 立即停止并报告失败命令。
- 所有子命令返回 0，输出 `Suite result: PASS`。
- `--seed` 会传给 stress，便于复现。

## 命令

快速集成套件：

```powershell
TestAndBenchmark.exe suite --profile quick --group smoke
```

标准集成套件：

```powershell
TestAndBenchmark.exe suite --profile standard --group smoke
```

空锁：

```powershell
TestAndBenchmark.exe suite --profile quick --group empty
```

极短临界区：

```powershell
TestAndBenchmark.exe suite --profile quick --group short
```

长 work 边界：

```powershell
TestAndBenchmark.exe suite --profile quick --group long
```

workload 分组：

```powershell
TestAndBenchmark.exe suite --profile quick --group workloads
```

100 份独立锁实例，总 works 对齐 workload 分组：

```powershell
TestAndBenchmark.exe suite --profile quick --group instances100
```

完整分组：

```powershell
TestAndBenchmark.exe suite --profile standard --group all
```

带 BenchmarkDotNet 低竞争冒烟：

```powershell
TestAndBenchmark.exe suite --profile quick --include-micro
```

指定 stress seed：

```powershell
TestAndBenchmark.exe suite --profile quick --seed 123456
```
