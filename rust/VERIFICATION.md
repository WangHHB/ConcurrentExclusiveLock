# Verification Status

## Implementation review

The Rust port was produced against the complete C# reference project and follows the same protocol structure:

- low 32 counter bits: Concurrent holder count;
- high 32 counter bits: Exclusive/upgrade pressure;
- preemptive ordinary Exclusive;
- upgrade priority over ordinary Exclusive;
- in-place downgrade behavior;
- ContextID / EpochID conditional convergence;
- conditional-upgrade failure automatically releases the original Concurrent permission;
- Scope final-state release;
- synchronous Pipeline transition table and panic-path cleanup;
- no ownership token in the direct core API;
- no ticket queue or strict FIFO guarantee.

The core crate contains no `unsafe` code. The benchmark uses a narrowly scoped `UnsafeCell` adapter so CEL can protect a shared `MemoryWork`; that unsafe code is outside the library crate.

## Required local verification

This package must be verified with an installed Rust toolchain before publication:

```powershell
.\build.ps1
.\run-tests.ps1
```

The scripts perform or expose the following checks:

```text
cargo fmt --all
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
cargo test --release --workspace
full semantic regression
Pipeline semantic regression
short randomized Pipeline stress
```

For release validation also run:

```powershell
cargo run --release -p cel-test-and-benchmark -- --pipeline-stress 24h --lock-instances 8 --semantic-workers 8
cargo run --release -p cel-test-and-benchmark -- --contention-stress 60s --semantic-workers 32
```

and execute the suite on Windows, Linux, and macOS.

## Sandbox limitation

The generation environment did not contain `rustc` or `cargo`, and external toolchain installation was unavailable. Therefore this archive has received static source/protocol review, but no claim is made here that it was compiled or executed inside that environment. The first authoritative verification is the output of the included local build and test scripts.
