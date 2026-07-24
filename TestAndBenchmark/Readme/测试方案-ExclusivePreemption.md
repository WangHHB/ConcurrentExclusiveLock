# Exclusive Preemption 抢占延迟测试

日期：2026-07-21

## 测试方案

固定数量 Concurrent 工作线程持续获取 Concurrent 权限，让 Exclusive 请求周期性到达。

推荐参数使用统一命名：`--threads` 表示 Concurrent 洪流线程数；`--exclusive-hold-ms`、`--exclusive-pause-ms`、`--exclusive-timeout-ms` 使用 kebab-case。

同参数对比：

- `scope`：ConcurrentExclusiveLockScope。
- `rwls`：.NET ReaderWriterLockSlim。
- `monitor`：Monitor / lock。

推荐使用 `dictionary` workload，让 Concurrent / Exclusive 区域都包含接近业务状态表的访问。

workload 参数与稳态并发基准一致：

- `--workload`：`cpu`、`memory`、`dictionary`、`ledger`、`payload`。
- `--work`：同时设置 ConcurrentWork 和 ExclusiveWork。
- `--read-work`：设置 ConcurrentWork。
- `--write-work`：设置 ExclusiveWork。
- `--memory-mb`：memory workload 的共享内存规模。
- `--dictionary-size`：dictionary / ledger / payload 的规模参数。

`--concurrent-spin` 只用于增加 Concurrent 区域停留时间，不作为业务 workload。

## 好结果

- Scope 的 `Exclusive failed` 为 `0`。
- Scope 的 Exclusive wait p95 / p99 / max 稳定。
- Scope 相对 RWLS 的 Exclusive 抢占延迟优势明显。
- 所有 target 输出同一 CSV，方便横向比较。

## 命令

标准对比：

```powershell
TestAndBenchmark.exe exclusive-preemption --profile standard --target all --workload dictionary --dictionary-size 65536 --read-work 64 --write-work 128
```

快速冒烟：

```powershell
TestAndBenchmark.exe exclusive-preemption --profile quick --target all --workload dictionary --dictionary-size 1280 --read-work 8 --write-work 16
```

指定线程数：

```powershell
TestAndBenchmark.exe exclusive-preemption --profile standard --target all --workload dictionary --threads 64 --dictionary-size 65536 --read-work 64 --write-work 128
```
