using System;
using System.Runtime.CompilerServices;

namespace IntomicLib
{
    /// <summary>
    /// A convenience wrapper for using <see cref="ConcurrentExclusiveLock"/>.
    /// </summary>
    /// <remarks>
    /// During the lifetime of this scope, the caller may transition between Concurrent and Exclusive states according to the protocol,
    /// including Concurrent acquisition, Exclusive acquisition, in-place upgrade to Exclusive, and downgrade from Exclusive to Concurrent.
    /// The lock does not provide recursive nesting privileges.
    ///
    /// The caller may explicitly release the currently held access permission. If it is not released explicitly,
    /// <see cref="Dispose"/> automatically performs the corresponding release based on the state currently held by the scope.
    ///
    /// Dispose releases only the access permission ultimately held by the scope; it does not restore ContextID or EpochID.
    /// ContextID and EpochID represent business state associated with the lock and must be set, switched, advanced, or restored by business code.
    ///
    /// This type simplifies lock-state management in a using scope and reduces release errors caused by exception paths, upgrade/downgrade paths, or early returns.
    /// A scope instance is intended for a single calling context, does not support concurrent use by multiple threads, and should not be copied.
    /// </remarks>
    public struct ConcurrentExclusiveLockScope : IDisposable
    {
        private readonly ConcurrentExclusiveLock Locker;
        private long CounterMate;

        /// <summary>
        /// An observational snapshot of the lock's current state.
        /// </summary>
        /// <remarks>
        /// This value reflects only the lock state observed at the instant of the read. It must not be treated as an exact state check and is intended only for diagnostics and monitoring.
        ///
        /// After a preemptive Exclusive request enters the contention window, this value may be observed as Exclusive
        /// even while the request is still waiting for current Concurrent holders to release.
        /// Therefore, ObservedState represents the lock's current access tendency or transition state;
        /// it does not mean that a thread is already executing Exclusive business code.
        /// </remarks>
        public ConcurrentExclusiveLockState ObservedState { get { return Locker.ObservedState; } }

        /// <summary>
        /// Gets an observational indicator of the lock's current contention pressure.
        /// </summary>
        /// <remarks>
        /// This value is a snapshot of contention pressure observed at the instant of the read and is intended only for diagnostics, monitoring, or scheduling guidance.
        ///
        /// Therefore, the value is 0 in a purely Concurrent scenario;
        /// once Exclusive pressure exists, it reports the currently observed combined Concurrent and Exclusive pressure.
        /// </remarks>
        public int ObservedContention { get { return Locker.ObservedContention; } }

        /// <summary>
        /// Atomically gets or sets the business context ID associated with the current lock.
        /// </summary>
        /// <remarks>
        /// This property stores additional business state outside the lock protocol, such as an identifier for the current business context,
        /// allowing the same context to be recognized and redundant permission acquisition to be avoided.
        ///
        /// Setting this property directly unconditionally overwrites the current ContextID.
        /// To determine whether the context changed, use <see cref="SwitchContextID(int)"/>.
        ///
        /// A value of 0 indicates that no context ID is set.
        /// The meaning, allocation, validation, and cleanup of nonzero values are the caller's responsibility.
        /// </remarks>
        public int ContextID { get { return Locker.ContextID; } set { Locker.ContextID = value; } }

        /// <summary>
        /// Atomically gets or sets the business phase ID associated with the current lock.
        /// </summary>
        /// <remarks>
        /// This property stores additional business state outside the lock protocol, such as the lifecycle phase of an Exclusive region,
        /// a data version, a processing batch, or another business phase.
        ///
        /// Setting this property directly unconditionally overwrites the current EpochID.
        /// It does not check whether the phase advances and does not prevent rollback or reset.
        /// To require the phase ID to increase monotonically, use <see cref="RaiseEpochID(int)"/>.
        ///
        /// A value of 0 indicates that no phase ID is set.
        /// The meaning, allocation, validation, and cleanup of nonzero values are the caller's responsibility.
        /// </remarks>
        public int EpochID { get { return Locker.EpochID; } set { Locker.EpochID = value; } }

        /// <summary>
        /// Sets a new ContextID and returns whether this operation changed the previous value.
        /// Returns false if the new value is equal to the previous value.
        /// </summary>
        /// <remarks>
        /// This method only switches the business context ID.
        /// </remarks>
        /// <param name="newContextID">The new context ID.</param>
        /// <returns>True if ContextID was set to a different value; false if the new value was equal to the previous value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SwitchContextID(int newContextID)
        {
            return Locker.SwitchContextID(newContextID);
        }

