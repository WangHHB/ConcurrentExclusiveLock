use concurrent_exclusive_lock::{
    ConcurrentExclusiveLock, ConcurrentExclusiveLockPipeline,
    ConcurrentExclusiveLockSegment, ConcurrentExclusiveLockState, IDType,
};
use std::sync::atomic::{AtomicBool, AtomicI32, Ordering};
use std::sync::{Arc, Barrier};
use std::thread;

#[test]
fn concurrent_regions_overlap_but_exclusive_does_not() {
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    let readers = Arc::new(AtomicI32::new(0));
    let writer = Arc::new(AtomicBool::new(false));
    let mut handles = Vec::new();

    for worker in 0..8 {
        let lock = Arc::clone(&lock);
        let readers = Arc::clone(&readers);
        let writer = Arc::clone(&writer);
        handles.push(thread::spawn(move || {
            for operation in 0..200 {
                if (worker + operation) % 7 == 0 {
                    lock.acquire_exclusive();
                    assert!(!writer.swap(true, Ordering::SeqCst));
                    assert_eq!(readers.load(Ordering::SeqCst), 0);
                    writer.store(false, Ordering::SeqCst);
                    lock.release_exclusive();
                } else {
                    lock.acquire_concurrent().unwrap();
                    assert!(!writer.load(Ordering::SeqCst));
                    readers.fetch_add(1, Ordering::SeqCst);
                    assert!(!writer.load(Ordering::SeqCst));
                    readers.fetch_sub(1, Ordering::SeqCst);
                    lock.release_concurrent();
                }
            }
        }));
    }

    for handle in handles {
        handle.join().unwrap();
    }
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
}

#[test]
fn conditional_upgrade_has_one_winner_and_releases_failures() {
    let workers = 8;
    let lock = Arc::new(ConcurrentExclusiveLock::new());
    let barrier = Arc::new(Barrier::new(workers));
    let winners = Arc::new(AtomicI32::new(0));
    let mut handles = Vec::new();

    for _ in 0..workers {
        let lock = Arc::clone(&lock);
        let barrier = Arc::clone(&barrier);
        let winners = Arc::clone(&winners);
        handles.push(thread::spawn(move || {
            lock.acquire_concurrent().unwrap();
            barrier.wait();
            if lock.try_concurrent_to_exclusive_with_switch_context_id(42) {
                winners.fetch_add(1, Ordering::SeqCst);
                lock.release_exclusive();
            }
        }));
    }

    for handle in handles {
        handle.join().unwrap();
    }
    assert_eq!(winners.load(Ordering::SeqCst), 1);
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
}

#[test]
fn pipeline_skips_failed_business_id_and_continues_from_none() {
    let lock = ConcurrentExclusiveLock::new();
    let executed = AtomicI32::new(0);
    let pipeline = ConcurrentExclusiveLockPipeline::new(&lock);
    let mut segments = vec![
        ConcurrentExclusiveLockSegment::try_apply_id_converge_exclusive(
            || {
                executed.fetch_add(1, Ordering::SeqCst);
            },
            1,
            IDType::EpochID,
        ),
        ConcurrentExclusiveLockSegment::try_apply_id_converge_exclusive(
            || {
                executed.fetch_add(100, Ordering::SeqCst);
            },
            1,
            IDType::EpochID,
        ),
        ConcurrentExclusiveLockSegment::none(|| {
            executed.fetch_add(10, Ordering::SeqCst);
        }),
    ];

    pipeline.do_pipeline(&mut segments).unwrap();
    assert_eq!(executed.load(Ordering::SeqCst), 11);
    assert_eq!(lock.observed_state(), ConcurrentExclusiveLockState::Idle);
}
