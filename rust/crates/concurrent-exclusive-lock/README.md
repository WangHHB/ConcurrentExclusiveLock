# ConcurrentExclusiveLock for Rust

`ConcurrentExclusiveLock` is a high-performance, non-recursive synchronization lock built around **Concurrent / Exclusive access permissions**.

This Rust implementation is ported from the original C# implementation. The C# version remains the reference for protocol semantics, state transitions, and behavioral boundaries. Rust-specific changes are limited to language expression, lifetime management, error returns, and the blocking primitive used by the internal monitor.

The Rust port retains:

- lightweight atomic counting for ordinary Concurrent acquisition/release;
- preemptive Exclusive acquisition;
- in-place Concurrent → Exclusive upgrade;
- in-place Exclusive → Concurrent downgrade;
- ContextID / EpochID business-conditioned convergence;
- RAII Scope release management;
- synchronous Pipeline permission orchestration;
- observational state and contention snapshots;
- a serialized blocking slow path for Exclusive and upgrade contention without a strict FIFO promise.

> **Important:** `Concurrent` / `Exclusive` describe whether simultaneous access is permitted. They do not prescribe read/write intent. A Concurrent region may modify business state known not to conflict, while an Exclusive region may contain mostly reads.

---

## Relationship to the C# reference implementation

The port follows these rules:

1. C# remains the protocol reference implementation.
2. Rust does not redesign the access model.
3. The core lock uses direct object methods and does not return an ownership token.
4. A Concurrent ID is an entry number for the current uninterrupted concurrent round, not a release credential.
5. No ticket queue or strict FIFO layer is added.
6. Scope manages lifetime only and does not alter the core protocol.
7. Pipeline transitions correspond to the C# Segment state machine.
8. Rust `Result`, `Duration`, lifetimes, and `Drop` are language adaptations only.

Project layout:

