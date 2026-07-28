#![forbid(unsafe_code)]
#![deny(missing_docs)]
#![allow(clippy::upper_case_acronyms)]

//! A high-performance, non-recursive synchronization lock based on
//! Concurrent/Exclusive access permissions.
//!
//! This crate is a Rust port of the original C# `ConcurrentExclusiveLock`.
//! The C# implementation remains the reference for protocol semantics.
//!
//! Ordinary Concurrent acquisition/release uses lightweight atomic counting.
//! Exclusive acquisition is preemptive: once an Exclusive request enters the
//! contention window, new Concurrent entries are blocked while existing
//! Concurrent holders leave naturally. Exclusive acquisition and in-place
//! Concurrent-to-Exclusive conversion are coordinated through a serialized,
//! blocking monitor path. Strict FIFO ordering is not promised.
//!
//! Rust maps the reference implementation's internal `Monitor` directly to
//! [`std::sync::Mutex`]. Because Rust unlocks a mutex by dropping its guard,
//! Exclusive acquisition returns an [`ExclusiveGuard`] that must remain alive
//! until release or downgrade. The lock state machine and transition order remain
//! aligned with the C# reference implementation.

#[cfg(not(target_has_atomic = "64"))]
compile_error!("concurrent-exclusive-lock requires native 64-bit atomic support");

mod pipeline;
mod scope;

pub use pipeline::{
    ConcurrentExclusiveAccessMode, ConcurrentExclusiveLockPipeline, ConcurrentExclusiveLockSegment,
    IDType,
};
pub use scope::ConcurrentExclusiveLockScope;

use std::error::Error;
use std::fmt::{Display, Formatter};
use std::hint::spin_loop;
use std::sync::atomic::{AtomicI32, AtomicI64, Ordering};
use std::sync::{Mutex, MutexGuard, TryLockError};
use std::thread;
use std::time::{Duration, Instant};

const EXCLUSIVE_ADD: i64 = 1_i64 << 32;
const CONVERGE_ADD: i64 = EXCLUSIVE_ADD - 1;
const SHIFT_COUNT: u32 = 32;

/// The maximum number of simultaneous Concurrent holders supported by one lock.
pub const MAX_CONCURRENT: i32 = i32::MAX;

/// An observational snapshot of the current lock state.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum ConcurrentExclusiveLockState {
    /// No Concurrent or Exclusive pressure is currently observed.
    Idle = 0,
    /// One or more Concurrent holders are currently observed.
    Concurrent = 1,
    /// Exclusive pressure or an Exclusive transition is currently observed.
    Exclusive = 2,
}

/// Errors returned by Concurrent acquisition APIs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ConcurrentExclusiveLockError {
    /// `max_concurrent` was less than 1.
    InvalidMaxConcurrent,
    /// The internal 31-bit Concurrent holder capacity was exceeded.
    CapacityExceeded,
}

impl Display for ConcurrentExclusiveLockError {
    fn fmt(&self, formatter: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::InvalidMaxConcurrent => formatter.write_str("max_concurrent must be at least 1"),
            Self::CapacityExceeded => formatter.write_str(
                "the runtime number of Concurrent holders exceeded the internal 31-bit capacity",
            ),
        }
    }
}

impl Error for ConcurrentExclusiveLockError {}

/// Ownership token for one acquired or upgraded Exclusive permission.
///
/// Dropping this value releases Exclusive permission. Use
/// [`ConcurrentExclusiveLock::release_exclusive`] for an explicit release or
/// [`ConcurrentExclusiveLock::exclusive_to_concurrent`] to downgrade while
/// retaining Concurrent permission.
#[must_use = "Exclusive permission is released when the guard is dropped"]
pub struct ExclusiveGuard<'a> {
    lock: &'a ConcurrentExclusiveLock,
    monitor_guard: Option<MutexGuard<'a, ()>>,
    active: bool,
}

impl ExclusiveGuard<'_> {
    #[inline]
    fn new<'a>(
        lock: &'a ConcurrentExclusiveLock,
        monitor_guard: MutexGuard<'a, ()>,
    ) -> ExclusiveGuard<'a> {
        ExclusiveGuard {
            lock,
            monitor_guard: Some(monitor_guard),
            active: true,
        }
    }

    #[inline]
    fn disarm_and_unlock(&mut self) {
        self.active = false;
        drop(self.monitor_guard.take());
    }
}

