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
3. Concurrent remains a direct protocol; Rust introduces `ExclusiveGuard` only because the real standard `MutexGuard` must live across the Exclusive business region.
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
├─ vendor/                          # offline parking_lot benchmark dependencies
├─ TestBenchmarkResults/           # raw logs and CSV/JSON
├─ Artifacts/                       # prebuilt executables
├─ Cargo.toml                       # Cargo workspace
├─ README.md
├─ README_CN.md
├─ TESTING.md
├─ TESTING_CN.md
├─ PERFORMANCE.md
├─ PERFORMANCE_CN.md
├─ VERIFICATION.md
├─ THIRD_PARTY_NOTICES.md             # Vendored dependency versions and licenses
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

The C# reference `Monitor` is mapped directly to Rust's standard
`std::sync::Mutex<()>`. Rust releases a mutex by dropping its `MutexGuard`, so
Exclusive acquisition returns an `ExclusiveGuard` that retains the real standard
mutex guard across the Exclusive business region. No `RawMonitor`, held-state
atomics, waiter counters, `Condvar`, or custom fairness protocol is added.

The unavoidable Rust API adaptation is therefore:

- `acquire_exclusive()` returns `ExclusiveGuard`;
- explicit release consumes that guard;
- downgrade consumes that guard and retains Concurrent permission;
- dropping the guard releases Exclusive permission safely.

The counter state machine, upgrade priority, branch order, and release order remain
aligned with the C# reference implementation.

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
cargo build --release --workspace --offline
cargo test --release --workspace --offline
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

This release package also includes the Linux x64 executable built and verified in the test environment:

```text
Artifacts/linux-x64/cel-test-and-benchmark
```

It also includes the core crate archive verified by `cargo package`:

```text
Artifacts/crate/concurrent-exclusive-lock-1.0.0.crate
```

Running `build-windows.ps1` on Windows copies the generated executable to:

```text
Artifacts\windows-x64\cel-test-and-benchmark.exe
```

The core library crate has no third-party dependencies. Vendored benchmark dependency versions and licenses are listed in [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). The benchmark uses `parking_lot 0.12.5`; its source and required dependencies are included under `vendor/`, so the complete workspace still builds with an empty Cargo registry cache by using `--offline`.

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
let guard = lock.acquire_exclusive();

// Isolated business region.

lock.release_exclusive(guard);
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
if let Some(guard) = lock.try_acquire_exclusive(true) {
    lock.release_exclusive(guard);
}
```

`true` does not mean “never waits.” It permits entry into preemptive Exclusive contention: if no other Exclusive pressure is initially observed, the request may block new Concurrent entrants and wait for existing holders. If an in-place upgrade appears, the ordinary request may yield and return `false`.

Idle-only immediate attempt:

```rust
if let Some(guard) = lock.try_acquire_exclusive(false) {
    lock.release_exclusive(guard);
}
```

Timed preemptive attempt:

```rust
if let Some(guard) = lock.try_acquire_exclusive_for(Duration::from_millis(100)) {
    lock.release_exclusive(guard);
}
```

`Duration::ZERO` is an immediate Idle-only attempt.

---

## In-place upgrade and downgrade

### Concurrent → Exclusive

```rust
lock.acquire_concurrent()?;

// Concurrent phase.

let guard = lock.concurrent_to_exclusive();

// Continuous Exclusive phase; no release/reacquire gap.

lock.release_exclusive(guard);
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

After conversion, the original Concurrent permission has become Exclusive and must not be released as Concurrent.

### Exclusive → Concurrent

```rust
let guard = lock.acquire_exclusive();

// Exclusive phase.

lock.exclusive_to_concurrent(guard);

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

if let Some(guard) = lock.try_concurrent_to_exclusive_with_switch_context_id(100) {
    // Context changed and Exclusive is held.
    lock.release_exclusive(guard);
} else {
    // The original Concurrent permission was released automatically.
}
# Ok::<(), concurrent_exclusive_lock::ConcurrentExclusiveLockError>(())
```

Epoch variant:

```rust
lock.acquire_concurrent()?;

if let Some(guard) = lock.try_concurrent_to_exclusive_with_raise_epoch_id(20) {
    lock.release_exclusive(guard);
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
cargo test --release --workspace --offline
```