```text
rust/
├─ crates/
│  ├─ concurrent-exclusive-lock/   # Core lock, Scope, Pipeline
│  └─ test-and-benchmark/           # Semantic tests, stress tests, benchmark
├─ Cargo.toml                       # Cargo workspace
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

## Design overview

### Concurrent fast path

When no Exclusive pressure is visible and the caller limit has not been reached, ordinary Concurrent acquisition primarily performs:

```text
atomic counter read
→ atomic +1
→ validate that entry is still inside the allowed Concurrent range
→ return the Concurrent ID
```

Ordinary Concurrent release is primarily one atomic subtraction.

The ordinary Concurrent path does not join the Exclusive blocking queue. This is the main performance basis for read-dominant and many-entity-lock workloads.

### Preemptive Exclusive

An Exclusive request records pressure in the upper half of the combined counter. New ordinary Concurrent requests are then prevented from entering, while existing Concurrent holders leave naturally. The Exclusive request waits on the internal monitor path until it may actually execute in isolation.

This addresses a common business problem in conventional reader/writer scheduling:

> A write is already necessary, but a continuing stream of new reads keeps delaying it even though those reads may soon become stale after the write.

### Internal monitor

Rust's standard `Mutex` API intentionally unlocks by dropping a Guard and does not expose a public raw `lock()/unlock()` pair that can be retained across separate methods. To preserve the direct C#/Java-style lock API, this crate implements an internal standard-library-only `RawMonitor` from:

```text
AtomicBool      monitor-held state
AtomicUsize     waiter pressure
Mutex + Condvar blocking wait and wake-up
```

The monitor is:

- non-recursive;
- held across `acquire_exclusive()` and `release_exclusive()`;
- shared by ordinary Exclusive and upgrade scheduling;
- resistant to unrestricted barging through waiter tracking;
- not a ticket queue;
- not a strict FIFO guarantee.

The objective is the same type of balance provided by the C# `Monitor`: useful ordering and blocking coordination without paying the cost or head-of-line blocking of absolute fairness.

### Upgrade priority

Concurrent → Exclusive conversion is not implemented by releasing Concurrent and acquiring Exclusive again. The current Concurrent holder becomes an upgrade signal in the combined counter. Ordinary Exclusive requests yield while an upgrade batch is active.

Multiple successful upgraders still execute their Exclusive business regions serially.

### Continuous downgrade

An ordinary Exclusive → Concurrent downgrade preserves a continuous access context when possible. Under heavy upgrade contention, the reference protocol may split the current context and reacquire Concurrent so that remaining upgraders can complete. The Rust port preserves this behavior.

---

## Requirements

- stable Rust;
- Cargo;
- minimum supported Rust version: 1.75;
- a target with 64-bit atomic support;
- Visual Studio C++ Build Tools for the Windows MSVC toolchain;
- GCC or Clang as linker support on typical Linux systems;
- Xcode Command Line Tools on macOS.

After installing Rust on Windows, reopen PowerShell and verify:

```powershell
rustc --version
cargo --version
```

---

## Build

From the `rust` directory:

```powershell
cargo build --release --workspace
cargo test --release --workspace
```

Or run:

```powershell
.\build.ps1
```

The executable is normally generated at:

```text
target\release\cel-test-and-benchmark.exe   # Windows
target/release/cel-test-and-benchmark       # Linux/macOS
```

The workspace has no third-party crate dependencies. The core library and the test/benchmark executable can therefore build with an empty Cargo registry cache once the Rust toolchain is installed.

---

## Add the dependency

### Local path dependency

```toml
[dependencies]
concurrent-exclusive-lock = { path = "../ConcurrentExclusiveLock/rust/crates/concurrent-exclusive-lock" }
```

Rust source imports use underscores:

```rust
use concurrent_exclusive_lock::ConcurrentExclusiveLock;
```

After an actual crates.io publication, the dependency may become:

```toml
[dependencies]
concurrent-exclusive-lock = "1.0.0"
```

Until publication is complete, use a path or Git dependency rather than treating the version coordinate as already downloadable.

---

## Core lock usage

### Concurrent

```rust
use concurrent_exclusive_lock::ConcurrentExclusiveLock;

let lock = ConcurrentExclusiveLock::new();
let concurrent_id = lock.acquire_concurrent()?;

// May execute together with other Concurrent business regions.
// The ID is not an ownership token.

lock.release_concurrent();
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

Caller-limited acquisition:

```rust
let concurrent_id = lock.acquire_concurrent_with_max(64)?;
assert!((1..=64).contains(&concurrent_id));
lock.release_concurrent();
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

A limit below 1 returns `InvalidMaxConcurrent`. Blocking acquisition returns `CapacityExceeded` if the internal 31-bit holder capacity is exceeded; Try-style acquisition reports `None`, matching the C# Try contract. The capacity boundary is documented but effectively unreachable in normal workloads.

### Exclusive

```rust
lock.acquire_exclusive();

// Isolated business region.

