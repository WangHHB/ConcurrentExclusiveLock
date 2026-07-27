use crate::options::Options;
use concurrent_exclusive_lock::{
    ConcurrentExclusiveLock,
    ConcurrentExclusiveLockPipeline, ConcurrentExclusiveLockScope,
    ConcurrentExclusiveLockSegment, ConcurrentExclusiveLockState, IDType,
};
use std::hint::spin_loop;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicBool, AtomicI32, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Barrier, Mutex};
use std::thread;
use std::time::{Duration, Instant};

pub fn run_full(options: &Options) {
    println!("ConcurrentExclusiveLock Rust full semantics");
    deterministic_contracts();
    randomized_legal_paths(options);
    println!("PASS: full semantic regression");
}

pub fn run_pipeline_semantics() {
    println!("ConcurrentExclusiveLock Rust Pipeline semantics");
    pipeline_contracts();
    println!("PASS: Pipeline semantic regression");
}

pub fn run_pipeline_stress(options: &Options, duration: Duration) {
    println!("ConcurrentExclusiveLock Rust Pipeline stress");
    println!(
        "duration={:?}, locks={}, workers/lock={}, seed=0x{:016X}",
        duration, options.lock_instances, options.semantic_workers, options.semantic_seed
    );

    let locks: Vec<_> = (0..options.lock_instances)
        .map(|_| Arc::new(ConcurrentExclusiveLock::new()))
        .collect();
    let validators: Vec<_> = (0..options.lock_instances)
        .map(|_| Arc::new(AccessValidator::default()))
        .collect();
    let total_workers = options.lock_instances * options.semantic_workers;
    let barrier = Arc::new(Barrier::new(total_workers));
    let total_rounds = AtomicU64::new(0);
    let run_duration = duration.max(Duration::from_millis(1));

    thread::scope(|scope| {
        let mut handles = Vec::with_capacity(total_workers);
        for worker_index in 0..total_workers {
            let lock_index = worker_index / options.semantic_workers;
            let lock = Arc::clone(&locks[lock_index]);
            let validator = Arc::clone(&validators[lock_index]);
            let barrier = Arc::clone(&barrier);
            let total_rounds = &total_rounds;
            handles.push(scope.spawn(move || {
                let mut random = seed64(options.semantic_seed, worker_index as u64);
                let mut round = 0_u64;
                barrier.wait();
                let deadline = Instant::now() + run_duration;
                while Instant::now() < deadline {
                    random = next64(random.wrapping_add(round));
                    let segment_count = 3 + (random as usize % 8);
                    let mut segments = Vec::with_capacity(segment_count);
                    for segment_index in 0..segment_count {
                        random = next64(random.wrapping_add(segment_index as u64));
                        let mode = (random % 9) as u8;
                        segments.push(make_stress_segment(
                            mode,
                            Arc::clone(&validator),
                            ((worker_index as u64) << 32 | round) as i32,
                            (round as i32).wrapping_add(segment_index as i32 + 1),
                        ));
                    }
                    ConcurrentExclusiveLockPipeline::new(&lock)
                        .do_pipeline(&mut segments)
                        .expect("Pipeline Concurrent capacity exceeded");
                    round += 1;
                }
                total_rounds.fetch_add(round, Ordering::Relaxed);
            }));
        }
        for handle in handles {
            handle.join().expect("Pipeline stress worker panicked");
        }
    });

    for (index, validator) in validators.iter().enumerate() {
        validator.assert_idle();
        assert_eq!(locks[index].observed_state(), ConcurrentExclusiveLockState::Idle);
    }
    println!(
        "PASS: {} randomized Pipeline rounds, {} validated callbacks",
        total_rounds.load(Ordering::Relaxed),
        validators
            .iter()
            .map(|value| value.operations.load(Ordering::Relaxed))
            .sum::<u64>()
    );
}