impl Drop for ExclusiveGuard<'_> {
    #[inline]
    fn drop(&mut self) {
        if self.active {
            self.lock.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
            self.active = false;
        }
        // `monitor_guard` is dropped after this method body, matching the
        // reference order: update Counter first, then Monitor.Exit.
    }
}

/// A non-recursive Concurrent/Exclusive permission lock.
///
/// `Concurrent` and `Exclusive` describe whether simultaneous access is
/// permitted; they do not prescribe read/write intent. Concurrent business code
/// may modify disjoint state, while Exclusive business code may perform mostly
/// reads.
///
/// Exclusive permission is thread-affine. It must be released or downgraded on
/// the same thread that acquired or upgraded it. Ordinary Exclusive acquisition
/// must not be requested while holding Concurrent permission; use
/// [`Self::concurrent_to_exclusive`] instead. Ordinary Concurrent acquisition
/// must not be requested while holding Exclusive permission; use
/// [`Self::exclusive_to_concurrent`] instead.
pub struct ConcurrentExclusiveLock {
    counter: AtomicI64,
    context_id: AtomicI32,
    epoch_id: AtomicI32,
    monitor: Mutex<()>,
}

impl ConcurrentExclusiveLock {
    /// Creates a new idle lock.
    #[must_use]
    pub const fn new() -> Self {
        Self {
            counter: AtomicI64::new(0),
            context_id: AtomicI32::new(0),
            epoch_id: AtomicI32::new(0),
            monitor: Mutex::new(()),
        }
    }

    /// Returns an observational snapshot of the lock's current access tendency.
    ///
    /// A preemptive Exclusive request may make this return `Exclusive` before a
    /// thread has actually entered its Exclusive business region. This property
    /// is for diagnostics and monitoring only; it is not an authoritative
    /// synchronization check.
    #[must_use]
    pub fn observed_state(&self) -> ConcurrentExclusiveLockState {
        let counter = self.counter.load(Ordering::Acquire);
        if counter >= EXCLUSIVE_ADD {
            ConcurrentExclusiveLockState::Exclusive
        } else if counter > 0 {
            ConcurrentExclusiveLockState::Concurrent
        } else {
            ConcurrentExclusiveLockState::Idle
        }
    }

    /// Returns an observational indicator of current contention pressure.
    ///
    /// Purely Concurrent operation reports 0. Once Exclusive pressure exists,
    /// this returns the observed scale of Concurrent plus Exclusive pressure.
    #[must_use]
    pub fn observed_contention(&self) -> i32 {
        let counter = self.counter.load(Ordering::Acquire);
        let exclusive = (counter >> SHIFT_COUNT) as i32;
        if exclusive == 0 {
            0
        } else {
            (counter as i32).wrapping_add(exclusive)
        }
    }

    /// Gets the current business ContextID.
    #[must_use]
    pub fn context_id(&self) -> i32 {
        self.context_id.load(Ordering::Acquire)
    }

    /// Unconditionally sets the business ContextID.
    pub fn set_context_id(&self, value: i32) {
        self.context_id.store(value, Ordering::Release);
    }

    /// Gets the current business EpochID.
    #[must_use]
    pub fn epoch_id(&self) -> i32 {
        self.epoch_id.load(Ordering::Acquire)
    }

    /// Unconditionally sets the business EpochID.
    pub fn set_epoch_id(&self, value: i32) {
        self.epoch_id.store(value, Ordering::Release);
    }

    /// Sets a new ContextID and returns whether the previous value changed.
    pub fn switch_context_id(&self, new_context_id: i32) -> bool {
        self.context_id.swap(new_context_id, Ordering::SeqCst) != new_context_id
    }

    /// Advances EpochID only when `new_epoch_id` is greater than the current ID.
    pub fn raise_epoch_id(&self, new_epoch_id: i32) -> bool {
        loop {
            let old_epoch_id = self.epoch_id.load(Ordering::Acquire);
            if new_epoch_id <= old_epoch_id {
                return false;
            }
            if self
                .epoch_id
                .compare_exchange_weak(
                    old_epoch_id,
                    new_epoch_id,
                    Ordering::SeqCst,
                    Ordering::Acquire,
                )
                .is_ok()
            {
                return true;
            }
        }
    }

