# ConcurrentExclusiveLock Rust 版

`ConcurrentExclusiveLock` 是一种面向 **Concurrent / Exclusive 访问权限** 的高性能、非递归同步锁。

本 Rust 实现严格参考原始 C# 版本的权限协议移植。C# 版本仍然是设计、状态转换和边界语义的参考实现；Rust 版仅在语言表达、生命周期管理、错误返回和阻塞原语方面做必要适配。

Rust 版保留的核心能力包括：

- 普通 Concurrent 获取与释放以轻量原子计数为主；
- 抢占式 Exclusive；
- Concurrent → Exclusive 原地升级；
- Exclusive → Concurrent 原地降级；
- ContextID / EpochID 业务条件收敛；
- Scope 自动释放；
- 同步 Pipeline 权限流程编排；
- 状态与竞争度观察快照；
- 不承诺严格 FIFO，但 Exclusive 和升级竞争进入串行阻塞慢路径。

> **重要：**`Concurrent` / `Exclusive` 表示“是否允许并发访问”，不是传统意义上的“读 / 写”意图。Concurrent 区域可以修改业务上互不冲突的状态；Exclusive 区域也可以包含大量读取逻辑。

---

## 与 C# 原版的关系

本实现遵循以下原则：

1. C# 是协议语义参考实现；
2. Rust 不重新设计权限模型；
3. Concurrent 仍采用直接对象协议；Rust 仅为必须跨业务区持有的标准 `MutexGuard` 引入 `ExclusiveGuard`；
4. Concurrent ID 是本轮并发进入编号，不是释放凭证；
5. 不增加 ticket、公平队列或严格 FIFO；
6. Scope 只负责 RAII 生命周期，不改变底层协议；
7. Pipeline 状态机严格对应 C# 的 Segment 语义；
8. Rust 的 `Result`、`Duration`、生命周期和 `Drop` 仅用于语言层适配。

目录结构：

```text
rust/
├─ crates/
│  ├─ concurrent-exclusive-lock/   # 核心库、Scope、Pipeline
│  └─ test-and-benchmark/           # 语义测试、压力测试、性能对比
├─ vendor/                          # parking_lot 及离线依赖，仅供评测程序使用
├─ TestBenchmarkResults/           # 原始测试日志与 CSV/JSON
├─ Artifacts/                       # 已构建可执行文件
├─ Cargo.toml                       # Cargo Workspace
├─ README.md
├─ README_CN.md
├─ TESTING.md
├─ TESTING_CN.md
├─ PERFORMANCE.md
├─ PERFORMANCE_CN.md
├─ VERIFICATION.md
├─ THIRD_PARTY_NOTICES.md             # 内置评测依赖版本与许可证
├─ build.ps1
├─ run-tests.ps1
└─ run-benchmark.ps1
```

---

## 设计概览

### Concurrent 快速路径

没有 Exclusive 压力、并发数量未达到调用方上限时，普通 Concurrent 获取主要执行：

```text
读取原子计数
→ 原子 +1
→ 验证仍处于允许进入的 Concurrent 区间
→ 返回 Concurrent ID
```

普通 Concurrent 释放主要是一次原子减法。

普通 Concurrent 不进入 Exclusive 的阻塞调度队列，这是该锁在读主导、实体级多锁场景中的主要性能基础。

### 抢占式 Exclusive

Exclusive 请求进入竞争窗口后，会在计数器高位登记 Exclusive 压力。此后新的普通 Concurrent 请求不能继续插入，已有 Concurrent 持有者自然退出；请求在内部 Monitor 慢路径中等待实际进入 Exclusive。

这解决的是传统读写锁中常见的业务问题：

> 写入已经必要，但新的读取持续涌入，使写入迟迟不能发生，而这些读取很快又会因为写入而失效。

### 内部 Monitor

C# 原作的 `Monitor` 直接映射为 Rust 标准库的
`std::sync::Mutex<()>`。Rust 通过释放 `MutexGuard` 解锁，因此 Exclusive
获取返回 `ExclusiveGuard`，由它在整个 Exclusive 业务区内持有真实的标准
`MutexGuard`。不再增加 `RawMonitor`、持有状态原子量、等待者计数、
`Condvar` 或自定义公平策略。

Rust 唯一必要的接口差异是：

- `acquire_exclusive()` 返回 `ExclusiveGuard`；
- 显式释放需要消费这个 Guard；
- 降级需要消费 Guard，并继续保留 Concurrent 权限；
- Guard 被丢弃时自动安全释放 Exclusive。

Counter 状态机、升级优先级、分支顺序和释放顺序仍与 C# 原作对齐。

### 升级优先关系

Concurrent → Exclusive 原地升级不是“释放 Concurrent 后重新抢 Exclusive”。升级请求会把当前 Concurrent 持有者直接转换为升级信号，并阻止普通 Exclusive 越过仍在进行的升级批次。

多个升级成功者的 Exclusive 业务区仍然串行执行。

### 降级连续性