        /// <summary>
        /// Attempts to advance EpochID to a new phase and returns whether the advance succeeded.
        /// Returns false if the new value is less than or equal to the current value.
        /// </summary>
        /// <remarks>
        /// This method only advances the business phase ID.
        /// </remarks>
        /// <param name="newEpochID">The new phase ID. It must be greater than the current EpochID for the advance to succeed.</param>
        /// <returns>True if EpochID was successfully advanced to a greater value; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool RaiseEpochID(int newEpochID)
        {
            return Locker.RaiseEpochID(newEpochID);
        }

        /// <summary>
        /// Initializes a scope bound to the specified <see cref="ConcurrentExclusiveLock"/>.
        /// </summary>
        /// <remarks>
        /// The constructor itself does not acquire any Concurrent or Exclusive access permission.
        /// The caller must explicitly acquire Concurrent or Exclusive permission, or perform an in-place upgrade or downgrade through this scope.
        ///
        /// <see cref="Dispose"/> automatically performs the corresponding release based on the final state held by the scope,
        /// simplifying lock-state management within a using scope.
        /// </remarks>
        /// <param name="locker">
        /// The <see cref="ConcurrentExclusiveLock"/> instance to be managed by this scope.
        /// </param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ConcurrentExclusiveLockScope(ConcurrentExclusiveLock locker)
        {
            Locker = locker;
            CounterMate = 0;
        }


        /// <summary>
        /// Waits to acquire Concurrent access permission.
        /// </summary>
        /// <remarks>
        /// This method returns when entry into the Concurrent state is permitted.
        ///
        /// After acquiring Concurrent permission, the caller may explicitly release it later by calling <c>ReleaseConcurrent()</c>;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        /// If the held state is later converted through another protocol, it must be released according to the converted state.
        ///
        /// <paramref name="maxConcurrent"/> limits the maximum Concurrent ID allowed for this acquisition.
        /// <paramref name="maxConcurrent"/> must not be less than 1.
        /// </remarks>
        /// <param name="maxConcurrent">
        /// The maximum allowed Concurrent ID.
        /// Valid return values are in the range [1, maxConcurrent].
        /// </param>
        /// <returns>
        /// Returns the Concurrent ID acquired by this operation, in the range [1, maxConcurrent].
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="maxConcurrent"/> is less than 1.</exception>
        /// <exception cref="ConcurrentExclusiveLockCapacityExceededException">
        /// Thrown when the number of simultaneously held Concurrent permissions exceeds the internal limit at runtime.
        /// The current implementation supports a 31-bit Concurrent count space; this limit is effectively unreachable in practical runtime environments.
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int AcquireConcurrent(int maxConcurrent = ConcurrentExclusiveLock.MaxConcurrent)
        {
            int concurrentID = Locker.AcquireConcurrent(maxConcurrent);
            CounterMate++;
            return concurrentID;
        }

        /// <summary>
        /// Attempts to acquire Concurrent access permission.
        /// </summary>
        /// <remarks>
        /// This method makes a single attempt to enter the Concurrent state and does not wait for the lock state to change.
        ///
        /// A nonzero return value means Concurrent permission was acquired, and the caller may explicitly release it later by calling <c>ReleaseConcurrent()</c>;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        /// If the held state is later converted through another protocol, it must be released according to the converted state.
        ///
        /// <paramref name="maxConcurrent"/> limits the maximum Concurrent ID allowed for this acquisition.
        /// <paramref name="maxConcurrent"/> must not be less than 1.
        /// </remarks>
        /// <param name="maxConcurrent">
        /// The maximum allowed Concurrent ID.
        /// Successful return values are in the range [1, maxConcurrent].
        /// </param>
        /// <returns>
        /// Returns the Concurrent ID acquired by this operation, in the range [1, maxConcurrent];
        /// returns 0 if Concurrent permission was not acquired.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="maxConcurrent"/> is less than 1.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int TryAcquireConcurrent(int maxConcurrent = ConcurrentExclusiveLock.MaxConcurrent)
        {
            int concurrentID = Locker.TryAcquireConcurrent(maxConcurrent);
            if (concurrentID != 0)
            {
                CounterMate++;
            }
            return concurrentID;
        }

