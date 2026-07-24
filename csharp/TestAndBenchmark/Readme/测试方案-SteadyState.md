# Steady State 稳态并发基准

日期：2026-07-22

## 测试方案

固定数量的独立锁实例同时运行；每个锁实例分配固定数量的专用工作线程；每个线程执行固定次数的 Concurrent / Exclusive 权限获取、业务执行和释放。

- `--lock-instances`：同时运行的独立单锁案例数量。
- `--threads`：每个锁实例的工作线程数量。
- `--operations`：每个线程执行的操作次数。
- `--concurrent-percent`：Concurrent 操作比例，支持小数；不传时默认跑 100/0、99.5/0.5、90/10、50/50、30/70、0/100 六组场景。
- `--work`：同时设置 ConcurrentWork 和 ExclusiveWork。
- `--read-work`：兼容旧基准命令名，用于设置 ConcurrentWork。
- `--write-work`：兼容旧基准命令名，用于设置 ExclusiveWork。

对比目标：

- `scope`：ConcurrentExclusiveLockScope。
- `rwls`：.NET ReaderWriterLockSlim。
- `monitor`：Monitor / lock。

同一组参数下，各 target 使用相同锁实例数、相同线程数、相同操作次数、相同 deterministic 请求序列。单锁竞争对比使用 `--lock-instances 1`；多锁案例表示多份独立单锁案例同时运行，不能和单锁竞争结论混用。

输出格式对齐旧 LockBenchmark，并额外输出 `avg excl ns`。

## 通过标准

- 各 target 的 total / concurrent / exclusive 数量一致。
- 输出 `state`，用于确认各 target 的最终业务状态一致。
- `works/s` 越高表示整轮总吞吐越好；`works/s/lock` 用于多锁案例下观察单锁平均吞吐。
- `work/cpu%` 用于观察单位 CPU 使用率对应的完成量，只作为观察项。
- `avg excl ns` 是 Exclusive 操作从请求权限前开始，到业务执行并释放完成后的平均耗时。

## 命令

快速冒烟：

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 4 --workload cpu --operations 100 --work 4 --target all
```

单锁自身开销：

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload cpu --operations 100000 --work 0 --target all
```

单锁线程扩展扫描：

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 1,2,4,8,16,32 --workload cpu --operations 100000 --work 256 --target all --concurrent-percent 100
```

单锁 CPU 工作：

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload cpu --operations 100000 --read-work 256 --write-work 256 --target all
```

单锁大内存随机访存：

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 128 --target all
```

单锁字典缓存：

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload dictionary --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all
```

单锁账户账本：

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload ledger --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all
```

单锁二进制报文：

```powershell
TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload payload --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all
```

4 份单锁案例同时运行，每把锁 32 个线程：

```powershell
TestAndBenchmark.exe steady --lock-instances 4 --threads 32 --workload ledger --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all
```

8 份短临界区单锁案例同时运行，每把锁 16 个线程：

```powershell
TestAndBenchmark.exe steady --lock-instances 8 --threads 16 --workload cpu --operations 100000 --work 4 --target all
```

1000 锁案例同时运行，字典缓存：

```powershell
TestAndBenchmark.exe steady --lock-instances 1000 --threads 8 --workload dictionary --operations 100 --dictionary-size 1280 --read-work 64 --write-work 128 --target all
```

1000 锁案例同时运行，CPU：

```powershell
TestAndBenchmark.exe steady --lock-instances 1000 --threads 8 --workload cpu --operations 100 --read-work 64 --write-work 128 --target all
```