Full semantic regression:

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- `
  --full-semantics `
  --lock-instances 8 `
  --semantic-workers 4 `
  --semantic-operations 256
```

Deterministic Pipeline semantics:

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- --pipeline-semantics
```

Randomized Pipeline stress:

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 8 `
  --semantic-seed 0x12345678
```

Exclusive contention progress:

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- `
  --contention-stress 30s `
  --semantic-workers 16
```

Endurance test:

```powershell
cargo run --release --offline -p cel-test-and-benchmark -- `
  --endurance 24h `
  --lock-instances 8 `
  --semantic-workers 8
```

The formal 30-minute Pipeline stress completed `2,732,232,429` rounds and `14,775,380,351` validated callbacks. The 60-second Exclusive contention run completed `401,719,852` acquisitions with progress from all 32 workers.

See [`TESTING.md`](TESTING.md) for details.

---

## Performance evaluation summary

### Scope and comparability

The benchmark compares six strategies:

- `std::sync::Mutex`;
- `std::sync::RwLock`;
- `parking_lot::Mutex` 0.12.5;
- `parking_lot::RwLock` 0.12.5;
- CEL;
- `CEL(ExclusiveOnly)`, where every operation uses CEL Exclusive as a mutual-exclusion baseline.

Every strategy receives fresh lock and `MemoryWork` instances for every scenario, with the same deterministic random seed, read/write decisions, shared-memory size, and work count. Read/write totals and final state hashes are compared after each scenario; any mismatch fails the benchmark.

The formal data set covers 1, 4, 16, and 64 threads; single-lock and multi-lock layouts; work sizes 1, 64, and 256; and six read/write ratios. It contains 10 configurations and 360 strategy rows. The main 16-thread configuration was repeated three complete times and reports the median; the extended configurations are single runs.

The environment was Rust 1.75.0 on Linux 6.12 x86_64 in KVM, with about four processors reported by `available_parallelism()`. The 16- and 64-thread cases are oversubscribed. These numbers compare relative behavior in the same constrained environment; they are not fixed rankings for Windows, bare metal, NUMA servers, or other Rust versions.

### Complete formal benchmark matrix

All README numbers come directly from `TestBenchmarkResults/final/benchmarks/`; they are not hand-picked samples. The formal run contained these 10 configurations:

| configuration | locks | threads/lock | total threads | operations/thread | memory/lock | work | runs | purpose |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| `single_1t_w64` | 1 | 1 | 1 | 100,000 | 64 MiB | 64 | 1 | uncontended baseline |
| `single_4t_w64` | 1 | 4 | 4 | 30,000 | 64 MiB | 64 | 1 | near available CPU count |
| `single_16t_w64_r1` | 1 | 16 | 16 | 10,000 | 64 MiB | 64 | run 1 | repeated main configuration |
| `single_16t_w64_r2` | 1 | 16 | 16 | 10,000 | 64 MiB | 64 | run 2 | repeated main configuration |
| `single_16t_w64_r3` | 1 | 16 | 16 | 10,000 | 64 MiB | 64 | run 3 | repeated main configuration |
| `single_64t_w64` | 1 | 64 | 64 | 3,000 | 64 MiB | 64 | 1 | high contention / oversubscription |
| `single_16t_w1` | 1 | 16 | 16 | 50,000 | 64 MiB | 1 | 1 | very short critical region |
| `single_16t_w256` | 1 | 16 | 16 | 3,000 | 64 MiB | 256 | 1 | longer critical region |
| `multi_8x4_w64` | 8 | 4 | 32 | 5,000 | 16 MiB | 64 | 1 | medium multi-lock layout |
| `multi_64x2_w64` | 64 | 2 | 128 | 2,000 | 4 MiB | 64 | 1 | many independent locks / scheduler pressure |

Each configuration ran all six read/write ratios and all six strategies, for `10 × 6 × 6 = 360` raw result rows. CSV, JSON, per-run logs, scripts, and generated summaries are retained under:

```text
TestBenchmarkResults/final/benchmarks/
```

### Uncontended baseline: one lock, one thread, work=64

With one thread there is no lock contention, so the results mostly reflect fixed API, guard, atomic, and work costs (`works/s`):