        /// <summary>
        /// Attempts to acquire Concurrent access permission within the specified time.
        /// </summary>
        /// <remarks>
        /// This method waits, within the specified timeout, for the lock to enter a state in which Concurrent permission can be acquired.
        ///
        /// A nonzero return value means Concurrent permission was acquired, and the caller may explicitly release it later by calling <c>ReleaseConcurrent()</c>;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        /// If the held state is later converted through another protocol, it must be released according to the converted state.
        ///
        /// A <paramref name="millisecondsTimeout"/> value of 0 performs a single attempt without waiting; a negative value means an infinite wait.
        ///
        /// <paramref name="maxConcurrent"/> limits the maximum Concurrent ID allowed for this acquisition.
        /// <paramref name="maxConcurrent"/> must not be less than 1.
        /// If <paramref name="millisecondsTimeout"/> is greater than or equal to 0, the method waits at most until the timeout and then returns 0;
        /// if <paramref name="millisecondsTimeout"/> is negative, the method waits indefinitely.
        /// </remarks>
        /// <param name="millisecondsTimeout">
        /// The maximum number of milliseconds to wait for Concurrent permission.
        /// 0 performs a single attempt without waiting; a negative value means an infinite wait.
        /// </param>
        /// <param name="maxConcurrent">
        /// The maximum allowed Concurrent ID.
        /// Successful return values are in the range [1, maxConcurrent].
        /// </param>
        /// <returns>
        /// Returns the Concurrent ID acquired by this operation, in the range [1, maxConcurrent];
        /// returns 0 if Concurrent permission was not acquired within the specified time.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="maxConcurrent"/> is less than 1.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int TryAcquireConcurrent(int millisecondsTimeout, int maxConcurrent = ConcurrentExclusiveLock.MaxConcurrent)
        {
            int concurrentID = Locker.TryAcquireConcurrent(millisecondsTimeout, maxConcurrent);
            if (concurrentID != 0)
            {
                CounterMate++;
            }
            return concurrentID;
        }

        /// <summary>
        /// Releases one currently held Concurrent access permission.
        /// </summary>
        /// <remarks>
        /// This method must be called while the current scope holds Concurrent permission.
        ///
        /// After this call, the current scope no longer holds that Concurrent access permission,
        /// and no business code that depends on that permission should continue to execute.
        ///
        /// A permission released explicitly will not be released again by <see cref="Dispose"/>.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseConcurrent()
        {
            Locker.ReleaseConcurrent();
            CounterMate--;
        }

        /// <summary>
        /// Waits to acquire Exclusive access permission.
        /// </summary>
        /// <remarks>
        /// This method requests preemptive Exclusive permission.
        ///
        /// After the request is made, the lock blocks new Concurrent entrants
        /// and waits for current Concurrent holders to release.
        /// This method returns after the lock enters the Exclusive state.
        ///
        /// After acquiring Exclusive permission, the caller may explicitly release it later by calling <c>ReleaseExclusive()</c>,
        /// or downgrade it to Concurrent through <c>ExclusiveToConcurrent()</c> and then release it according to the Concurrent protocol;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AcquireExclusive()
        {
            Locker.AcquireExclusive();
            CounterMate += ConcurrentExclusiveLock.Exclusive_Add;
        }

        /// <summary>
        /// Attempts to acquire Exclusive access permission.
        /// </summary>
        /// <remarks>
        /// When <paramref name="preemptConcurrent"/> is true,
        /// this method requests Exclusive permission preemptively.
        /// If Concurrent holders currently exist, the lock blocks new Concurrent entrants
        /// and participates in Exclusive contention;
        /// a successful Exclusive request waits for current Concurrent holders to release before acquiring Exclusive permission.
        ///
        /// When <paramref name="preemptConcurrent"/> is false,
        /// this method does not preempt Concurrent holders.
        /// It returns false immediately whenever Concurrent or Exclusive permission is currently held.
        ///
        /// A return value of true means Exclusive permission was acquired, and the caller may explicitly release it later by calling <c>ReleaseExclusive()</c>,
        /// or downgrade it to Concurrent through <c>ExclusiveToConcurrent()</c> and then release it according to the Concurrent protocol;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        ///
        /// A return value of false means Exclusive permission was not acquired, and the caller must not execute business code that depends on Exclusive permission.
        /// </remarks>
        /// <param name="preemptConcurrent">
        /// true allows Concurrent holders to be preempted;
        /// false does not preempt Concurrent holders and can acquire Exclusive permission only when the lock is Idle.
        /// </param>
        /// <returns>
        /// true means Exclusive permission was acquired;
        /// false means Exclusive permission was not acquired.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAcquireExclusive(bool preemptConcurrent = true)
        {
            bool sucess = Locker.TryAcquireExclusive(preemptConcurrent);
            if (sucess)
            {
                CounterMate += ConcurrentExclusiveLock.Exclusive_Add;
            }
            return sucess;
        }

