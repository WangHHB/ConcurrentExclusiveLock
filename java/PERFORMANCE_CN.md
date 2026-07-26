# Java 版性能跑分说明

标准性能测试会在相同 Work、线程数量、锁实例数量和读写比例下比较：

- `synchronized`；
- 非公平 `ReentrantLock`；
- 非公平 `ReentrantReadWriteLock`；
- `StampedLock`；
- `ConcurrentExclusiveLock`（`CEL`）；
- `CEL(ExclusiveOnly)`，即读写全部通过 Exclusive 路径。

每种策略、每个读写比例都会创建独立的锁实例和独立 Work。所有专用线程先到达 ready 屏障，再由统一 start gate 开始计时。

自动测试以下读写比例：

```text
100 / 0
99.5 / 0.5
90 / 10
50 / 50
30 / 70
0 / 100
```

## 参数

| 参数 | 含义 |
|---|---|
| `--lock-instances` | 独立“锁 + Work”实例数量。 |
| `--threads` | 每把锁的专用线程数量。 |
| `--operations` | 每个线程执行的操作数。 |
| `--workload` | `cpu`、`memory`、`dictionary`、`ledger` 或 `payload`。 |
| `--work` | 同时设置读写业务步骤数。 |
| `--read-work` | 读业务步骤数。 |
| `--write-work` | 写业务步骤数。 |
| `--memory-mb` | 每把锁的 memory Work 大小，单位 MiB。 |
| `--dictionary-size` | dictionary、ledger 和 payload 的数据规模。 |

```text
总线程数   = lock-instances × threads
总操作数   = lock-instances × threads × operations
```

## 输出

- `elapsed`：当前策略和场景完成全部操作的时间；
- `cpu%`：进程 CPU 时间按计时区间和 JVM 可见处理器数量归一化后的辅助指标；
- `works/s`：每秒完成操作数；
- `works/s/lock`：平均每把锁吞吐；
- `work/cpu%`：单位采样 CPU 对应吞吐，仅作辅助；
- `reads` / `writes`：实际读写次数；
- `avg write ns`：写操作平均耗时，包含获取、Work 和释放；
- `state`：最终 Work 状态，用于检查各策略结果是否一致。

`cpu%` 不是硬件级精确测量。极短场景可能出现 0 或短暂超过 100%，正式结论应以足够长的 `elapsed`、`works/s`、最终状态一致性和多次重复的稳定区间为主。

## 对应 C# 示例的命令

```powershell
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 1 `
  --threads 64 `
  --workload memory `
  --operations 10000 `
  --memory-mb 64 `
  --read-work 64 `
  --write-work 64
```

沙箱中已实际执行一次完整对比，输出位于：

```text
TestResults/benchmark-memory-64threads.txt
```

该结果用于确认六种策略、六组读写比例都能完成，并产生相同最终 Work 状态。具体速度只代表当时的 Linux/JDK/CPU 环境，不能直接当作 Windows 正式宣传数据。
