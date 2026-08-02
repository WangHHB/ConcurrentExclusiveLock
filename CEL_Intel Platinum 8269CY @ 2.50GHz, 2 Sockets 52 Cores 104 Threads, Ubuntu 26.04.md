# CEL Benchmark Results

**Intel Platinum 8269CY @ 2.50GHz · 2 Sockets · 52 Cores · 104 Threads · Ubuntu 26.04**

> This document is a Markdown-formatted presentation of the original benchmark output. Numeric measurements and PASS results are preserved exactly as reported.

## Source Header

| Field | Value |
| --- | --- |
| Original `MACHINE` value | `Intel Platinum 8269CY @ 2.50GHz, 2 Way 104 Core, Ubuntu 26.04` |
| Original `OUT` value | `/root/csharp/CEL_Intel Platinum 8269CY @ 2.50GHz, 2 Sockets 52 Cores 104 Threads, Ubuntu 26.04.jsonl` |

## Environment

| Field | Value |
| --- | --- |
| Runtime | .NET 8.0.29 |
| Operating system | Ubuntu 26.04 LTS |
| Process architecture | X64 |
| OS architecture | X64 |
| CPU model | Intel(R) Xeon(R) Platinum 8269CY CPU @ 2.50GHz |
| Logical processors | 104 |
| CPU set reported | 116 |
| Physical cores | 52 |
| Sockets | 2 |
| NUMA nodes | 2 |
| SMT active | True |
| Server GC | False |
| GC latency mode | Interactive |
| Stopwatch frequency | 1,000,000,000 |

## Correctness

### Advanced Lock Correctness

Mass-independent mode: locks=8, participants/lock=2, threads=16, operations/participant=4, seed=23456.

- ✅ ExclusiveToConcurrent keeps a continuous Concurrent context
- ✅ ContextID Concurrent-to-Exclusive upgrades are isolated and release failed Concurrent contexts
- ✅ Mass independent locks preserve advanced semantics
  - `mass locks=8, operations/lock=4, context-upgrade winners=8, losers=9, seed=23456`

**Summary: passed=3, failed=0, total=3**

### Full Semantic Correctness

Random valid paths: locks=8, workers/lock=8, rounds/lock=1,000, total-threads=64, seed=12345, pipeline-exception-permille=10.

- ✅ Concurrent acquire IDs, limits, immediate attempts, timeouts, and release semantics
- ✅ Exclusive acquire preemption, non-preemption, immediate attempts, timeouts, and release semantics
- ✅ ConcurrentExclusiveLockScope binds the original lock storage
  - `scope random paths=2,000, injected-exceptions=1,491, normal-exits=509, seed=5357811`
- ✅ ConcurrentExclusiveLockScope releases every legal final state and exception path
- ✅ ContextID safely supports the documented same-context Exclusive nesting protocol
- ✅ State snapshot exposes Idle, Concurrent, and Exclusive
  - `contention waiters=8, final=0`
- ✅ Contention snapshot becomes observable under pressure and returns to zero
- ✅ ExclusiveToConcurrent keeps a continuous Concurrent context
- ✅ Exclusive/Concurrent permission conversion can cycle without an insertion window
- ✅ Unconditional ConcurrentToExclusive serializes every participant and releases correctly
- ✅ ContextID Concurrent-to-Exclusive upgrades are isolated and release failed Concurrent contexts
  - `pipeline random locks=8, workers/lock=8, rounds/lock=1,000, segments=320,128, injected=2,798, exception-permille=10, seed=12345`
- ✅ ConcurrentExclusiveLockPipeline preserves declared segment access semantics
  - `random paths concurrent=13,052, exclusive=12,951, downgrade=12,693, upgrade=12,672, conversion-cycle=12,632`
- ✅ Randomized valid call paths preserve access and transition invariants

**Summary: passed=13, failed=0, total=13**

## Throughput

### 1 Lock × 64 Threads

