# 性能评测说明

## 定位

项目内置的是可复现对比程序，不是普遍适用的锁排名。

它评测同步策略与可配置业务 Work 之间的综合作用。结果会受到编译器、标准库、操作系统、CPU 拓扑、线程数、锁数量、内存放置和 Work 大小影响。

## 标准对比策略

- `std::mutex`；
- `std::shared_mutex`；
- `CEL`：读走 Concurrent，写走 Exclusive；
- `CEL(ExclusiveOnly)`：读写都走 Exclusive。

`CEL(ExclusiveOnly)` 用于区分 Exclusive 路径自身开销和 Concurrent 并行带来的收益。

## Work

### `memory`

默认 Work 参考 C# 内存评测模型。

每把锁拥有独立共享缓冲区。一次读操作：

1. 从共享状态哈希开始；
2. 推进线程本地 xorshift 随机游标；
3. 执行随机索引读取；
4. 执行 64 位混合。

一次写操作：

1. 推进串行 writer 随机游标；
2. 执行随机索引读取和原位写入；
3. 推进共享状态哈希。

默认：

```text
每把锁共享 64 MiB
每个完整读 Work 执行 32 steps
每个完整写 Work 执行 32 steps
```

### `cpu`

只使用少量标量整数混合，几乎没有内存压力。它主要放大极短临界区中的锁开销，不代表常规业务负载。

## 场景

标准运行包含：

```text
100/0
99.5/0.5
90/10
50/50
30/70
0/100
```

表示近似读写比例。

## 正确性控制

每种策略和场景都满足：

- 每个工作线程使用相同的确定性读写选择种子；
- 所有策略完成相同操作数；
- 每种策略使用全新的锁和 Work；
- 读次数必须一致；
- 写次数必须一致；
- 最终状态哈希必须一致。

只要不一致，评测程序会报错退出，而不是输出不可比较的吞吐量。

## 指标

| 指标 | 含义 |
|---|---|
| `elapsed` | 当前策略/场景的墙钟时间。 |
| `cpu%` | 按硬件并发数归一化的进程 CPU 时间；短跑可能波动。 |
| `works/s` | 每秒完成的“获取权限 + Work”数量。 |
| `works/s/lock` | 吞吐量除以锁实例数。 |
| `work/cpu%` | 吞吐量除以归一化 CPU 百分比。 |
| `reads` / `writes` | 完成的读写次数。 |
| `avg write ns` | 写路径从获取前到 Work 和释放后的平均纳秒数。 |
| `state` | 最终确定性 Work 状态哈希。 |

## 命令

默认：

```shell
./build/TestAndBenchmark/TestAndBenchmark
```

单把热点锁：

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --lock-instances 1 \
  --threads 64 \
  --workload memory \
  --operations 100000 \
  --memory-mb 64 \
  --read-work 32 \
  --write-work 32
```

大量独立锁：

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --lock-instances 64 \
  --threads 1 \
  --workload memory \
  --operations 100000 \
  --memory-mb 8 \
  --read-work 32 \
  --write-work 32
```

长跑参考命令：

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --lock-instances 8 \
  --threads 8 \
  --workload memory \
  --operations 500000 \
  --memory-mb 64 \
  --read-work 32 \
  --write-work 32
```

CPU 基线：

```shell
./build/TestAndBenchmark/TestAndBenchmark \
  --workload cpu \
  --operations 1000000 \
  --read-work 64 \
  --write-work 64
```

## 如何理解结果

### 单把热点锁

单锁高竞争最容易体现 Concurrent 并行与完全串行之间的差异。

### 大量独立锁

锁数量增加后，普通互斥锁也会因为实例分散而自然并行。因此 CEL 的相对吞吐倍数可能缩小，但实体级权限语义仍然存在价值。

### 写延迟

吞吐量不能完整描述稀有 Exclusive 多快开始执行。读主导业务还应在真实应用中观察平均和尾部写延迟。

### Work 大小

Work 极小时，锁自身开销占主导；Work 增大后，缓存、NUMA、调度和业务计算会占据更大比例。

## 推荐记录内容

- 精确提交/版本；
- 编译器和版本；
- 构建类型与优化参数；
- 操作系统；
- CPU 型号和拓扑；
- 锁实例数；
- 每把锁线程数；
- Work 类型和大小；
- 每线程操作数；
- 读写 steps；
- 完整原始输出。

不应只发布最有利场景，也不应隐藏其他策略表现更强的配置。