lock.release_exclusive();
```

Exclusive permission is thread-affine. Acquisition, release, or downgrade must occur on the same thread.

### TryConcurrent

```rust
if let Some(id) = lock.try_acquire_concurrent()? {
    lock.release_concurrent();
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

Timed form:

```rust
use std::time::Duration;

if let Some(id) = lock.try_acquire_concurrent_for(Duration::from_millis(100))? {
    lock.release_concurrent();
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

### TryExclusive

Preemptive Try:

```rust
if lock.try_acquire_exclusive(true) {
    lock.release_exclusive();
}
```

`true` does not mean “never waits.” It permits entry into preemptive Exclusive contention: if no other Exclusive pressure is initially observed, the request may block new Concurrent entrants and wait for existing holders. If an in-place upgrade appears, the ordinary request may yield and return `false`.

Idle-only immediate attempt:

```rust
if lock.try_acquire_exclusive(false) {
    lock.release_exclusive();
}
```

Timed preemptive attempt:

```rust
if lock.try_acquire_exclusive_for(Duration::from_millis(100)) {
    lock.release_exclusive();
}
```

`Duration::ZERO` is an immediate Idle-only attempt.

---

## In-place upgrade and downgrade

### Concurrent → Exclusive

```rust
lock.acquire_concurrent()?;

// Concurrent phase.

lock.concurrent_to_exclusive();

// Continuous Exclusive phase; no release/reacquire gap.

lock.release_exclusive();
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

After conversion, the original Concurrent permission has become Exclusive and must not be released as Concurrent.

### Exclusive → Concurrent

```rust
lock.acquire_exclusive();

// Exclusive phase.

lock.exclusive_to_concurrent();

// The caller now retains Concurrent permission.

lock.release_concurrent();
```

After downgrade, do not call `release_exclusive()`.

---

## ContextID and EpochID

ContextID and EpochID are business state outside the lock protocol:

- `ContextID` identifies the current business context;
- `EpochID` represents a monotonically advancing lifecycle, version, or phase.

Their allocation, meaning, validation, and cleanup remain the caller's responsibility.

### ContextID

```rust
lock.set_context_id(10);
assert_eq!(lock.context_id(), 10);
assert!(lock.switch_context_id(11));
assert!(!lock.switch_context_id(11));
```

### EpochID

```rust
assert!(lock.raise_epoch_id(1));
assert!(lock.raise_epoch_id(2));
assert!(!lock.raise_epoch_id(2));
assert!(!lock.raise_epoch_id(1));
```

`set_epoch_id` is unconditional and may reset or roll back the value. Use `raise_epoch_id` when monotonic advancement is required.

### Conditional upgrade

```rust
lock.acquire_concurrent()?;

if lock.try_concurrent_to_exclusive_with_switch_context_id(100) {
    // Context changed and Exclusive is held.
    lock.release_exclusive();
} else {
    // The original Concurrent permission was released automatically.
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

Epoch variant:

```rust
lock.acquire_concurrent()?;

if lock.try_concurrent_to_exclusive_with_raise_epoch_id(20) {
    lock.release_exclusive();
} else {
    // The original Concurrent permission was released automatically.
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

On conditional-upgrade failure, calling `release_concurrent()` again is an error because the method already removed the caller's original Concurrent permission.

---

## Scope: RAII release management

[`ConcurrentExclusiveLockScope`](crates/concurrent-exclusive-lock/src/scope.rs) is the Rust RAII layer corresponding to C# `IDisposable` and Java `AutoCloseable` convenience wrappers.

```rust
use concurrent_exclusive_lock::{
    ConcurrentExclusiveLock,
    ConcurrentExclusiveLockScope,
};

let lock = ConcurrentExclusiveLock::new();

{
    let mut scope = ConcurrentExclusiveLockScope::new(&lock);
    scope.acquire_concurrent()?;
    // Early return or panic unwinding drops the scope and releases permission.
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

Upgrade:

```rust
{
    let mut scope = ConcurrentExclusiveLockScope::new(&lock);
    scope.acquire_concurrent()?;
    scope.concurrent_to_exclusive();
    // Drop releases the final Exclusive state.
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

Downgrade:

```rust
{
    let mut scope = ConcurrentExclusiveLockScope::new(&lock);
    scope.acquire_exclusive();
    scope.exclusive_to_concurrent();
    // Drop releases the final Concurrent state.
}
```

Scope boundaries:

- construction does not acquire permission;
- explicit release updates Scope state so `Drop` does not release twice;
- `Drop` releases only the final permission, not ContextID or EpochID;
- Scope is neither `Send` nor `Sync`;
- Scope is not a core ownership token; direct lock methods remain available;
- `std::mem::forget(scope)` intentionally prevents `Drop` and leaks permission, which is caller misuse.

---

## Pipeline

Pipeline executes synchronous Segments and automatically uses the last successful permission to release, reacquire, continue, upgrade, downgrade, or apply a business-ID condition.

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
        // Independent Concurrent segment.
    }),
    ConcurrentExclusiveLockSegment::try_apply_id_converge_exclusive(
        || {
            // Runs only when EpochID advances and Exclusive is held.
        },
        10,
        IDType::EpochID,
    ),
    ConcurrentExclusiveLockSegment::converge_concurrent(|| {
        // Continue/acquire Concurrent or downgrade from Exclusive.
    }),
    ConcurrentExclusiveLockSegment::none(|| {
        // No permission.
    }),
];

