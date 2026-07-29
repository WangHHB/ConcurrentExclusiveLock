/* Design notes:
 * This lock expresses access permissions rather than read/write intent.
 *
 * Concurrent Concurrent means that the current operation may enter together with other Concurrent operations;
 * Exclusive Exclusive means that the current operation must enter exclusively and cannot run concurrently with any other operation.
 *
 * Business code inside an Exclusive region may still contain substantial read logic;
 * Concurrent a Concurrent region may also perform modifications that the business logic guarantees will not conflict.
 *
 * Therefore, Concurrent / Exclusive describes whether concurrent access is permitted,
 * not whether the code itself reads or writes data.
 *
 * Concurrent Ordinary Concurrent acquisition and release primarily use lightweight atomic counting and do not enter the Monitor scheduling queue.
 *
 * Exclusive Exclusive is preemptive. After an Exclusive request enters the contention window, it blocks new
 * Concurrent Concurrent operations from entering and waits for existing Concurrent holders to leave naturally.
 * This is the defining characteristic of this lock.
 *
 * Ordinary Exclusive acquisition, as well as in-place conversion from Concurrent to Exclusive,
 * uses the mutual exclusion, waiting, signaling, and ordering mechanisms of Monitor(lock) for exclusive scheduling.
 *
 * This lock does not additionally guarantee strict FIFO fairness or stronger scheduling fairness than Monitor.
 * The actual execution order of threads is still affected by operating-system scheduling, CPU topology, cache state,
 * system load, and the duration of caller business logic.
 *
 * ContextID ContextID identifies a business context and can be used to avoid repeated Exclusive acquisition within the same context.
 * EpochID EpochID represents a monotonically advancing lifecycle, version, or phase.
 * Both are business identifiers outside the lock protocol; their meaning, allocation, validation, and cleanup are the caller's responsibility.
 *
 * Concurrent Concurrent does not provide recursive nesting semantics.
 * Do not request an ordinary Exclusive permission while holding Concurrent; use the in-place upgrade protocol instead.
 * Do not request an ordinary Concurrent permission while holding Exclusive; use the in-place downgrade protocol instead.
 * Exclusive Exclusive does not provide recursive counting. To support repeated entry within the same business context, use ContextID
 * to establish a protocol at the business layer.
 *
 * ConcurrentExclusiveLock ConcurrentExclusiveLock is a value type, and a default-initialized instance is unusable.
 * Instances must be created with ConcurrentExclusiveLock.Create().
 * The actual state is stored in the internal CELToken; copying a ConcurrentExclusiveLock value does not copy the lock state.
 *
 * CELToken The core state fields of CELToken occupy 128 bits:
 * Counter Counter records the Concurrent / Exclusive counts, ContextID records the business context,
 * EpochID and EpochID records the monotonically advancing lifecycle version.
 * Monitor Monitor operates directly on the CELToken instance; no separate synchronization object is created.
 *
 * Theoretical limit:
 * The lock uses a 31-bit count space to record the number of Concurrent holders at a given instant.
 * This is not the total number of requests issued by callers and is effectively unreachable in real workloads;
 * it is documented only to make the design boundary explicit.
 */

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace IntomicLib
{
    internal class CELToken
    {
        public long Counter;  // The lower 32 bits store the Concurrent count; the upper 32 bits store the Exclusive count.

        public int _ContextID;  // Business context ID.

        public int _EpochID;  // Monotonically advancing lifecycle version ID.
    }


    /// <summary>
    /// Provides a high-performance, non-recursive synchronization lock based on Concurrent / Exclusive access permissions.
    /// </summary>
    /// <remarks>
    /// This lock expresses access permissions rather than read/write intent.
    /// Concurrent Concurrent means that the current operation may enter together with other Concurrent operations;
    /// Exclusive Exclusive means that the current operation must enter exclusively and cannot run concurrently with any other operation.
    /// This lock does not provide recursive nesting privileges.
    ///
    /// This lock supports preemptive Exclusive acquisition, in-place upgrade from Concurrent to Exclusive,
    /// Exclusive in-place downgrade from Exclusive to Concurrent, and observational snapshots of state and contention.
    ///
    /// Concurrent Ordinary Concurrent acquisition and release primarily use lightweight atomic counting and do not enter the Monitor scheduling queue.
    /// Ordinary Exclusive acquisition, as well as conversion from Concurrent to Exclusive,
    /// uses the mutual exclusion, waiting, signaling, and ordering mechanisms of Monitor for exclusive scheduling.
    ///
    /// This lock does not guarantee strict FIFO ordering or stronger scheduling fairness than Monitor.
    /// The actual execution order of threads is still affected by operating-system scheduling, CPU topology, cache state, system load,
    /// and the duration of caller business logic.
    ///
    /// This type is intended for synchronous, non-recursive, highly concurrent shared-state scenarios,
    /// especially server-side scenarios with many fine-grained lock instances.
    /// 
    /// Exclusive Exclusive permission is thread-affine.
    /// After acquiring Exclusive, it must be released or downgraded by the same thread that acquired it;
    /// an execution flow that depends on Exclusive permission must not cross an <c>await</c>.
    ///
    /// This type is a value type, and a default-initialized instance is unusable.
    /// Use <see cref="Create"/> to create an instance.
    /// </remarks>
    public readonly struct ConcurrentExclusiveLock
    {
        /// <summary>
        /// The maximum number of Concurrent holders supported by a single lock instance.
        /// </summary>
        public const int MaxConcurrent = int.MaxValue;
        internal const long Exclusive_Add = 4294967296;
        internal const long Converge_Add = 4294967295;
        internal const int Shift_Count = 32;

        private readonly CELToken _Token;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private CELToken GetToken()
        {
            if (_Token == null)
            {
                throw new InvalidOperationException("ConcurrentExclusiveLock has not been initialized. Use ConcurrentExclusiveLock.Create() to create an instance.");
            }
            return _Token;
        }

        private ConcurrentExclusiveLock(CELToken token)
        {
            _Token = token;
        }

        /// <summary>
        /// Creates a correctly initialized <see cref="ConcurrentExclusiveLock"/> instance.
        /// </summary>
        /// <remarks>
        /// <see cref="ConcurrentExclusiveLock"/> <see cref="ConcurrentExclusiveLock"/> is a value type, and a default-initialized instance is unusable.
        /// Always use this method to create a new instance.
        ///
        /// Copying the returned <see cref="ConcurrentExclusiveLock"/> value does not copy the lock state;
        /// the copied value still references the same internal lock state.
        /// </remarks>
        public static ConcurrentExclusiveLock Create()
        {
            return new ConcurrentExclusiveLock(new CELToken());
        }

        /// <summary>
        /// An observational snapshot of the current lock state.
        /// </summary>
        /// <remarks>
        /// This value only represents the state observed at the instant of the read. It must not be used as an authoritative state check and is intended only for diagnostics and monitoring.
        ///
        /// After a preemptive Exclusive request enters the contention window, this value may already be observed as Exclusive
        /// even while the request is still waiting for current Concurrent holders to release.
        /// Therefore, ObservedState represents the lock's current access tendency or transition state;
        /// it does not mean that a thread is already executing Exclusive business code.
        /// </remarks>
        public ConcurrentExclusiveLockState ObservedState
        {
            get
            {
                long counter = Volatile.Read(ref GetToken().Counter);
                if (counter >= Exclusive_Add)
                {
                    return ConcurrentExclusiveLockState.Exclusive;
                }
                else if (counter > 0)
                {
                    return ConcurrentExclusiveLockState.Concurrent;
                }
                else
                {
                    return ConcurrentExclusiveLockState.Idle;
                }
            }
        }

        /// <summary>
        /// Gets an observational indicator of the current contention pressure on the lock.
        /// </summary>
        /// <remarks>
        /// This value is a snapshot of contention pressure observed at the instant of the read and is intended only for diagnostics, monitoring, or scheduling reference.
        ///
        /// Therefore, this value is 0 in a purely Concurrent scenario;
        /// once Exclusive pressure exists, it returns the currently observed scale of Concurrent + Exclusive pressure.
        /// </remarks>
        public int ObservedContention
        {
            get
            {
                long counter = Volatile.Read(ref GetToken().Counter);
                int exc = (int)(counter >> Shift_Count);
                return exc == 0 ? 0 : (int)counter + exc;
            }
        }

        /// <summary>
        /// Atomically gets or sets the business context ID associated with the current lock.
        /// </summary>
        /// <remarks>
        /// This property records additional business state outside the lock protocol,
        /// for example, the current business context so that identical contexts can be recognized.
        ///
        /// Setting this property directly unconditionally overwrites the current ContextID.
        /// To determine whether a different context was applied, use <see cref="SwitchContextID(int)"/>.
        ///
        /// A value of 0 means that no context ID is set.
        /// The meaning, allocation, validation, and cleanup of nonzero values are the caller's responsibility.
        /// </remarks>
        public int ContextID
        {
            get
            {
                return Volatile.Read(ref GetToken()._ContextID);
            }
            set
            {
                Volatile.Write(ref GetToken()._ContextID, value);
            }
        }

        /// <summary>
        /// Atomically gets or sets the business phase ID associated with the current lock.
        /// </summary>
        /// <remarks>
        /// This property records additional business state outside the lock protocol, such as the lifecycle phase of an exclusive region,
        /// a data version, processing batch, or another business phase.
        ///
        /// Setting this property directly unconditionally overwrites the current EpochID;
        /// it does not check whether the phase advances and does not prevent rollback or reset.
        /// To ensure that the phase ID can only increase, use <see cref="RaiseEpochID(int)"/>.
        ///
        /// A value of 0 means that no phase ID is set.
        /// The meaning, allocation, validation, and cleanup of nonzero values are the caller's responsibility.
        /// </remarks>
        public int EpochID
        {
            get
            {
                return Volatile.Read(ref GetToken()._EpochID);
            }
            set
            {
                Volatile.Write(ref GetToken()._EpochID, value);
            }
        }

        /// <summary>
        /// Sets a new ContextID and returns whether this operation changed the previous value.
        /// Returns false if the new value is the same as the previous value.
        /// </summary>
        /// <remarks>
        /// This method only switches the business context ID.
        /// </remarks>
        /// <param name="newContextID">The new context ID.</param>
        /// <returns>True if ContextID was changed to a different value; false if the new value was already current.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SwitchContextID(int newContextID)
        {
            return Interlocked.Exchange(ref GetToken()._ContextID, newContextID) != newContextID;
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
            CELToken token = GetToken();
            while (true)
            {
                int oldEpochID = Volatile.Read(ref token._EpochID);
                if (newEpochID <= oldEpochID)
                {
                    return false;
                }
                if (Interlocked.CompareExchange(ref token._EpochID, newEpochID, oldEpochID) == oldEpochID)
                {
                    return true;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AdjustWait(ref int adjustTurn)
        {
            const int a = 2048;
            if (adjustTurn < a)
            {
                adjustTurn++;
                Thread.SpinWait(1);
            }
            else
            {
                Thread.Yield();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AdjustWait2(ref int adjustTurn)
        {
            const int a = 48;
            if (adjustTurn < a)
            {
                adjustTurn++;
                Thread.SpinWait(1);
            }
            else
            {
                Thread.Yield();
            }
        }

        /// <summary>
        /// Waits to acquire Concurrent access permission.
        /// </summary>
        /// <remarks>
        /// This method returns when entry into the Concurrent state is permitted.
        ///
        /// After acquiring Concurrent, the caller must later call <c>ReleaseConcurrent()</c> to release it;
        /// if another protocol subsequently changes the held state, release according to the resulting state.
        ///
        /// <paramref name="maxConcurrent"/> <paramref name="maxConcurrent"/> limits the maximum Concurrent ID that this acquisition may obtain.
        /// <paramref name="maxConcurrent"/> <paramref name="maxConcurrent"/> must be at least 1.
        /// </remarks>
        /// <param name="maxConcurrent">
        /// The maximum permitted Concurrent ID.
        /// Valid return values are in the range [1, maxConcurrent].
        /// </param>
        /// <returns>
        /// Returns the Concurrent ID obtained by this successful entry, in the range [1, maxConcurrent].
        /// IDs obtained by uninterrupted concurrent acquisitions in the same round are distinct; after a release, a later acquisition may reuse a previously returned ID.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="maxConcurrent"/> is less than 1.</exception>
        /// <exception cref="ConcurrentExclusiveLockCapacityExceededException">
        /// Thrown when the number of Concurrent holders present at runtime exceeds the internal limit.
        /// The current implementation supports a 31-bit concurrent count space; this limit is effectively unreachable in normal environments.
        /// </exception>
        public int AcquireConcurrent(int maxConcurrent = MaxConcurrent)
        {
            int adjustTurn = 0;
            long counter;
            if (maxConcurrent < 1)
            {
                throw new ArgumentException("maxConcurrent must >0");
            }

            CELToken token = GetToken();
        Redo:
            counter = Volatile.Read(ref token.Counter);
            if (counter >= maxConcurrent)
            {
                adjustTurn++;
                if (adjustTurn == 1)
                {
                    if (counter < Exclusive_Add * 2)
                    {
                        Monitor.Enter(token);
                        Monitor.Exit(token);
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }
                else if (adjustTurn < 33)
                {
                    Thread.Yield();
                }
                else
                {
                    adjustTurn = 1;
                    Thread.Sleep(5);
                }
                goto Redo;
            }
        Redo2:
            counter = Interlocked.Add(ref token.Counter, 1);
            if ((int)counter < 0)  // Runtime Concurrent count overflowed int.MaxValue; effectively impossible in practice.
            {
                Interlocked.Add(ref token.Counter, -1);
                throw new ConcurrentExclusiveLockCapacityExceededException();
            }
            if (counter <= maxConcurrent)
            {
                return (int)counter;
            }
            counter = Interlocked.Add(ref token.Counter, -1);
            if (counter < Exclusive_Add)
            {
                goto Redo2;
            }
            else
            {
                goto Redo;
            }
        }


        /// <summary>
        /// Attempts to acquire Concurrent access permission.
        /// </summary>
        /// <remarks>
        /// This method checks only once whether Concurrent entry is currently possible and does not wait for the lock state to change.
        ///
        /// A nonzero return value means Concurrent was acquired, and the caller must later call <c>ReleaseConcurrent()</c> to release it;
        /// if another protocol subsequently changes the held state, release according to the resulting state.
        ///
        /// <paramref name="maxConcurrent"/> <paramref name="maxConcurrent"/> limits the maximum Concurrent ID that this acquisition may obtain.
        /// <paramref name="maxConcurrent"/> <paramref name="maxConcurrent"/> must be at least 1.
        /// </remarks>
        /// <param name="maxConcurrent">
        /// The maximum permitted Concurrent ID.
        /// Successful return values are in the range [1, maxConcurrent].
        /// </param>
        /// <returns>
        /// Returns the Concurrent ID obtained by this successful entry, in the range [1, maxConcurrent].
        /// IDs obtained by uninterrupted concurrent acquisitions in the same round are distinct; after a release, a later acquisition may reuse a previously returned ID.
        /// Returns 0 if Concurrent was not acquired.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="maxConcurrent"/> is less than 1.</exception>
        public int TryAcquireConcurrent(int maxConcurrent = MaxConcurrent)
        {
            long counter;
            if (maxConcurrent < 1)
            {
                throw new ArgumentException("maxConcurrent must >0");
            }

            CELToken token = GetToken();
            counter = Volatile.Read(ref token.Counter);
            if (counter >= maxConcurrent)
            {
                return 0;
            }
            counter = Interlocked.Add(ref token.Counter, 1);
            if ((int)counter < 0)  // Runtime Concurrent count overflowed int.MaxValue; effectively impossible in practice.
            {
                Interlocked.Add(ref token.Counter, -1);
                return 0;
            }
            if (counter <= maxConcurrent)
            {
                return (int)counter;
            }
            Interlocked.Add(ref token.Counter, -1);
            return 0;
        }

        /// <summary>
        /// Attempts to acquire Concurrent access permission within the specified time.
        /// </summary>
        /// <remarks>
        /// This method waits, within the specified timeout, for the lock to enter a state in which Concurrent can be acquired.
        ///
        /// A nonzero return value means Concurrent was acquired, and the caller must later call <c>ReleaseConcurrent()</c> to release it;
        /// if another protocol subsequently changes the held state, release according to the resulting state.
        ///
        /// <paramref name="millisecondsTimeout"/> When <paramref name="millisecondsTimeout"/> is 0, the method tries once without waiting; a negative value means an infinite wait.
        ///
        /// <paramref name="maxConcurrent"/> <paramref name="maxConcurrent"/> limits the maximum Concurrent ID that this acquisition may obtain.
        /// <paramref name="maxConcurrent"/> <paramref name="maxConcurrent"/> must be at least 1.
        /// If <paramref name="millisecondsTimeout"/> is nonnegative, the method waits at most until the timeout and then returns 0;
        /// if <paramref name="millisecondsTimeout"/> is negative, it waits indefinitely.
        /// </remarks>
        /// <param name="millisecondsTimeout">
        /// The maximum number of milliseconds to wait for Concurrent.
        /// 0 0 means try once without waiting; a negative value means wait indefinitely.
        /// </param>
        /// <param name="maxConcurrent">
        /// The maximum permitted Concurrent ID.
        /// Successful return values are in the range [1, maxConcurrent].
        /// </param>
        /// <returns>
        /// Returns the Concurrent ID obtained by this successful entry, in the range [1, maxConcurrent].
        /// IDs obtained by uninterrupted concurrent acquisitions in the same round are distinct; after a release, a later acquisition may reuse a previously returned ID.
        /// Returns 0 if Concurrent was not acquired within the specified time.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown when <paramref name="maxConcurrent"/> is less than 1.</exception>
        public int TryAcquireConcurrent(int millisecondsTimeout, int maxConcurrent = MaxConcurrent)
        {
            if (maxConcurrent < 1)
            {
                throw new ArgumentException("maxConcurrent must >0");
            }

            if (millisecondsTimeout < 0) { return AcquireConcurrent(maxConcurrent); }

            int adjustTurn = 0;
            long counter;

            int nowTick = Environment.TickCount;
            millisecondsTimeout += nowTick;
            CELToken token = GetToken();
        Redo:
            if (millisecondsTimeout - nowTick >= 0)
            {
                counter = Volatile.Read(ref token.Counter);
                if (counter >= maxConcurrent)
                {
                    adjustTurn++;
                    if (adjustTurn == 1)
                    {
                        if (counter < Exclusive_Add * 2)
                        {
                            if (Monitor.TryEnter(token, millisecondsTimeout - nowTick))
                            {
                                Monitor.Exit(token);
                            }
                            else
                            {
                                return 0;
                            }
                        }
                        else
                        {
                            Thread.Yield();
                        }
                    }
                    else if (adjustTurn < 33)
                    {
                        Thread.Yield();
                    }
                    else
                    {
                        adjustTurn = 1;
                        Thread.Sleep(5);
                    }
                    nowTick = Environment.TickCount;
                    goto Redo;
                }
            Redo2:
                counter = Interlocked.Add(ref token.Counter, 1);
                if ((int)counter < 0)  // Runtime Concurrent count overflowed int.MaxValue; effectively impossible in practice.
                {
                    Interlocked.Add(ref token.Counter, -1);
                    return 0;
                }
                if (counter <= maxConcurrent)
                {
                    return (int)counter;
                }
                counter = Interlocked.Add(ref token.Counter, -1);
                if (counter < Exclusive_Add)
                {
                    nowTick = Environment.TickCount;
                    if (millisecondsTimeout - nowTick > 0)
                    {
                        goto Redo2;
                    }
                }
                else
                {
                    nowTick = Environment.TickCount;
                    goto Redo;
                }
            }
            return 0;
        }

        /// <summary>
        /// Releases one currently held Concurrent access permission.
        /// </summary>
        /// <remarks>
        /// This method must be called from a context that currently holds Concurrent.
        ///
        /// After this call, the current context no longer holds that Concurrent access permission
        /// and must not continue executing business code that depends on it.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseConcurrent()
        {
            Interlocked.Add(ref GetToken().Counter, -1);
        }

        /// <summary>
        /// Waits to acquire Exclusive access permission.
        /// </summary>
        /// <remarks>
        /// This method requests preemptive Exclusive access.
        ///
        /// After the request is issued, the lock blocks new Concurrent entries
        /// and waits for current Concurrent holders to release.
        /// This method returns after the caller has actually acquired Exclusive access permission.
        ///
        /// After acquiring Exclusive, the caller must later call <c>ReleaseExclusive()</c> to release it,
        /// or downgrade through <c>ExclusiveToConcurrent()</c> and then release according to the Concurrent protocol.
        /// </remarks>
        public void AcquireExclusive()
        {
            int adjustTurn = 0;
            long counter;

            CELToken token = GetToken();
        ReDo:
            Monitor.Enter(token);
            counter = Interlocked.Add(ref token.Counter, Exclusive_Add);
            if (counter != Exclusive_Add)
            {
                if (counter < Exclusive_Add * 2)
                {
                    while ((counter = Volatile.Read(ref token.Counter)) != Exclusive_Add)
                    {
                        if (counter < Exclusive_Add * 2)
                        {
                            AdjustWait(ref adjustTurn);
                        }
                        else   // Yield to ConcurrentToExclusive.
                        {
                            Interlocked.Add(ref token.Counter, -Exclusive_Add);
                            Monitor.Exit(token);
                            Thread.Yield();
                            goto ReDo;
                        }
                    }
                }
                else  // Yield to ConcurrentToExclusive.
                {
                    Interlocked.Add(ref token.Counter, -Exclusive_Add);
                    Monitor.Exit(token);
                    Thread.Yield();
                    goto ReDo;
                }
            }
        }

        /// <summary>
        /// Attempts to acquire Exclusive access permission.
        /// </summary>
        /// <remarks>
        /// When <paramref name="preemptConcurrent"/> is true,
        /// this method requests Exclusive preemptively and may wait.
        ///
        /// After entering exclusive contention, the request blocks new Concurrent and ordinary Exclusive entries.
        /// The method waits for callers that currently hold Concurrent permission to release it,
        /// then returns true after Exclusive has been acquired; the caller then holds Exclusive permission.
        ///
        /// If a Concurrent to Exclusive upgrade request appears during contention,
        /// the current request leaves contention and returns false,
        /// yielding exclusive scheduling to the upgrade request.
        ///
        /// When <paramref name="preemptConcurrent"/> is false,
        /// this method does not preempt Concurrent and does not wait for the lock state to change;
        /// Exclusive can be acquired only when the lock is Idle.
        ///
        /// After acquiring Exclusive, the caller must later call <c>ReleaseExclusive()</c> to release it,
        /// or downgrade through <c>ExclusiveToConcurrent()</c> and then release according to the Concurrent protocol.
        /// </remarks>
        /// <param name="preemptConcurrent">
        /// true True requests Exclusive preemptively;
        /// false false does not preempt Concurrent and attempts to acquire Exclusive only when the lock is Idle.
        /// </param>
        /// <returns>
        /// true True if Exclusive was acquired;
        /// false false if Exclusive was not acquired.
        /// </returns>
        public bool TryAcquireExclusive(bool preemptConcurrent = true)
        {
            int adjustTurn = 0;
            long counter;

            CELToken token = GetToken();
            if (preemptConcurrent)
            {
                if (Volatile.Read(ref token.Counter) < Exclusive_Add)
                {
                    Monitor.Enter(token);
                    counter = Interlocked.Add(ref token.Counter, Exclusive_Add);
                    if (counter != Exclusive_Add)
                    {
                        if (counter < Exclusive_Add * 2)
                        {
                            while ((counter = Volatile.Read(ref token.Counter)) != Exclusive_Add)
                            {
                                if (counter < Exclusive_Add * 2)
                                {
                                    AdjustWait(ref adjustTurn);
                                }
                                else
                                {
                                    Interlocked.Add(ref token.Counter, -Exclusive_Add);
                                    Monitor.Exit(token);
                                    return false;
                                }
                            }
                            return true;
                        }
                        else
                        {
                            Interlocked.Add(ref token.Counter, -Exclusive_Add);
                            Monitor.Exit(token);
                            return false;
                        }
                    }
                    return true;
                }
                return false;
            }
            else
            {
                if (Monitor.TryEnter(token))
                {
                    if (Interlocked.CompareExchange(ref token.Counter, Exclusive_Add, 0) == 0)
                    {
                        return true;
                    }
                    else
                    {
                        Monitor.Exit(token);
                        return false;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Attempts to acquire Exclusive access permission within the specified time.
        /// </summary>
        /// <remarks>
        /// This method attempts to acquire preemptive Exclusive within the specified timeout.
        ///
        /// <paramref name="millisecondsTimeout"/> When <paramref name="millisecondsTimeout"/> is 0, the method tries once without waiting;
        /// a negative value means an infinite wait.
        ///
        /// A return value of true means Exclusive was acquired, and the caller must later call <c>ReleaseExclusive()</c> to release it,
        /// or downgrade through <c>ExclusiveToConcurrent()</c> and then release according to the Concurrent protocol.
        ///
        /// A return value of false means Exclusive was not acquired within the specified time,
        /// and the caller must not execute business code that depends on Exclusive permission.
        /// </remarks>
        /// <param name="millisecondsTimeout">
        /// The maximum number of milliseconds to wait for Exclusive.
        /// 0 0 means try once without waiting; a negative value means wait indefinitely.
        /// </param>
        /// <returns>
        /// true True if Exclusive was acquired;
        /// false false if Exclusive was not acquired within the specified time.
        /// </returns>
        public bool TryAcquireExclusive(int millisecondsTimeout)
        {
            int adjustTurn = 0;
            int nowTick;
            long counter;

            if (millisecondsTimeout < 0)
            {
                AcquireExclusive();
                return true;
            }

            nowTick = Environment.TickCount;
            millisecondsTimeout += nowTick;
            CELToken token = GetToken();
        ReDo:
            if (Monitor.TryEnter(token, millisecondsTimeout - nowTick))
            {
                counter = Interlocked.Add(ref token.Counter, Exclusive_Add);
                if (counter != Exclusive_Add)
                {
                    if (counter < Exclusive_Add * 2)
                    {
                        while ((counter = Volatile.Read(ref token.Counter)) != Exclusive_Add)
                        {
                            if (counter < Exclusive_Add * 2)
                            {
                                if (millisecondsTimeout - nowTick > 0)
                                {
                                    AdjustWait(ref adjustTurn);
                                    nowTick = Environment.TickCount;
                                }
                                else
                                {
                                    Interlocked.Add(ref token.Counter, -Exclusive_Add);
                                    Monitor.Exit(token);
                                    return false;
                                }
                            }
                            else
                            {
                                Interlocked.Add(ref token.Counter, -Exclusive_Add);
                                Monitor.Exit(token);
                                Thread.Yield();
                                nowTick = Environment.TickCount;
                                if (millisecondsTimeout - nowTick > 0)
                                {
                                    goto ReDo;
                                }
                                else
                                {
                                    return false;
                                }
                            }
                        }
                        return true;
                    }
                    else
                    {
                        Interlocked.Add(ref token.Counter, -Exclusive_Add);
                        Monitor.Exit(token);
                        Thread.Yield();
                        nowTick = Environment.TickCount;
                        if (millisecondsTimeout - nowTick > 0)
                        {
                            goto ReDo;
                        }
                        else
                        {
                            return false;
                        }
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Releases the currently held Exclusive access permission.
        /// </summary>
        /// <remarks>
        /// This method must be called from a context that currently holds Exclusive.
        ///
        /// After this call, the current context no longer holds Exclusive access permission
        /// and must not continue executing business code that depends on it.
        ///
        /// After release, the lock returns to normal contention,
        /// and waiting Concurrent or Exclusive requests continue attempting entry according to the lock's contention rules.
        ///
        /// To retain Concurrent access after ending Exclusive,
        /// call <c>ExclusiveToConcurrent()</c>
        /// instead of this method.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseExclusive()
        {
            CELToken token = GetToken();
            Interlocked.Add(ref token.Counter, -Exclusive_Add);
            Monitor.Exit(token);
        }

        /// <summary>
        /// Downgrades the currently held Exclusive access permission to Concurrent access permission.
        /// </summary>
        /// <remarks>
        /// This method must be called from a context that currently holds Exclusive.
        ///
        /// After this call, the current context no longer holds Exclusive access permission
        /// and must not continue executing business code that depends on it.
        ///
        /// The current context instead holds Concurrent access permission
        /// and must later call <c>ReleaseConcurrent()</c> to release it.
        ///
        /// If the current Exclusive permission was acquired through an ordinary Exclusive request,
        /// the downgrade retains Concurrent continuously, avoiding the access window caused by releasing and reacquiring.
        ///
        /// If the current Exclusive permission resulted from an in-place Concurrent upgrade
        /// and other upgrade requests are still waiting, the downgrade cuts the current access context and reacquires Concurrent
        /// so that the remaining upgrade requests can continue acquiring Exclusive.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExclusiveToConcurrent()
        {
            long counter;

            CELToken token = GetToken();
            counter = Interlocked.Add(ref token.Counter, -Converge_Add);
            Monitor.Exit(token);

            if (counter >= Exclusive_Add)  // Under heavy contention, split the segment and reacquire.
            {
                Interlocked.Add(ref token.Counter, -1);
                AcquireConcurrent();
            }
        }

        /// <summary>
        /// Upgrades the currently held Concurrent permission to Exclusive.
        /// Upgrade requests take priority over ordinary Exclusive requests and execute serially until all have completed.
        /// </summary>
        /// <remarks>
        /// This method must be called from a context that currently holds Concurrent permission.
        ///
        /// After this call, the lock blocks new Concurrent entries into the current conversion window;
        /// callers that upgrade successfully execute in isolation under Exclusive semantics.
        ///
        /// After a successful upgrade, call <c>ReleaseExclusive()</c> to release it,
        /// or downgrade through <c>ExclusiveToConcurrent()</c>
        /// and then release according to the Concurrent protocol.
        ///
        /// Use this method to continue from a completed Concurrent phase into a continuous Exclusive access context,
        /// avoiding the access window caused by releasing Concurrent and then reacquiring Exclusive.
        ///
        /// When multiple Concurrent holders request an upgrade at the same time, their Exclusive regions execute serially.
        /// Too many upgrade requests may create substantial contention pressure;
        /// when the business logic can identify which callers actually need to upgrade by context or phase,
        /// prefer <see cref="TryConcurrentToExclusiveWithSwitchContextID(int)"/>
        /// or <see cref="TryConcurrentToExclusiveWithRaiseEpochID(int)"/>.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ConcurrentToExclusive()
        {
            int adjustTurn = 0;
            CELToken token = GetToken();
            if ((int)Interlocked.Add(ref token.Counter, Converge_Add) != 0)  // Semantically equivalent to directly converting the current Concurrent holder into one of multiple Exclusive signals.
            {
                while ((int)Volatile.Read(ref token.Counter) != 0)
                {
                    AdjustWait2(ref adjustTurn);
                }
            }
            Monitor.Enter(token);
        }

        /// <summary>
        /// While holding Concurrent permission, attempts to upgrade to Exclusive through SwitchContextID.
        /// Upgraded Exclusive requests block ordinary Exclusive acquisition until all upgrades have completed.
        /// When multiple Concurrent holders call this method with the same newContextID, only the holder that successfully switches ContextID can upgrade.
        /// When callers use different newContextID values, multiple callers may succeed, but their successful Exclusive regions are still isolated and ordered.
        /// On success, the caller holds Exclusive permission; on failure, the previously held Concurrent permission is released automatically.
        /// </summary>
        /// <remarks>
        /// This method must be called from a context that holds Concurrent permission.
        ///
        /// After this call, the lock blocks new Concurrent entries into the current conversion window,
        /// and callers that upgrade successfully execute in isolation under Exclusive semantics.
        /// ContextID Whether ContextID is switched is determined by <c>SwitchContextID(newContextID)</c>.
        /// 
        /// After a successful upgrade, call <c>ReleaseExclusive()</c> to release it,
        /// or downgrade through <c>ExclusiveToConcurrent()</c>
        /// and then release according to the Concurrent protocol.
        /// 
        /// After failure, the original Concurrent permission has already been released; do not call <c>ReleaseConcurrent()</c> again.
        /// Exclusive regions produced by multiple Concurrent upgrades remain serialized.
        /// </remarks>
        /// <param name="newContextID">
        /// The new business context ID to attempt to apply.
        /// </param>
        /// <returns>
        /// true True if ContextID was switched and Exclusive has been acquired;
        /// false false if ContextID did not change and the original Concurrent permission was released automatically.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryConcurrentToExclusiveWithSwitchContextID(int newContextID)
        {
            int adjustTurn = 0;
            CELToken token = GetToken();
            if ((int)Interlocked.Add(ref token.Counter, Converge_Add) != 0)  // Semantically equivalent to directly converting the current Concurrent holder into one of multiple Exclusive signals.
            {
                while ((int)Volatile.Read(ref token.Counter) != 0)
                {
                    AdjustWait2(ref adjustTurn);
                }
            }
            if (SwitchContextID(newContextID))
            {
                Monitor.Enter(token);
                return true;
            }
            else
            {
                Interlocked.Add(ref token.Counter, -Exclusive_Add);
                return false;
            }
        }

        /// <summary>
        /// While holding Concurrent permission, attempts to upgrade to Exclusive through RaiseEpochID.
        /// Upgraded Exclusive requests block ordinary Exclusive acquisition until all upgrades have completed.
        /// The caller can advance the phase and upgrade successfully only when newEpochID is greater than the current EpochID.
        /// When newEpochID is less than or equal to the current EpochID, the upgrade fails and EpochID remains unchanged.
        /// When callers use different increasing newEpochID values, multiple callers may succeed, but their successful Exclusive regions are still isolated and ordered.
        /// On success, the caller holds Exclusive permission; on failure, the previously held Concurrent permission is released automatically.
        /// </summary>
        /// <remarks>
        /// This method must be called from a context that currently holds Concurrent permission.
        ///
        /// This method is suitable when EpochID represents a lifecycle, version number, or phase number.
        /// EpochID EpochID may advance only to a greater value; it cannot remain unchanged or move backward.
        /// After this call, the lock blocks new Concurrent entries into the current conversion window,
        /// and callers that upgrade successfully execute in isolation under Exclusive semantics.
        /// EpochID Whether EpochID advances is determined by <c>RaiseEpochID(newEpochID)</c>.
        ///
        /// After a successful upgrade, call <c>ReleaseExclusive()</c> to release it,
        /// or downgrade through <c>ExclusiveToConcurrent()</c>
        /// and then release according to the Concurrent protocol.
        /// 
        /// After failure, the original Concurrent permission has already been released; do not call <c>ReleaseConcurrent()</c> again.
        /// Exclusive regions produced by multiple Concurrent upgrades remain serialized.
        /// </remarks>
        /// <param name="newEpochID">
        /// The new phase ID to attempt to advance to.
        /// </param>
        /// <returns>
        /// true True if EpochID was successfully advanced and Exclusive has been acquired;
        /// false false if EpochID was not advanced and the original Concurrent permission was released automatically.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryConcurrentToExclusiveWithRaiseEpochID(int newEpochID)
        {
            int adjustTurn = 0;
            CELToken token = GetToken();
            if ((int)Interlocked.Add(ref token.Counter, Converge_Add) != 0)  // Semantically equivalent to directly converting the current Concurrent holder into one of multiple Exclusive signals.
            {
                while ((int)Volatile.Read(ref token.Counter) != 0)
                {
                    AdjustWait2(ref adjustTurn);
                }
            }
            if (RaiseEpochID(newEpochID))
            {
                Monitor.Enter(token);
                return true;
            }
            else
            {
                Interlocked.Add(ref token.Counter, -Exclusive_Add);
                return false;
            }
        }

        /// <summary>
        /// Helper method.
        /// </summary>
        /// <param name="counter">The delta to apply to the internal state counter.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void _FreeRelease(long counter)
        {
            CELToken token = GetToken();
            Interlocked.Add(ref token.Counter, counter);
            if (counter <= -Exclusive_Add)
            {
                Monitor.Exit(token);
            }
        }
    }

    /// <summary>
    /// ConcurrentExclusiveLock lock state.
    /// </summary>
    public enum ConcurrentExclusiveLockState : byte
    {
        /// <summary>
        /// Idle
        /// </summary>
        Idle = 0,

        /// <summary>
        /// Concurrent
        /// </summary>
        Concurrent = 1,

        /// <summary>
        /// Exclusive
        /// </summary>
        Exclusive = 2,
    }

    /// <summary>
    /// Indicates that the runtime number of Concurrent holders exceeded the internal 31-bit count capacity.
    /// This exception is not normally expected under real-world business conditions.
    /// </summary>
    public class ConcurrentExclusiveLockCapacityExceededException : Exception
    {
        /// <summary>
        /// Initializes a new
        /// <see cref="ConcurrentExclusiveLockCapacityExceededException"/> <see cref="ConcurrentExclusiveLockCapacityExceededException"/> instance.
        /// </summary>
        public ConcurrentExclusiveLockCapacityExceededException() : base()
        { }
    }
}