        /// <summary>
        /// Attempts to acquire Exclusive access permission within the specified time.
        /// </summary>
        /// <remarks>
        /// This method attempts to acquire preemptive Exclusive permission within the specified timeout.
        ///
        /// A <paramref name="millisecondsTimeout"/> value of 0 performs a single attempt without waiting;
        /// a negative value means an infinite wait.
        ///
        /// A return value of true means Exclusive permission was acquired, and the caller may explicitly release it later by calling <c>ReleaseExclusive()</c>,
        /// or downgrade it to Concurrent through <c>ExclusiveToConcurrent()</c> and then release it according to the Concurrent protocol;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        ///
        /// A return value of false means Exclusive permission was not acquired within the specified time,
        /// and the caller must not execute business code that depends on Exclusive permission.
        /// </remarks>
        /// <param name="millisecondsTimeout">
        /// The maximum number of milliseconds to wait for Exclusive permission.
        /// 0 performs a single attempt without waiting; a negative value means an infinite wait.
        /// </param>
        /// <returns>
        /// true means Exclusive permission was acquired;
        /// false means Exclusive permission was not acquired within the specified time.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryAcquireExclusive(int millisecondsTimeout)
        {
            bool sucess = Locker.TryAcquireExclusive(millisecondsTimeout);
            if (sucess)
            {
                CounterMate += ConcurrentExclusiveLock.Exclusive_Add;
            }
            return sucess;
        }

        /// <summary>
        /// Releases the currently held Exclusive access permission.
        /// </summary>
        /// <remarks>
        /// This method must be called while the current scope holds Exclusive permission.
        ///
        /// After this call, the current scope no longer holds Exclusive access permission,
        /// and no business code that depends on Exclusive permission should continue to execute.
        ///
        /// After the release completes, the lock returns to normal contention,
        /// and waiting Concurrent or Exclusive requests continue attempting to enter according to the lock's contention rules.
        ///
        /// To retain Concurrent access after ending Exclusive access,
        /// call <c>ExclusiveToConcurrent()</c>
        /// instead of this method.
        ///
        /// A permission released explicitly will not be released again by <see cref="Dispose"/>.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseExclusive()
        {
            Locker.ReleaseExclusive();
            CounterMate -= ConcurrentExclusiveLock.Exclusive_Add;
        }

        /// <summary>
        /// Downgrades the currently held Exclusive access permission to Concurrent access permission.
        /// </summary>
        /// <remarks>
        /// This method must be called while the current scope holds Exclusive permission.
        ///
        /// After this call, the current scope no longer holds Exclusive access permission,
        /// and no business code that depends on Exclusive permission should continue to execute.
        ///
        /// The current scope continues to hold Concurrent access permission,
        /// which may later be explicitly released by calling <c>ReleaseConcurrent()</c>;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        ///
        /// This method preserves a continuous Concurrent access context after Exclusive modification is complete,
        /// avoiding the access window that would result from releasing Exclusive and then reacquiring Concurrent.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExclusiveToConcurrent()
        {
            Locker.ExclusiveToConcurrent();
            CounterMate -= ConcurrentExclusiveLock.Converge_Add;
        }

        /// <summary>
        /// Upgrades the current permission from Concurrent to Exclusive while Concurrent permission is held.
        /// Upgraded Exclusive requests prevent ordinary Exclusive requests from acquiring permission until all upgrades complete.
        /// </summary>
        /// <remarks>
        /// This method must be called while the current scope holds Concurrent permission.
        ///
        /// After this call, the lock blocks new Concurrent entrants from the current conversion window,
        /// and successfully upgraded callers execute in isolation under Exclusive semantics.
        /// The acquired permission may later be explicitly released by calling <c>ReleaseExclusive()</c>;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        ///
        /// This method preserves a continuous Exclusive access context after the Concurrent phase is complete,
        /// avoiding the access window that would result from releasing Concurrent and then reacquiring Exclusive.
        /// Exclusive regions produced by multiple Concurrent upgrades remain serialized.
        /// A large number of upgrade requests may create significant contention pressure;
        /// when business logic can use a context or phase to select the callers that actually need to upgrade,
        /// prefer <see cref="TryConcurrentToExclusiveWithSwitchContextID(int)"/>
        /// or <see cref="TryConcurrentToExclusiveWithRaiseEpochID(int)"/>.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ConcurrentToExclusive()
        {
            Locker.ConcurrentToExclusive();
            CounterMate += ConcurrentExclusiveLock.Converge_Add;
        }