    /// Waits to acquire ordinary Concurrent permission with the default limit.
    ///
    /// The returned Concurrent ID is in `1..=MAX_CONCURRENT`. IDs held
    /// simultaneously in one uninterrupted Concurrent round are distinct; a
    /// later acquisition may reuse a released ID.
    pub fn acquire_concurrent(&self) -> Result<i32, ConcurrentExclusiveLockError> {
        self.acquire_concurrent_with_max(MAX_CONCURRENT)
    }

    /// Waits to acquire ordinary Concurrent permission with a caller limit.
    pub fn acquire_concurrent_with_max(
        &self,
        max_concurrent: i32,
    ) -> Result<i32, ConcurrentExclusiveLockError> {
        validate_max(max_concurrent)?;
        let mut adjust_turn = 0_i32;

        'redo: loop {
            let mut counter = self.counter.load(Ordering::Acquire);
            if counter >= i64::from(max_concurrent) {
                adjust_turn += 1;
                if adjust_turn == 1 {
                    if counter < EXCLUSIVE_ADD * 2 {
                        drop(lock_no_poison(&self.monitor));
                    } else {
                        thread::yield_now();
                    }
                } else if adjust_turn < 33 {
                    thread::yield_now();
                } else {
                    adjust_turn = 1;
                    thread::sleep(Duration::from_millis(5));
                }
                continue 'redo;
            }

            loop {
                counter = self.counter.fetch_add(1, Ordering::SeqCst) + 1;
                if low_i32(counter) < 0 {
                    self.counter.fetch_sub(1, Ordering::SeqCst);
                    return Err(ConcurrentExclusiveLockError::CapacityExceeded);
                }
                if counter <= i64::from(max_concurrent) {
                    return Ok(counter as i32);
                }
                counter = self.counter.fetch_sub(1, Ordering::SeqCst) - 1;
                if counter >= EXCLUSIVE_ADD {
                    continue 'redo;
                }
            }
        }
    }

    /// Makes one immediate attempt to acquire ordinary Concurrent permission.
    pub fn try_acquire_concurrent(&self) -> Result<Option<i32>, ConcurrentExclusiveLockError> {
        self.try_acquire_concurrent_with_max(MAX_CONCURRENT)
    }

    /// Makes one immediate attempt to acquire ordinary Concurrent permission
    /// with a caller limit.
    pub fn try_acquire_concurrent_with_max(
        &self,
        max_concurrent: i32,
    ) -> Result<Option<i32>, ConcurrentExclusiveLockError> {
        validate_max(max_concurrent)?;
        let counter = self.counter.load(Ordering::Acquire);
        if counter >= i64::from(max_concurrent) {
            return Ok(None);
        }

        let counter = self.counter.fetch_add(1, Ordering::SeqCst) + 1;
        if low_i32(counter) < 0 {
            self.counter.fetch_sub(1, Ordering::SeqCst);
            return Ok(None);
        }
        if counter <= i64::from(max_concurrent) {
            Ok(Some(counter as i32))
        } else {
            self.counter.fetch_sub(1, Ordering::SeqCst);
            Ok(None)
        }
    }

    /// Attempts to acquire Concurrent permission within `timeout`.
    pub fn try_acquire_concurrent_for(
        &self,
        timeout: Duration,
    ) -> Result<Option<i32>, ConcurrentExclusiveLockError> {
        self.try_acquire_concurrent_for_with_max(timeout, MAX_CONCURRENT)
    }

    /// Attempts to acquire Concurrent permission within `timeout` with a caller
    /// limit.
    pub fn try_acquire_concurrent_for_with_max(
        &self,
        timeout: Duration,
        max_concurrent: i32,
    ) -> Result<Option<i32>, ConcurrentExclusiveLockError> {
        validate_max(max_concurrent)?;
        if timeout.is_zero() {
            return self.try_acquire_concurrent_with_max(max_concurrent);
        }

        let Some(deadline) = Instant::now().checked_add(timeout) else {
            return self.acquire_concurrent_with_max(max_concurrent).map(Some);
        };
        let mut adjust_turn = 0_i32;

        'redo: loop {
            if deadline_expired(deadline) {
                return Ok(None);
            }

            let mut counter = self.counter.load(Ordering::Acquire);
            if counter >= i64::from(max_concurrent) {
                adjust_turn += 1;
                if adjust_turn == 1 {
                    if counter < EXCLUSIVE_ADD * 2 {
                        let Some(remaining) = remaining(deadline) else {
                            return Ok(None);
                        };
                        if let Some(guard) = try_lock_for(&self.monitor, remaining) {
                            drop(guard);
                        } else {
                            return Ok(None);
                        }
                    } else {
                        thread::yield_now();
                    }
                } else if adjust_turn < 33 {
                    thread::yield_now();
                } else {
                    adjust_turn = 1;
                    let sleep = remaining(deadline)
                        .map(|left| left.min(Duration::from_millis(5)))
                        .unwrap_or(Duration::ZERO);
                    if sleep.is_zero() {
                        return Ok(None);
                    }
                    thread::sleep(sleep);
                }
                continue 'redo;
            }

            loop {
                counter = self.counter.fetch_add(1, Ordering::SeqCst) + 1;
                if low_i32(counter) < 0 {
                    self.counter.fetch_sub(1, Ordering::SeqCst);
                    return Ok(None);
                }
                if counter <= i64::from(max_concurrent) {
                    return Ok(Some(counter as i32));
                }
                counter = self.counter.fetch_sub(1, Ordering::SeqCst) - 1;
                if counter < EXCLUSIVE_ADD {
                    if deadline_expired(deadline) {
                        return Ok(None);
                    }
                } else {
                    continue 'redo;
                }
            }
        }
    }

    /// Releases one currently held Concurrent permission.
    #[inline]
    pub fn release_concurrent(&self) {
        self.counter.fetch_sub(1, Ordering::SeqCst);
    }

    /// Waits to acquire preemptive Exclusive permission.
    ///
    /// The returned guard owns the standard-library mutex until it is released,
    /// dropped, or consumed by [`Self::exclusive_to_concurrent`].
    pub fn acquire_exclusive(&self) -> ExclusiveGuard<'_> {
        let mut adjust_turn = 0_i32;

        'redo: loop {
            let monitor_guard = lock_no_poison(&self.monitor);
            let mut counter =
                self.counter.fetch_add(EXCLUSIVE_ADD, Ordering::SeqCst) + EXCLUSIVE_ADD;
            if counter != EXCLUSIVE_ADD {
                if counter < EXCLUSIVE_ADD * 2 {
                    while {
                        counter = self.counter.load(Ordering::Acquire);
                        counter != EXCLUSIVE_ADD
                    } {
                        if counter < EXCLUSIVE_ADD * 2 {
                            adjust_wait(&mut adjust_turn);
                        } else {
                            self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
                            drop(monitor_guard);
                            thread::yield_now();
                            continue 'redo;
                        }
                    }
                } else {
                    self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
                    drop(monitor_guard);
                    thread::yield_now();
                    continue 'redo;
                }
            }
            return ExclusiveGuard::new(self, monitor_guard);
        }
    }

    /// Attempts to acquire Exclusive permission.
    ///
    /// With `preempt_concurrent = true`, the request may wait for existing
    /// Concurrent holders, but it returns `None` when another Exclusive request
    /// is already visible or when an in-place upgrade appears during contention.
    /// With `false`, it makes an immediate Idle-only attempt.
    pub fn try_acquire_exclusive(&self, preempt_concurrent: bool) -> Option<ExclusiveGuard<'_>> {
        let mut adjust_turn = 0_i32;

        if preempt_concurrent {
            if self.counter.load(Ordering::Acquire) >= EXCLUSIVE_ADD {
                return None;
            }

            let monitor_guard = lock_no_poison(&self.monitor);
            let mut counter =
                self.counter.fetch_add(EXCLUSIVE_ADD, Ordering::SeqCst) + EXCLUSIVE_ADD;
            if counter != EXCLUSIVE_ADD {
                if counter < EXCLUSIVE_ADD * 2 {
                    while {
                        counter = self.counter.load(Ordering::Acquire);
                        counter != EXCLUSIVE_ADD
                    } {
                        if counter < EXCLUSIVE_ADD * 2 {
                            adjust_wait(&mut adjust_turn);
                        } else {
                            self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
                            drop(monitor_guard);
                            return None;
                        }
                    }
                    return Some(ExclusiveGuard::new(self, monitor_guard));
                }
                self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
                drop(monitor_guard);
                return None;
            }
            Some(ExclusiveGuard::new(self, monitor_guard))
        } else {
            let monitor_guard = try_lock_no_poison(&self.monitor)?;
            if self
                .counter
                .compare_exchange(0, EXCLUSIVE_ADD, Ordering::SeqCst, Ordering::Acquire)
                .is_ok()
            {
                Some(ExclusiveGuard::new(self, monitor_guard))
            } else {
                drop(monitor_guard);
                None
            }
        }
    }

    /// Attempts to acquire preemptive Exclusive permission within `timeout`.
    pub fn try_acquire_exclusive_for(&self, timeout: Duration) -> Option<ExclusiveGuard<'_>> {
        if timeout.is_zero() {
            return self.try_acquire_exclusive(false);
        }

        let Some(deadline) = Instant::now().checked_add(timeout) else {
            return Some(self.acquire_exclusive());
        };
        let mut adjust_turn = 0_i32;

        'redo: loop {
            let remaining = remaining(deadline)?;
            let monitor_guard = try_lock_for(&self.monitor, remaining)?;

            let mut counter =
                self.counter.fetch_add(EXCLUSIVE_ADD, Ordering::SeqCst) + EXCLUSIVE_ADD;
            if counter != EXCLUSIVE_ADD {
                if counter < EXCLUSIVE_ADD * 2 {
                    while {
                        counter = self.counter.load(Ordering::Acquire);
                        counter != EXCLUSIVE_ADD
                    } {
                        if counter < EXCLUSIVE_ADD * 2 {
                            if deadline_expired(deadline) {
                                self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
                                drop(monitor_guard);
                                return None;
                            }
                            adjust_wait(&mut adjust_turn);
                        } else {
                            self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
                            drop(monitor_guard);
                            thread::yield_now();
                            if deadline_expired(deadline) {
                                return None;
                            }
                            continue 'redo;
                        }
                    }
                    return Some(ExclusiveGuard::new(self, monitor_guard));
                }

                self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
                drop(monitor_guard);
                thread::yield_now();
                if deadline_expired(deadline) {
                    return None;
                }
                continue 'redo;
            }
            return Some(ExclusiveGuard::new(self, monitor_guard));
        }
    }

    /// Releases currently held Exclusive permission.
    #[inline]
    pub fn release_exclusive(&self, guard: ExclusiveGuard<'_>) {
        debug_assert!(std::ptr::eq(self, guard.lock));
        drop(guard);
    }

    /// Downgrades currently held Exclusive permission to Concurrent permission.
    ///
    /// The caller continues to hold Concurrent permission and must later call
    /// [`Self::release_concurrent`].
    #[inline]
    pub fn exclusive_to_concurrent(&self, mut guard: ExclusiveGuard<'_>) {
        debug_assert!(std::ptr::eq(self, guard.lock));
        let counter = self.counter.fetch_sub(CONVERGE_ADD, Ordering::SeqCst) - CONVERGE_ADD;
        guard.disarm_and_unlock();
        if counter >= EXCLUSIVE_ADD {
            self.counter.fetch_sub(1, Ordering::SeqCst);
            self.acquire_concurrent()
                .expect("reacquiring Concurrent after releasing one slot cannot exceed capacity");
        }
    }

    /// Upgrades currently held Concurrent permission to Exclusive permission.
    #[inline]
    pub fn concurrent_to_exclusive(&self) -> ExclusiveGuard<'_> {
        let mut adjust_turn = 0_i32;
        if low_i32(self.counter.fetch_add(CONVERGE_ADD, Ordering::SeqCst) + CONVERGE_ADD) != 0 {
            while low_i32(self.counter.load(Ordering::Acquire)) != 0 {
                adjust_wait2(&mut adjust_turn);
            }
        }
        ExclusiveGuard::new(self, lock_no_poison(&self.monitor))
    }

    /// While holding Concurrent permission, conditionally upgrades by switching
    /// ContextID. On failure, the original Concurrent permission is released.
    #[inline]
    pub fn try_concurrent_to_exclusive_with_switch_context_id(
        &self,
        new_context_id: i32,
    ) -> Option<ExclusiveGuard<'_>> {
        let mut adjust_turn = 0_i32;
        if low_i32(self.counter.fetch_add(CONVERGE_ADD, Ordering::SeqCst) + CONVERGE_ADD) != 0 {
            while low_i32(self.counter.load(Ordering::Acquire)) != 0 {
                adjust_wait2(&mut adjust_turn);
            }
        }
        if self.switch_context_id(new_context_id) {
            Some(ExclusiveGuard::new(self, lock_no_poison(&self.monitor)))
        } else {
            self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
            None
        }
    }

    /// While holding Concurrent permission, conditionally upgrades by advancing
    /// EpochID. On failure, the original Concurrent permission is released.
    #[inline]
    pub fn try_concurrent_to_exclusive_with_raise_epoch_id(
        &self,
        new_epoch_id: i32,
    ) -> Option<ExclusiveGuard<'_>> {
        let mut adjust_turn = 0_i32;
        if low_i32(self.counter.fetch_add(CONVERGE_ADD, Ordering::SeqCst) + CONVERGE_ADD) != 0 {
            while low_i32(self.counter.load(Ordering::Acquire)) != 0 {
                adjust_wait2(&mut adjust_turn);
            }
        }
        if self.raise_epoch_id(new_epoch_id) {
            Some(ExclusiveGuard::new(self, lock_no_poison(&self.monitor)))
        } else {
            self.counter.fetch_sub(EXCLUSIVE_ADD, Ordering::SeqCst);
            None
        }
    }

    #[inline]
    pub(crate) fn free_release_concurrent(&self, count: i64) {
        self.counter.fetch_sub(count, Ordering::SeqCst);
    }
}