| read/write | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 618,810 | 612,489 | **636,147** | 579,282 | 594,852 | 588,689 |
| 99.5/0.5 | 567,708 | 572,140 | 575,756 | 557,009 | **583,535** | 568,501 |
| 90/10 | **591,151** | 571,345 | 581,760 | 583,009 | 566,104 | 570,733 |
| 50/50 | 518,260 | **538,132** | 501,885 | 533,362 | 512,943 | 478,164 |
| 30/70 | 503,101 | 503,414 | **506,711** | 503,773 | 494,638 | 482,750 |
| 0/100 | 481,560 | 474,150 | 485,258 | 470,524 | **490,807** | 467,799 |

There is no stable winner in this baseline. It shows that CEL has no abnormal uncontended fixed cost, but uncontended data alone does not establish a concurrency advantage.

### Near machine concurrency: one lock, four threads, work=64

The VM exposes about four available CPUs, so this configuration is closer to actual hardware concurrency than the 16- and 64-thread runs:

| read/write | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 372,767 | **1,787,040** | 428,539 | 1,651,290 | 1,749,075 | 356,814 |
| 99.5/0.5 | 400,332 | 1,179,255 | 330,386 | 1,484,585 | **2,222,444** | 356,100 |
| 90/10 | 430,552 | 442,452 | 426,983 | 686,100 | **918,912** | 408,557 |
| 50/50 | 383,622 | 336,925 | 362,777 | 338,245 | **470,486** | 364,260 |
| 30/70 | 368,546 | 334,918 | 371,582 | 281,488 | **460,153** | 351,083 |
| 0/100 | 326,740 | 276,987 | **344,459** | 305,641 | 331,291 | 338,942 |

The standard RwLock narrowly led pure reads; CEL led the mixed ratios from 99.5/0.5 through 30/70; pure writes again favored the simpler mutex-class strategies.

### Main result: one lock, 16 threads, 64 MiB, work=64

Median throughput from three complete runs (`works/s`):

| read/write | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 555,908 | 2,483,393 | 256,295 | 2,539,576 | **2,695,156** | 508,805 |
| 99.5/0.5 | 519,450 | **1,575,629** | 257,357 | 1,355,039 | 1,491,291 | 519,608 |
| 90/10 | 540,791 | 379,354 | 255,487 | 463,697 | **713,582** | 524,334 |
| 50/50 | **494,775** | 312,405 | 235,382 | 221,611 | 470,606 | 475,282 |
| 30/70 | **467,314** | 409,725 | 239,319 | 214,644 | 440,282 | 465,727 |
| 0/100 | **466,230** | 460,028 | 242,002 | 234,290 | 456,459 | 453,863 |

Objective observations:

- At 100/0, CEL was about 8.5% above the standard RwLock and 6.1% above parking_lot RwLock; all three remained in the same broad performance class.
- At 99.5/0.5, the standard RwLock was fastest. CEL was about 5.4% lower, while remaining about 10.1% above parking_lot RwLock in this environment.
- At 90/10, CEL showed its clearest advantage: about 88% above the standard RwLock and 54% above parking_lot RwLock.
- At 50/50, the standard Mutex was about 5% faster than CEL, while CEL remained well above both RwLocks.
- At 30/70 and 0/100, CEL, the standard Mutex, and the standard RwLock converged into a similar range. CEL did not dominate write-heavy workloads.
- parking_lot remained competitive for pure reads but was slower under write-heavy single-lock contention on this particular Rust 1.75/Linux/KVM/oversubscribed setup. That is an environment-specific result, not a universal conclusion about parking_lot.

### Three-run range for the 16-thread main configuration

The table reports `minimum / median / maximum` for the three primary Concurrent/Exclusive strategies (`works/s`):