- `lock-instances=1, threads/lock=64, total-threads=64, works/thread=100,000, concurrent-work=64, exclusive-work=64`
- `workload=memory (8 MiB shared, concurrent-work=64, exclusive-work=64)`
- `Exclusive-op timing=acquire+work+release`

#### Concurrent/Exclusive 100.0/0.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 6.137s | 1.7% | 1042930 | 1042930 | 618013 | 6,400,000 | 0 | 0.0 | 0000000000000000 |
| ReaderWriterLockSlim | 4.266s | 49.1% | 1500294 | 1500294 | 30549 | 6,400,000 | 0 | 0.0 | 0000000000000000 |
| CEL | 0.804s | 50.7% | 7958741 | 7958741 | 156981 | 6,400,000 | 0 | 0.0 | 0000000000000000 |

#### Concurrent/Exclusive 99.5/0.5

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 6.058s | 1.7% | 1056529 | 1056529 | 622638 | 6,367,977 | 32,023 | 1364922.8 | 1D0C3C69C5DE2EA9 |
| ReaderWriterLockSlim | 7.766s | 47.6% | 824102 | 824102 | 17314 | 6,367,977 | 32,023 | 274172.4 | 1D0C3C69C5DE2EA9 |
| CEL | 1.148s | 11.5% | 5575349 | 5575349 | 484778 | 6,367,977 | 32,023 | 14227.6 | 1D0C3C69C5DE2EA9 |

#### Concurrent/Exclusive 90.0/10.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 7.102s | 1.7% | 901213 | 901213 | 533333 | 5,759,717 | 640,283 | 214418.2 | 08310ABD63DF45F1 |
| ReaderWriterLockSlim | 31.604s | 37.3% | 202507 | 202507 | 5432 | 5,759,717 | 640,283 | 292678.1 | 08310ABD63DF45F1 |
| CEL | 3.196s | 6.3% | 2002332 | 2002332 | 318469 | 5,759,717 | 640,283 | 47548.7 | 08310ABD63DF45F1 |

#### Concurrent/Exclusive 50.0/50.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 9.394s | 1.7% | 681298 | 681298 | 398324 | 3,201,610 | 3,198,390 | 108977.1 | 090DD06BEE923678 |
| ReaderWriterLockSlim | 45.031s | 42.7% | 142124 | 142124 | 3328 | 3,201,610 | 3,198,390 | 147440.1 | 090DD06BEE923678 |
| CEL | 9.481s | 2.9% | 675022 | 675022 | 236196 | 3,201,610 | 3,198,390 | 113859.3 | 090DD06BEE923678 |

#### Concurrent/Exclusive 30.0/70.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 9.579s | 1.8% | 668150 | 668150 | 376258 | 1,919,814 | 4,480,186 | 100060.7 | 7850C6891E9A9136 |
| ReaderWriterLockSlim | 45.111s | 37.4% | 141873 | 141873 | 3798 | 1,919,814 | 4,480,186 | 137398.5 | 7850C6891E9A9136 |
| CEL | 8.401s | 2.4% | 761786 | 761786 | 315899 | 1,919,814 | 4,480,186 | 94293.2 | 7850C6891E9A9136 |

#### Concurrent/Exclusive 0.0/100.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 10.735s | 1.8% | 596162 | 596162 | 335822 | 0 | 6,400,000 | 101402.3 | C0C1750DCF993128 |
| ReaderWriterLockSlim | 22.790s | 5.4% | 280821 | 280821 | 51645 | 0 | 6,400,000 | 221545.3 | C0C1750DCF993128 |
| CEL | 10.876s | 1.8% | 588474 | 588474 | 331309 | 0 | 6,400,000 | 104494.7 | C0C1750DCF993128 |

### 8 Locks × 8 Threads

- `lock-instances=8, threads/lock=8, total-threads=64, works/thread=100,000, concurrent-work=64, exclusive-work=64`
- `workload=memory (8 MiB shared, concurrent-work=64, exclusive-work=64)`
- `Exclusive-op timing=acquire+work+release`

