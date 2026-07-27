# Rust 版性能测试说明

## 1. 测试目的

性能测试比较以下同步策略在同一共享内存 Work 下的表现：

- `std::sync::Mutex`；
- `std::sync::RwLock`；
- `ConcurrentExclusiveLock`（CEL）；
- `CEL(ExclusiveOnly)`：读写全部走 Exclusive，用于观察 CEL Monitor 慢路径本身。

测试不是为了证明某个锁在所有负载下都最快。重点是观察：

- 读主导与读写均衡场景的总体吞吐；
- 抢占式 Exclusive 对写等待的影响；
- 多把实体级锁时的扩展性；
- Work 变重后 Concurrent 并行是否转化为实际收益；
- CEL 与 CEL(ExclusiveOnly) 的差异。

## 2. Work 模型

每把锁对应一个独立 `MemoryWork`：

- 分配指定大小的 `i64` 共享数组；
- Concurrent Work 使用线程本地随机状态，从共享数组随机读取并进行 64 位混合；
- Exclusive Work 使用锁内共享随机状态，随机读取、修改数组并推进最终状态；
- 各策略使用相同的随机算法、读写次数和 Work 参数；
- 每个策略和场景都创建全新的锁与 Work 实例；
- 测试结束后比较最终状态哈希，确保各策略执行了等价写入数量。

这套 Work 参考 C# 项目的随机共享内存负载。它比空临界区或单次整数加法更接近本锁的目标场景。

## 3. 读写比例

标准测试依次运行：

```text
100/0
99.5/0.5
90/10
50/50
30/70
0/100
```

其中 `99.5/0.5` 与 `90/10` 最适合观察读主导负载下吞吐和写等待的综合表现；`50/50` 用于观察读写均衡；高写比例用于确认 CEL 是否自然收敛到互斥锁级别，而不是用于证明 Concurrent 优势。

## 4. 构建要求

必须使用 Release：

```powershell
cargo build --release --workspace
```

不要使用 Debug 结果做性能结论。测试期间尽量关闭高占用后台任务，并记录：

- CPU 型号和逻辑核心数；
- 操作系统版本；
- Rust 版本和 target；
- 电源计划；
- NUMA 拓扑；
- 完整命令；
- 原始输出。

## 5. 标准命令

### 单把热点锁

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 1 `
  --threads 32 `
  --operations 100000 `
  --workload memory `
  --memory-mb 64 `
  --read-work 256 `
  --write-work 256
```

### 多把实体锁

八把锁、每把八个线程，总计 64 个工作线程：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 8 `
  --threads 8 `
  --operations 100000 `
  --workload memory `
  --memory-mb 64 `
  --read-work 256 `
  --write-work 256
```

注意：`--memory-mb` 是每把锁的共享内存。上例每个策略大约使用 `8 × 64 MiB` 的 Work 数据，另有线程栈和运行时开销。

## 6. Work 档位

短临界区主要测量同步原语自身开销，不能充分体现 Concurrent 并行价值。建议至少跑三档：

```text
Work 64    较短/基线
Work 256   中等业务量
Work 640   较重业务量
```

较重 Work：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 8 `
  --threads 8 `
  --operations 100000 `
  --memory-mb 64 `
  --read-work 640 `
  --write-work 640
```

不要只发布最有利的一组。至少保留 Work 64、256、640 的完整六场景输出。

## 7. 输出指标

程序输出：

- `elapsed`：该策略完成当前场景的总时间；
- `works/s`：总吞吐；
- `works/s/lock`：每把锁平均吞吐；
- `avg write ns`：从写操作开始申请权限到 Work 完成并释放的平均时间；
- `reads` / `writes`：实际操作数量；
- `state`：最终业务状态哈希。

`avg write ns` 包含等待和 Exclusive Work 本身，不是纯锁获取延迟。这正适合观察写请求在真实临界区下的完成时间。

## 8. 结果解释边界

合理结论应限定于实际机器、参数和负载。例如：

> 在本机、指定线程拓扑和随机内存 Work 下，CEL 在读主导到读写均衡的负载范围内表现出更好的吞吐/写完成时间组合。

不要写成：

> CEL 在所有场景下都比 Mutex 或 RwLock 快。

原因包括：

- `std::sync::RwLock` 的实现和公平策略随平台变化；
- CPU、NUMA、调度器和缓存结构会改变结果；
- 临界区过短时原子计数成本可能超过并行收益；
- 高写比例下任何 Concurrent 型锁都会逐渐接近普通互斥执行；
- Rust 内部 RawMonitor 与 C# Monitor、Java ReentrantLock 的具体调度特征不同。

## 9. 保存结果

```powershell
New-Item .\TestResults -ItemType Directory -Force | Out-Null

cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 8 `
  --threads 8 `
  --operations 100000 `
  --memory-mb 64 `
  --read-work 640 `
  --write-work 640 `
  2>&1 | Tee-Object .\TestResults\rust-windows-work640.txt
```

每个参数组合建议独立进程运行三次，保存全部原始输出，再报告中位数或同时列出三次结果。
