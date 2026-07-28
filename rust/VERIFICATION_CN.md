# 验证状态

## 工具链与平台

```text
rustc 1.75.0
cargo 1.75.0
Linux 6.12.13 x86_64，KVM
AMD EPYC 9V74
Rust available_parallelism()：4
```

## 源码与构建检查

以下项目均已通过：

```text
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --offline --no-deps -- -D warnings
cargo check --workspace --offline
cargo build --release --workspace --offline
cargo test --release --workspace --offline
```

核心库启用了 `#![forbid(unsafe_code)]`。仅 benchmark 使用一个范围明确的 `UnsafeCell` 适配器，让 CEL 在 Exclusive 权限下保护同一个可变 `MemoryWork`。

## 语义与压力验证

已通过：

- Cargo 集成测试；
- 完整固定与随机语义回归；
- Pipeline 固定语义回归；
- 30 秒 Pipeline 随机烟雾压力；
- 正式 30 分钟 Pipeline 随机压力：`2,732,232,429` 轮、`14,775,380,351` 次校验回调；
- 60 秒 Exclusive 高竞争进展测试：32 个 worker、`401,719,852` 次获取，每个 worker 均取得进展；
- 60 秒 Endurance：完成 58 个确定性批次；
- 每个模式结束后的 Idle 状态检查。

权威日志位于 `TestResults/final/`。

## 性能测试验证

共完成 10 组完整配置，包括：

- 单锁 1、4、16、64 线程；
- 16 线程主配置完整重复 3 次；
- Work=1、64、256；
- 8 把锁 × 每锁 4 线程；
- 64 把锁 × 每锁 2 线程；
- 六种读写比例；
- 标准 Mutex/RwLock、parking_lot Mutex/RwLock、CEL 和 CEL(ExclusiveOnly)。

每个策略和场景的最终状态哈希均一致。原始日志、CSV、JSON 和三轮中位数表位于 `TestResults/final/benchmarks/`。

## 离线依赖验证

核心 crate 仍无第三方依赖。评测程序使用的 `parking_lot 0.12.5` 及必要依赖已经放入 `vendor/`。Cargo.lock 使用本地路径源生成，整个 Workspace 已通过 `--offline` 构建，并在空 `CARGO_HOME` 下完成了一次全新 Release 重建。

## Windows 可执行文件状态

源码、`windows-link`、离线依赖和 `build-windows.ps1` 均已准备好。但当前 Linux 验证容器缺少：

```text
Rust 目标：x86_64-pc-windows-gnu
MinGW 链接器：x86_64-w64-mingw32-gcc
```

交叉检查在编译项目代码之前就因 Windows Rust 标准库中的 `core`、`alloc`、`compiler_builtins` 缺失而失败。完整输出保存在 `TestResults/final/windows-cross-check.log`。

因此本包包含已验证的 Linux x64 可执行文件。Windows `.exe` 需要在 Windows 上运行 `build-windows.ps1`，或先给该容器补齐 Windows Rust target 与 MinGW 链接器。
