# Rust 版语义测试与压力测试

## 1. 测试范围

测试程序覆盖：

- Concurrent ID 唯一性与可复用性；
- 多个 Concurrent 区域可重叠；
- Exclusive 与 Concurrent/Exclusive 隔离；
- 抢占式 Exclusive 阻止新的 Concurrent 插入；
- Concurrent → Exclusive 原地升级；
- Exclusive → Concurrent 降级；
- 多个升级请求串行执行；
- ContextID / EpochID 条件升级；
- Try 和超时接口；
- Scope 正常释放与 panic unwind 释放；
- Pipeline 每种 Segment 的固定语义；
- Pipeline panic unwind；
- 随机 Pipeline 组合压力；
- 高竞争 Exclusive 的持续进展；
- 最终锁状态与访问验证器恢复 Idle。

## 2. 快速验证

Linux/macOS：

```bash
./build.sh
./run-tests.sh
```

Windows PowerShell：

```powershell
.\build.ps1
.\run-tests.ps1
```

所有 Cargo 命令默认带 `--offline`。`parking_lot-vendor.zip` 包含 parking_lot 和全部所需依赖，脚本会按需解压到 `vendor/`。

## 3. Cargo 测试

```bash
cargo test --release --workspace --offline
```

当前集成测试包含：

- Concurrent 与 Exclusive 的重叠/隔离；
- 条件升级单赢家及失败释放；
- Pipeline TryApplyID 失败后从 None 继续。

## 4. 完整语义回归

```bash
./target/release/cel-test-and-benchmark \
  --full-semantics \
  --lock-instances 2 \
  --semantic-workers 4 \
  --semantic-operations 512 \
  --semantic-seed 0x6A09E667F3BCC909
```

该模式执行固定契约和随机合法路径。Scope/Pipeline unwind 测试会故意触发 panic 并通过 `catch_unwind` 捕获；默认 panic hook 可能打印故意制造的 panic 文本，但最终出现 `PASS` 才代表通过。

## 5. Pipeline 固定语义

```bash
./target/release/cel-test-and-benchmark --pipeline-semantics
```

覆盖：

- None；
- Concurrent / TryConcurrent；
- Exclusive / TestExclusive / TryExclusive；
- ConvergeConcurrent；
- ConvergeExclusive；
- TryApplyIDConvergeExclusive；
- Try 失败时跳过当前 Segment；
- 后续 Segment 从 None 状态继续；
- panic 时 Scope 自动释放。

## 6. Pipeline 随机压力

```bash
./target/release/cel-test-and-benchmark \
  --pipeline-stress 30m \
  --lock-instances 1 \
  --semantic-workers 4 \
  --semantic-seed 0x6A09E667F3BCC909
```

每轮生成 3～10 个随机 Segment。每个回调通过 `AccessValidator` 检查：

- Exclusive 回调之间不能重叠；
- Exclusive 不能与 Concurrent 回调重叠；
- Concurrent 回调允许彼此重叠；
- 测试结束后验证器与锁必须 Idle。

长测每隔最多 60 秒打印累计轮次和回调数。正式结果保存在：

```text
TestBenchmarkResults/final/pipeline-stress-30m.log
```

本次正式 30 分钟实测通过：

```text
2,732,232,429 randomized Pipeline rounds
14,775,380,351 validated callbacks
```

## 7. Exclusive 竞争进展

```bash
./target/release/cel-test-and-benchmark \
  --contention-stress 60s \
  --semantic-workers 32
```

测试检查：

- Exclusive 业务区绝不重叠；
- 总获取数大于 0；
- 每个 worker 都至少成功获取一次；
- 测试结束后锁恢复 Idle。

本次 60 秒实测：32 个 worker 共完成 `401,719,852` 次 Exclusive 获取，单 worker 最少 `11,457,115` 次，全部取得进展。

## 8. Endurance

```bash
./target/release/cel-test-and-benchmark \
  --endurance 30m \
  --lock-instances 2 \
  --semantic-workers 4
```

Endurance 反复执行固定语义回归，并穿插短 Pipeline 压力，用于检查异常释放、计数残留和长期状态漂移。

## 9. 可复现随机种子

默认种子：

```text
0x6A09E667F3BCC909
```

复现某次测试时保持以下参数不变：

- `--lock-instances`；
- `--semantic-workers`；
- `--semantic-operations`；
- `--semantic-seed`；
- Rust 版本、操作系统和 CPU 调度环境。

## 10. 建议发布前顺序

```text
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --offline --no-deps -- -D warnings
cargo test --release --workspace --offline
--full-semantics
--pipeline-semantics
--pipeline-stress 30m
--contention-stress 60s
性能测试与状态哈希校验
```

原始日志统一保存在 `TestBenchmarkResults/final/`。
