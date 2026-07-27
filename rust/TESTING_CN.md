# Rust 版语义测试与压力测试说明

## 1. 测试目标

本项目的测试用于验证 `ConcurrentExclusiveLock`、`ConcurrentExclusiveLockScope` 和 `ConcurrentExclusiveLockPipeline` 在竞争、升级、降级、Try 失败、panic 展开和长时间运行后的协议正确性。

测试回答的是：

> 在真实多线程竞争中，权限是否仍然严格满足 Concurrent / Exclusive 访问协议，并且每轮结束后锁是否仍可继续复用？

吞吐量不是语义测试的通过条件。`observed_state()` 与 `observed_contention()` 只是观察快照，不参与同步正确性判断。

## 2. 快速执行

在 `rust` 目录运行：

```powershell
.\run-tests.ps1
```

或直接使用 Cargo：

```powershell
cargo test --release --workspace
cargo run --release -p cel-test-and-benchmark -- --full-semantics
cargo run --release -p cel-test-and-benchmark -- --pipeline-semantics
```

## 3. 主要模式

### `cargo test --release --workspace`

运行核心 crate 的集成测试，覆盖：

- Concurrent 可以重叠而 Exclusive 不得重叠；
- ContextID 条件升级只有一个成功者，失败者自动释放原 Concurrent；
- Pipeline 在业务 ID 条件失败后跳过当前段并从 None 继续。

### `--full-semantics`

先执行确定性协议检查，再执行模型约束下的随机合法路径。覆盖：

- Concurrent / Exclusive 获取与释放；
- 抢占式 Exclusive；
- Concurrent → Exclusive 原地升级；
- Exclusive → Concurrent 原地降级；
- ContextID / EpochID 条件升级；
- Scope 正常退出和 panic 展开释放；
- 超时接口；
- 状态与竞争度快照；
- Pipeline 状态转换；
- 多把独立锁并行运行。

示例：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --full-semantics `
  --lock-instances 8 `
  --semantic-workers 4 `
  --semantic-operations 1000 `
  --semantic-seed 0x6A09E667F3BCC909
```

### `--pipeline-semantics`

执行固定组合的 Pipeline 测试，验证：

- 独立 Concurrent / Exclusive 段；
- `ConvergeConcurrent`；
- `ConvergeExclusive`；
- `TryApplyIDConvergeExclusive`；
- Try 条件失败后的跳过与 None 状态；
- Pipeline callback panic 后 Scope 自动释放。

```powershell
cargo run --release -p cel-test-and-benchmark -- --pipeline-semantics
```

### `--pipeline-stress <duration>`

按随机种子持续生成同步 Segment 组合。每个批次会覆盖无锁段、普通获取、Try、升级、降级和业务 ID 条件收敛。

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 8 `
  --semantic-seed 0x123456789ABCDEF0
```

时长支持：

```text
500ms
30s
10m
24h
1d
01:30:00
```

### `--contention-stress <duration>`

让多线程持续竞争同一把锁的 Exclusive 权限，检查：

- Exclusive 区域绝不重叠；
- 所有等待线程在测试窗口内取得实际进展；
- 测试结束后锁回到 Idle；
- 输出每线程最少、最多获取次数，用于观察调度倾斜，而不是要求严格 FIFO。

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --contention-stress 30s `
  --lock-instances 1 `
  --semantic-workers 32
```

### `--endurance <duration>`

反复运行确定性协议检查与短批次 Pipeline 随机压力测试，复用锁对象并观察长时间稳定性。

```powershell
cargo run --release -p cel-test-and-benchmark -- --endurance 24h
```

## 4. 核心验证规则

测试根据模式持续验证以下规则：

1. Exclusive 业务区不得与任何 Concurrent 或另一个 Exclusive 业务区重叠；
2. 同一连续 Concurrent 轮次中的 Concurrent ID 必须处于合法范围；
3. 抢占式 Exclusive 出现后，新 Concurrent 不得继续插入当前窗口；
4. 多个升级请求产生的 Exclusive 区域必须串行；
5. 条件升级失败后，原 Concurrent 已自动释放；
6. 降级后调用方持有 Concurrent，而不是仍持有 Exclusive；
7. Scope 在正常退出和 panic 展开时按最终状态释放；
8. Pipeline Try 失败时跳过当前段，并从 None 继续后续段；
9. 每轮结束后锁必须回到可复用状态；
10. 多把锁之间不得共享协议状态。

## 5. 随机种子复现

随机测试使用 `--semantic-seed`。出现失败时保存：

- 执行模式；
- 完整命令；
- seed；
- lock 数量；
- worker 数量；
- operation 数量；
- panic 链和控制台输出。

以完全相同的参数重新运行即可复现同一调用形状。

## 6. 建议执行顺序

日常修改：

```text
cargo test --release --workspace
→ --full-semantics
→ --pipeline-semantics
→ --pipeline-stress 10m
```

发布前：

```text
cargo fmt --all -- --check
→ cargo clippy --workspace --all-targets -- -D warnings
→ cargo test --release --workspace
→ --full-semantics（较大参数）
→ --pipeline-stress 数小时
→ --contention-stress 30s 或更长
```

重大同步算法修改后，建议把 Pipeline 压力测试延长到 24 小时以上，并分别在 Windows、Linux 和 macOS 上运行。