| read/write | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 100/0 | 2,134,108 / 2,483,393 / 2,858,710 | 1,938,258 / 2,539,576 / 3,263,309 | 2,625,290 / 2,695,156 / 3,338,169 |
| 99.5/0.5 | 1,524,671 / 1,575,629 / 1,586,392 | 1,312,349 / 1,355,039 / 1,355,498 | 1,322,869 / 1,491,291 / 1,748,381 |
| 90/10 | 357,860 / 379,354 / 441,569 | 460,819 / 463,697 / 473,767 | 621,665 / 713,582 / 814,223 |
| 50/50 | 301,136 / 312,405 / 313,910 | 221,002 / 221,611 / 255,398 | 426,447 / 470,606 / 475,392 |
| 30/70 | 388,299 / 409,725 / 425,057 | 203,509 / 214,644 / 229,394 | 419,337 / 440,282 / 458,694 |
| 0/100 | 442,967 / 460,028 / 473,773 | 232,631 / 234,290 / 234,989 | 445,817 / 456,459 / 481,919 |

The repeated runs are not noise-free, especially for pure reads and some CEL mixed ratios under VM scheduling. The README therefore reports medians rather than selecting the best run. All three raw outputs remain in `TestBenchmarkResults/final/benchmarks/single_16t_w64_r*.log`.

### 64-thread contention

With 64 threads on about four available CPUs, this is mainly an oversubscription and wake-up behavior test:

| read/write | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 100/0 | 2,655,356 | 2,452,647 | **5,329,420** |
| 99.5/0.5 | **1,116,340** | 808,276 | 1,060,577 |
| 90/10 | 262,941 | 284,170 | **654,907** |
| 50/50 | 206,418 | 201,012 | **459,576** |
| 30/70 | 439,304 | 199,050 | **455,274** |
| 0/100 | **452,001** | 229,147 | 440,729 |

CEL was strong at pure reads and 90/10, while the standard RwLock narrowly led at 99.5/0.5 and pure writes. Because the thread count greatly exceeds the available CPUs, scheduler and parking behavior are part of these results; they should not be treated as data from a physical 64-core machine.

`avg write ns` is end-to-end latency from before acquisition through work and release, including queueing and scheduling. Representative 64-thread values:

| read/write | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 99.5/0.5 | 276,617 ns | 393,168 ns | **97,037 ns** |
| 90/10 | 105,000 ns | 247,545 ns | **71,791 ns** |
| 50/50 | 344,689 ns | 297,299 ns | **104,858 ns** |
| 0/100 | 97,755 ns | 272,694 ns | **94,372 ns** |

### Critical-region length

The relative result changes as work grows:

| work | scenario | std RwLock | parking RwLock | CEL | CEL / std RwLock |
|---:|---:|---:|---:|---:|---:|
| 1 | 100/0 | 21,061,252 | 21,405,547 | **31,136,259** | 1.48x |
| 1 | 90/10 | 4,834,923 | 6,538,139 | **7,861,429** | 1.63x |
| 1 | 0/100 | **3,092,052** | 1,430,813 | 2,582,370 | 0.84x |
| 64 | 100/0 | 2,483,393 | 2,539,576 | **2,695,156** | 1.09x |
| 64 | 90/10 | 379,354 | 463,697 | **713,582** | 1.88x |
| 64 | 0/100 | **460,028** | 234,290 | 456,459 | 0.99x |
| 256 | 100/0 | **1,183,342** | 649,555 | 828,225 | 0.70x |
| 256 | 90/10 | **191,707** | 147,245 | 164,580 | 0.86x |
| 256 | 0/100 | 112,913 | 73,625 | **120,843** | 1.07x |

The work=64 rows use the three-run median from the main configuration; work=1 and work=256 are single-run extended configurations. This table is intended to show trends rather than claim exact cross-configuration ratios.

Short regions expose synchronization and cache-line costs. As the business work grows, lock differences become a smaller fraction of total time. In the work=256 pure-read and 90/10 cases, the standard RwLock outperformed CEL. Pure writes increasingly become a mutual-exclusion comparison, where CEL's Concurrent design offers no inherent advantage.

### Multi-lock: 8 locks, 4 threads per lock

With eight independent locks and four threads per lock, the ranking was mixed:

| read/write | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 100/0 | 2,864,727 | **3,042,378** | 2,802,405 |
| 99.5/0.5 | **6,261,619** | 2,289,339 | 1,970,930 |
| 90/10 | **2,263,285** | 1,691,974 | 2,193,316 |
| 50/50 | **2,095,450** | 1,632,556 | 1,750,895 |
| 30/70 | 2,049,541 | 1,903,300 | **2,564,973** |
| 0/100 | 1,714,731 | 1,879,581 | **2,012,275** |

