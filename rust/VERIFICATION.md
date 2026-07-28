# Verification status

## Toolchain and platform

```text
rustc 1.75.0
cargo 1.75.0
Linux 6.12.13 x86_64 under KVM
AMD EPYC 9V74
available_parallelism() reported by Rust: 4
```

## Source and build checks

Passed:

```text
cargo fmt --all -- --check
cargo clippy --workspace --all-targets --offline --no-deps -- -D warnings
cargo check --workspace --offline
cargo build --release --workspace --offline
cargo test --release --workspace --offline
```

The core library has `#![forbid(unsafe_code)]`. The benchmark uses one narrowly scoped `UnsafeCell` adapter to expose the same mutable `MemoryWork` under CEL Exclusive protection.

## Semantic validation

Passed:

- integration tests;
- full deterministic and randomized semantic regression;
- deterministic Pipeline regression;
- 30-second randomized Pipeline smoke stress;
- formal 30-minute randomized Pipeline stress: `2,732,232,429` rounds and `14,775,380,351` validated callbacks;
- 60-second Exclusive contention/progress stress: 32 workers and `401,719,852` acquisitions, with progress from every worker;
- 60-second Endurance run: 58 deterministic batches;
- final Idle-state checks after every semantic/stress mode.

Authoritative logs are under `TestResults/final/`.

## Benchmark validation

Ten complete benchmark configurations were executed, covering:

- 1, 4, 16, and 64 threads on one lock;
- three repeated 16-thread main runs;
- work sizes 1, 64, and 256;
- 8 locks × 4 threads;
- 64 locks × 2 threads;
- all six read/write ratios;
- `std::sync::Mutex`, `std::sync::RwLock`, `parking_lot::Mutex`, `parking_lot::RwLock`, CEL, and CEL(ExclusiveOnly).

Every strategy/scenario completed with the same final state hash. Raw logs, CSV, JSON, and the repeated-run median table are under `TestResults/final/benchmarks/`.

## Offline dependency validation

The core crate remains dependency-free. The benchmark's `parking_lot 0.12.5` dependency and required crates are included under `vendor/`. A clean Cargo lockfile was generated with path sources, and the complete workspace builds with `--offline`, including a fresh Release build with an empty `CARGO_HOME`.

## Windows executable status

The source tree includes Windows support, `windows-link`, offline dependencies, and `build-windows.ps1`. The Linux validation container does not contain:

```text
Rust target: x86_64-pc-windows-gnu
MinGW linker: x86_64-w64-mingw32-gcc
```

The attempted cross-check fails before compiling project code because the Windows Rust standard library (`core`, `alloc`, and `compiler_builtins`) is not installed. The exact output is retained in `TestResults/final/windows-cross-check.log`.

Therefore this package includes a verified Linux x64 executable. A Windows `.exe` must be produced by running `build-windows.ps1` on Windows or by installing the missing cross target and linker.