pub fn run_contention_stress(options: &Options, duration: Duration) {
    println!("ConcurrentExclusiveLock Rust Exclusive contention stress");
    let workers = options
        .lock_instances
        .saturating_mul(options.semantic_workers)
        .max(2);
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    let active = Arc::new(AtomicBool::new(false));
    let barrier = Arc::new(Barrier::new(workers));
    let run_duration = duration.max(Duration::from_secs(1));
    let counts: Vec<AtomicU64> = (0..workers).map(|_| AtomicU64::new(0)).collect();

    thread::scope(|scope| {
        let mut handles = Vec::with_capacity(workers);
        for worker in 0..workers {
            let lock = Arc::clone(&lock);
            let active = Arc::clone(&active);
            let barrier = Arc::clone(&barrier);
            let count = &counts[worker];
            handles.push(scope.spawn(move || {
                barrier.wait();
                let deadline = Instant::now() + run_duration;
                while Instant::now() < deadline {
                    lock.acquire_exclusive();
                    assert!(
                        active
                            .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
                            .is_ok(),
                        "Exclusive regions overlapped"
                    );
                    spin_loop();
                    active.store(false, Ordering::SeqCst);
                    lock.release_exclusive();
                    count.fetch_add(1, Ordering::Relaxed);
                }
            }));
        }
        for handle in handles {
            handle.join().expect("contention worker panicked");
        }
    });

    let acquisitions: Vec<u64> = counts
        .iter()
        .map(|count| count.load(Ordering::Relaxed))
        .collect();
    let total: u64 = acquisitions.iter().sum();
    let minimum = acquisitions.iter().copied().min().unwrap_or(0);
    let maximum = acquisitions.iter().copied().max().unwrap_or(0);
    assert!(total > 0);
    assert!(minimum > 0, "at least one Exclusive waiter made no progress");
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
    println!(
        "PASS: workers={workers}, acquisitions={total}, min/worker={minimum}, max/worker={maximum}"
    );
}

pub fn run_endurance(options: &Options, duration: Duration) {
    println!("ConcurrentExclusiveLock Rust endurance");
    let start = Instant::now();
    let mut batches = 0_u64;
    while start.elapsed() < duration {
        deterministic_contracts();
        batches += 1;
        if start.elapsed() < duration {
            let remaining = duration.saturating_sub(start.elapsed());
            run_pipeline_stress(options, remaining.min(Duration::from_secs(1)));
        }
    }
    println!("PASS: endurance completed {batches} deterministic batches");
}

fn deterministic_contracts() {
    concurrent_id_uniqueness();
    concurrent_overlap();
    exclusive_isolation();
    preemptive_exclusive();
    upgrade_and_downgrade();
    conditional_context_single_winner();
    conditional_epoch_single_winner();
    scope_unwind_release();
    timeout_contracts();
    snapshot_contracts();
    pipeline_contracts();
}

fn concurrent_id_uniqueness() {
    let workers = 16;
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    let barrier = Arc::new(Barrier::new(workers + 1));
    let release = Arc::new(Barrier::new(workers + 1));
    let ids = Arc::new(Mutex::new(Vec::with_capacity(workers)));
    let mut handles = Vec::with_capacity(workers);

    for _ in 0..workers {
        let lock = Arc::clone(&lock);
        let barrier = Arc::clone(&barrier);
        let release = Arc::clone(&release);
        let ids = Arc::clone(&ids);
        handles.push(thread::spawn(move || {
            let id = lock.acquire_concurrent_with_max(workers as i32).unwrap();
            ids.lock().unwrap().push(id);
            barrier.wait();
            release.wait();
            lock.release_concurrent();
        }));
    }

    barrier.wait();
    let mut observed = ids.lock().unwrap().clone();
    observed.sort_unstable();
    assert_eq!(observed, (1..=workers as i32).collect::<Vec<_>>());
    release.wait();

    for handle in handles {
        handle.join().unwrap();
    }
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
}

fn concurrent_overlap() {
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    let active = Arc::new(AtomicI32::new(0));
    let maximum = Arc::new(AtomicI32::new(0));
    let barrier = Arc::new(Barrier::new(4));
    let mut handles = Vec::new();
    for _ in 0..4 {
        let lock = Arc::clone(&lock);
        let active = Arc::clone(&active);
        let maximum = Arc::clone(&maximum);
        let barrier = Arc::clone(&barrier);
        handles.push(thread::spawn(move || {
            barrier.wait();
            lock.acquire_concurrent().unwrap();
            let now = active.fetch_add(1, Ordering::SeqCst) + 1;
            maximum.fetch_max(now, Ordering::SeqCst);
            thread::sleep(Duration::from_millis(5));
            active.fetch_sub(1, Ordering::SeqCst);
            lock.release_concurrent();
        }));
    }
    for handle in handles {
        handle.join().unwrap();
    }
    assert!(maximum.load(Ordering::SeqCst) >= 2);
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
}