#### Concurrent/Exclusive 100.0/0.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 3.590s | 7.2% | 1782641 | 222830 | 246976 | 6,400,000 | 0 | 0.0 | A57B1FF0E740A896 |
| ReaderWriterLockSlim | 0.606s | 44.5% | 10560192 | 1320024 | 237291 | 6,400,000 | 0 | 0.0 | A57B1FF0E740A896 |
| CEL | 0.505s | 43.5% | 12663047 | 1582881 | 291419 | 6,400,000 | 0 | 0.0 | A57B1FF0E740A896 |

#### Concurrent/Exclusive 99.5/0.5

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 3.498s | 7.0% | 1829427 | 228678 | 262564 | 6,368,084 | 31,916 | 462297.4 | B3F0BD68C35C9C53 |
| ReaderWriterLockSlim | 2.042s | 29.4% | 3134284 | 391785 | 106564 | 6,368,084 | 31,916 | 455884.5 | B3F0BD68C35C9C53 |
| CEL | 0.342s | 40.2% | 18717106 | 2339638 | 465129 | 6,368,084 | 31,916 | 12205.9 | B3F0BD68C35C9C53 |

#### Concurrent/Exclusive 90.0/10.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 3.832s | 6.9% | 1670356 | 208794 | 241773 | 5,759,558 | 640,442 | 80592.2 | 862722F453C9087E |
| ReaderWriterLockSlim | 9.595s | 25.5% | 667017 | 83377 | 26137 | 5,759,558 | 640,442 | 209928.8 | 862722F453C9087E |
| CEL | 0.812s | 32.4% | 7881775 | 985222 | 243631 | 5,759,558 | 640,442 | 9470.1 | 862722F453C9087E |

#### Concurrent/Exclusive 50.0/50.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 4.065s | 7.8% | 1574511 | 196814 | 201148 | 3,200,512 | 3,199,488 | 43697.0 | 70D2BBF54AC15631 |
| ReaderWriterLockSlim | 15.223s | 25.3% | 420426 | 52553 | 16647 | 3,200,512 | 3,199,488 | 119403.0 | 70D2BBF54AC15631 |
| CEL | 2.030s | 15.4% | 3152177 | 394022 | 204234 | 3,200,512 | 3,199,488 | 18665.3 | 70D2BBF54AC15631 |

#### Concurrent/Exclusive 30.0/70.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 4.196s | 7.9% | 1525418 | 190677 | 191926 | 1,918,650 | 4,481,350 | 38446.5 | 4DBF0498208C9C61 |
| ReaderWriterLockSlim | 16.626s | 24.0% | 384950 | 48119 | 16069 | 1,918,650 | 4,481,350 | 107354.7 | 4DBF0498208C9C61 |
| CEL | 2.627s | 11.8% | 2436303 | 304538 | 207158 | 1,918,650 | 4,481,350 | 22282.6 | 4DBF0498208C9C61 |

#### Concurrent/Exclusive 0.0/100.0

| lock type | elapsed | cpu% | works/s | works/s/lock | work/cpu% | concurrent | exclusive | avg Exclusive op ns | state |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 4.064s | 8.2% | 1574646 | 196831 | 191926 | 0 | 6,400,000 | 35215.4 | 4AE4F4FB883B8108 |
| ReaderWriterLockSlim | 8.002s | 24.9% | 799760 | 99970 | 32091 | 0 | 6,400,000 | 74872.3 | 4AE4F4FB883B8108 |
| CEL | 4.013s | 8.2% | 1594835 | 199354 | 193996 | 0 | 6,400,000 | 33176.7 | 4AE4F4FB883B8108 |

## Acquisition Latency

### 1 Lock × 8 Threads

- `lock-instances=1, threads/lock=8, total-threads=8, operations/thread=50,000, sample-every=10`
- `workload=memory (8 MiB shared, concurrent-work=64, exclusive-work=64)`
- `measurement=acquisition; retention=1 sample/worker block`

#### Concurrent/Exclusive 90.0/10.0

