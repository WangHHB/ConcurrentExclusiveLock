# Advanced Perf 高级语义性能测试

日期：2026-07-21

## 测试方案

使用固定数量专用工作线程，对 CEL 高级语义路径做性能测试。

当前覆盖：

- Concurrent enter / release。
- Exclusive enter / release。
- ExclusiveToConcurrent 原地降级。
- ConcurrentToExclusive 原地升级。
- Concurrent 与降级路径 50/50 混合。
- Exclusive 与升级路径 50/50 混合。

每个 semantic loop 只执行一次公共 `BenchmarkWork` payload。每个 case 使用 fresh lock。

workload 参数与稳态并发基准一致：

- `--workload`：`cpu`、`memory`、`dictionary`、`ledger`、`payload`。
- `--work`：同时设置 ConcurrentWork 和 ExclusiveWork。
- `--read-work`：设置 ConcurrentWork。
- `--write-work`：设置 ExclusiveWork。
- `--memory-mb`：memory workload 的共享内存规模。
- `--dictionary-size`：dictionary / ledger / payload 的规模参数。

CEL 的升级 / 降级是原地权限转换：

- `ExclusiveToConcurrent` 是原地降级，中间不释放访问窗口。
- `ConcurrentToExclusive` 是原地升级，升级前不需要额外进入特殊可升级模式。
- 原地升级得到的 Exclusive 权限比普通 Exclusive 更抢占。

可选 target：

- `scope`：ConcurrentExclusiveLockScope。
- `rwls`：.NET ReaderWriterLockSlim 的近似对照路径。
- `monitor`：Monitor / lock 的近似对照路径。

RWLS 和 Monitor 没有 CEL 的真正原地升级 / 降级语义，因此高级语义 case 中它们只作为模拟 / 近似性能对照，不是语义等价对照。

## 好结果

- 每个 case 最终状态为 `Idle`。
- 所有线程完成指定 loops。
- 原地升级 / 降级路径的 `ns/op` 和吞吐稳定。
- 混合路径表现可解释，没有异常长尾或最终状态泄漏。
- `cpu%` 只作为观察项，不作为通过标准。

## 命令

快速运行：

```powershell
TestAndBenchmark.exe advanced-perf --threads 16 --operations 10000 --workload cpu --work 64
```

标准运行：

```powershell
TestAndBenchmark.exe advanced-perf --target all --threads 64 --operations 10000 --workload dictionary --dictionary-size 65536 --read-work 64 --write-work 128
```

和 .NET 内置锁对比：

```powershell
TestAndBenchmark.exe advanced-perf --target all --threads 64 --operations 10000 --workload dictionary --dictionary-size 65536 --read-work 64 --write-work 128
```
