# ConcurrentExclusivePack TestAndBenchmark 项目概要

日期：2026-07-21

## 一、项目定位

`TestAndBenchmark` 是 `ConcurrentExclusivePack` 的独立测试与性能验证项目，用于按接近 BCL / .NET Runtime 评审的方式验证 `ConcurrentExclusiveLock` 及其封装能力。

本项目只通过公开 API 使用 `ConcurrentExclusivePack`，不读取源码、不反射 DLL、不反编译实现内容。允许读取构建产物中的 XML 文档作为公开 API 调用契约。测试结论应基于可复现的黑盒行为、稳定性能数据和长期压力结果。

## 二、测试目标

本项目不追求单次跑分最大化，而是验证以下问题：

1. 正确性是否稳定、可复现。
2. 性能测试是否测到了锁本身，而不是线程启动、调度器抖动或 workload 偏差。
3. 对照组是否合理，能否说明 CEL 距离理论上限和 BCL 现有锁的差距。
4. Exclusive 抢占、升级、降级、Scope、Pipeline 等高级语义是否有清晰代价。
5. 测试是否可以长期运行，并能放入持续性能或持续压力体系。

## 三、测试分层

性能测试统一使用 `Common/Workloads/BenchmarkWork` 工作模型。`steady`、`advanced-perf`、`exclusive-preemption` 共享 `cpu`、`memory`、`dictionary`、`ledger`、`payload` 五类 workload，并统一使用 `--work`、`--read-work`、`--write-work`、`--memory-mb`、`--dictionary-size` 参数。

常用验证入口使用 `suite` 集成 correctness、steady、advanced-perf、exclusive-preemption 和 stress；`suite --group empty|short|long|workloads|instances100|all` 用于按空锁、极短临界区、长 work、workload matrix 和 100 锁实例分组执行。BenchmarkDotNet micro 默认单独运行，也可通过 `suite --include-micro` 做短冒烟。

### 1. 标准微基准

使用 BenchmarkDotNet 建立可重复的无竞争或低竞争微基准，主要测 `ns/op`、吞吐、分配和生成代码质量。

建议覆盖：

- NoLock workload 上限
- AtomicOnly 原子操作上限
- Monitor / lock
- ReaderWriterLockSlim
- ConcurrentExclusiveLock
- ConcurrentExclusiveLock ExclusiveOnly
- Concurrent enter / release
- Exclusive enter / release
- Try 失败路径
- ExclusiveToConcurrent 降级
- ConcurrentToExclusive 升级
- Scope 单段成本
- Pipeline 单段和多段转换成本

微基准只用于说明单路径成本，不用于证明复杂并发公平性。

### 2. 稳态并发基准

使用固定数量独立锁实例、每锁固定数量专用工作线程和统一开始门控，按固定 operations/thread 执行同一 deterministic 操作序列，并统计完成时间与吞吐。该层对齐旧 LockBenchmark 的标准横向性能测试口径。

建议线程规模：

- 1
- 2
- 4
- 物理核心数的一半
- 物理核心数
- 逻辑核心数
- 逻辑核心数 × 2

建议运行方式：

- 每个 target 使用相同 lock-instances / threads / operations/thread
- 每个 target 使用同一 deterministic 操作序列
- 默认遍历 100/0、99.5/0.5、90/10、50/50、30/70、0/100 六组场景
- 输出 elapsed / cpu% / works/s / works/s/lock / work/cpu% / Concurrent 数 / Exclusive 数 / avg Exclusive latency / state

锁实例模型：

- 单锁竞争：`--lock-instances 1`
- 多份独立单锁案例同时运行：显式指定 `--lock-instances`
- `--threads` 表示每个锁实例的专用工作线程数量

### 3. Exclusive 抢占延迟测试

CEL 的重要价值之一是抢占式 Exclusive，因此必须单独证明 Exclusive 及时性，而不能只看总吞吐。

建议指标：

- Exclusive wait p50 / p95 / p99 / max
- Exclusive 到达后又成功进入的新 Concurrent 数量
- Concurrent 洪流下 Exclusive 是否必然进入
- 单次 Exclusive 对 Concurrent 吞吐造成的暂停时间
- 连续 Exclusive 下 Concurrent 是否长期无法进入

该层用于证明：Exclusive 请求进入竞争窗口后，可以限制旧 Concurrent 状态继续扩散。

### 4. 确定性正确性测试

按 BCL 风格拆成小而明确的行为测试，每个测试只验证一个契约点。

建议覆盖：

- Concurrent 之间可以并行
- Exclusive 排斥全部访问
- 抢占式 Exclusive 阻止新的 Concurrent
- 非抢占式 Exclusive 只在 Idle 时成功
- 多个 Concurrent 竞争升级时结果正确
- 升级失败后 Concurrent 权限已清理
- Exclusive 原地降级为 Concurrent
- ContextID 切换语义
- EpochID 推进语义
- Scope 异常路径释放
- Pipeline 段异常路径释放
- Pipeline 权限转换状态机
- ObservedState 只作为观察快照
- ObservedContention 只作为竞争压力观察指标
- default struct 使用错误
- 非法释放与协议错误
- 极限计数边界

### 5. 长期压力与随机语义测试

保留 LockBenchmark 已经验证过的随机状态机和 Pipeline 暴力压测思路，但在新项目中重新组织为长期黑盒压力测试。

建议覆盖：

- 24h / 72h / 240h 随机压力
- 固定 seed 复现失败批次
- 高 CPU 负载
- 高 GC 压力
- 超额订阅线程
- ThreadPool / Task / 专用 Thread 混合
- Scope 与 Pipeline 随机组合
- ContextID / EpochID 随机竞争

任一断言失败、线程异常、无进展超时或最终状态泄漏，都应立即输出 seed、形状参数和最小复现命令。

## 四、现有 LockBenchmark 的定位

现有 `LockBenchmark` 结果不作废，但在新测试体系中重新分类：

- 单锁、正常线程数、实际 workload：作为早期性能证据。
- Pipeline 暴力压测：作为长期随机语义证据。
- 1000 锁、大量专用 OS 线程、每线程少量操作：作为线程唤醒与过载压力测试，不再作为稳态吞吐证据。
- 高级语义性能测试：作为路径代价探索工具，后续迁移到微基准和稳态基准中。

## 五、输出要求

每个测试入口应输出：

- .NET 版本
- OS 信息
- CPU 逻辑核心数
- GC 模式
- 测试参数
- 对照组
- 总吞吐
- 每线程吞吐
- 延迟分位数
- CPU 使用率
- 分配量
- 最终状态
- seed 或复现参数

性能结论应基于多次运行的稳定区间，避免使用单次极值。

## 六、建议目录结构

```text
TestAndBenchmark
  Benchmarks
    Micro
    SteadyState
    ExclusivePreemption
  Correctness
    Core
    Scope
    Pipeline
  Stress
    RandomStateMachine
    PipelineStress
    Endurance
  Common
    Workloads
    Metrics
    Threading
    Reporting
  Readme
    项目概要.md
```

## 七、阶段目标

第一阶段先完成测试框架骨架和确定性正确性测试。

第二阶段完成 BenchmarkDotNet 微基准。

第三阶段完成固定工作线程的稳态实体基准。

第四阶段完成 Exclusive 抢占延迟专项测试。

第五阶段接入长期随机压力和自动报告。

最终目标是形成一套可复现、可质疑、可长期维护的证据体系，而不只是证明某一次运行中 CEL 更快。