| lock type | elapsed | cpu% | ops/s | ops/s/lock | permission | samples | mean | p50 | p95 | p99 | p99.9 | max |
| --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 0.532s | 1.8% | 751469 | 751469 | Concurrent | 36,001 | 7.228us | 45ns | 110ns | 5.556us | 1.984ms | 9.874ms |
| lock | 0.532s | 1.8% | 751469 | 751469 | Exclusive | 3,999 | 9.444us | 45ns | 112ns | 10.696us | 1.918ms | 4.633ms |
| ReaderWriterLockSlim | 0.376s | 7.1% | 1065043 | 1065043 | Concurrent | 36,001 | 3.937us | 284ns | 26.264us | 58.949us | 102.373us | 286.277us |
| ReaderWriterLockSlim | 0.376s | 7.1% | 1065043 | 1065043 | Exclusive | 3,999 | 18.422us | 14.411us | 52.771us | 68.644us | 97.636us | 111.527us |
| CEL | 0.218s | 7.2% | 1836074 | 1836074 | Concurrent | 36,001 | 2.743us | 725ns | 8.345us | 30.925us | 71.965us | 182.032us |
| CEL | 0.218s | 7.2% | 1836074 | 1836074 | Exclusive | 3,999 | 3.913us | 2.348us | 8.89us | 30.132us | 75.718us | 88.928us |

### 1 Lock × 64 Threads

- `lock-instances=1, threads/lock=64, total-threads=64, operations/thread=50,000, sample-every=10`
- `workload=memory (8 MiB shared, concurrent-work=64, exclusive-work=64)`
- `measurement=acquisition; retention=1 sample/worker block`

#### Concurrent/Exclusive 90.0/10.0

| lock type | elapsed | cpu% | ops/s | ops/s/lock | permission | samples | mean | p50 | p95 | p99 | p99.9 | max |
| --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 4.132s | 1.7% | 774471 | 774471 | Concurrent | 287,845 | 56.954us | 45ns | 104ns | 1.376us | 15.669ms | 87.568ms |
| lock | 4.132s | 1.7% | 774471 | 774471 | Exclusive | 32,155 | 56.635us | 45ns | 105ns | 3.575us | 14.322ms | 78.656ms |
| ReaderWriterLockSlim | 12.848s | 40.2% | 249070 | 249070 | Concurrent | 287,845 | 196.333us | 1.811us | 857.342us | 1.257ms | 1.923ms | 4.847ms |
| ReaderWriterLockSlim | 12.848s | 40.2% | 249070 | 249070 | Exclusive | 32,155 | 160.032us | 54.142us | 678.338us | 833.643us | 1.017ms | 2.477ms |
| CEL | 1.492s | 7.3% | 2145211 | 2145211 | Concurrent | 287,845 | 27.056us | 253ns | 8.786us | 1.026ms | 1.924ms | 3.336ms |
| CEL | 1.492s | 7.3% | 2145211 | 2145211 | Exclusive | 32,155 | 33.042us | 2.154us | 9.74us | 1.039ms | 1.214ms | 2.264ms |

### 8 Locks × 8 Threads

- `lock-instances=8, threads/lock=8, total-threads=64, operations/thread=50,000, sample-every=10`
- `workload=memory (8 MiB shared, concurrent-work=64, exclusive-work=64)`
- `measurement=acquisition; retention=1 sample/worker block`

#### Concurrent/Exclusive 90.0/10.0