fn exclusive_isolation() {
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    let validator = Arc::new(AccessValidator::default());
    let mut handles = Vec::new();
    for worker in 0..8 {
        let lock = Arc::clone(&lock);
        let validator = Arc::clone(&validator);
        handles.push(thread::spawn(move || {
            for operation in 0..200 {
                if (worker + operation) % 5 == 0 {
                    lock.acquire_exclusive();
                    validator.exclusive_callback();
                    lock.release_exclusive();
                } else {
                    lock.acquire_concurrent().unwrap();
                    validator.concurrent_callback();
                    lock.release_concurrent();
                }
            }
        }));
    }
    for handle in handles {
        handle.join().unwrap();
    }
    validator.assert_idle();
}

fn preemptive_exclusive() {
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    lock.acquire_concurrent().unwrap();
    let entered = Arc::new(AtomicBool::new(false));
    let writer_lock = Arc::clone(&lock);
    let writer_entered = Arc::clone(&entered);
    let handle = thread::spawn(move || {
        writer_lock.acquire_exclusive();
        writer_entered.store(true, Ordering::SeqCst);
        writer_lock.release_exclusive();
    });

    let deadline = Instant::now() + Duration::from_secs(2);
    while lock.observed_state() != ConcurrentExclusiveLockState::Exclusive {
        assert!(Instant::now() < deadline, "Exclusive pressure was not observed");
        thread::yield_now();
    }
    assert!(lock.try_acquire_concurrent().unwrap().is_none());
    assert!(!entered.load(Ordering::SeqCst));
    lock.release_concurrent();
    handle.join().unwrap();
    assert!(entered.load(Ordering::SeqCst));
}

fn upgrade_and_downgrade() {
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    let barrier = Arc::new(Barrier::new(2));
    let active_exclusive = Arc::new(AtomicBool::new(false));
    let mut handles = Vec::new();
    for _ in 0..2 {
        let lock = Arc::clone(&lock);
        let barrier = Arc::clone(&barrier);
        let active_exclusive = Arc::clone(&active_exclusive);
        handles.push(thread::spawn(move || {
            lock.acquire_concurrent().unwrap();
            barrier.wait();
            lock.concurrent_to_exclusive();
            assert!(
                active_exclusive
                    .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
                    .is_ok()
            );
            active_exclusive.store(false, Ordering::SeqCst);
            lock.exclusive_to_concurrent();
            lock.release_concurrent();
        }));
    }
    for handle in handles {
        handle.join().unwrap();
    }
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
}

fn conditional_context_single_winner() {
    conditional_single_winner(false);
}

fn conditional_epoch_single_winner() {
    conditional_single_winner(true);
}

fn conditional_single_winner(epoch: bool) {
    let workers = 8;
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    let barrier = Arc::new(Barrier::new(workers));
    let successes = Arc::new(AtomicUsize::new(0));
    let mut handles = Vec::new();
    for _ in 0..workers {
        let lock = Arc::clone(&lock);
        let barrier = Arc::clone(&barrier);
        let successes = Arc::clone(&successes);
        handles.push(thread::spawn(move || {
            lock.acquire_concurrent().unwrap();
            barrier.wait();
            let success = if epoch {
                lock.try_concurrent_to_exclusive_with_raise_epoch_id(1)
            } else {
                lock.try_concurrent_to_exclusive_with_switch_context_id(1)
            };
            if success {
                successes.fetch_add(1, Ordering::SeqCst);
                lock.release_exclusive();
            }
        }));
    }
    for handle in handles {
        handle.join().unwrap();
    }
    assert_eq!(successes.load(Ordering::SeqCst), 1);
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
}

fn scope_unwind_release() {
    let lock = ConcurrentExclusiveLock::new();
    let result = catch_unwind(AssertUnwindSafe(|| {
        let mut scope = ConcurrentExclusiveLockScope::new(&lock);
        scope.acquire_exclusive();
        panic!("intentional scope unwind");
    }));
    assert!(result.is_err());
    assert!(lock.try_acquire_exclusive(false));
    lock.release_exclusive();

    {
        let mut scope = ConcurrentExclusiveLockScope::new(&lock);
        scope.acquire_concurrent().unwrap();
    }
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
}

