using System;
using System.Threading.Tasks;

namespace IntomicLib
{
    /// <summary>
    /// An access-permission pipeline for <see cref="ConcurrentExclusiveLock"/>.
    /// </summary>
    /// <remarks>
    /// The Pipeline executes a sequence of synchronous business segments, each of which declares the access permission required for execution.
    /// During execution, it uses the permission successfully held by the preceding segment
    /// to automatically release, reacquire, continue, upgrade, or downgrade access.
    ///
    /// The specific semantics of each access mode are defined by <see cref="ConcurrentExclusiveAccessMode"/>.
    ///
    /// When a try-type segment does not satisfy its execution condition, that segment is not executed;
    /// the Pipeline releases any access permission it still holds
    /// and continues processing subsequent segments from the None state.
    ///
    /// All Pipeline segments are synchronous, and a segment delegate must not cross an <c>await</c> boundary.
    /// Exceptions thrown by a segment delegate propagate to the caller, and subsequent segments are not executed;
    /// when the Pipeline exits, any access permission still held is released.
    ///
    /// This type is a value type, and a default-initialized instance is unusable.
    /// Create an instance by using the constructor that accepts a <see cref="ConcurrentExclusiveLock"/>.
    /// </remarks>
    public readonly struct ConcurrentExclusiveLockPipeline
    {
        /// <summary>
        /// The lock instance bound to the current pipeline.
        /// </summary>
        public readonly ConcurrentExclusiveLock Locker;

        /// <summary>
        /// Creates an access-permission pipeline bound to the specified lock instance.
        /// </summary>
        /// <param name="locker">The lock instance to be used by the pipeline.</param>
        /// <remarks>
        /// A Pipeline continues to reuse this lock instance; multiple Pipeline instances may also be bound to the same lock instance.
        /// </remarks>
        public ConcurrentExclusiveLockPipeline(ConcurrentExclusiveLock locker)
        {
            Locker = locker;
        }

        /// <summary>
        /// Executes a synchronous Pipeline operation on the thread pool.
        /// </summary>
        /// <remarks>
        /// This method does not make <see cref="ConcurrentExclusiveLockSegment"/> asynchronous and does not perform asynchronous waits within a segment.
        /// It only schedules the synchronous <see cref="DoPipeline"/> method to the thread pool through <see cref="Task.Run(Action)"/>.
        /// If the caller is already running on a worker thread, thread-pool thread, or server request thread, <see cref="DoPipeline"/> should normally be called directly.
        /// </remarks>
        /// <param name="segments">The Pipeline segments to execute in sequence.</param>
        /// <returns>A task representing this Pipeline execution.</returns>
        public Task DoPipelineAsync(params ConcurrentExclusiveLockSegment[] segments)
        {
            ConcurrentExclusiveLockPipeline pipeline = this;
            return Task.Run(() => { pipeline.DoPipeline(segments); });
        }

        /// <summary>
        /// Executes a sequence of Pipeline segments in order.
        /// </summary>
        /// <remarks>
        /// Each segment declares the access permission required for its execution.
        /// Based on the permission successfully held by the preceding segment, the Pipeline
        /// automatically handles the permission state according to the definition of <see cref="ConcurrentExclusiveAccessMode"/>.
        ///
        /// When a try-type segment does not satisfy its execution condition, that segment is not executed;
        /// the Pipeline releases any access permission it still holds
        /// and continues processing subsequent segments from the None state.
        ///
        /// Exceptions thrown by a segment delegate propagate to the caller, and subsequent segments are not executed;
        /// when this method exits, any access permission still held is released.
        /// </remarks>
        /// <param name="segments">The Pipeline segments to execute in sequence.</param>
        public void DoPipeline(params ConcurrentExclusiveLockSegment[] segments)
        {
            using (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(Locker))
            {
                bool isSuccess;
                ConcurrentExclusiveAccessMode lastSuccessAccess = ConcurrentExclusiveAccessMode.None;  // lastSuccessAccess can only be None, Concurrent, or Exclusive
                foreach (var segment in segments)
                {
                    switch (segment.Access)
                    {
                        case ConcurrentExclusiveAccessMode.Concurrent:
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    scope.ReleaseConcurrent();
                                    scope.AcquireConcurrent();
                                    segment.Segment();
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    scope.ReleaseExclusive();
                                    scope.AcquireConcurrent();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.Concurrent;
                                    segment.Segment();
                                    break;
                                default:
                                    scope.AcquireConcurrent();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.Concurrent;
                                    segment.Segment();
                                    break;
                            }
                            break;
                        case ConcurrentExclusiveAccessMode.TryConcurrent:
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    scope.ReleaseConcurrent();
                                    if (scope.TryAcquireConcurrent() != 0)
                                    {
                                        segment.Segment();
                                    }
                                    else
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    }
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    scope.ReleaseExclusive();
                                    if (scope.TryAcquireConcurrent() != 0)
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.Concurrent;
                                        segment.Segment();
                                    }
                                    else
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    }
                                    break;
                                default:
                                    if (scope.TryAcquireConcurrent() != 0)
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.Concurrent;
                                        segment.Segment();
                                    }
                                    break;
                            }
                            break;
                        case ConcurrentExclusiveAccessMode.Exclusive:
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    scope.ReleaseConcurrent();
                                    scope.AcquireExclusive();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                    segment.Segment();
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    scope.ReleaseExclusive();
                                    scope.AcquireExclusive();
                                    segment.Segment();
                                    break;
                                default:
                                    scope.AcquireExclusive();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                    segment.Segment();
                                    break;
                            }
                            break;
                        case ConcurrentExclusiveAccessMode.TestExclusive:
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    scope.ReleaseConcurrent();
                                    if (scope.TryAcquireExclusive(false))
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                        segment.Segment();
                                    }
                                    else
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    }
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    scope.ReleaseExclusive();
                                    if (scope.TryAcquireExclusive(false))
                                    {
                                        segment.Segment();
                                    }
                                    else
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    }
                                    break;
                                default:
                                    if (scope.TryAcquireExclusive(false))
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                        segment.Segment();
                                    }
                                    break;
                            }
                            break;
                        case ConcurrentExclusiveAccessMode.TryExclusive:
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    scope.ReleaseConcurrent();
                                    if (scope.TryAcquireExclusive(true))
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                        segment.Segment();
                                    }
                                    else
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    }
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    scope.ReleaseExclusive();
                                    if (scope.TryAcquireExclusive(true))
                                    {
                                        segment.Segment();
                                    }
                                    else
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    }
                                    break;
                                default:
                                    if (scope.TryAcquireExclusive(true))
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                        segment.Segment();
                                    }
                                    break;
                            }
                            break;
                        case ConcurrentExclusiveAccessMode.ConvergeConcurrent:
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    segment.Segment();
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    scope.ExclusiveToConcurrent();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.Concurrent;
                                    segment.Segment();
                                    break;
                                default:
                                    scope.AcquireConcurrent();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.Concurrent;
                                    segment.Segment();
                                    break;
                            }
                            break;
                        case ConcurrentExclusiveAccessMode.ConvergeExclusive:
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    scope.ConcurrentToExclusive();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                    segment.Segment();
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    segment.Segment();
                                    break;
                                default:
                                    scope.AcquireExclusive();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                    segment.Segment();
                                    break;
                            }
                            break;
                        case ConcurrentExclusiveAccessMode.TryApplyIDConvergeExclusive:
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    isSuccess = (segment.IDKind == ConcurrentExclusiveLockSegment.IDType.ContextID) ? scope.TryConcurrentToExclusiveWithSwitchContextID(segment.ContextOrEpochID) : scope.TryConcurrentToExclusiveWithRaiseEpochID(segment.ContextOrEpochID);
                                    if (isSuccess)
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                        segment.Segment();
                                    }
                                    else
                                    {
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    }
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    isSuccess = (segment.IDKind == ConcurrentExclusiveLockSegment.IDType.ContextID) ? scope.SwitchContextID(segment.ContextOrEpochID) : scope.RaiseEpochID(segment.ContextOrEpochID);
                                    if (isSuccess)
                                    {
                                        segment.Segment();
                                    }
                                    else
                                    {
                                        scope.ReleaseExclusive();
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    }
                                    break;
                                default:
                                    isSuccess = (segment.IDKind == ConcurrentExclusiveLockSegment.IDType.ContextID) ? scope.SwitchContextID(segment.ContextOrEpochID) : scope.RaiseEpochID(segment.ContextOrEpochID);
                                    if (isSuccess)
                                    {
                                        scope.AcquireExclusive();
                                        lastSuccessAccess = ConcurrentExclusiveAccessMode.Exclusive;
                                        segment.Segment();
                                    }
                                    break;
                            }
                            break;
                        default:  // Treat all other values as ConcurrentExclusiveAccessMode.None
                            switch (lastSuccessAccess)
                            {
                                case ConcurrentExclusiveAccessMode.Concurrent:
                                    scope.ReleaseConcurrent();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    segment.Segment();
                                    break;
                                case ConcurrentExclusiveAccessMode.Exclusive:
                                    scope.ReleaseExclusive();
                                    lastSuccessAccess = ConcurrentExclusiveAccessMode.None;
                                    segment.Segment();
                                    break;
                                default:
                                    segment.Segment();
                                    break;
                            }
                            break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The access-permission mode declared by a Pipeline segment.
    /// </summary>
    /// <remarks>
    /// Each enum value defines the access permission required by the current segment
    /// and how it is handled relative to the state successfully held by the preceding segment.
    /// </remarks>
    public enum ConcurrentExclusiveAccessMode : byte
    {
        /// <summary>
        /// No access permission.
        /// </summary>
        /// <remarks>
        /// Before the current segment runs, any permission still held by the preceding segment is released, and the current segment runs without access permission.
        /// </remarks>
        None = 0,

        /// <summary>
        /// Acquires an independent Concurrent permission segment.
        /// </summary>
        /// <remarks>
        /// If the preceding segment already holds Concurrent permission, it is still released and reacquired.
        /// To continue the current Concurrent context, use <see cref="ConvergeConcurrent"/>.
        /// </remarks>
        Concurrent = 1,

        /// <summary>
        /// Attempts to acquire an independent Concurrent permission segment.
        /// </summary>
        /// <remarks>
        /// If Concurrent permission is not acquired, the current segment is not executed and the remaining pipeline continues from the None state.
        /// If the preceding segment still holds permission, that permission is released before acquisition is attempted.
        /// </remarks>
        TryConcurrent = 2,

        /// <summary>
        /// Acquires an independent Exclusive permission segment.
        /// </summary>
        /// <remarks>
        /// Exclusive represents an independent exclusive-permission segment.
        /// If the preceding segment already holds Exclusive permission, it is still released and reacquired.
        /// To continue an existing Exclusive context, use <see cref="ConvergeExclusive"/>;
        /// to additionally condition execution of the current segment on a business ID, use <see cref="TryApplyIDConvergeExclusive"/>.
        /// </remarks>
        Exclusive = 3,

        /// <summary>
        /// Attempts to acquire Exclusive permission only while the lock is Idle.
        /// </summary>
        /// <remarks>
        /// This mode does not preempt Concurrent access and does not wait for the lock state to change.
        /// If the preceding segment still holds permission, that permission is released before acquisition is attempted.
        /// The current segment is executed only when the lock is Idle and exclusive scheduling can be entered immediately;
        /// otherwise, the current segment is not executed and the remaining pipeline continues from the None state.
        /// </remarks>
        TestExclusive = 4,

        /// <summary>
        /// Attempts to acquire Exclusive permission preemptively.
        /// </summary>
        /// <remarks>
        /// This mode requests Exclusive permission preemptively and may wait.
        /// If the preceding segment still holds permission, that permission is released before acquisition is attempted.
        /// If a Concurrent-to-Exclusive upgrade request appears during contention, the current request may yield and fail.
        /// If Exclusive permission is not acquired, the current segment is not executed and the remaining pipeline continues from the None state.
        /// </remarks>
        TryExclusive = 5,

        /// <summary>
        /// Continues, downgrades to, or acquires Concurrent permission.
        /// </summary>
        /// <remarks>
        /// If the preceding segment already holds Concurrent permission, the current Concurrent context is continued and the current segment executes directly.
        /// If the preceding segment holds Exclusive permission, <c>ExclusiveToConcurrent()</c> is called to downgrade before executing the current segment.
        /// Under upgrade contention, the downgrade may cut the current access context and reacquire Concurrent permission.
        /// If no access permission is currently held, ordinary Concurrent permission is acquired.
        /// </remarks>
        ConvergeConcurrent = 6,

        /// <summary>
        /// Continues, upgrades to, or acquires Exclusive permission.
        /// </summary>
        /// <remarks>
        /// If the preceding segment holds Concurrent permission, <c>ConcurrentToExclusive()</c> is called to upgrade in place before executing the current segment.
        /// If the preceding segment already holds Exclusive permission, the current Exclusive context is continued and the current segment executes directly.
        /// If no access permission is currently held, ordinary Exclusive permission is acquired.
        ///
        /// When multiple Concurrent holders request upgrades simultaneously, their upgraded Exclusive sections execute serially in sequence.
        /// Too many upgrade requests may create significant contention;
        /// when the business can use ContextID or EpochID to select which callers actually need to upgrade,
        /// consider using <see cref="TryApplyIDConvergeExclusive"/>.
        /// </remarks>
        ConvergeExclusive = 7,

        /// <summary>
        /// Continues, upgrades to, or acquires Exclusive permission, conditioned on the result of applying a business ID.
        /// </summary>
        /// <remarks>
        /// The Try semantics of this mode apply to the result of applying the business ID; they do not imply that no waiting occurs.
        ///
        /// When ContextID is used, success means switching to a different business context;
        /// when EpochID is used, success means advancing to a greater business stage.
        ///
        /// If the business ID is applied successfully, the current segment executes with Exclusive permission.
        /// If the business ID is not applied, the current segment is not executed;
        /// the Pipeline releases any access permission it still holds
        /// and continues processing subsequent segments from the None state.
        ///
        /// When the preceding segment holds Concurrent permission, it attempts to upgrade to Exclusive subject to the business ID;
        /// when the preceding segment already holds Exclusive permission, the business ID is applied within the current Exclusive context;
        /// when the preceding segment holds no access permission, Exclusive permission is acquired after the business ID is applied successfully.
        /// </remarks>
        TryApplyIDConvergeExclusive = 8,
    }


    /// <summary>
    /// A business segment in a Pipeline.
    /// </summary>
    /// <remarks>
    /// Each segment declares the access permission required to run it and stores the synchronous business code to execute.
    /// This type is a value type. A default-initialized instance cannot be executed; create business segments through the corresponding static factory methods.
    /// </remarks>
    public readonly struct ConcurrentExclusiveLockSegment
    {
        /// <summary>
        /// Specifies the business ID type represented by <see cref="ContextOrEpochID"/>.
        /// </summary>
        public enum IDType : byte
        {
            /// <summary>
            /// Uses ContextID to switch the business context.
            /// </summary>
            ContextID = 0,

            /// <summary>
            /// Uses EpochID to advance the business stage monotonically.
            /// </summary>
            EpochID = 1,
        }

        /// <summary>
        /// The synchronous business code to be executed by the current segment.
        /// </summary>
        public readonly Action Segment;

        /// <summary>
        /// The business-context ID or business-stage ID to be applied by the current segment.
        /// </summary>
        /// <remarks>
        /// This value is meaningful only for <see cref="ConcurrentExclusiveAccessMode.TryApplyIDConvergeExclusive"/>.
        /// When <see cref="IDKind"/> is <see cref="IDType.ContextID"/>, this value represents a ContextID;
        /// when <see cref="IDKind"/> is <see cref="IDType.EpochID"/>, it represents an EpochID.
        /// </remarks>
        public readonly int ContextOrEpochID;

        /// <summary>
        /// Specifies whether <see cref="ContextOrEpochID"/> is a ContextID or an EpochID.
        /// </summary>
        /// <remarks>
        /// This value is meaningful only for <see cref="ConcurrentExclusiveAccessMode.TryApplyIDConvergeExclusive"/>.
        /// </remarks>
        public readonly IDType IDKind;

        /// <summary>
        /// The access-permission mode declared by the current segment.
        /// </summary>
        public readonly ConcurrentExclusiveAccessMode Access;

        private ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode access, Action segment, int contextOrEpochID = 0, IDType idKind = ConcurrentExclusiveLockSegment.IDType.ContextID)
        {
            if (segment == null) { throw new ArgumentNullException(nameof(segment)); }

            Access = access;
            Segment = segment;
            ContextOrEpochID = contextOrEpochID;
            IDKind = idKind;
        }

        /// <summary>
        /// Creates a business segment that requires no access permission.
        /// </summary>
        /// <param name="segment">The business code to execute without lock access.</param>
        public static ConcurrentExclusiveLockSegment None(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.None, segment);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment None(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Creates a business segment that acquires independent Concurrent permission.
        /// </summary>
        /// <param name="segment">The business code to execute while Concurrent permission is held successfully.</param>
        public static ConcurrentExclusiveLockSegment Concurrent(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.Concurrent, segment);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment Concurrent(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Creates a business segment that acquires independent Exclusive permission.
        /// </summary>
        /// <param name="segment">The business code to execute while Exclusive permission is held successfully.</param>
        public static ConcurrentExclusiveLockSegment Exclusive(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.Exclusive, segment);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment Exclusive(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Creates a business segment that attempts to acquire independent Concurrent permission.
        /// </summary>
        /// <param name="segment">The business code to execute while Concurrent permission is held successfully.</param>
        public static ConcurrentExclusiveLockSegment TryConcurrent(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.TryConcurrent, segment);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment TryConcurrent(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Creates a business segment that attempts to acquire Exclusive permission only while the lock is Idle.
        /// </summary>
        /// <param name="segment">The business code to execute while Exclusive permission is held successfully.</param>
        public static ConcurrentExclusiveLockSegment TestExclusive(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.TestExclusive, segment);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment TestExclusive(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Creates a business segment that attempts to acquire Exclusive permission preemptively.
        /// </summary>
        /// <param name="segment">The business code to execute while Exclusive permission is held successfully.</param>
        public static ConcurrentExclusiveLockSegment TryExclusive(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.TryExclusive, segment);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment TryExclusive(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Creates a business segment that continues, downgrades to, or acquires Concurrent permission.
        /// </summary>
        /// <param name="segment">The business code to execute while Concurrent permission is held successfully.</param>
        public static ConcurrentExclusiveLockSegment ConvergeConcurrent(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.ConvergeConcurrent, segment);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment ConvergeConcurrent(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Creates a business segment that continues, upgrades to, or acquires Exclusive permission.
        /// </summary>
        /// <param name="segment">The business code to execute while Exclusive permission is held successfully.</param>
        public static ConcurrentExclusiveLockSegment ConvergeExclusive(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.ConvergeExclusive, segment);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment ConvergeExclusive(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Creates a business segment that converges to Exclusive permission, conditioned on the result of applying a business ID.
        /// </summary>
        /// <param name="segment">The synchronous business code to execute when the business ID is applied successfully and Exclusive permission is held.</param>
        /// <param name="contextOrEpochID">The business-context ID to switch to, or the business-stage ID to advance to.</param>
        /// <param name="idType">Specifies the business ID type represented by <paramref name="contextOrEpochID"/>.</param>
        public static ConcurrentExclusiveLockSegment TryApplyIDConvergeExclusive(Action segment, int contextOrEpochID, IDType idType)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.TryApplyIDConvergeExclusive, segment, contextOrEpochID, idType);
        }
        /// <summary>
        /// Disabled overload used to reject asynchronous pipeline segments at compile time.
        /// </summary>
        /// <param name="segment"></param>
        /// <param name="contextOrEpochID"></param>
        /// <param name="idType"></param>
        /// <returns></returns>
        /// <exception cref="NotSupportedException"></exception>
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment TryApplyIDConvergeExclusive(Func<Task> segment, int contextOrEpochID, IDType idType)
        {
            throw new NotSupportedException();
        }
    }
}
