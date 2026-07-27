use crate::{
    ConcurrentExclusiveLock, ConcurrentExclusiveLockError, ConcurrentExclusiveLockScope,
};

/// The permission mode declared by a Pipeline segment.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum ConcurrentExclusiveAccessMode {
    /// Release any permission retained from the previous segment and run without
    /// permission.
    None = 0,
    /// Release any retained permission and acquire an independent Concurrent
    /// segment. Use `ConvergeConcurrent` to continue an existing Concurrent
    /// context.
    Concurrent = 1,
    /// Release retained permission and make one immediate Concurrent attempt.
    /// On failure, skip the segment and continue from `None`.
    TryConcurrent = 2,
    /// Release any retained permission and acquire an independent Exclusive
    /// segment. Use `ConvergeExclusive` to continue an existing Exclusive
    /// context.
    Exclusive = 3,
    /// Release retained permission and make an Idle-only, non-preemptive
    /// Exclusive attempt. On failure, skip the segment and continue from `None`.
    TestExclusive = 4,
    /// Release retained permission and attempt preemptive Exclusive acquisition.
    /// The request may wait for existing Concurrent holders, but can yield and
    /// fail when an in-place upgrade appears.
    TryExclusive = 5,
    /// Continue Concurrent, downgrade Exclusive to Concurrent, or acquire
    /// Concurrent from `None`.
    ConvergeConcurrent = 6,
    /// Continue Exclusive, upgrade Concurrent to Exclusive, or acquire Exclusive
    /// from `None`.
    ConvergeExclusive = 7,
    /// Converge to Exclusive only when the supplied ContextID/EpochID operation
    /// succeeds. On failure, skip the segment, release retained permission, and
    /// continue from `None`.
    TryApplyIDConvergeExclusive = 8,
}

/// The business-ID operation used by a conditional Exclusive convergence.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum IDType {
    /// Switch to a different business ContextID.
    ContextID = 0,
    /// Monotonically advance the business EpochID.
    EpochID = 1,
}

/// A synchronous business segment in a [`ConcurrentExclusiveLockPipeline`].
///
/// The callback must complete all protected work before returning. Starting
/// detached work inside the callback and returning early is outside the protocol.
pub struct ConcurrentExclusiveLockSegment<'a> {
    access: ConcurrentExclusiveAccessMode,
    callback: Box<dyn FnMut() + 'a>,
    context_or_epoch_id: i32,
    id_type: IDType,
}

impl<'a> ConcurrentExclusiveLockSegment<'a> {
    fn new<F>(
        access: ConcurrentExclusiveAccessMode,
        callback: F,
        context_or_epoch_id: i32,
        id_type: IDType,
    ) -> Self
    where
        F: FnMut() + 'a,
    {
        Self {
            access,
            callback: Box::new(callback),
            context_or_epoch_id,
            id_type,
        }
    }

    /// Creates a segment that runs without lock permission.
    pub fn none<F>(callback: F) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::None,
            callback,
            0,
            IDType::ContextID,
        )
    }

    /// Creates an independent Concurrent segment.
    pub fn concurrent<F>(callback: F) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::Concurrent,
            callback,
            0,
            IDType::ContextID,
        )
    }

    /// Creates an immediate TryConcurrent segment.
    pub fn try_concurrent<F>(callback: F) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::TryConcurrent,
            callback,
            0,
            IDType::ContextID,
        )
    }

    /// Creates an independent Exclusive segment.
    pub fn exclusive<F>(callback: F) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::Exclusive,
            callback,
            0,
            IDType::ContextID,
        )
    }

    /// Creates an Idle-only, non-preemptive Exclusive segment.
    pub fn test_exclusive<F>(callback: F) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::TestExclusive,
            callback,
            0,
            IDType::ContextID,
        )
    }

    /// Creates a preemptive TryExclusive segment.
    pub fn try_exclusive<F>(callback: F) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::TryExclusive,
            callback,
            0,
            IDType::ContextID,
        )
    }

    /// Creates a segment that continues, downgrades to, or acquires Concurrent.
    pub fn converge_concurrent<F>(callback: F) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::ConvergeConcurrent,
            callback,
            0,
            IDType::ContextID,
        )
    }

    /// Creates a segment that continues, upgrades to, or acquires Exclusive.
    pub fn converge_exclusive<F>(callback: F) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::ConvergeExclusive,
            callback,
            0,
            IDType::ContextID,
        )
    }

    /// Creates a conditional Exclusive-convergence segment.
    pub fn try_apply_id_converge_exclusive<F>(
        callback: F,
        context_or_epoch_id: i32,
        id_type: IDType,
    ) -> Self
    where
        F: FnMut() + 'a,
    {
        Self::new(
            ConcurrentExclusiveAccessMode::TryApplyIDConvergeExclusive,
            callback,
            context_or_epoch_id,
            id_type,
        )
    }

    /// Returns the declared access mode.
    #[must_use]
    pub fn access(&self) -> ConcurrentExclusiveAccessMode {
        self.access
    }

    /// Returns the ContextID or EpochID used by a conditional segment.
    #[must_use]
    pub fn context_or_epoch_id(&self) -> i32 {
        self.context_or_epoch_id
    }

    /// Returns the business-ID type used by a conditional segment.
    #[must_use]
    pub fn id_type(&self) -> IDType {
        self.id_type
    }

    #[inline]
    fn run(&mut self) {
        (self.callback)();
    }
}

