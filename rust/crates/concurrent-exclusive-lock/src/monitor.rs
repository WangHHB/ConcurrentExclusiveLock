use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};

/// A non-recursive, direct lock/unlock monitor used by the core protocol.
///
/// The monitor deliberately does not expose a guard. `ConcurrentExclusiveLock`
/// keeps it locked across an Exclusive business region and unlocks it from the
/// explicit release/downgrade path, matching the reference implementation's
/// direct API. Waiting threads are blocked with a condition variable. The
/// waiter count discourages unrestricted barging while still making no strict
/// FIFO promise.
pub(crate) struct RawMonitor {
    held: AtomicBool,
    waiters: AtomicUsize,
    gate: Mutex<()>,
    wake: Condvar,
}

impl RawMonitor {
    pub(crate) const fn new() -> Self {
        Self {
            held: AtomicBool::new(false),
            waiters: AtomicUsize::new(0),
            gate: Mutex::new(()),
            wake: Condvar::new(),
        }
    }

    #[inline]
    pub(crate) fn lock(&self) {
        if self.try_lock_fast() {
            return;
        }

        self.waiters.fetch_add(1, Ordering::SeqCst);
        let mut gate = self.lock_gate();
        loop {
            if self
                .held
                .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
                .is_ok()
            {
                self.waiters.fetch_sub(1, Ordering::SeqCst);
                return;
            }
            gate = self.wait_no_poison(gate);
        }
    }

    #[inline]
    pub(crate) fn try_lock(&self) -> bool {
        // Avoid routinely jumping ahead of registered waiters. A race can still
        // permit occasional barging, which is intentional: ordering is practical,
        // not strict FIFO.
        if self.waiters.load(Ordering::Acquire) != 0 {
            return false;
        }
        self.held
            .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
            .is_ok()
    }

    pub(crate) fn try_lock_for(&self, timeout: Duration) -> bool {
        if self.try_lock() {
            return true;
        }
        if timeout.is_zero() {
            return false;
        }

        let Some(deadline) = Instant::now().checked_add(timeout) else {
            self.lock();
            return true;
        };
        self.waiters.fetch_add(1, Ordering::SeqCst);
        let mut gate = self.lock_gate();

        loop {
            if self
                .held
                .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
                .is_ok()
            {
                self.waiters.fetch_sub(1, Ordering::SeqCst);
                return true;
            }

            let remaining = match deadline.checked_duration_since(Instant::now()) {
                Some(remaining) if !remaining.is_zero() => remaining,
                _ => {
                    self.waiters.fetch_sub(1, Ordering::SeqCst);
                    return false;
                }
            };

            let (next_gate, timed_out) = self.wait_timeout_no_poison(gate, remaining);
            gate = next_gate;
            if timed_out {
                // Check once more while holding the gate so an unlock racing the
                // timeout can still be observed before failure is reported.
                if self
                    .held
                    .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
                    .is_ok()
                {
                    self.waiters.fetch_sub(1, Ordering::SeqCst);
                    return true;
                }
                self.waiters.fetch_sub(1, Ordering::SeqCst);
                return false;
            }
        }
    }

    #[inline]
    pub(crate) fn unlock(&self) {
        // Holding the gate while publishing the unlocked state and notifying a
        // waiter prevents the classic check-then-sleep lost-wakeup race.
        let _gate = self.lock_gate();
        debug_assert!(self.held.load(Ordering::Relaxed));
        self.held.store(false, Ordering::Release);
        if self.waiters.load(Ordering::Acquire) != 0 {
            self.wake.notify_one();
        }
    }

    #[inline]
    fn try_lock_fast(&self) -> bool {
        self.waiters.load(Ordering::Relaxed) == 0
            && self
                .held
                .compare_exchange(false, true, Ordering::Acquire, Ordering::Relaxed)
                .is_ok()
    }

    #[inline]
    fn lock_gate(&self) -> MutexGuard<'_, ()> {
        self.gate.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    #[inline]
    fn wait_no_poison<'a>(&self, guard: MutexGuard<'a, ()>) -> MutexGuard<'a, ()> {
        self.wake
            .wait(guard)
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    #[inline]
    fn wait_timeout_no_poison<'a>(
        &self,
        guard: MutexGuard<'a, ()>,
        timeout: Duration,
    ) -> (MutexGuard<'a, ()>, bool) {
        match self.wake.wait_timeout(guard, timeout) {
            Ok((guard, result)) => (guard, result.timed_out()),
            Err(poisoned) => {
                let (guard, result) = poisoned.into_inner();
                (guard, result.timed_out())
            }
        }
    }
}

impl Default for RawMonitor {
    fn default() -> Self {
        Self::new()
    }
}