| lock type | elapsed | cpu% | ops/s | ops/s/lock | permission | samples | mean | p50 | p95 | p99 | p99.9 | max |
| --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| lock | 2.122s | 7.5% | 1508150 | 188519 | Concurrent | 287,885 | 27.107us | 45ns | 142ns | 764.007us | 4.597ms | 22.769ms |
| lock | 2.122s | 7.5% | 1508150 | 188519 | Exclusive | 32,115 | 28.775us | 45ns | 149ns | 804.621us | 4.613ms | 14.312ms |
| ReaderWriterLockSlim | 4.614s | 26.4% | 693518 | 86690 | Concurrent | 287,885 | 47.246us | 105ns | 310.448us | 1.153ms | 2.056ms | 4.42ms |
| ReaderWriterLockSlim | 4.614s | 26.4% | 693518 | 86690 | Exclusive | 32,115 | 151.731us | 10.576us | 891.384us | 1.569ms | 2.362ms | 3.558ms |
| CEL | 0.417s | 35.6% | 7668425 | 958553 | Concurrent | 287,885 | 5.595us | 358ns | 8.752us | 149.146us | 463.174us | 2.679ms |
| CEL | 0.417s | 35.6% | 7668425 | 958553 | Exclusive | 32,115 | 7.576us | 2.635us | 10.549us | 143.991us | 525.963us | 3.065ms |

## Exclusive Progress During a Fixed Concurrent Flood

### 1 Lock × 64 Concurrent Threads

- `lock-instances=1, concurrent-threads/lock=64, total-concurrent-threads=64, operations/concurrent-thread=100,000, total-concurrent-operations=6,400,000, exclusive-writers=1`
- `concurrent-work=64, exclusive-work=8`
- `workload=cpu (concurrent-work=64, exclusive-work=8)`
- `topology=1 Exclusive writer/lock; writer-arm=10ms once before measurement; reentry-gate=at least 1 new same-lock Concurrent completion after each Exclusive completion; measurement=Exclusive completions before that lock's Concurrent flood finishes`

| lock type | elapsed | cpu% | Concurrent ops/s | Exclusive entries | entries/1M C | Exclusive ops/s | min-lock entries | max-lock entries |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| ReaderWriterLockSlim | 4.453s | 55.3% | 1437149 | 14,774 | 2308.438 | 3318 | 14,774 | 14,774 |
| CEL | 2.015s | 12.6% | 3175810 | 519,254 | 81133.438 | 257664 | 519,254 | 519,254 |

### 8 Locks × 8 Concurrent Threads

- `lock-instances=8, concurrent-threads/lock=8, total-concurrent-threads=64, operations/concurrent-thread=100,000, total-concurrent-operations=6,400,000, exclusive-writers=8`
- `concurrent-work=64, exclusive-work=8`
- `workload=cpu (concurrent-work=64, exclusive-work=8)`
- `topology=1 Exclusive writer/lock; writer-arm=10ms once before measurement; reentry-gate=at least 1 new same-lock Concurrent completion after each Exclusive completion; measurement=Exclusive completions before that lock's Concurrent flood finishes`

| lock type | elapsed | cpu% | Concurrent ops/s | Exclusive entries | entries/1M C | Exclusive ops/s | min-lock entries | max-lock entries |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| ReaderWriterLockSlim | 1.133s | 56.6% | 5648125 | 245,361 | 38337.656 | 216536 | 26,945 | 32,526 |
| CEL | 1.004s | 42.6% | 6377560 | 874,909 | 136704.531 | 871841 | 99,145 | 126,682 |

## CEL Pipeline Performance Evaluation

### 1 Lock × 64 Threads

- `lock-instances=1, threads/lock=64, total-threads=64, operations/thread=100,000, prepare=128, commit=16, post=128`

| Category | strategy | elapsed | cpu% | ops/s | ops/s/lock | ns/op | commits |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| CEL internal ablation | CEL Core converge | 5.052s | 2.0% | 1266767 | 1266767 | 789 | 6,400,000 |
| CEL internal ablation | CEL Scope converge | 5.156s | 2.0% | 1241331 | 1241331 | 806 | 6,400,000 |
| CEL internal ablation | CEL Pipeline converge | 5.194s | 2.0% | 1232187 | 1232187 | 812 | 6,400,000 |
| CEL internal ablation | CEL Core handoff | 9.078s | 4.3% | 705003 | 705003 | 1418 | 6,400,000 |
| Portable baseline | RWLS handoff | 36.838s | 41.5% | 173734 | 173734 | 5756 | 6,400,000 |
| Portable baseline | Monitor serialized | 5.359s | 1.7% | 1194235 | 1194235 | 837 | 6,400,000 |