/// A synchronous access-permission pipeline bound to one lock.
///
/// Each segment declares the permission it requires. The Pipeline uses the
/// permission successfully retained from the preceding segment to release,
/// reacquire, continue, upgrade, or downgrade according to the segment mode.
/// Try-type failures skip only the current segment and continue later segments
/// from `None`. If a callback panics, Rust unwinding drops the internal Scope and
/// releases any retained permission before the panic continues.
pub struct ConcurrentExclusiveLockPipeline<'a> {
    lock: &'a ConcurrentExclusiveLock,
}

impl<'a> ConcurrentExclusiveLockPipeline<'a> {
    /// Creates a Pipeline bound to `lock`.
    #[must_use]
    pub fn new(lock: &'a ConcurrentExclusiveLock) -> Self {
        Self { lock }
    }

    /// Returns the bound lock.
    #[must_use]
    pub fn lock(&self) -> &'a ConcurrentExclusiveLock {
        self.lock
    }

    /// Executes all segments in order.
    pub fn do_pipeline(
        &self,
        segments: &mut [ConcurrentExclusiveLockSegment<'_>],
    ) -> Result<(), ConcurrentExclusiveLockError> {
        let mut scope = ConcurrentExclusiveLockScope::new(self.lock);
        let mut last_success = ConcurrentExclusiveAccessMode::None;

        for segment in segments {
            match segment.access {
                ConcurrentExclusiveAccessMode::Concurrent => {
                    match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => {
                            scope.release_concurrent();
                            scope.acquire_concurrent()?;
                        }
                        ConcurrentExclusiveAccessMode::Exclusive => {
                            scope.release_exclusive();
                            scope.acquire_concurrent()?;
                            last_success = ConcurrentExclusiveAccessMode::Concurrent;
                        }
                        _ => {
                            scope.acquire_concurrent()?;
                            last_success = ConcurrentExclusiveAccessMode::Concurrent;
                        }
                    }
                    segment.run();
                }
                ConcurrentExclusiveAccessMode::TryConcurrent => {
                    match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => {
                            scope.release_concurrent();
                            if scope.try_acquire_concurrent()?.is_none() {
                                last_success = ConcurrentExclusiveAccessMode::None;
                                continue;
                            }
                        }
                        ConcurrentExclusiveAccessMode::Exclusive => {
                            scope.release_exclusive();
                            if scope.try_acquire_concurrent()?.is_some() {
                                last_success = ConcurrentExclusiveAccessMode::Concurrent;
                            } else {
                                last_success = ConcurrentExclusiveAccessMode::None;
                                continue;
                            }
                        }
                        _ => {
                            if scope.try_acquire_concurrent()?.is_some() {
                                last_success = ConcurrentExclusiveAccessMode::Concurrent;
                            } else {
                                continue;
                            }
                        }
                    }
                    segment.run();
                }
                ConcurrentExclusiveAccessMode::Exclusive => {
                    match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => {
                            scope.release_concurrent();
                            scope.acquire_exclusive();
                            last_success = ConcurrentExclusiveAccessMode::Exclusive;
                        }
                        ConcurrentExclusiveAccessMode::Exclusive => {
                            scope.release_exclusive();
                            scope.acquire_exclusive();
                        }
                        _ => {
                            scope.acquire_exclusive();
                            last_success = ConcurrentExclusiveAccessMode::Exclusive;
                        }
                    }
                    segment.run();
                }
                ConcurrentExclusiveAccessMode::TestExclusive => {
                    match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => {
                            scope.release_concurrent();
                            if scope.try_acquire_exclusive(false) {
                                last_success = ConcurrentExclusiveAccessMode::Exclusive;
                            } else {
                                last_success = ConcurrentExclusiveAccessMode::None;
                                continue;
                            }
                        }
                        ConcurrentExclusiveAccessMode::Exclusive => {
                            scope.release_exclusive();
                            if !scope.try_acquire_exclusive(false) {
                                last_success = ConcurrentExclusiveAccessMode::None;
                                continue;
                            }
                        }
                        _ => {
                            if scope.try_acquire_exclusive(false) {
                                last_success = ConcurrentExclusiveAccessMode::Exclusive;
                            } else {
                                continue;
                            }
                        }
                    }
                    segment.run();
                }
                ConcurrentExclusiveAccessMode::TryExclusive => {
                    match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => {
                            scope.release_concurrent();
                            if scope.try_acquire_exclusive(true) {
                                last_success = ConcurrentExclusiveAccessMode::Exclusive;
                            } else {
                                last_success = ConcurrentExclusiveAccessMode::None;
                                continue;
                            }
                        }
                        ConcurrentExclusiveAccessMode::Exclusive => {
                            scope.release_exclusive();
                            if !scope.try_acquire_exclusive(true) {
                                last_success = ConcurrentExclusiveAccessMode::None;
                                continue;
                            }
                        }
                        _ => {
                            if scope.try_acquire_exclusive(true) {
                                last_success = ConcurrentExclusiveAccessMode::Exclusive;
                            } else {
                                continue;
                            }
                        }
                    }
                    segment.run();
                }
                ConcurrentExclusiveAccessMode::ConvergeConcurrent => {
                    match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => {}
                        ConcurrentExclusiveAccessMode::Exclusive => {
                            scope.exclusive_to_concurrent();
                            last_success = ConcurrentExclusiveAccessMode::Concurrent;
                        }
                        _ => {
                            scope.acquire_concurrent()?;
                            last_success = ConcurrentExclusiveAccessMode::Concurrent;
                        }
                    }
                    segment.run();
                }
                ConcurrentExclusiveAccessMode::ConvergeExclusive => {
                    match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => {
                            scope.concurrent_to_exclusive();
                            last_success = ConcurrentExclusiveAccessMode::Exclusive;
                        }
                        ConcurrentExclusiveAccessMode::Exclusive => {}
                        _ => {
                            scope.acquire_exclusive();
                            last_success = ConcurrentExclusiveAccessMode::Exclusive;
                        }
                    }
                    segment.run();
                }
                ConcurrentExclusiveAccessMode::TryApplyIDConvergeExclusive => {
                    let applied = match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => match segment.id_type {
                            IDType::ContextID => scope
                                .try_concurrent_to_exclusive_with_switch_context_id(
                                    segment.context_or_epoch_id,
                                ),
                            IDType::EpochID => scope
                                .try_concurrent_to_exclusive_with_raise_epoch_id(
                                    segment.context_or_epoch_id,
                                ),
                        },
                        ConcurrentExclusiveAccessMode::Exclusive => match segment.id_type {
                            IDType::ContextID => {
                                scope.switch_context_id(segment.context_or_epoch_id)
                            }
                            IDType::EpochID => scope.raise_epoch_id(segment.context_or_epoch_id),
                        },
                        _ => match segment.id_type {
                            IDType::ContextID => {
                                scope.switch_context_id(segment.context_or_epoch_id)
                            }
                            IDType::EpochID => scope.raise_epoch_id(segment.context_or_epoch_id),
                        },
                    };

                    if !applied {
                        if last_success == ConcurrentExclusiveAccessMode::Exclusive {
                            scope.release_exclusive();
                        }
                        last_success = ConcurrentExclusiveAccessMode::None;
                        continue;
                    }

                    if last_success == ConcurrentExclusiveAccessMode::Concurrent {
                        last_success = ConcurrentExclusiveAccessMode::Exclusive;
                    } else if last_success != ConcurrentExclusiveAccessMode::Exclusive {
                        scope.acquire_exclusive();
                        last_success = ConcurrentExclusiveAccessMode::Exclusive;
                    }
                    segment.run();
                }
                ConcurrentExclusiveAccessMode::None => {
                    match last_success {
                        ConcurrentExclusiveAccessMode::Concurrent => {
                            scope.release_concurrent();
                        }
                        ConcurrentExclusiveAccessMode::Exclusive => {
                            scope.release_exclusive();
                        }
                        _ => {}
                    }
                    last_success = ConcurrentExclusiveAccessMode::None;
                    segment.run();
                }
            }
        }

        Ok(())
    }
}