pipeline.do_pipeline(&mut segments)?;
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

### Segment semantics

| Factory | Semantics |
|---|---|
| `none` | Release retained permission and run without permission |
| `concurrent` | Release retained permission and acquire an independent Concurrent segment |
| `try_concurrent` | Release retained permission, try Concurrent once, skip on failure and continue from None |
| `exclusive` | Release retained permission and acquire an independent Exclusive segment |
| `test_exclusive` | Release retained permission and make an Idle-only Exclusive attempt |
| `try_exclusive` | Release retained permission and attempt preemptive Exclusive; an upgrade may cause failure |
| `converge_concurrent` | Continue Concurrent, downgrade Exclusive, or acquire Concurrent from None |
| `converge_exclusive` | Continue Exclusive, upgrade Concurrent, or acquire Exclusive from None |
| `try_apply_id_converge_exclusive` | Converge to Exclusive only when ContextID/EpochID application succeeds |

A Try-type failure means:

```text
skip the current Segment
→ current permission becomes None
→ no acquisition-failure exception
→ continue later Segments
```

The Try in `try_apply_id_converge_exclusive` applies to the business-ID result, not to an absolute no-wait promise. From Concurrent state it still participates in upgrade coordination.

### Panic propagation

A panic from a Segment callback stops later Segments and propagates to the caller. During normal unwinding, the Pipeline's internal Scope is dropped and releases retained permission.

With `panic = "abort"`, Rust performs no stack unwinding and no RAII cleanup. This workspace keeps `panic = "unwind"` for Release builds.

### Synchronous boundary

Segments are synchronous `FnMut()` callbacks. All protected work must finish before the callback returns.

Detached work is not protected after callback return:

```rust
ConcurrentExclusiveLockSegment::exclusive(|| {
    std::thread::spawn(|| {
        // The Pipeline may already have released or converted permission.
    });
});
```

To run a complete synchronous Pipeline from an async application, schedule the entire call on the runtime's blocking facility, such as Tokio `spawn_blocking`. The core crate intentionally has no async-runtime dependency.

---

## Observational snapshots

```rust
let state = lock.observed_state();
let contention = lock.observed_contention();
```

Snapshots are for diagnostics, logging, monitoring, or scheduling hints. They are not synchronization predicates. State may change immediately after observation.

A preemptive Exclusive request can make `observed_state()` report `Exclusive` while the requester is still waiting for existing Concurrent holders to leave.

---

## Non-recursion and thread rules

The lock does not provide recursive permission:

- do not call ordinary `acquire_exclusive()` while holding Concurrent;
- use the upgrade API instead;
- do not call ordinary `acquire_concurrent()` while holding Exclusive;
- use the downgrade API instead;
- acquire, release, and downgrade Exclusive on the same thread;
- do not move an Exclusive-dependent flow across a thread-migrating await;
- do not destroy a lock still accessed by another thread.

Rust lifetimes prevent some object-lifetime errors, but the direct lock API remains a permission protocol. It does not dynamically track what each thread holds. Double release, wrong-mode release, illegal nesting, and cross-thread release remain caller errors.

Scope reduces cross-thread misuse by being `!Send` and `!Sync`, while the direct core API preserves the low-overhead C#/Java-style surface.