普通 Exclusive → Concurrent 降级尽量保留连续访问上下文，避免释放 Exclusive 后重新获取 Concurrent 形成可见窗口。

在多个升级请求仍然排队的高竞争情况下，降级可能切断当前上下文并重新获取 Concurrent，使剩余升级者继续完成 Exclusive 阶段。这与 C# 参考实现一致。

---

## 环境要求

- Rust stable；
- Cargo；
- 最低 Rust 版本：1.75；
- 目标平台必须支持 64 位原子操作；
- Windows 使用 MSVC toolchain 时需要 Visual Studio C++ Build Tools；
- Linux 通常需要 GCC 或 Clang 作为链接器；
- macOS 需要 Xcode Command Line Tools。

Windows 安装 Rust 后重新打开 PowerShell：

```powershell
rustc --version
cargo --version
```

---

## 构建

在 `rust` 目录执行：

```powershell
cargo build --release --workspace --offline
```

运行 Cargo 测试：

```powershell
cargo test --release --workspace --offline
```

也可以执行：

```powershell
.\build.ps1
```

生成文件通常位于：

```text
target\release\cel-test-and-benchmark.exe   # Windows
target/release/cel-test-and-benchmark       # Linux/macOS
```

本发布包同时包含已在本测试环境构建并验证的 Linux x64 可执行文件：

```text
Artifacts/linux-x64/cel-test-and-benchmark
```

同时包含已通过 `cargo package` 验证的核心 crate 包：

```text
Artifacts/crate/concurrent-exclusive-lock-1.0.0.crate
```

Windows 用户执行 `build-windows.ps1` 后，脚本会把 `.exe` 复制到：

```text
Artifacts\windows-x64\cel-test-and-benchmark.exe
```

核心库 crate 本身没有第三方依赖。内置评测依赖的版本与许可证见 [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)。评测程序依赖 `parking_lot 0.12.5`，其源码及所需依赖已放入 `vendor/`，因此整个 Workspace 仍可在 Cargo registry 为空时使用 `--offline` 构建。

---

## 添加依赖

### 本地路径依赖

```toml
[dependencies]
concurrent-exclusive-lock = { path = "../ConcurrentExclusiveLock/rust/crates/concurrent-exclusive-lock" }
```

代码中的 crate 名称使用下划线：

```rust
use concurrent_exclusive_lock::ConcurrentExclusiveLock;
```

发布到 crates.io 后可改为版本依赖：

```toml
[dependencies]
concurrent-exclusive-lock = "1.0.0"
```

在实际发布完成前，应继续使用本地路径或 Git 依赖，不要把尚未发布的版本坐标当作已经可下载。

---

## 核心锁使用

### Concurrent

```rust
use concurrent_exclusive_lock::ConcurrentExclusiveLock;

let lock = ConcurrentExclusiveLock::new();

let concurrent_id = lock.acquire_concurrent()?;

// 允许与其他 Concurrent 业务同时执行。
// concurrent_id 是当前连续并发轮次中的编号，不是释放 Token。

lock.release_concurrent();
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

限制最大 Concurrent ID：

```rust
let concurrent_id = lock.acquire_concurrent_with_max(64)?;
assert!((1..=64).contains(&concurrent_id));
lock.release_concurrent();
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

`max_concurrent` 小于 1 时返回 `InvalidMaxConcurrent`。阻塞获取在运行时 Concurrent 数量超过内部 31 位容量时返回 `CapacityExceeded`；Try 类获取则按照 C# 的 Try 契约返回 `None`。这个容量边界在现实业务中基本不可达。

### Exclusive

```rust
let guard = lock.acquire_exclusive();

// 当前线程独占访问。

lock.release_exclusive(guard);
```

Exclusive 是线程关联的：获取、释放或降级必须发生在同一线程。

### TryConcurrent

```rust
if let Some(concurrent_id) = lock.try_acquire_concurrent()? {
    // 已获得 Concurrent。
    lock.release_concurrent();
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

立即 Try 只检查当前是否可以进入，不等待状态变化。

带超时：

```rust
use std::time::Duration;