fn timeout_contracts() {
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    lock.acquire_exclusive();
    let other = Arc::clone(&lock);
    let handle = thread::spawn(move || {
        assert!(other
            .try_acquire_concurrent_for(Duration::from_millis(20))
            .unwrap()
            .is_none());
        assert!(!other.try_acquire_exclusive_for(Duration::from_millis(20)));
    });
    handle.join().unwrap();
    lock.release_exclusive();
}

fn snapshot_contracts() {
    let lock = ConcurrentExclusiveLock::new();
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
    assert_eq!(lock.observed_contention(), 0);
    lock.acquire_concurrent().unwrap();
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Concurrent);
    assert_eq!(lock.observed_contention(), 0);
    lock.release_concurrent();
    lock.acquire_exclusive();
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Exclusive);
    assert!(lock.observed_contention() >= 1);
    lock.release_exclusive();
}

fn pipeline_contracts() {
    let lock = ConcurrentExclusiveLock::new();
    let markers = Mutex::new(Vec::new());
    let pipeline = ConcurrentExclusiveLockPipeline::new(&lock);
    let mut segments = vec![
        ConcurrentExclusiveLockSegment::concurrent(|| markers.lock().unwrap().push(1)),
        ConcurrentExclusiveLockSegment::converge_exclusive(|| markers.lock().unwrap().push(2)),
        ConcurrentExclusiveLockSegment::converge_concurrent(|| markers.lock().unwrap().push(3)),
        ConcurrentExclusiveLockSegment::none(|| markers.lock().unwrap().push(4)),
        ConcurrentExclusiveLockSegment::try_apply_id_converge_exclusive(
            || markers.lock().unwrap().push(5),
            7,
            IDType::ContextID,
        ),
        ConcurrentExclusiveLockSegment::try_apply_id_converge_exclusive(
            || markers.lock().unwrap().push(6),
            7,
            IDType::ContextID,
        ),
        ConcurrentExclusiveLockSegment::none(|| markers.lock().unwrap().push(7)),
    ];
    pipeline.do_pipeline(&mut segments).unwrap();
    assert_eq!(*markers.lock().unwrap(), vec![1, 2, 3, 4, 5, 7]);
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);

    let unwind = catch_unwind(AssertUnwindSafe(|| {
        let mut panic_segments = vec![ConcurrentExclusiveLockSegment::exclusive(|| {
            panic!("intentional Pipeline unwind")
        })];
        pipeline.do_pipeline(&mut panic_segments).unwrap();
    }));
    assert!(unwind.is_err());
    assert!(lock.try_acquire_exclusive(false));
    lock.release_exclusive();
}

fn randomized_legal_paths(options: &Options) {
    let locks: Vec<_> = (0..options.lock_instances)
        .map(|_| Arc::new(ConcurrentExclusiveLock::new()))
        .collect();
    let validators: Vec<_> = (0..options.lock_instances)
        .map(|_| Arc::new(AccessValidator::default()))
        .collect();
    let total_workers = options.lock_instances * options.semantic_workers;

    thread::scope(|scope| {
        let mut handles = Vec::with_capacity(total_workers);
        for worker in 0..total_workers {
            let lock_index = worker / options.semantic_workers;
            let lock = Arc::clone(&locks[lock_index]);
            let validator = Arc::clone(&validators[lock_index]);
            handles.push(scope.spawn(move || {
                let mut random = seed64(options.semantic_seed, worker as u64);
                for operation in 0..options.semantic_operations {
                    random = next64(random.wrapping_add(operation as u64));
                    match random % 6 {
                        0 => {
                            lock.acquire_concurrent().unwrap();
                            validator.concurrent_callback();
                            lock.release_concurrent();
                        }
                        1 => {
                            lock.acquire_exclusive();
                            validator.exclusive_callback();
                            lock.release_exclusive();
                        }
                        2 => {
                            lock.acquire_concurrent().unwrap();
                            validator.concurrent_callback();
                            lock.concurrent_to_exclusive();
                            validator.exclusive_callback();
                            lock.release_exclusive();
                        }
                        3 => {
                            lock.acquire_exclusive();
                            validator.exclusive_callback();
                            lock.exclusive_to_concurrent();
                            validator.concurrent_callback();
                            lock.release_concurrent();
                        }
                        4 => {
                            let mut scope = ConcurrentExclusiveLockScope::new(&lock);
                            scope.acquire_concurrent().unwrap();
                            validator.concurrent_callback();
                        }
                        _ => {
                            let mut scope = ConcurrentExclusiveLockScope::new(&lock);
                            scope.acquire_exclusive();
                            validator.exclusive_callback();
                            scope.exclusive_to_concurrent();
                            validator.concurrent_callback();
                        }
                    }
                }
            }));
        }
        for handle in handles {
            handle.join().expect("randomized semantic worker panicked");
        }
    });

    for (index, validator) in validators.iter().enumerate() {
        validator.assert_idle();
        assert_eq!(locks[index].observed_state(), ConcurrentExclusiveLockState::Idle);
    }
}