        /// <summary>
        /// While holding Concurrent permission, attempts to upgrade the current permission to Exclusive through SwitchContextID.
        /// Upgraded Exclusive requests prevent ordinary Exclusive requests from acquiring permission until all upgrades complete.
        /// When multiple Concurrent holders call this method with the same newContextID, only the holder that successfully switches ContextID can upgrade.
        /// When callers use different newContextID values, multiple callers may succeed separately, but their resulting Exclusive regions are still isolated and ordered.
        /// On success, the current scope holds Exclusive permission; on failure, the Concurrent permission held by the current scope is released automatically.
        /// </summary>
        /// <remarks>
        /// This method must be called while the current scope holds Concurrent permission.
        ///
        /// After this call, the lock blocks new Concurrent entrants from the current conversion window,
        /// and successfully upgraded callers execute in isolation under Exclusive semantics.
        /// Whether ContextID is switched successfully is determined by <c>SwitchContextID(newContextID)</c>.
        ///
        /// The acquired permission may later be explicitly released by calling <c>ReleaseExclusive()</c>;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        /// After failure, the original Concurrent permission has already been released and <c>ReleaseConcurrent()</c> must not be called again.
        /// Exclusive regions produced by multiple Concurrent upgrades remain serialized.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryConcurrentToExclusiveWithSwitchContextID(int newContextID)
        {
            bool success = Locker.TryConcurrentToExclusiveWithSwitchContextID(newContextID);
            if (success)
            {
                CounterMate += ConcurrentExclusiveLock.Converge_Add;
            }
            else
            {
                CounterMate--;
            }
            return success;
        }

        /// <summary>
        /// While holding Concurrent permission, attempts to upgrade the current permission to Exclusive through RaiseEpochID.
        /// Upgraded Exclusive requests prevent ordinary Exclusive requests from acquiring permission until all upgrades complete.
        /// The caller can advance the phase and upgrade successfully only when newEpochID is greater than the current EpochID.
        /// When newEpochID is less than or equal to the current EpochID, the upgrade fails and EpochID remains unchanged.
        /// When multiple callers use distinct increasing newEpochID values, multiple callers may succeed separately, but their resulting Exclusive regions are still isolated and ordered.
        /// On success, the current scope holds Exclusive permission; on failure, the Concurrent permission held by the current scope is released automatically.
        /// </summary>
        /// <remarks>
        /// This method must be called while the current scope holds Concurrent permission.
        ///
        /// This method is suitable when EpochID represents a lifecycle, version number, or phase number.
        /// EpochID may advance only to a greater value; it cannot remain unchanged or move backward.
        /// After this call, the lock blocks new Concurrent entrants from the current conversion window,
        /// and successfully upgraded callers execute in isolation under Exclusive semantics.
        /// Whether EpochID is advanced successfully is determined by <c>RaiseEpochID(newEpochID)</c>.
        ///
        /// The acquired permission may later be explicitly released by calling <c>ReleaseExclusive()</c>;
        /// if it is not released explicitly, <see cref="Dispose"/> releases it according to the scope's final state.
        /// After failure, the original Concurrent permission has already been released and <c>ReleaseConcurrent()</c> must not be called again.
        /// Exclusive regions produced by multiple Concurrent upgrades remain serialized.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryConcurrentToExclusiveWithRaiseEpochID(int newEpochID)
        {
            bool success = Locker.TryConcurrentToExclusiveWithRaiseEpochID(newEpochID);
            if (success)
            {
                CounterMate += ConcurrentExclusiveLock.Converge_Add;
            }
            else
            {
                CounterMate--;
            }
            return success;
        }


        /// <summary>
        /// Releases any access permission still held by this scope.
        /// </summary>
        /// <remarks>
        /// Dispose releases Concurrent or Exclusive permission solely according to the final held state recorded by the scope.
        /// Permissions already released explicitly are not released again.
        /// Dispose does not restore or clear ContextID or EpochID.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (CounterMate != 0)
            {
                Locker._FreeRelease(-CounterMate);
                CounterMate = 0;
            }
        }
    }
}