### 8 Locks × 8 Threads

- `lock-instances=8, threads/lock=8, total-threads=64, operations/thread=100,000, prepare=128, commit=16, post=128`

| Category | strategy | elapsed | cpu% | ops/s | ops/s/lock | ns/op | commits |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| CEL internal ablation | CEL Core converge | 1.495s | 13.9% | 4281066 | 535133 | 234 | 6,400,000 |
| CEL internal ablation | CEL Scope converge | 1.539s | 14.1% | 4159181 | 519898 | 240 | 6,400,000 |
| CEL internal ablation | CEL Pipeline converge | 1.232s | 13.9% | 5194121 | 649265 | 193 | 6,400,000 |
| CEL internal ablation | CEL Core handoff | 2.327s | 28.7% | 2750616 | 343827 | 364 | 6,400,000 |
| Portable baseline | RWLS handoff | 4.284s | 31.6% | 1493862 | 186733 | 669 | 6,400,000 |
| Portable baseline | Monitor serialized | 1.149s | 12.2% | 5571475 | 696434 | 179 | 6,400,000 |

## Concurrent-to-Exclusive Upgrade Contention

### 1 Lock × 64 Upgrade Threads + 0 Ordinary Exclusive per Lock

- `lock-instances=1, upgrade-threads/lock=64, ordinary-exclusive/lock=0, total-upgrade-threads=64, total-ordinary-exclusive=0`

| first | drain | upgrades/s | acq p50 | acq p95 | acq p99 | acq max | worst-lock p99 | worst drain | ordinary-before |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.602ms | 2.093ms | 30578 | 1.788ms | 2.062ms | 2.086ms | 2.092ms | 2.086ms | 2.093ms | 0 |

- ✅ 1 lock instance(s); no ordinary Exclusive request entered before its own upgrade chain drained.

### 1 Lock × 64 Upgrade Threads + 16 Ordinary Exclusive per Lock

- `lock-instances=1, upgrade-threads/lock=64, ordinary-exclusive/lock=16, total-upgrade-threads=64, total-ordinary-exclusive=16`

| first | drain | upgrades/s | acq p50 | acq p95 | acq p99 | acq max | worst-lock p99 | worst drain | ordinary-before |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1.517ms | 6.663ms | 9605 | 3.816ms | 4.705ms | 6.645ms | 6.662ms | 6.645ms | 6.663ms | 0 |

- ✅ 1 lock instance(s); no ordinary Exclusive request entered before its own upgrade chain drained.

### 8 Locks × 8 Upgrade Threads + 0 Ordinary Exclusive per Lock

- `lock-instances=8, upgrade-threads/lock=8, ordinary-exclusive/lock=0, total-upgrade-threads=64, total-ordinary-exclusive=0`

| first | drain | upgrades/s | acq p50 | acq p95 | acq p99 | acq max | worst-lock p99 | worst drain | ordinary-before |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 89.9us | 651.3us | 98265 | 471.291us | 648.455us | 650.342us | 650.879us | 650.819us | 651.3us | 0 |

- ✅ 8 lock instance(s); no ordinary Exclusive request entered before its own upgrade chain drained.

### 8 Locks × 8 Upgrade Threads + 4 Ordinary Exclusive per Lock

- `lock-instances=8, upgrade-threads/lock=8, ordinary-exclusive/lock=4, total-upgrade-threads=64, total-ordinary-exclusive=32`

| first | drain | upgrades/s | acq p50 | acq p95 | acq p99 | acq max | worst-lock p99 | worst drain | ordinary-before |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 284.8us | 907.8us | 70500 | 621.922us | 865.551us | 897.688us | 907.11us | 906.063us | 907.8us | 0 |

- ✅ 8 lock instance(s); no ordinary Exclusive request entered before its own upgrade chain drained.

---

Generated from the original benchmark text without modifying measured values.
