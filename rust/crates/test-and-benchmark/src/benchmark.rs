use crate::options::Options;
use crate::workload::{create_worker_seed, next_random, MemoryWork};
use concurrent_exclusive_lock::ConcurrentExclusiveLock;
use std::cell::UnsafeCell;
use std::hint::black_box;
use std::sync::{Arc, Barrier, Mutex, RwLock};
use std::thread;
use std::time::{Duration, Instant};

const SCENARIOS: &[(u16, &str)] = &[
    (1000, "100/0"),
    (995, "99.5/0.5"),
    (900, "90/10"),
    (500, "50/50"),
    (300, "30/70"),
    (0, "0/100"),
];

#[derive(Clone, Copy)]
enum StrategyKind {
    Mutex,
    RwLock,
    Cel,
    CelExclusiveOnly,
}

impl StrategyKind {
    const ALL: [Self; 4] = [
        Self::Mutex,
        Self::RwLock,
        Self::Cel,
        Self::CelExclusiveOnly,
    ];

    fn name(self) -> &'static str {
        match self {
            Self::Mutex => "Mutex",
            Self::RwLock => "RwLock",
            Self::Cel => "CEL",
            Self::CelExclusiveOnly => "CEL(ExclusiveOnly)",
        }
    }
}

trait Strategy: Send + Sync {
    fn execute_read(&self, random: &mut u32) -> i64;
    fn execute_write(&self) -> i64;
    fn state_hash(&self) -> i64;
}

struct MutexStrategy {
    work: Mutex<MemoryWork>,
}

impl Strategy for MutexStrategy {
    #[inline]
    fn execute_read(&self, random: &mut u32) -> i64 {
        self.work
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .tick_read(random)
    }

    #[inline]
    fn execute_write(&self) -> i64 {
        self.work
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .tick_write()
    }

    fn state_hash(&self) -> i64 {
        self.work
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .state_hash()
    }
}

struct RwLockStrategy {
    work: RwLock<MemoryWork>,
}

impl Strategy for RwLockStrategy {
    #[inline]
    fn execute_read(&self, random: &mut u32) -> i64 {
        self.work
            .read()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .tick_read(random)
    }

    #[inline]
    fn execute_write(&self) -> i64 {
        self.work
            .write()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .tick_write()
    }

    fn state_hash(&self) -> i64 {
        self.work
            .read()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .state_hash()
    }
}

struct CelWork {
    lock: ConcurrentExclusiveLock,
    work: UnsafeCell<MemoryWork>,
    exclusive_only: bool,
}

// All access to `work` is protected by the CEL protocol. Concurrent callbacks
// receive only `&MemoryWork`; mutable access is available only under Exclusive.
unsafe impl Sync for CelWork {}

impl Strategy for CelWork {
    #[inline]
    fn execute_read(&self, random: &mut u32) -> i64 {
        if self.exclusive_only {
            self.lock.acquire_exclusive();
            let result = unsafe { (&*self.work.get()).tick_read(random) };
            self.lock.release_exclusive();
            result
        } else {
            self.lock
                .acquire_concurrent()
                .expect("Concurrent capacity cannot be reached by the benchmark");
            let result = unsafe { (&*self.work.get()).tick_read(random) };
            self.lock.release_concurrent();
            result
        }
    }

    #[inline]
    fn execute_write(&self) -> i64 {
        self.lock.acquire_exclusive();
        let result = unsafe { (&mut *self.work.get()).tick_write() };
        self.lock.release_exclusive();
        result
    }

    fn state_hash(&self) -> i64 {
        self.lock.acquire_exclusive();
        let state = unsafe { (&*self.work.get()).state_hash() };
        self.lock.release_exclusive();
        state
    }
}

#[derive(Default)]
struct WorkerResult {
    reads: u64,
    writes: u64,
    write_latency: Duration,
    checksum: i64,
}

struct BenchmarkResult {
    name: &'static str,
    elapsed: Duration,
    reads: u64,
    writes: u64,
    write_latency: Duration,
    state: i64,
    checksum: i64,
}

pub fn run(options: &Options) {
    let cpu = thread::available_parallelism().map_or(1, usize::from);
    println!("ConcurrentExclusiveLock Rust benchmark");
    println!(
        "Rust={}, OS={}, CPU={cpu}",
        option_env!("CARGO_PKG_RUST_VERSION").unwrap_or("stable"),
        std::env::consts::OS
    );
    println!(
        "lock_instances={}, threads/lock={}, total_threads={}, operations/thread={}",
        options.lock_instances,
        options.threads,
        options.lock_instances * options.threads,
        options.operations
    );
    println!(
        "workload=memory, memory={} MiB/lock, read_work={}, write_work={}",
        options.memory_mb, options.read_work, options.write_work
    );
    println!("Each strategy/scenario uses fresh lock and Work instances.\n");

    warm_up(options);

    for &(read_permille, label) in SCENARIOS {
        println!("Scenario: read/write {label}");
        println!(
            "  {:<24} {:>10} {:>14} {:>14} {:>14} {:>13} {:>13} {:>18}",
            "lock type", "elapsed", "works/s", "works/s/lock", "avg write ns", "reads", "writes", "state"
        );

        let mut expected_state = None;
        for kind in StrategyKind::ALL {
            let result = run_case(kind, options, read_permille);
            if let Some(expected) = expected_state {
                assert_eq!(
                    expected, result.state,
                    "strategy state mismatch in scenario {label}: {}",
                    result.name
                );
            } else {
                expected_state = Some(result.state);
            }

            let works = result.reads + result.writes;
            let seconds = result.elapsed.as_secs_f64().max(1e-9);
            let works_per_second = works as f64 / seconds;
            let per_lock = works_per_second / options.lock_instances as f64;
            let average_write_ns = if result.writes == 0 {
                0.0
            } else {
                result.write_latency.as_nanos() as f64 / result.writes as f64
            };
            println!(
                "  {:<24} {:>9.3}s {:>14.0} {:>14.0} {:>14.0} {:>13} {:>13} {:>18X}",
                result.name,
                seconds,
                works_per_second,
                per_lock,
                average_write_ns,
                result.reads,
                result.writes,
                result.state as u64
            );
            black_box(result.checksum);
        }
        println!();
    }
}

