import csv
from pathlib import Path
P=Path('/mnt/data/rust_validation/ConcurrentExclusiveLock_Rust_Release')
D=P/'TestBenchmarkResults/final/benchmarks'
rows=list(csv.DictReader((D/'all_results.csv').open()))
med=list(csv.DictReader((D/'single_16t_w64_median.csv').open()))
strategies=['std::sync::Mutex','std::sync::RwLock','parking_lot::Mutex','parking_lot::RwLock','CEL','CEL(ExclusiveOnly)']
scenarios=['100/0','99.5/0.5','90/10','50/50','30/70','0/100']

def fmt(n): return f"{int(n):,}"
def table_median():
    lookup={(r['scenario'],r['strategy']):r for r in med}
    out=['| 读/写 | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |',
         '|---:|---:|---:|---:|---:|---:|---:|']
    for sc in scenarios:
        vals=[fmt(lookup[(sc,st)]['median_works_per_s']) for st in strategies]
        out.append('| '+sc+' | '+' | '.join(vals)+' |')
    return '\n'.join(out)

def table_config(config, per_lock=False):
    lookup={(r['scenario'],r['strategy']):r for r in rows if r['config']==config}
    metric='works_per_s_per_lock' if per_lock else 'works_per_s'
    out=['| 读/写 | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |',
         '|---:|---:|---:|---:|---:|---:|---:|']
    for sc in scenarios:
        vals=[fmt(lookup[(sc,st)][metric]) for st in strategies]
        out.append('| '+sc+' | '+' | '.join(vals)+' |')
    return '\n'.join(out)

def table_latency(config):
    lookup={(r['scenario'],r['strategy']):r for r in rows if r['config']==config}
    out=['| 读/写 | std Mutex | std RwLock | parking Mutex | parking RwLock | CEL | CEL ExclusiveOnly |',
         '|---:|---:|---:|---:|---:|---:|---:|']
    for sc in scenarios[1:]:
        vals=[fmt(lookup[(sc,st)]['avg_write_ns']) for st in strategies]
        out.append('| '+sc+' | '+' | '.join(vals)+' |')
    return '\n'.join(out)

def selected_work_table():
    out=['| Work | 场景 | std RwLock | parking RwLock | CEL | CEL / std RwLock |',
         '|---:|---:|---:|---:|---:|---:|']
    for cfg,work in [('single_16t_w1','1'),('single_16t_w64_r1','64（单轮）'),('single_16t_w256','256')]:
        d={(r['scenario'],r['strategy']):int(r['works_per_s']) for r in rows if r['config']==cfg}
        for sc in ['100/0','90/10','50/50','0/100']:
            a=d[(sc,'std::sync::RwLock')]; b=d[(sc,'parking_lot::RwLock')]; c=d[(sc,'CEL')]
            out.append(f'| {work} | {sc} | {fmt(a)} | {fmt(b)} | {fmt(c)} | {(c/a):.2f}× |')
    return '\n'.join(out)