This configuration does not support a claim that one lock wins every multi-lock workload. Independent lock progress, memory bandwidth, scheduling, and run duration all influence total throughput.

### 64 locks with two threads per lock

This configuration creates 128 dedicated threads on about four available CPUs, with only 256,000 total operations per strategy/scenario. It is primarily a progress and state-consistency test for many independent locks, not a precise ranking. The three Concurrent/Exclusive strategy throughputs are still reported as measured:

| read/write | std RwLock | parking RwLock | CEL |
|---:|---:|---:|---:|
| 100/0 | **3,035,962** | 2,817,826 | 2,815,580 |
| 99.5/0.5 | 2,773,926 | 3,250,122 | **11,259,869** |
| 90/10 | 3,197,306 | 2,551,568 | **4,544,870** |
| 50/50 | 3,112,502 | 4,042,819 | **8,874,252** |
| 30/70 | 2,882,352 | **7,186,587** | 5,889,522 |
| 0/100 | 2,560,876 | **10,973,384** | 2,082,153 |

The ranking changes sharply across ratios, which is consistent with a short run under 128-thread oversubscription. What can be asserted is that all six strategies completed the same read/write counts and produced the same final state hashes. These values do not establish stable order-of-magnitude advantages.

### Correctness and sustained stress validation

In addition to the performance runs, this release completed the following validation chain:

| Validation | Result |
|---|---|
| `cargo fmt --check` | PASS |
| `cargo clippy --workspace --all-targets -- -D warnings` | PASS |
| Release workspace build | PASS |
| All Release Cargo tests | PASS |
| Full semantic regression | PASS |
| Deterministic Pipeline semantics | PASS |
| Empty Cargo cache, fully offline rebuild | PASS |
| 60-second Exclusive contention | `401,719,852` acquisitions; all 32 workers made progress |
| 60-second Endurance | 58 deterministic batches |
| 30-minute Pipeline stress | `2,732,232,429` rounds; `14,775,380,351` validated callbacks |

After the 30-minute Pipeline run, both the lock and the independent access validator returned to Idle. Raw logs are stored at:

```text
TestBenchmarkResults/final/pipeline-stress-30m.log
TestBenchmarkResults/final/contention-stress-60s.log
TestBenchmarkResults/final/endurance-60s.log
TestBenchmarkResults/final/full-semantics.log
TestBenchmarkResults/final/pipeline-semantics.log
```

### Overall interpretation

The cautious conclusions from this environment are:

- CEL's strongest region was a mixed workload where reads should remain lightweight while pending writes should stop new readers; 90/10 was the clearest example.
- CEL was competitive for pure reads but did not always beat mature RwLocks. The standard RwLock won some longer-work configurations.
- CEL does not inherently beat a Mutex in write-heavy or pure-write workloads. As the write ratio grows, a simpler Mutex can be equally fast or faster.
- parking_lot is an essential high-performance baseline, but its local result must not be generalized across platforms.
- Throughput and write latency must be considered together.
- CEL also provides in-place upgrade/downgrade, ContextID/EpochID, and Pipeline semantics that ordinary RwLocks do not directly provide. Performance tables measure cost; they do not replace functional selection criteria.

The complete 360-row data set and raw results are available in [`PERFORMANCE.md`](PERFORMANCE.md) and:

```text
TestBenchmarkResults/final/benchmarks/all_results.csv
TestBenchmarkResults/final/benchmarks/all_results.json
TestBenchmarkResults/final/benchmarks/
```

---

## Platforms and memory model

Core state uses:

```rust
AtomicI64  // combined Concurrent / Exclusive counter
AtomicI32  // ContextID
AtomicI32  // EpochID
```

Protocol transitions use `SeqCst`; observational reads use Acquire; unconditional business-ID stores use Release. The first port favors correspondence with C# `Interlocked` / `Volatile` semantics over aggressive memory-order weakening.

The internal monitor is the Rust standard-library `Mutex<()>`; the standard library maps it to the target operating system on Windows, Linux, macOS, Android, iOS, and other supported targets.

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