#[derive(Default)]
struct AccessValidator {
    concurrent: AtomicI32,
    exclusive: AtomicBool,
    operations: AtomicU64,
}

impl AccessValidator {
    fn concurrent_callback(&self) {
        assert!(!self.exclusive.load(Ordering::SeqCst));
        self.concurrent.fetch_add(1, Ordering::SeqCst);
        assert!(!self.exclusive.load(Ordering::SeqCst));
        spin_loop();
        self.concurrent.fetch_sub(1, Ordering::SeqCst);
        self.operations.fetch_add(1, Ordering::Relaxed);
    }

    fn exclusive_callback(&self) {
        assert!(
            self.exclusive
                .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
                .is_ok(),
            "Exclusive callbacks overlapped"
        );
        assert_eq!(
            self.concurrent.load(Ordering::SeqCst),
            0,
            "Exclusive callback overlapped Concurrent callback"
        );
        spin_loop();
        self.exclusive.store(false, Ordering::SeqCst);
        self.operations.fetch_add(1, Ordering::Relaxed);
    }

    fn assert_idle(&self) {
        assert_eq!(self.concurrent.load(Ordering::SeqCst), 0);
        assert!(!self.exclusive.load(Ordering::SeqCst));
    }
}

fn make_stress_segment(
    mode: u8,
    validator: Arc<AccessValidator>,
    context_id: i32,
    epoch_id: i32,
) -> ConcurrentExclusiveLockSegment<'static> {
    match mode {
        0 => ConcurrentExclusiveLockSegment::none(move || {
            validator.operations.fetch_add(1, Ordering::Relaxed);
        }),
        1 => ConcurrentExclusiveLockSegment::concurrent(move || {
            validator.concurrent_callback();
        }),
        2 => ConcurrentExclusiveLockSegment::try_concurrent(move || {
            validator.concurrent_callback();
        }),
        3 => ConcurrentExclusiveLockSegment::exclusive(move || {
            validator.exclusive_callback();
        }),
        4 => ConcurrentExclusiveLockSegment::test_exclusive(move || {
            validator.exclusive_callback();
        }),
        5 => ConcurrentExclusiveLockSegment::try_exclusive(move || {
            validator.exclusive_callback();
        }),
        6 => ConcurrentExclusiveLockSegment::converge_concurrent(move || {
            validator.concurrent_callback();
        }),
        7 => ConcurrentExclusiveLockSegment::converge_exclusive(move || {
            validator.exclusive_callback();
        }),
        _ => {
            let id_type = if epoch_id & 1 == 0 {
                IDType::ContextID
            } else {
                IDType::EpochID
            };
            let id = if id_type == IDType::ContextID {
                context_id
            } else {
                epoch_id
            };
            ConcurrentExclusiveLockSegment::try_apply_id_converge_exclusive(
                move || validator.exclusive_callback(),
                id,
                id_type,
            )
        }
    }
}

#[inline]
fn seed64(base: u64, ordinal: u64) -> u64 {
    next64(base ^ ordinal.wrapping_mul(0x9E37_79B9_7F4A_7C15))
}

#[inline]
fn next64(mut value: u64) -> u64 {
    value ^= value << 13;
    value ^= value >> 7;
    value ^= value << 17;
    value
}