fn warm_up(options: &Options) {
    let mut warm = options.clone();
    warm.lock_instances = 1;
    warm.threads = options.threads.min(4).max(1);
    warm.operations = options.operations.min(2_000).max(100);
    warm.read_work = options.read_work.min(8);
    warm.write_work = options.write_work.min(8);
    for kind in StrategyKind::ALL {
        black_box(run_case(kind, &warm, 900));
    }
}

fn run_case(kind: StrategyKind, options: &Options, read_permille: u16) -> BenchmarkResult {
    let instances: Vec<Arc<dyn Strategy>> = (0..options.lock_instances)
        .map(|_| create_strategy(kind, options))
        .collect();
    let total_threads = options.lock_instances * options.threads;
    let barrier = Arc::new(Barrier::new(total_threads + 1));
    let worker_results = thread::scope(|scope| {
        let mut handles = Vec::with_capacity(total_threads);
        for global_worker in 0..total_threads {
            let lock_index = global_worker / options.threads;
            let local_worker = global_worker % options.threads;
            let strategy = Arc::clone(&instances[lock_index]);
            let barrier = Arc::clone(&barrier);
            handles.push(scope.spawn(move || {
                let mut result = WorkerResult::default();
                let mut random = create_worker_seed(lock_index, local_worker);
                barrier.wait();

                for operation in 0..options.operations {
                    random = next_random(random.wrapping_add(operation as u32));
                    let is_read = read_permille == 1000
                        || (read_permille != 0 && random % 1000 < u32::from(read_permille));
                    if is_read {
                        result.checksum = result
                            .checksum
                            .wrapping_add(strategy.execute_read(&mut random));
                        result.reads += 1;
                    } else {
                        let write_start = Instant::now();
                        result.checksum = result.checksum.wrapping_add(strategy.execute_write());
                        result.write_latency += write_start.elapsed();
                        result.writes += 1;
                    }
                }
                result
            }));
        }

        barrier.wait();
        let measured_start = Instant::now();
        let results: Vec<WorkerResult> = handles
            .into_iter()
            .map(|handle| handle.join().expect("benchmark worker panicked"))
            .collect();
        (results, measured_start.elapsed())
    });

    let (workers, elapsed) = worker_results;
    let mut reads = 0_u64;
    let mut writes = 0_u64;
    let mut write_latency = Duration::ZERO;
    let mut checksum = 0_i64;
    for worker in workers {
        reads += worker.reads;
        writes += worker.writes;
        write_latency += worker.write_latency;
        checksum = checksum.wrapping_add(worker.checksum);
    }

    let state = combine_state_hashes(&instances);
    BenchmarkResult {
        name: kind.name(),
        elapsed,
        reads,
        writes,
        write_latency,
        state,
        checksum,
    }
}

fn create_strategy(kind: StrategyKind, options: &Options) -> Arc<dyn Strategy> {
    let work = MemoryWork::new(options.read_work, options.write_work, options.memory_mb);
    match kind {
        StrategyKind::Mutex => Arc::new(MutexStrategy {
            work: Mutex::new(work),
        }),
        StrategyKind::RwLock => Arc::new(RwLockStrategy {
            work: RwLock::new(work),
        }),
        StrategyKind::Cel => Arc::new(CelWork {
            lock: ConcurrentExclusiveLock::new(),
            work: UnsafeCell::new(work),
            exclusive_only: false,
        }),
        StrategyKind::CelExclusiveOnly => Arc::new(CelWork {
            lock: ConcurrentExclusiveLock::new(),
            work: UnsafeCell::new(work),
            exclusive_only: true,
        }),
    }
}

fn combine_state_hashes(instances: &[Arc<dyn Strategy>]) -> i64 {
    if instances.len() == 1 {
        return instances[0].state_hash();
    }

    let mut combined = 0x6A09_E667_F3BC_C909_u64;
    for (index, instance) in instances.iter().enumerate() {
        let state = instance.state_hash() as u64;
        combined ^= state
            .wrapping_add(0x9E37_79B9_7F4A_7C15)
            .wrapping_add(combined << 6)
            .wrapping_add(combined >> 2);
        combined ^= index as u32 as u64;
    }
    combined as i64
}
