# Rust performance benchmark and measured results

## Compared strategies

- `std::sync::Mutex`
- `std::sync::RwLock`
- `parking_lot::Mutex` 0.12.5
- `parking_lot::RwLock` 0.12.5
- CEL
- CEL(ExclusiveOnly)

All strategies receive fresh lock/work instances, the same deterministic read/write sequence, the same shared-memory workload, and a final state-hash equality check. `avg write ns` is end-to-end write-request latency including waiting, scheduling, work, and release.

The `parking_lot` source and required dependencies are stored in `parking_lot-vendor.zip`; the supplied scripts extract `vendor/` on demand, so the benchmark builds offline.

## Environment

```text
Rust 1.75.0
Linux 6.12.13 x86_64 under KVM
AMD EPYC 9V74
available_parallelism() = 4
Release: opt-level=3, thin LTO, codegen-units=1
```

## Main result: one lock, 16 threads, 64 MiB, work=64

Three complete runs were executed. Median throughput (`works/s`):

| read/write | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 555,908 | 2,483,393 | 256,295 | 2,539,576 | 2,695,156 | 508,805 |
| 99.5/0.5 | 519,450 | 1,575,629 | 257,357 | 1,355,039 | 1,491,291 | 519,608 |
| 90/10 | 540,791 | 379,354 | 255,487 | 463,697 | 713,582 | 524,334 |
| 50/50 | 494,775 | 312,405 | 235,382 | 221,611 | 470,606 | 475,282 |
| 30/70 | 467,314 | 409,725 | 239,319 | 214,644 | 440,282 | 465,727 |
| 0/100 | 466,230 | 460,028 | 242,002 | 234,290 | 456,459 | 453,863 |

CEL was close to both RwLocks for pure reads, substantially ahead at 90/10 in this environment, close to the standard Mutex at high write ratios, and close to the standard Mutex/RwLock for 100% writes. `parking_lot` was slower under high-write single-lock oversubscription on this particular Linux VM; this is not a universal ranking.

## 64-thread oversubscribed single lock

| read/write | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |
|---:|---:|---:|---:|---:|---:|---:|
| 100/0 | 564,279 | 2,655,356 | 246,253 | 2,452,647 | 5,329,420 | 574,821 |
| 99.5/0.5 | 588,392 | 1,116,340 | 235,024 | 808,276 | 1,060,577 | 542,371 |
| 90/10 | 512,326 | 262,941 | 204,392 | 284,170 | 654,907 | 525,457 |
| 50/50 | 474,506 | 206,418 | 219,646 | 201,012 | 459,576 | 463,750 |
| 30/70 | 463,907 | 439,304 | 246,953 | 199,050 | 455,274 | 476,394 |
| 0/100 | 473,203 | 452,001 | 223,601 | 229,147 | 440,729 | 443,865 |

## Critical-region length

| Work | scenario | std RwLock | parking RwLock | CEL | CEL / std RwLock |
|---:|---:|---:|---:|---:|---:|
| 1 | 100/0 | 21,061,252 | 21,405,547 | 31,136,259 | 1.48× |
| 1 | 90/10 | 4,834,923 | 6,538,139 | 7,861,429 | 1.63× |
| 1 | 50/50 | 2,037,329 | 1,811,041 | 3,287,067 | 1.61× |
| 1 | 0/100 | 3,092,052 | 1,430,813 | 2,582,370 | 0.84× |
| 64（单轮） | 100/0 | 2,483,393 | 2,539,576 | 2,625,290 | 1.06× |
| 64（单轮） | 90/10 | 379,354 | 473,767 | 814,223 | 2.15× |
| 64（单轮） | 50/50 | 313,910 | 221,611 | 470,606 | 1.50× |
| 64（单轮） | 0/100 | 473,773 | 232,631 | 481,919 | 1.02× |
| 256 | 100/0 | 1,183,342 | 649,555 | 828,225 | 0.70× |
| 256 | 90/10 | 191,707 | 147,245 | 164,580 | 0.86× |
| 256 | 50/50 | 107,649 | 85,557 | 110,950 | 1.03× |
| 256 | 0/100 | 112,913 | 73,625 | 120,843 | 1.07× |

## Multi-lock tests

The complete 8 locks × 4 threads and 64 locks × 2 threads results are retained in `TestBenchmarkResults/final/benchmarks/`. The 64×2 case uses 128 OS threads on roughly four available CPUs and is treated as a completion/state-consistency stress case rather than a stable throughput ranking.

## Raw data

```text
TestBenchmarkResults/final/benchmarks/all_results.csv
TestBenchmarkResults/final/benchmarks/all_results.json
TestBenchmarkResults/final/benchmarks/single_16t_w64_median.csv
TestBenchmarkResults/final/benchmarks/*.log
```

Performance depends on OS, runtime, CPU topology, thread count, lock count, critical-region duration, read/write ratio, oversubscription, and background load. Strategies run in a fixed order; the primary 16-thread configuration was repeated three times and reports medians, while extension configurations are single runs. Re-run the included executable on the target system before making deployment decisions.