cn=f'''# Rust 版性能测试与实测结果

## 1. 测试目的

本评测比较以下六种策略：

- `std::sync::Mutex`；
- `std::sync::RwLock`；
- `parking_lot::Mutex` 0.12.5；
- `parking_lot::RwLock` 0.12.5；
- `ConcurrentExclusiveLock`（CEL）；
- `CEL(ExclusiveOnly)`，即所有操作均走 CEL Exclusive，用作纯互斥基线。

`parking_lot` 及其依赖已压缩为 `parking_lot-vendor.zip`；构建脚本按需解压到 `vendor/`，测试程序仍可在 Cargo registry 为空时离线构建。

## 2. 公平比较条件

每种策略、每个读写比例均重新创建锁和 `MemoryWork`。所有策略使用：

- 相同 worker 数量；
- 相同随机种子生成规则；
- 相同读写判定序列；
- 相同共享内存大小；
- 相同读写 Work 步数；
- 相同每线程操作数；
- 相同最终状态哈希校验。

若同一场景中任何策略的最终 `state` 不一致，benchmark 会直接失败。

`avg write ns` 是一次写请求从**申请锁之前**到**写 Work 完成并释放锁之后**的平均端到端时间，包含排队、调度、获取、Work 和释放，不是单纯的锁指令开销。

## 3. 测试环境

```text
Rust: 1.75.0
OS: Linux 6.12.13 x86_64, KVM
CPU model: AMD EPYC 9V74
Rust available_parallelism(): 4
Build: --release, opt-level=3, thin LTO, codegen-units=1
```

这是受限虚拟机，16/64/128 线程场景均存在明显超额订阅。结果用于观察相同环境下的相对行为，不代表 Windows、裸机或其他 Linux 内核上的固定排名。

## 4. 核心结果：单锁、16 线程、64 MiB、Work=64

命令：

```bash
./cel-test-and-benchmark \\
  --lock-instances 1 --threads 16 --operations 10000 \\
  --workload memory --memory-mb 64 --read-work 64 --write-work 64
```

该配置完整重复 3 次。下表为吞吐中位数，单位 `works/s`：

{table_median()}

主要观察：

- 纯读时 CEL 为 `2,695,156/s`，与两种 RwLock 处于同一量级；
- 99.5/0.5 时 CEL 略低于标准 RwLock，但高于本机的 parking_lot RwLock；
- 90/10 时 CEL 中位数为 `713,582/s`，比标准 RwLock 高约 88%，比 parking_lot RwLock 高约 54%；
- 50/50 时 CEL 与标准 Mutex 接近，并高于两种 RwLock；
- 纯写时 CEL、标准 Mutex、标准 RwLock 基本处于同一档；
- 本虚拟机上 parking_lot 在高写单锁争用中偏慢，这不是对 parking_lot 在所有平台上的普遍结论。

三轮最小值与最大值保存在：

```text
TestBenchmarkResults/final/benchmarks/single_16t_w64_median.csv
```

## 5. 64 线程高争用单锁

参数仍为 64 MiB、Work=64，每线程 3,000 次。吞吐量：

{table_config('single_64t_w64')}

该机器只有约 4 个可用 CPU，因此这是调度压力测试：

- CEL 纯读达到 `5,329,420/s`，约为标准 RwLock 的 2 倍；
- 90/10 时 CEL 为 `654,907/s`，仍明显领先两种 RwLock；
- 高写比例下 CEL、标准 Mutex 和标准 RwLock 接近；
- parking_lot 的结果反映此 Linux/KVM/超额订阅组合，不能外推为 Rust 生态总体结论。

64 线程写请求平均端到端延迟（ns）：

{table_latency('single_64t_w64')}

## 6. 临界区长度

16 线程下改变每次内存 Work 步数：

{selected_work_table()}

解释：

- Work=1 更接近同步原语和缓存线竞争测试，CEL 的轻量 Concurrent 路径优势最明显；
- Work=256 时内存访问占比增大，锁实现差异被业务 Work 稀释；
- 高写比例最终趋近纯互斥吞吐，CEL 不应被期待在所有写密集场景都领先 Mutex。

## 7. 多锁结果

### 8 把锁 × 每锁 4 线程

每锁 16 MiB、Work=64、每线程 5,000 次。下表为**总吞吐**：

{table_config('multi_8x4_w64')}

每锁吞吐可直接查看原始日志。多锁并行后，调度、内存带宽和不同锁实例间的独立进展会显著影响结果；此时单把锁的倍率通常会收敛。

### 64 把锁 × 每锁 2 线程

该组共有 128 个 OS 线程，而虚拟机约有 4 个可用 CPU，且每轮持续时间较短，调度噪声明显。结果完整保留在：

```text
TestBenchmarkResults/final/benchmarks/multi_64x2_w64.log
```

这组用于检查大量锁实例能否同时完成、状态是否一致，不作为锁吞吐排名的主要依据。

## 8. 无争用基线

单线程、Work=64 时，各策略大多落在约 47 万～64 万 works/s，差距远小于高并发场景。说明共享内存 Work 本身已经占据显著成本，不能仅凭无争用数字判断锁在竞争状态下的行为。

原始日志：

```text
TestBenchmarkResults/final/benchmarks/single_1t_w64.log
```

## 9. 原始数据

```text
TestBenchmarkResults/final/benchmarks/all_results.csv
TestBenchmarkResults/final/benchmarks/all_results.json
TestBenchmarkResults/final/benchmarks/single_16t_w64_median.csv
TestBenchmarkResults/final/benchmarks/*.log
```

所有表格均由上述日志自动解析生成，没有手工改写数字。

## 10. 结果边界

性能排名依赖：

- 操作系统与标准库实现；
- CPU 数量、缓存、NUMA 和虚拟化；
- 线程数与锁实例数；
- 临界区长度和内存访问模式；
- 读写比例；
- 是否存在超额订阅；
- 系统后台负载。

因此 README 中的数据应理解为“本测试环境中的可复现实测”，而不是任何机器上的绝对承诺。正式选型应在目标服务器上使用同一可执行文件重复测试。
'''

# Simple English version mirrors the facts but stays shorter.
en=f'''# Rust performance benchmark and measured results

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

{table_median().replace('读/写','read/write')}

CEL was close to both RwLocks for pure reads, substantially ahead at 90/10 in this environment, close to the standard Mutex at high write ratios, and close to the standard Mutex/RwLock for 100% writes. `parking_lot` was slower under high-write single-lock oversubscription on this particular Linux VM; this is not a universal ranking.

## 64-thread oversubscribed single lock

{table_config('single_64t_w64').replace('读/写','read/write')}

## Critical-region length

{selected_work_table().replace('Work | 场景','Work | scenario').replace('CEL / std RwLock','CEL / std RwLock')}

## Multi-lock tests

The complete 8 locks × 4 threads and 64 locks × 2 threads results are retained in `TestBenchmarkResults/final/benchmarks/`. The 64×2 case uses 128 OS threads on roughly four available CPUs and is treated as a completion/state-consistency stress case rather than a stable throughput ranking.

## Raw data

```text
TestBenchmarkResults/final/benchmarks/all_results.csv
TestBenchmarkResults/final/benchmarks/all_results.json
TestBenchmarkResults/final/benchmarks/single_16t_w64_median.csv
TestBenchmarkResults/final/benchmarks/*.log
```

Performance depends on OS, runtime, CPU topology, thread count, lock count, critical-region duration, read/write ratio, oversubscription, and background load. Re-run the included executable on the target system before making deployment decisions.
'''
(P/'PERFORMANCE_CN.md').write_text(cn)
(P/'PERFORMANCE.md').write_text(en)