if let Some(id) = lock.try_acquire_concurrent_for(Duration::from_millis(100))? {
    lock.release_concurrent();
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

### TryExclusive

抢占式 Try：

```rust
if let Some(guard) = lock.try_acquire_exclusive(true) {
    // 已获得 Exclusive。
    lock.release_exclusive(guard);
}
```

`true` 并不表示“绝不等待”。它表示允许进入抢占式 Exclusive 竞争：如果调用时尚未观察到其他 Exclusive 压力，请求可以阻止新 Concurrent，并等待已有 Concurrent 退出；若竞争期间出现升级请求，当前普通 Exclusive 请求可能让出并返回 `false`。

Idle-only 测试：

```rust
if let Some(guard) = lock.try_acquire_exclusive(false) {
    lock.release_exclusive(guard);
}
```

`false` 不抢占 Concurrent，也不等待锁状态变化；只有当前处于 Idle 且可以立即进入 Monitor 时才成功。

带超时的抢占式 Exclusive：

```rust
if let Some(guard) = lock.try_acquire_exclusive_for(Duration::from_millis(100)) {
    lock.release_exclusive(guard);
}
```

`Duration::ZERO` 等价于一次 Idle-only 立即尝试。

---

## 原地升级与降级

### Concurrent → Exclusive

```rust
lock.acquire_concurrent()?;

// Concurrent 阶段。

let guard = lock.concurrent_to_exclusive();

// 连续进入 Exclusive 阶段，没有先释放 Concurrent 再抢锁的窗口。

lock.release_exclusive(guard);
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

升级成功后，原 Concurrent 权限已经转换为 Exclusive，不能再调用 `release_concurrent()`。

### Exclusive → Concurrent

```rust
let guard = lock.acquire_exclusive();

// Exclusive 修改阶段。

lock.exclusive_to_concurrent(guard);

// 当前线程继续持有 Concurrent。

lock.release_concurrent();
```

降级后不能再调用 `release_exclusive()`。

---

## ContextID 与 EpochID

ContextID 和 EpochID 是锁协议之外的业务状态：

- `ContextID`：当前业务上下文；
- `EpochID`：只能单调推进的生命周期、版本或阶段。

它们的分配、含义、校验和清理由业务代码负责。

### ContextID

```rust
lock.set_context_id(10);
assert_eq!(lock.context_id(), 10);

let changed = lock.switch_context_id(11);
assert!(changed);

let unchanged = lock.switch_context_id(11);
assert!(!unchanged);
```

`switch_context_id` 使用原子交换。新值与旧值不同返回 `true`。

### EpochID

```rust
assert!(lock.raise_epoch_id(1));
assert!(lock.raise_epoch_id(2));
assert!(!lock.raise_epoch_id(2));
assert!(!lock.raise_epoch_id(1));
```

直接调用 `set_epoch_id` 可以覆盖、回退或清零；需要单调语义时必须使用 `raise_epoch_id`。

### 条件升级

```rust
lock.acquire_concurrent()?;

if let Some(guard) = lock.try_concurrent_to_exclusive_with_switch_context_id(100) {
    // ContextID 切换成功，并持有 Exclusive。
    lock.release_exclusive(guard);
} else {
    // 原 Concurrent 已自动释放。
    // 这里不能再次 release_concurrent()。
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

EpochID 版本：

```rust
lock.acquire_concurrent()?;

if let Some(guard) = lock.try_concurrent_to_exclusive_with_raise_epoch_id(20) {
    lock.release_exclusive(guard);
} else {
    // 原 Concurrent 已自动释放。
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

多个 Concurrent 持有者使用同一个 ContextID 或 EpochID 条件竞争时，通常只有成功改变业务 ID 的调用者升级；失败者自动退出原 Concurrent 权限。

---

## Scope：RAII 自动释放

Rust 版 Scope 对应 C# `IDisposable` / Java `AutoCloseable` 的便利层。

```rust
use concurrent_exclusive_lock::{
    ConcurrentExclusiveLock,
    ConcurrentExclusiveLockScope,
};

let lock = ConcurrentExclusiveLock::new();

{
    let mut scope = ConcurrentExclusiveLockScope::new(&lock);
    scope.acquire_concurrent()?;

    // 提前 return 或 panic unwind 时，Drop 自动释放最终权限。
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

升级：

```rust
{
    let mut scope = ConcurrentExclusiveLockScope::new(&lock);
    scope.acquire_concurrent()?;
    scope.concurrent_to_exclusive();
    // Drop 按最终 Exclusive 状态释放。
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

降级：

```rust
{
    let mut scope = ConcurrentExclusiveLockScope::new(&lock);
    scope.acquire_exclusive();
    scope.exclusive_to_concurrent();
    // Drop 按最终 Concurrent 状态释放。
}
```

Scope 具有以下边界：

- 构造函数不自动获取权限；
- 可以手动释放，Drop 不会重复释放已经从 Scope 记录中移除的权限；
- Drop 只释放最终权限，不恢复 ContextID / EpochID；
- Scope 不能在线程之间移动，也不能多线程共享；
- Scope 不是底层 Token，核心锁仍然支持直接对象调用；
- `std::mem::forget(scope)` 会故意阻止 Drop，导致权限泄漏，属于调用方误用。

---

## Pipeline

Pipeline 执行一组同步 Segment，并根据上一个成功 Segment 保留的权限自动完成：

- 释放；
- 重新获取；
- 延续；
- 升级；
- 降级；
- ContextID / EpochID 条件收敛。

```rust
use concurrent_exclusive_lock::{
    ConcurrentExclusiveLock,
    ConcurrentExclusiveLockPipeline,
    ConcurrentExclusiveLockSegment,
    IDType,
};

let lock = ConcurrentExclusiveLock::new();
let pipeline = ConcurrentExclusiveLockPipeline::new(&lock);

let mut segments = vec![
    ConcurrentExclusiveLockSegment::concurrent(|| {
        // 独立 Concurrent 区段。
    }),
    ConcurrentExclusiveLockSegment::try_apply_id_converge_exclusive(
        || {
            // 仅在 EpochID 推进成功并持有 Exclusive 时执行。
        },
        10,
        IDType::EpochID,
    ),
    ConcurrentExclusiveLockSegment::converge_concurrent(|| {
        // 从 Exclusive 降级，或延续/获取 Concurrent。
    }),
    ConcurrentExclusiveLockSegment::none(|| {
        // 无权限区段。
    }),
];

pipeline.do_pipeline(&mut segments)?;
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

### Segment 语义

| 工厂方法 | 语义 |
|---|---|
| `none` | 释放上一个 Segment 保留的权限，然后无锁执行 |
| `concurrent` | 释放旧权限，独立获取一次 Concurrent |
| `try_concurrent` | 释放旧权限，立即尝试 Concurrent；失败则跳过并从 None 继续 |
| `exclusive` | 释放旧权限，独立获取一次 Exclusive |
| `test_exclusive` | 释放旧权限，只在 Idle 时立即尝试 Exclusive |
| `try_exclusive` | 释放旧权限，尝试抢占式 Exclusive；升级出现时可能失败 |
| `converge_concurrent` | 延续 Concurrent；或将 Exclusive 降级；或从 None 获取 Concurrent |
| `converge_exclusive` | 延续 Exclusive；或将 Concurrent 原地升级；或从 None 获取 Exclusive |
| `try_apply_id_converge_exclusive` | 仅在 ContextID/EpochID 应用成功时收敛到 Exclusive |

Try 类型失败时：

```text
当前 Segment 不执行
→ 当前权限成为 None
→ 不抛出“未获取”异常
→ 后续 Segment 继续运行
```

`try_apply_id_converge_exclusive` 中的 Try 针对业务 ID 是否应用成功，不表示整个操作绝不等待。当前处于 Concurrent 时，它仍然会进入升级协调过程。

### panic 传播

Segment callback 中的 panic 会正常向调用方传播，后续 Segment 不再执行。Rust 展开栈时，Pipeline 内部 Scope 的 Drop 会释放仍然持有的最终权限。

当编译配置使用 `panic = "abort"` 时不存在栈展开，任何 RAII 清理都不会执行；这不是本锁特有的行为。本项目 Release 配置保留 `panic = "unwind"`。

### 同步边界

Segment 是同步 `FnMut()` 回调。受保护业务必须在 callback 返回前完成。

下面这种行为不受协议保护：

```rust
ConcurrentExclusiveLockSegment::exclusive(|| {
    std::thread::spawn(|| {
        // callback 已经返回后，这段工作仍继续执行；
        // Pipeline 已经可能释放或转换权限。
    });
});
```

Rust 版没有把单个 Segment 伪装成 async。需要把整个同步 Pipeline 放到线程池或 async runtime 的 blocking 任务中时，由调用方在外层调度，例如 Tokio 的 `spawn_blocking`；核心 crate 不依赖任何 async runtime。

---

## 快照

```rust
let state = lock.observed_state();
let contention = lock.observed_contention();
```

它们只能用于：

- 诊断；
- 日志；
- 监控；
- 调度参考。

不能把快照作为同步正确性的依据。读取快照后，锁状态可能立即变化。

抢占式 Exclusive 请求进入窗口后，`observed_state()` 可能已经是 `Exclusive`，即使请求仍在等待已有 Concurrent 退出。

---

## 非递归与线程规则

本锁不提供递归权限：

- 持有 Concurrent 时，不要调用普通 `acquire_exclusive()`；
- 持有 Exclusive 时，不要调用普通 `acquire_concurrent()`；
- Concurrent → Exclusive 使用升级 API；
- Exclusive → Concurrent 使用降级 API；
- Exclusive 获取、释放和降级必须在同一线程；
- 不要把持有 Exclusive 的执行流跨越可迁移线程的 await；
- 不要销毁仍被其他线程访问的锁对象。

Rust 借用系统可以防止一部分生命周期错误，但直接锁 API 是权限协议，不会追踪“当前线程到底持有什么权限”。错误释放、重复释放、非法嵌套和跨线程释放仍属于调用方协议错误。

Scope 通过 `!Send / !Sync` 限制减少跨线程误用，但核心直接 API 保持与 C# / Java 一致的低开销形式。

---

## 测试

Cargo 单元/集成测试：

```powershell
cargo test --release --workspace --offline
```

完整语义回归：

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- `
  --full-semantics `
  --lock-instances 8 `
  --semantic-workers 4 `
  --semantic-operations 256
```

Pipeline 固定语义：

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- --pipeline-semantics
```

Pipeline 随机压力测试：

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 8 `
  --semantic-seed 0x12345678
```

Exclusive 高竞争进展测试：

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- `
  --contention-stress 30s `
  --semantic-workers 16
```

耐久测试：

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- `
  --endurance 24h `
  --lock-instances 8 `
  --semantic-workers 8
```

本发布包的正式 30 分钟 Pipeline 压力完成 `2,732,232,429` 轮和 `14,775,380,351` 次校验回调；60 秒 Exclusive 竞争完成 `401,719,852` 次获取，32 个 worker 全部取得进展。

详细内容参阅 [`TESTING_CN.md`](TESTING_CN.md)。

---

## 性能评测概要

### 评测对象与可比性

评测程序统一比较六种策略：

- `std::sync::Mutex`；
- `std::sync::RwLock`；
- `parking_lot::Mutex` 0.12.5；
- `parking_lot::RwLock` 0.12.5；
- CEL；
- `CEL(ExclusiveOnly)`，即所有操作都走 CEL Exclusive 的纯互斥基线。

每种策略在每个场景中都会重新创建锁和 `MemoryWork`，使用相同的随机种子、相同的读写判定序列、相同的共享内存和相同的 Work 步数。测试结束后会比较：

- 实际读次数；
- 实际写次数；
- 最终状态哈希。

任意策略的操作次数或最终状态不一致，整组 benchmark 都会失败。本次正式数据共包含 **10 组配置、6 种读写比例、6 种策略，合计 360 行结果**。

测试环境：

```text
Rust        : 1.75.0
OS          : Linux 6.12 x86_64
Virtualized : KVM
Available CPU: 约 4
```

16 线程和 64 线程都属于超额订阅，因此这些数字适合比较**同一受限环境中的相对行为**，不能直接视为 Windows、裸机 Linux、物理 64 核、多 NUMA 服务器或其他 Rust 版本上的固定排名。

所有 README 数据都直接来自：

```text
TestBenchmarkResults/final/benchmarks/
```

其中保留了逐组原始日志、CSV、JSON、三轮中位数、运行脚本和生成文档的脚本。

### 完整正式测试矩阵

| 配置标识 | 锁实例 | 每锁线程 | 总线程 | 每线程操作 | 每锁内存 | Work | 次数 | 主要目的 |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| `single_1t_w64` | 1 | 1 | 1 | 100,000 | 64 MiB | 64 | 1 | 无争用固定成本基线 |
| `single_4t_w64` | 1 | 4 | 4 | 30,000 | 64 MiB | 64 | 1 | 接近机器可用 CPU 数 |
| `single_16t_w64_r1` | 1 | 16 | 16 | 10,000 | 64 MiB | 64 | 第 1 轮 | 主配置重复测试 |
| `single_16t_w64_r2` | 1 | 16 | 16 | 10,000 | 64 MiB | 64 | 第 2 轮 | 主配置重复测试 |
| `single_16t_w64_r3` | 1 | 16 | 16 | 10,000 | 64 MiB | 64 | 第 3 轮 | 主配置重复测试 |
| `single_64t_w64` | 1 | 64 | 64 | 3,000 | 64 MiB | 64 | 1 | 高争用与超额订阅 |
| `single_16t_w1` | 1 | 16 | 16 | 50,000 | 64 MiB | 1 | 1 | 极短临界区 |
| `single_16t_w256` | 1 | 16 | 16 | 3,000 | 64 MiB | 256 | 1 | 较长临界区 |
| `multi_8x4_w64` | 8 | 4 | 32 | 5,000 | 16 MiB | 64 | 1 | 中等规模多锁 |
| `multi_64x2_w64` | 64 | 2 | 128 | 2,000 | 4 MiB | 64 | 1 | 大量独立锁与调度压力 |

每组配置都执行：

```text
100/0
99.5/0.5
90/10
50/50
30/70
0/100
```

下面不是只展示 CEL 占优的场景，而是按测试矩阵完整说明主要结果和限制。

### 无争用基线：单锁、1 线程、Work=64

单线程时没有锁竞争，结果主要反映 API、Guard、原子操作和 Work 本身的固定成本。单位为 `works/s`：

| 读/写 | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 618,810 | 612,489 | **636,147** | 579,282 | 594,852 | 588,689 |
| 99.5/0.5 | 567,708 | 572,140 | 575,756 | 557,009 | **583,535** | 568,501 |
| 90/10 | **591,151** | 571,345 | 581,760 | 583,009 | 566,104 | 570,733 |
| 50/50 | 518,260 | **538,132** | 501,885 | 533,362 | 512,943 | 478,164 |
| 30/70 | 503,101 | 503,414 | **506,711** | 503,773 | 494,638 | 482,750 |
| 0/100 | 481,560 | 474,150 | 485,258 | 470,524 | **490,807** | 467,799 |

这一组没有稳定赢家，各策略整体处于相近范围。它说明 CEL 在无争用时没有异常高的固定开销，但无争用数据本身不能证明并发优势。

### 接近机器并发度：单锁、4 线程、Work=64

当前虚拟机约有 4 个可用 CPU，因此这组比 16/64 线程更接近机器实际并发度：

| 读/写 | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 372,767 | **1,787,040** | 428,539 | 1,651,290 | 1,749,075 | 356,814 |
| 99.5/0.5 | 400,332 | 1,179,255 | 330,386 | 1,484,585 | **2,222,444** | 356,100 |
| 90/10 | 430,552 | 442,452 | 426,983 | 686,100 | **918,912** | 408,557 |
| 50/50 | 383,622 | 336,925 | 362,777 | 338,245 | **470,486** | 364,260 |
| 30/70 | 368,546 | 334,918 | 371,582 | 281,488 | **460,153** | 351,083 |
| 0/100 | 326,740 | 276,987 | **344,459** | 305,641 | 331,291 | 338,942 |

客观来看：

- 纯读由标准 `RwLock` 略胜 CEL；
- CEL 在 99.5/0.5 到 30/70 的混合比例中领先；
- 纯写重新回到简单 Mutex 类策略更有竞争力的状态；
- `parking_lot::RwLock` 在 99.5/0.5 和 90/10 中优于标准 `RwLock`，但低于 CEL。

### 主配置：单锁、16 线程、64 MiB、Work=64

命令参数：

```text
--lock-instances 1 --threads 16 --workload memory
--operations 10000 --memory-mb 64 --read-work 64 --write-work 64
```

这一配置完整运行 3 次。下表使用吞吐中位数，单位 `works/s`：

| 读/写 | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 555,908 | 2,483,393 | 256,295 | 2,539,576 | **2,695,156** | 508,805 |
| 99.5/0.5 | 519,450 | **1,575,629** | 257,357 | 1,355,039 | 1,491,291 | 519,608 |
| 90/10 | 540,791 | 379,354 | 255,487 | 463,697 | **713,582** | 524,334 |
| 50/50 | **494,775** | 312,405 | 235,382 | 221,611 | 470,606 | 475,282 |
| 30/70 | **467,314** | 409,725 | 239,319 | 214,644 | 440,282 | 465,727 |
| 0/100 | **466,230** | 460,028 | 242,002 | 234,290 | 456,459 | 453,863 |

对应结论：

- **100/0：** CEL 比标准 `RwLock` 高约 8.5%，比 `parking_lot::RwLock` 高约 6.1%，但三者仍可视为同一性能档；
- **99.5/0.5：** 标准 `RwLock` 最快，CEL 比它低约 5.4%，说明 CEL 并非在所有读主导比例中领先；
- **90/10：** CEL 为 `713,582/s`，比标准 `RwLock` 高约 88%，比 `parking_lot::RwLock` 高约 54%，是这组测试中 CEL 优势最明确的比例；
- **50/50：** 标准 Mutex 比 CEL 高约 5%，但 CEL 明显高于两种 `RwLock`；
- **30/70：** 标准 Mutex、CEL、CEL ExclusiveOnly 和标准 `RwLock` 已进入相近区间，没有数量级差异；
- **0/100：** 标准 Mutex、标准 `RwLock`、CEL 基本收敛，CEL 不具备纯写天然优势；
- **本机的 parking_lot：** 纯读正常处于高性能档，但高写单锁争用偏慢。该结果只代表 `parking_lot 0.12.5 + Rust 1.75 + 当前 Linux/KVM`，不能外推为跨平台结论。

### 16 线程主配置的三轮波动范围

下面为三种主要 Concurrent/Exclusive 策略的 `最小值 / 中位数 / 最大值`，单位 `works/s`：

| 读/写 | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 100/0 | 2,134,108 / 2,483,393 / 2,858,710 | 1,938,258 / 2,539,576 / 3,263,309 | 2,625,290 / 2,695,156 / 3,338,169 |
| 99.5/0.5 | 1,524,671 / 1,575,629 / 1,586,392 | 1,312,349 / 1,355,039 / 1,355,498 | 1,322,869 / 1,491,291 / 1,748,381 |
| 90/10 | 357,860 / 379,354 / 441,569 | 460,819 / 463,697 / 473,767 | 621,665 / 713,582 / 814,223 |
| 50/50 | 301,136 / 312,405 / 313,910 | 221,002 / 221,611 / 255,398 | 426,447 / 470,606 / 475,392 |
| 30/70 | 388,299 / 409,725 / 425,057 | 203,509 / 214,644 / 229,394 | 419,337 / 440,282 / 458,694 |
| 0/100 | 442,967 / 460,028 / 473,773 | 232,631 / 234,290 / 234,989 | 445,817 / 456,459 / 481,919 |

三轮并非完全无波动，尤其纯读和 CEL 的部分混合比例受虚拟机调度影响较明显。因此 README 使用中位数，而不是挑选单轮最高值。原始日志：

```text
TestBenchmarkResults/final/benchmarks/single_16t_w64_r1.log
TestBenchmarkResults/final/benchmarks/single_16t_w64_r2.log
TestBenchmarkResults/final/benchmarks/single_16t_w64_r3.log
```

### 64 线程高争用

64 个线程运行在约 4 个可用 CPU 上，这组主要观察超额订阅、停车/唤醒和高争用下的进展能力：

| 读/写 | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 100/0 | 2,655,356 | 2,452,647 | **5,329,420** |
| 99.5/0.5 | **1,116,340** | 808,276 | 1,060,577 |
| 90/10 | 262,941 | 284,170 | **654,907** |
| 50/50 | 206,418 | 201,012 | **459,576** |
| 30/70 | 439,304 | 199,050 | **455,274** |
| 0/100 | **452,001** | 229,147 | 440,729 |

这组中：

- CEL 在纯读、90/10、50/50 和 30/70 中较强；
- 标准 `RwLock` 在 99.5/0.5 和纯写中略胜 CEL；
- 64 线程远超过可用 CPU 数，调度器和停车策略成为结果的一部分，不能把它当成物理 64 核数据。

`avg write ns` 是写请求从申请锁之前，到写 Work 完成并释放锁之后的平均端到端延迟，包含排队、调度、获取、Work 和释放，不是单条锁指令成本：

| 读/写 | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 99.5/0.5 | 276,617 ns | 393,168 ns | **97,037 ns** |
| 90/10 | 105,000 ns | 247,545 ns | **71,791 ns** |
| 50/50 | 344,689 ns | 297,299 ns | **104,858 ns** |
| 0/100 | 97,755 ns | 272,694 ns | **94,372 ns** |

在这台超额订阅虚拟机中，CEL 的写请求推进延迟较低；但其中包含操作系统挂起和重新调度时间，因此不能把这些值解释成纯锁内部纳秒开销。

### 临界区长度变化

16 线程下改变 Work 长度，结果并不固定：

| Work | 场景 | std RwLock | parking RwLock | CEL | CEL / std RwLock |
|---:|---:|---:|---:|---:|---:|
| 1 | 100/0 | 21,061,252 | 21,405,547 | **31,136,259** | 1.48× |
| 1 | 90/10 | 4,834,923 | 6,538,139 | **7,861,429** | 1.63× |
| 1 | 0/100 | **3,092,052** | 1,430,813 | 2,582,370 | 0.84× |
| 64 | 100/0 | 2,483,393 | 2,539,576 | **2,695,156** | 1.09× |
| 64 | 90/10 | 379,354 | 463,697 | **713,582** | 1.88× |
| 64 | 0/100 | **460,028** | 234,290 | 456,459 | 0.99× |
| 256 | 100/0 | **1,183,342** | 649,555 | 828,225 | 0.70× |
| 256 | 90/10 | **191,707** | 147,245 | 164,580 | 0.86× |
| 256 | 0/100 | 112,913 | 73,625 | **120,843** | 1.07× |

其中 Work=64 为三轮中位数；Work=1 和 Work=256 为扩展配置单轮结果。

这说明：

- 极短临界区会放大同步协议、原子操作和缓存线竞争差异；
- Work 增大后，业务内存访问占比上升，锁本身的相对影响下降；
- Work=256 的纯读和 90/10 中，标准 `RwLock` 反而领先 CEL；
- 纯写越来越接近普通互斥比较，CEL 的 Concurrent 设计不再提供额外价值。

### 多锁：8 把锁、每锁 4 线程

| 读/写 | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 100/0 | 2,864,727 | **3,042,378** | 2,802,405 |
| 99.5/0.5 | **6,261,619** | 2,289,339 | 1,970,930 |
| 90/10 | **2,263,285** | 1,691,974 | 2,193,316 |
| 50/50 | **2,095,450** | 1,632,556 | 1,750,895 |
| 30/70 | 2,049,541 | 1,903,300 | **2,564,973** |
| 0/100 | 1,714,731 | 1,879,581 | **2,012,275** |

这组没有统一赢家：

- 标准 `RwLock` 在 99.5/0.5、90/10、50/50 中领先；
- `parking_lot::RwLock` 在纯读领先；
- CEL 在 30/70 和纯写领先；
- 多锁总吞吐同时受锁实例并行度、内存带宽、调度和测试时长影响，单锁倍率会收敛或改变。

### 多锁：64 把锁、每锁 2 线程

这组包含 128 个专用线程，但沙盒只有约 4 个可用 CPU，并且每个策略/比例仅有 256,000 次总操作。它主要验证大量独立锁能否并行推进和保持最终状态一致，不适合作为精确排名：

| 读/写 | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 100/0 | **3,035,962** | 2,817,826 | 2,815,580 |
| 99.5/0.5 | 2,773,926 | 3,250,122 | **11,259,869** |
| 90/10 | 3,197,306 | 2,551,568 | **4,544,870** |
| 50/50 | 3,112,502 | 4,042,819 | **8,874,252** |
| 30/70 | 2,882,352 | **7,186,587** | 5,889,522 |
| 0/100 | 2,560,876 | **10,973,384** | 2,082,153 |

这张表的排名跳动很大，符合短测试时长、128 线程超额订阅和独立锁调度共同作用的特征。可以确认的是：六种策略在所有比例下都完成了相同读写次数，并得到相同最终状态哈希；不能据此声称稳定的数量级优势。

### 正确性与持续压力验证

性能结果之外，本发布包还完成：

| 验证项 | 结果 |
|---|---|
| `cargo fmt --check` | PASS |
| `cargo clippy --workspace --all-targets -- -D warnings` | PASS |
| Release Workspace 构建 | PASS |
| Release 全部 Cargo 测试 | PASS |
| 完整语义测试 | PASS |
| Pipeline 固定语义测试 | PASS |
| 空 Cargo 缓存、纯离线重建 | PASS |
| 60 秒 Exclusive 竞争 | `401,719,852` 次获取，32 个 worker 全部进展 |
| 60 秒 Endurance | 58 个确定性批次 |
| 30 分钟 Pipeline 压力 | `2,732,232,429` 轮，`14,775,380,351` 次校验回调 |

30 分钟 Pipeline 测试结束后，锁状态和独立访问验证器均恢复 Idle。原始日志位于：

```text
TestBenchmarkResults/final/pipeline-stress-30m.log
TestBenchmarkResults/final/contention-stress-60s.log
TestBenchmarkResults/final/endurance-60s.log
TestBenchmarkResults/final/full-semantics.log
TestBenchmarkResults/final/pipeline-semantics.log
```

### 综合结论

基于本次环境和完整测试矩阵，可以得到的谨慎结论是：

- CEL 最有价值的区域是**读路径需要低调度成本，同时待处理写请求需要阻止新读继续进入**的混合负载，本次 90/10 是最典型场景；
- CEL 在纯读中有竞争力，但不会在所有线程数和 Work 长度下稳定胜过成熟 `RwLock`；
- 99.5/0.5 下标准 `RwLock` 在 16/64 线程主测试中都能胜过 CEL；
- Work=256 的纯读和 90/10 中，标准 `RwLock` 领先 CEL，说明业务工作变长后排名可能反转；
- 高写和纯写场景中，简单 Mutex 可以与 CEL 持平或更快，CEL 不具备纯写天然优势；
- `parking_lot::RwLock` 是必须保留的高性能基线，但当前 Linux/KVM 结果不能代表 Windows、裸机 Linux或不同 Rust 版本；
- 多锁测试没有统一赢家，锁实例数、每锁线程数、内存带宽和调度都会改变总吞吐；
- 吞吐和写延迟必须同时观察，较高吞吐不保证每个写请求延迟最低；
- CEL 的原地升级、降级、ContextID/EpochID 和 Pipeline 是普通 `RwLock` 不直接提供的语义能力。性能表只能回答执行成本，不能替代功能层面的选型。

完整 360 行结果和原始数据参阅 [`PERFORMANCE_CN.md`](PERFORMANCE_CN.md) 以及：

```text
TestBenchmarkResults/final/benchmarks/all_results.csv
TestBenchmarkResults/final/benchmarks/all_results.json
TestBenchmarkResults/final/benchmarks/
```

---

## 平台与内存模型

核心状态使用：

```rust
AtomicI64  // Concurrent / Exclusive 合并计数
AtomicI32  // ContextID
AtomicI32  // EpochID
```

关键协议转换使用 `SeqCst`，观察读取使用 Acquire，直接业务 ID 设置使用 Release。第一版优先保证与 C# Interlocked / Volatile 语义一致，没有为了减少屏障而激进弱化内存序。

内部 Monitor 直接使用 Rust 标准库 `Mutex<()>`，由标准库映射到 Windows、Linux、macOS、Android、iOS 等目标的系统等待原语。

目标必须支持 64 位原子；不支持 `AtomicI64` 的小型嵌入式目标不在当前版本支持范围内。裸机和 `no_std` 也不在当前版本范围内，因为阻塞 Monitor 需要线程调度与等待机制。

---

## 不保证的内容

本项目不保证：

- 严格 FIFO；
- 每个线程获得次数完全相等；
- 递归进入；
- 死锁检测；
- 自动识别非法释放；
- 跨线程迁移 Exclusive 权限；
- async-aware 锁持有；
- 所有目标平台上的相同性能；
- 快照读取后的状态仍然有效。

调度顺序仍会受到操作系统、Rust 标准库实现、CPU 拓扑、缓存、NUMA、系统负载和业务临界区长度影响。

---

## 适用场景

尤其适合：

- 玩家、房间、订单、缓存条目等实体级锁；
- 读主导但写必须及时发生的状态；
- Concurrent 阶段结束后需要连续进入 Exclusive 的流程；
- Exclusive 修改后需要连续降级为 Concurrent 的流程；
- 可以使用业务 ContextID / EpochID 减少重复升级的系统；
- 需要用 Pipeline 明确表达权限流程的同步业务。

不适合：

- 需要严格公平队列的协议；
- 临界区会跨越任意 async 迁移的代码；
- 非法嵌套无法在业务层约束的代码；
- 只需要简单互斥且不存在 Concurrent 并行价值的场景；
- `no_std` 裸机环境。

---

## License

双许可证：

```text
MIT OR Apache-2.0
```

参阅：

- [`LICENSE-MIT`](LICENSE-MIT)
- [`LICENSE-APACHE-2.0`](LICENSE-APACHE-2.0)