#[inline]
fn lock_no_poison(mutex: &Mutex<()>) -> MutexGuard<'_, ()> {
    mutex
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
}

#[inline]
fn try_lock_no_poison(mutex: &Mutex<()>) -> Option<MutexGuard<'_, ()>> {
    match mutex.try_lock() {
        Ok(guard) => Some(guard),
        Err(TryLockError::Poisoned(poisoned)) => Some(poisoned.into_inner()),
        Err(TryLockError::WouldBlock) => None,
    }
}

fn try_lock_for(mutex: &Mutex<()>, timeout: Duration) -> Option<MutexGuard<'_, ()>> {
    let deadline = Instant::now().checked_add(timeout)?;
    let mut adjust_turn = 0_i32;
    loop {
        if let Some(guard) = try_lock_no_poison(mutex) {
            return Some(guard);
        }
        if deadline_expired(deadline) {
            return None;
        }
        adjust_wait(&mut adjust_turn);
    }
}

impl Default for ConcurrentExclusiveLock {
    fn default() -> Self {
        Self::new()
    }
}

#[inline]
fn validate_max(max_concurrent: i32) -> Result<(), ConcurrentExclusiveLockError> {
    if max_concurrent < 1 {
        Err(ConcurrentExclusiveLockError::InvalidMaxConcurrent)
    } else {
        Ok(())
    }
}

#[inline]
fn low_i32(counter: i64) -> i32 {
    counter as i32
}

#[inline]
fn adjust_wait(adjust_turn: &mut i32) {
    if *adjust_turn < 2048 {
        *adjust_turn += 1;
        spin_loop();
    } else {
        thread::yield_now();
    }
}

#[inline]
fn adjust_wait2(adjust_turn: &mut i32) {
    if *adjust_turn < 48 {
        *adjust_turn += 1;
        spin_loop();
    } else {
        thread::yield_now();
    }
}

#[inline]
fn deadline_expired(deadline: Instant) -> bool {
    Instant::now() >= deadline
}

#[inline]
fn remaining(deadline: Instant) -> Option<Duration> {
    deadline.checked_duration_since(Instant::now())
}
