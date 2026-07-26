# Java 版语义测试与压力测试说明

测试项目用于验证：

- `ConcurrentExclusiveLock`；
- `ConcurrentExclusiveLockScope`；
- `ConcurrentExclusiveLockPipeline`。

测试通过公开 API 和专用 Java 线程制造竞争。权限重叠、断言失败、线程异常或超时都会返回非零退出码。

## 模式

### `--advanced-correctness`

验证升级串行、ContextID 唯一赢家、降级以及多把独立锁的合法权限路径。

### `--full-semantics`

执行确定性核心、Scope、Pipeline 契约，再执行模型驱动的随机合法路径，覆盖：

- Concurrent / Exclusive 获取和释放；
- 抢占式 Exclusive；
- Exclusive → Concurrent；
- Concurrent → Exclusive；
- ContextID / EpochID 条件升级；
- Scope 关闭和异常释放；
- Pipeline 状态转换和失败继续。

### `--pipeline-semantics`

执行固定 Pipeline 状态转换和并发固定批次。

### `--pipeline-stress <duration>`

随机选择锁数量、每锁线程数、轮数、Segment 组合和批次种子。失败时输出批次形状和 seed。

### `--endurance <duration>`

持续复用一组锁对象运行合法路径，并报告操作量、CPU、堆、线程和 GC 信息。

### `--contention-stress <duration>`

大量专用线程竞争同一把锁的专项测试。

## 建议命令

```powershell
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar --help

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --full-semantics `
  --lock-instances 64 `
  --semantic-workers 4 `
  --semantic-operations 256

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-semantics `
  --lock-instances 1 `
  --semantic-workers 64 `
  --semantic-operations 1000 `
  --semantic-seed 12345

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 64 `
  --semantic-operations 1000 `
  --semantic-seed 12345

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-stress 3h `
  --lock-instances 8 `
  --semantic-workers 128 `
  --semantic-operations 2000
```

确定性语义测试通过后，日常收口可主要运行数小时 Pipeline 随机压力测试；发布或重大修改后再延长到 24 小时或更久。
