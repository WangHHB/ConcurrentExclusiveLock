use crate::{
    ConcurrentExclusiveLock, ConcurrentExclusiveLockError, ConcurrentExclusiveLockState,
    CONVERGE_ADD, EXCLUSIVE_ADD, MAX_CONCURRENT,
};
use std::marker::PhantomData;
use std::rc::Rc;
use std::time::Duration;

/// An RAII wrapper for [`ConcurrentExclusiveLock`].
///
/// The constructor does not acquire permission. During the scope lifetime, the
/// caller may explicitly acquire, release, upgrade, or downgrade permission.
/// When the scope is dropped, any final permission still recorded by the scope
/// is released automatically. ContextID and EpochID are business state and are
/// never restored by `Drop`.
///
/// A scope is deliberately neither `Send` nor `Sync`: Exclusive permission is
/// thread-affine and must not be moved to another thread before release.
pub struct ConcurrentExclusiveLockScope<'a> {
    lock: &'a ConcurrentExclusiveLock,
    counter_mate: i64,
    _not_send_or_sync: PhantomData<Rc<()>>,
}

impl<'a> ConcurrentExclusiveLockScope<'a> {
    /// Creates an empty scope bound to `lock`.
    #[must_use]
    pub fn new(lock: &'a ConcurrentExclusiveLock) -> Self {
        Self {
            lock,
            counter_mate: 0,
            _not_send_or_sync: PhantomData,
        }
    }