---

## Tests

Cargo tests:

```powershell
cargo test --release --workspace
```

Full semantic regression:

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --full-semantics `
  --lock-instances 8 `
  --semantic-workers 4 `
  --semantic-operations 256
```

Deterministic Pipeline semantics:

```powershell
cargo run --release -p cel-test-and-benchmark -- --pipeline-semantics
```

Randomized Pipeline stress:

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 8 `
  --semantic-seed 0x12345678
```

Exclusive contention progress:

```powershell
cargo run --release -p cel-test-and-benchmark -- `
  --contention-stress 30s `
  --semantic-workers 16
```

See [`TESTING.md`](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/rust/TESTING.md) for details.

---

## Performance comparison

The included benchmark uses the random shared-memory Work from the C# project and compares:

- `std::sync::Mutex`;
- `std::sync::RwLock`;
- `ConcurrentExclusiveLock`;
- `CEL(ExclusiveOnly)`.

Scenarios:

```text
100/0
99.5/0.5
90/10
50/50
30/70
0/100
```

Use meaningful work inside the critical region for primary evaluation. Extremely short regions mostly measure synchronization overhead and cannot fully demonstrate the business value of Concurrent parallelism and preemptive Exclusive acquisition.

Single hot lock:

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

Many entity locks:

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

Key metrics:

- `works/s`: total throughput;
- `works/s/lock`: throughput per independent entity lock;
- `avg write ns`: average complete Exclusive request, wait, work, and release time;
- `state`: final business state, which must match across strategies.

Draw conclusions from repeated Release runs on the target machine. Do not compare raw numbers across different language runtimes as though they were the same benchmark environment.

See [`PERFORMANCE.md`](https://github.com/WangHHB/ConcurrentExclusiveLock/blob/main/rust/PERFORMANCE.md).

---

## Platforms and memory model

Core state uses:

```rust
AtomicI64  // combined Concurrent / Exclusive counter
AtomicI32  // ContextID
AtomicI32  // EpochID
```

Protocol transitions use `SeqCst`; observational reads use Acquire; unconditional business-ID stores use Release. The first port favors correspondence with C# `Interlocked` / `Volatile` semantics over aggressive memory-order weakening.

The internal monitor uses only Rust standard-library synchronization. The standard library maps its `Mutex` and `Condvar` to the target operating system on Windows, Linux, macOS, Android, iOS, and other supported targets.

Targets without 64-bit atomics, `no_std` bare-metal environments, and systems without blocking thread scheduling are outside the current scope.

---

## Not guaranteed

The crate does not guarantee:

- strict FIFO;
- equal acquisition counts per thread;
- recursive entry;
- deadlock detection;
- automatic detection of wrong releases;
- transfer of Exclusive permission between threads;
- async-aware lock holding;
- identical performance on all platforms;
- continued validity of an observational snapshot.

Scheduling remains affected by the operating system, Rust standard-library implementation, CPU topology, caches, NUMA, system load, and caller business-region duration.

---

## Suitable workloads

Particularly suitable for:

- entity-level locks for players, rooms, orders, or cache entries;
- read-dominant state where writes must occur promptly;
- flows requiring continuous Concurrent → Exclusive conversion;
- flows requiring continuous Exclusive → Concurrent downgrade;
- systems that can use ContextID / EpochID to avoid redundant upgrades;
- synchronous business flows expressed clearly with Pipeline segments.

Not intended for:

- protocols requiring strict fair queues;
- critical regions spanning arbitrary thread-migrating async work;
- code that cannot enforce non-recursive use;
- workloads needing only simple mutual exclusion;
- `no_std` bare-metal targets.

---

## License

Dual licensed under:

```text
MIT OR Apache-2.0
```

See [`LICENSE-MIT`](LICENSE-MIT) and [`LICENSE-APACHE-2.0`](LICENSE-APACHE-2.0).
