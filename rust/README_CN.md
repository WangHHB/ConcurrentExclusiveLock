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
3. 核心锁采用直接对象调用，不返回 ownership token；
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
├─ Cargo.toml                       # Cargo Workspace
├─ README.md
├─ README_CN.md
├─ TESTING.md
├─ TESTING_CN.md
├─ PERFORMANCE.md
├─ PERFORMANCE_CN.md
├─ VERIFICATION.md
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

Rust 标准库没有可脱离 Guard、跨方法直接 `lock()/unlock()` 的公共原始 Mutex API。为了保持与 C# / Java 一样的直接锁对象接口，本项目在纯 Rust 标准库之上实现了内部 `RawMonitor`：

```text
AtomicBool      记录 Monitor 是否持有
AtomicUsize     记录等待压力
Mutex + Condvar 提供阻塞等待与唤醒
```

这个 Monitor：

- 非递归；
- 可跨 `acquire_exclusive()` 与 `release_exclusive()` 方法保持持有状态；
- 让 Exclusive / 升级竞争进入同一个串行阻塞慢路径；
- 使用等待者计数抑制无约束插队；
- 不建立 ticket 队列；
- 不承诺严格 FIFO。

它追求的是与 C# `Monitor` 相同类型的综合平衡：具有实用顺序性和阻塞调度，但不为了“绝对公平”牺牲吞吐和可用性。

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
cargo build --release --workspace
```

运行 Cargo 测试：

```powershell
cargo test --release --workspace
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

本 Workspace 没有第三方 crate 依赖，核心库和测试程序都可以在 Cargo 缓存为空时离线构建。

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
lock.acquire_exclusive();

// 当前线程独占访问。

lock.release_exclusive();
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
if lock.try_acquire_exclusive(true) {
    // 已获得 Exclusive。
    lock.release_exclusive();
}
```

`true` 并不表示“绝不等待”。它表示允许进入抢占式 Exclusive 竞争：如果调用时尚未观察到其他 Exclusive 压力，请求可以阻止新 Concurrent，并等待已有 Concurrent 退出；若竞争期间出现升级请求，当前普通 Exclusive 请求可能让出并返回 `false`。

Idle-only 测试：

```rust
if lock.try_acquire_exclusive(false) {
    lock.release_exclusive();
}
```

`false` 不抢占 Concurrent，也不等待锁状态变化；只有当前处于 Idle 且可以立即进入 Monitor 时才成功。

带超时的抢占式 Exclusive：

```rust
if lock.try_acquire_exclusive_for(Duration::from_millis(100)) {
    lock.release_exclusive();
}
```

`Duration::ZERO` 等价于一次 Idle-only 立即尝试。

---

## 原地升级与降级

### Concurrent → Exclusive

```rust
lock.acquire_concurrent()?;

// Concurrent 阶段。

lock.concurrent_to_exclusive();

// 连续进入 Exclusive 阶段，没有先释放 Concurrent 再抢锁的窗口。

lock.release_exclusive();
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

升级成功后，原 Concurrent 权限已经转换为 Exclusive，不能再调用 `release_concurrent()`。

### Exclusive → Concurrent

```rust
lock.acquire_exclusive();

// Exclusive 修改阶段。

lock.exclusive_to_concurrent();

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

if lock.try_concurrent_to_exclusive_with_switch_context_id(100) {
    // ContextID 切换成功，并持有 Exclusive。
    lock.release_exclusive();
} else {
    // 原 Concurrent 已自动释放。
    // 这里不能再次 release_concurrent()。
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

EpochID 版本：

```rust
lock.acquire_concurrent()?;

if lock.try_concurrent_to_exclusive_with_raise_epoch_id(20) {
    lock.release_exclusive();
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
cargo test --release --workspace
```

完整语义回归：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --full-semantics `
  --lock-instances 8 `
  --semantic-workers 4 `
  --semantic-operations 256
```

Pipeline 固定语义：

```powershell
cargo run --release -p cel-test-and-benchmark -- --pipeline-semantics
```

Pipeline 随机压力测试：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 8 `
  --semantic-seed 0x12345678
```

Exclusive 高竞争进展测试：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --contention-stress 30s `
  --semantic-workers 16
```

耐久测试：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --endurance 24h `
  --lock-instances 8 `
  --semantic-workers 8
```

详细内容参阅 [`TESTING_CN.md`](TESTING_CN.md)。

---

## 性能对比

默认 Benchmark 使用与 C# 项目对应的随机共享内存 Work，并比较：

- `std::sync::Mutex`；
- `std::sync::RwLock`；
- `ConcurrentExclusiveLock`；
- `CEL(ExclusiveOnly)`。

场景：

```text
100/0
99.5/0.5
90/10
50/50
30/70
0/100
```

正式测试建议使用足够的临界区 Work。极短临界区主要测量锁本身成本，无法完整体现 Concurrent 并行和抢占式 Exclusive 的业务价值。

单锁热点：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 1 `
  --threads 16 `
  --operations 100000 `
  --workload memory `
  --memory-mb 64 `
  --read-work 256 `
  --write-work 256
```

多实体锁：

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --lock-instances 8 `
  --threads 8 `
  --operations 100000 `
  --workload memory `
  --memory-mb 64 `
  --read-work 640 `
  --write-work 640
```

关注指标：

- `works/s`：总吞吐；
- `works/s/lock`：每个实体锁吞吐；
- `avg write ns`：Exclusive 请求、等待、Work 和释放的平均总时间；
- `state`：不同策略最终业务状态必须一致。

正式结论应来自目标机器上的多轮 Release 测试，不应从单次、短临界区或不同语言运行时之间的数字直接推导。

详细方法参阅 [`PERFORMANCE_CN.md`](PERFORMANCE_CN.md)。

---

## 平台与内存模型

核心状态使用：

```rust
AtomicI64  // Concurrent / Exclusive 合并计数
AtomicI32  // ContextID
AtomicI32  // EpochID
```

关键协议转换使用 `SeqCst`，观察读取使用 Acquire，直接业务 ID 设置使用 Release。第一版优先保证与 C# Interlocked / Volatile 语义一致，没有为了减少屏障而激进弱化内存序。

内部 Monitor 完全基于 Rust 标准库，因此由标准库把 `Mutex` / `Condvar` 映射到 Windows、Linux、macOS、Android、iOS 等目标的系统等待原语。

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