    /// Returns the underlying lock.
    #[must_use]
    pub fn lock(&self) -> &'a ConcurrentExclusiveLock {
        self.lock
    }

    /// Returns an observational lock-state snapshot.
    #[must_use]
    pub fn observed_state(&self) -> ConcurrentExclusiveLockState {
        self.lock.observed_state()
    }

    /// Returns an observational contention snapshot.
    #[must_use]
    pub fn observed_contention(&self) -> i32 {
        self.lock.observed_contention()
    }

    /// Gets ContextID.
    #[must_use]
    pub fn context_id(&self) -> i32 {
        self.lock.context_id()
    }

    /// Unconditionally sets ContextID.
    pub fn set_context_id(&self, value: i32) {
        self.lock.set_context_id(value);
    }

    /// Gets EpochID.
    #[must_use]
    pub fn epoch_id(&self) -> i32 {
        self.lock.epoch_id()
    }

    /// Unconditionally sets EpochID.
    pub fn set_epoch_id(&self, value: i32) {
        self.lock.set_epoch_id(value);
    }

    /// Switches ContextID and reports whether it changed.
    pub fn switch_context_id(&self, new_context_id: i32) -> bool {
        self.lock.switch_context_id(new_context_id)
    }

    /// Monotonically advances EpochID and reports success.
    pub fn raise_epoch_id(&self, new_epoch_id: i32) -> bool {
        self.lock.raise_epoch_id(new_epoch_id)
    }

    /// Acquires Concurrent permission with the default limit.
    pub fn acquire_concurrent(&mut self) -> Result<i32, ConcurrentExclusiveLockError> {
        self.acquire_concurrent_with_max(MAX_CONCURRENT)
    }

    /// Acquires Concurrent permission with a caller limit.
    pub fn acquire_concurrent_with_max(
        &mut self,
        max_concurrent: i32,
    ) -> Result<i32, ConcurrentExclusiveLockError> {
        let id = self.lock.acquire_concurrent_with_max(max_concurrent)?;
        self.counter_mate += 1;
        Ok(id)
    }

    /// Makes one immediate Concurrent attempt with the default limit.
    pub fn try_acquire_concurrent(
        &mut self,
    ) -> Result<Option<i32>, ConcurrentExclusiveLockError> {
        self.try_acquire_concurrent_with_max(MAX_CONCURRENT)
    }

    /// Makes one immediate Concurrent attempt with a caller limit.
    pub fn try_acquire_concurrent_with_max(
        &mut self,
        max_concurrent: i32,
    ) -> Result<Option<i32>, ConcurrentExclusiveLockError> {
        let id = self.lock.try_acquire_concurrent_with_max(max_concurrent)?;
        if id.is_some() {
            self.counter_mate += 1;
        }
        Ok(id)
    }

    /// Attempts Concurrent permission within `timeout` with the default limit.
    pub fn try_acquire_concurrent_for(
        &mut self,
        timeout: Duration,
    ) -> Result<Option<i32>, ConcurrentExclusiveLockError> {
        self.try_acquire_concurrent_for_with_max(timeout, MAX_CONCURRENT)
    }

    /// Attempts Concurrent permission within `timeout` with a caller limit.
    pub fn try_acquire_concurrent_for_with_max(
        &mut self,
        timeout: Duration,
        max_concurrent: i32,
    ) -> Result<Option<i32>, ConcurrentExclusiveLockError> {
        let id = self
            .lock
            .try_acquire_concurrent_for_with_max(timeout, max_concurrent)?;
        if id.is_some() {
            self.counter_mate += 1;
        }
        Ok(id)
    }

    /// Releases one Concurrent permission recorded by the scope.
    pub fn release_concurrent(&mut self) {
        self.lock.release_concurrent();
        self.counter_mate -= 1;
    }

    /// Acquires preemptive Exclusive permission.
    pub fn acquire_exclusive(&mut self) {
        self.lock.acquire_exclusive();
        self.counter_mate += EXCLUSIVE_ADD;
    }

    /// Attempts Exclusive permission according to `preempt_concurrent`.
    pub fn try_acquire_exclusive(&mut self, preempt_concurrent: bool) -> bool {
        let success = self.lock.try_acquire_exclusive(preempt_concurrent);
        if success {
            self.counter_mate += EXCLUSIVE_ADD;
        }
        success
    }

    /// Attempts preemptive Exclusive permission within `timeout`.
    pub fn try_acquire_exclusive_for(&mut self, timeout: Duration) -> bool {
        let success = self.lock.try_acquire_exclusive_for(timeout);
        if success {
            self.counter_mate += EXCLUSIVE_ADD;
        }
        success
    }

    /// Releases Exclusive permission recorded by the scope.
    pub fn release_exclusive(&mut self) {
        self.lock.release_exclusive();
        self.counter_mate -= EXCLUSIVE_ADD;
    }

    /// Downgrades the scope's current Exclusive permission to Concurrent.
    pub fn exclusive_to_concurrent(&mut self) {
        self.lock.exclusive_to_concurrent();
        self.counter_mate -= CONVERGE_ADD;
    }

    /// Upgrades the scope's current Concurrent permission to Exclusive.
    pub fn concurrent_to_exclusive(&mut self) {
        self.lock.concurrent_to_exclusive();
        self.counter_mate += CONVERGE_ADD;
    }

    /// Conditionally upgrades by switching ContextID.
    ///
    /// On failure, the original Concurrent permission is already released and
    /// the scope records no permission for that access chain.
    pub fn try_concurrent_to_exclusive_with_switch_context_id(
        &mut self,
        new_context_id: i32,
    ) -> bool {
        let success = self
            .lock
            .try_concurrent_to_exclusive_with_switch_context_id(new_context_id);
        if success {
            self.counter_mate += CONVERGE_ADD;
        } else {
            self.counter_mate -= 1;
        }
        success
    }

    /// Conditionally upgrades by monotonically advancing EpochID.
    ///
    /// On failure, the original Concurrent permission is already released and
    /// the scope records no permission for that access chain.
    pub fn try_concurrent_to_exclusive_with_raise_epoch_id(
        &mut self,
        new_epoch_id: i32,
    ) -> bool {
        let success = self
            .lock
            .try_concurrent_to_exclusive_with_raise_epoch_id(new_epoch_id);
        if success {
            self.counter_mate += CONVERGE_ADD;
        } else {
            self.counter_mate -= 1;
        }
        success
    }

    /// Releases any permission still recorded by this scope immediately.
    ///
    /// Calling this method is optional; `Drop` performs the same final release.
    pub fn release_all(&mut self) {
        if self.counter_mate != 0 {
            self.lock.free_release(-self.counter_mate);
            self.counter_mate = 0;
        }
    }
}

impl Drop for ConcurrentExclusiveLockScope<'_> {
    fn drop(&mut self) {
        self.release_all();
    }
}
