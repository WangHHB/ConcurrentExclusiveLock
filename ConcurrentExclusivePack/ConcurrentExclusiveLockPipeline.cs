using System;
using System.Threading.Tasks;

namespace IntomicLib
{
    /// <summary>
    /// <see cref="ConcurrentExclusiveLock"/> 的访问权限流水线。
    /// </summary>
    /// <remarks>
    /// Pipeline 用一组连续的段描述业务流程，每个段声明自己需要的访问权限。
    /// 执行过程中会根据上一段成功持有的访问权限，自动完成释放、重新申请或原地转换。
    ///
    /// Concurrent 表示一段独立的普通并发权限；连续普通 Concurrent 段会切开并重新申请。
    /// ConvergeConcurrent 表示延续或形成 Concurrent 上下文；如果上一段是 Exclusive，则原地降级为 Concurrent。
    ///
    /// TryApplyIDConvergeExclusive 表示先尝试应用业务 ID，并在成功后收敛到 Exclusive：
    /// 如果上一段是 Concurrent，则根据 ID 类型调用对应的原地升级方法；
    /// 如果上一段是 Exclusive，则尝试在当前 Exclusive 上下文中应用业务 ID；
    /// 如果当前没有访问权限，则先尝试应用业务 ID，成功后再获取 Exclusive。
    ///
    /// Exclusive 段表示重新申请一段独立的 Exclusive 权限；
    /// 如果上一段已经持有 Exclusive，也会先释放再重新申请，让其他竞争者有机会进入。
    /// 如果希望在已有 Exclusive 上下文中连续执行并受业务 ID 控制，请使用 TryApplyIDConvergeExclusive。
    ///
    /// TryConcurrent、TestExclusive、TryExclusive 或 TryApplyIDConvergeExclusive 未获得目标执行条件时，
    /// 当前段不会执行；流水线不会抛出异常，也不会结束，
    /// 而是以 None 状态继续处理后续段。
    /// </remarks>
    public readonly struct ConcurrentExclusiveLockPipeline
    {
        /// <summary>
        /// 当前流水线绑定的锁实例。
        /// </summary>
        public readonly ConcurrentExclusiveLock Locker;

        /// <summary>
        /// 创建绑定到指定锁实例的访问权限流水线。
        /// </summary>
        /// <param name="locker">流水线要使用的锁实例。</param>
        /// <remarks>
        /// 同一个 Pipeline 会一直复用该锁实例；多个 Pipeline 也可以绑定到同一个锁实例。
        /// </remarks>
        public ConcurrentExclusiveLockPipeline(ConcurrentExclusiveLock locker)
        {
            Locker = locker;
        }

        /// <summary>
        /// 在线程池中执行一次同步 Pipeline。
        /// </summary>
        /// <remarks>
        /// 此方法不会让 <see cref="ConcurrentExclusiveLockSegment"/> 变为异步段，也不会在段内部执行异步等待。
        /// 它只是通过 <see cref="Task.Run(Action)"/> 将同步 <see cref="DoPipeline"/> 调度到线程池执行。
        /// 如果调用者已经位于工作线程、线程池线程或服务端请求线程中，通常应直接调用 <see cref="DoPipeline"/>。
        /// </remarks>
        /// <param name="segments">要顺序执行的 Pipeline 段。</param>
        /// <returns>表示本次 Pipeline 执行过程的任务。</returns>
        public Task DoPipelineAsync(params ConcurrentExclusiveLockSegment[] segments)
        {
            ConcurrentExclusiveLockPipeline pipeline = this;
            return Task.Run(() => { pipeline.DoPipeline(segments); });
        }

        /// <summary>
        /// 按顺序执行一组 Pipeline 段。
        /// </summary>
        /// <remarks>
        /// 每个段声明自己需要的访问权限，Pipeline 会根据上一段成功持有的权限自动选择对应处理方式。
        ///
        /// 普通 <see cref="ConcurrentExclusiveAccessMode.Concurrent"/> 和 <see cref="ConcurrentExclusiveAccessMode.Exclusive"/>
        /// 表示独立权限段；即使上一段持有同类权限，也会切开后重新申请。
        ///
        /// <see cref="ConcurrentExclusiveAccessMode.ConvergeConcurrent"/> 用于延续或形成 Concurrent 上下文；
        /// 如果上一段是 Exclusive，会原地降级为 Concurrent。
        ///
        /// <see cref="ConcurrentExclusiveAccessMode.TryApplyIDConvergeExclusive"/> 的 Try 语义针对业务 ID 更新。
        /// 只有业务 ID 应用成功，并且最终进入或保持 Exclusive 权限时，当前段才会执行。
        ///
        /// Try 类型段未获得目标执行条件时，当前段不会执行，后续段继续处理；
        /// 此时 Pipeline 会视为当前不再持有任何访问权限，后续段将从 None 状态继续解释。
        /// </remarks>
        /// <param name="segments">要顺序执行的 Pipeline 段。</param>
        public void DoPipeline(params ConcurrentExclusiveLockSegment[] segments)
        {
            using (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(Locker))
            {
                bool isSuccess;
                ConcurrentExclusiveAccessMode lastSuccessAccess = ConcurrentExclusiveAccessMode.None;  //lastSuccessAccess只能是None, Concurrent，Exclusive
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
                        default:  //全部当ConcurrentExclusiveAccess.None处理
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
    /// Pipeline 段声明的访问权限模式。
    /// </summary>
    /// <remarks>
    /// 该枚举只描述当前段希望以哪种访问权限执行。
    /// </remarks>
    public enum ConcurrentExclusiveAccessMode : byte
    {
        /// <summary>
        /// 无访问权限。
        /// </summary>
        /// <remarks>
        /// 执行当前段前会释放上一段仍然持有的权限，当前段在无锁状态下运行。
        /// </remarks>
        None = 0,

        /// <summary>
        /// 获取一段独立的 Concurrent 权限。
        /// </summary>
        /// <remarks>
        /// 如果上一段已经持有 Concurrent，也会先释放再重新申请。
        /// 如果希望延续当前 Concurrent 上下文，请使用 <see cref="ConvergeConcurrent"/>。
        /// </remarks>
        Concurrent = 1,

        /// <summary>
        /// 尝试获取一段独立的 Concurrent 权限。
        /// </summary>
        /// <remarks>
        /// 如果未获得 Concurrent，当前段不会执行，并以 None 状态继续后续流水线。
        /// 如果上一段仍持有权限，会先释放上一段权限再尝试获取。
        /// </remarks>
        TryConcurrent = 2,

        /// <summary>
        /// 获取一段独立的 Exclusive 权限。
        /// </summary>
        /// <remarks>
        /// Exclusive 表示一段独立的排他权限。
        /// 如果上一段已经持有 Exclusive，仍会先释放再重新申请。
        /// 如果希望在已有 Exclusive 上下文中连续执行并受业务 ID 控制，请使用 <see cref="TryApplyIDConvergeExclusive"/>。
        /// </remarks>
        Exclusive = 3,

        /// <summary>
        /// 仅在锁处于 Idle 时尝试获取 Exclusive 权限。
        /// </summary>
        /// <remarks>
        /// 此模式不抢占已有 Concurrent。
        /// 如果当前存在 Concurrent 或 Exclusive，当前段不会执行，并以 None 状态继续后续流水线。
        /// 如果上一段仍持有权限，会先释放上一段权限再尝试获取。
        /// </remarks>
        TestExclusive = 4,

        /// <summary>
        /// 抢占式尝试获取 Exclusive 权限。
        /// </summary>
        /// <remarks>
        /// 此模式允许阻止新的 Concurrent 进入，并尝试获得 Exclusive。
        /// 如果未获得 Exclusive，当前段不会执行，并以 None 状态继续后续流水线。
        /// 如果上一段仍持有权限，会先释放上一段权限再尝试获取。
        /// </remarks>
        TryExclusive = 5,

        /// <summary>
        /// 延续或获取 Concurrent 权限。
        /// </summary>
        /// <remarks>
        /// 如果上一段已经持有 Concurrent，则延续当前 Concurrent 上下文并直接执行当前段。
        /// 如果上一段持有 Exclusive，则调用 ExclusiveToConcurrent() 原地降级后执行当前段。
        /// 如果当前没有访问权限，则重新申请普通 Concurrent 权限。
        /// </remarks>
        ConvergeConcurrent = 6,

        /// <summary>
        /// 尝试应用业务 ID，并在成功后收敛到 Exclusive 权限。
        /// </summary>
        /// <remarks>
        /// 此模式的 Try 语义针对业务 ID 更新，而不是针对 Exclusive 获取。
        ///
        /// 如果当前持有 Concurrent，则根据段声明的 IDType 执行原地升级：
        /// 使用 ContextID 时调用 TryConcurrentToExclusiveWithSwitchContextID(contextID)；
        /// 使用 EpochID 时调用 TryConcurrentToExclusiveWithRaiseEpochID(epochID)。
        ///
        /// 如果当前已经持有 Exclusive，则尝试应用当前段声明的业务 ID：
        /// 使用 ContextID 时调用 SwitchContextID(contextID)；
        /// 使用 EpochID 时调用 RaiseEpochID(epochID)。
        ///
        /// 如果当前没有访问权限，则先尝试应用当前段声明的业务 ID；
        /// ID 应用成功后，会等待并获取 Exclusive 权限。
        ///
        /// 只有业务 ID 应用成功，并且最终进入或保持 Exclusive 权限时，当前段才会执行。
        /// 如果业务 ID 应用失败，当前段不会执行；流水线会释放当前仍持有的权限，
        /// 并以 None 状态继续后续流水线。
        /// </remarks>
        TryApplyIDConvergeExclusive = 7,
    }


    /// <summary>
    /// Pipeline 中的一个业务段。
    /// </summary>
    /// <remarks>
    /// 每个段声明运行该段需要的访问权限，并保存要执行的同步业务代码。
    /// </remarks>
    public readonly struct ConcurrentExclusiveLockSegment
    {
        /// <summary>
        /// 表示 <see cref="ContextOrEpochID"/> 的业务 ID 类型。
        /// </summary>
        public enum IDType : byte
        {
            /// <summary>
            /// 使用 ContextID 切换业务上下文。
            /// </summary>
            ContextID = 0,

            /// <summary>
            /// 使用 EpochID 单调推进业务阶段。
            /// </summary>
            EpochID = 1,
        }

        /// <summary>
        /// 当前段要执行的同步业务代码。
        /// </summary>
        public readonly Action Segment;

        /// <summary>
        /// 当前段要应用的业务上下文 ID 或业务阶段 ID。
        /// </summary>
        /// <remarks>
        /// 该值只对 <see cref="ConcurrentExclusiveAccessMode.TryApplyIDConvergeExclusive"/> 有意义。
        /// 当 <see cref="IDKind"/> 为 <see cref="IDType.ContextID"/> 时表示 ContextID；
        /// 当 <see cref="IDKind"/> 为 <see cref="IDType.EpochID"/> 时表示 EpochID。
        /// </remarks>
        public readonly int ContextOrEpochID;

        /// <summary>
        /// 表示 <see cref="ContextOrEpochID"/> 是 ContextID 还是 EpochID。
        /// </summary>
        public readonly IDType IDKind;

        /// <summary>
        /// 当前段声明的访问权限模式。
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
        /// 创建一个无访问权限的业务段。
        /// </summary>
        /// <param name="segment">在无锁状态下执行的业务代码。</param>
        public static ConcurrentExclusiveLockSegment None(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.None, segment);
        }
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment None(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 创建一个获取独立 Concurrent 权限的业务段。
        /// </summary>
        /// <param name="segment">在成功持有 Concurrent 权限时执行的业务代码。</param>
        public static ConcurrentExclusiveLockSegment Concurrent(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.Concurrent, segment);
        }
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment Concurrent(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 创建一个获取独立 Exclusive 权限的业务段。
        /// </summary>
        /// <param name="segment">在成功持有 Exclusive 权限时执行的业务代码。</param>
        public static ConcurrentExclusiveLockSegment Exclusive(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.Exclusive, segment);
        }
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment Exclusive(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 创建一个尝试获取独立 Concurrent 权限的业务段。
        /// </summary>
        /// <param name="segment">在成功持有 Concurrent 权限时执行的业务代码。</param>
        public static ConcurrentExclusiveLockSegment TryConcurrent(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.TryConcurrent, segment);
        }
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment TryConcurrent(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 创建一个仅在 Idle 状态下尝试获取 Exclusive 权限的业务段。
        /// </summary>
        /// <param name="segment">在成功持有 Exclusive 权限时执行的业务代码。</param>
        public static ConcurrentExclusiveLockSegment TestExclusive(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.TestExclusive, segment);
        }
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment TestExclusive(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 创建一个抢占式尝试获取 Exclusive 权限的业务段。
        /// </summary>
        /// <param name="segment">在成功持有 Exclusive 权限时执行的业务代码。</param>
        public static ConcurrentExclusiveLockSegment TryExclusive(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.TryExclusive, segment);
        }
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment TryExclusive(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 创建一个延续或获取 Concurrent 权限的业务段。
        /// </summary>
        /// <param name="segment">在成功持有 Concurrent 权限时执行的业务代码。</param>
        public static ConcurrentExclusiveLockSegment ConvergeConcurrent(Action segment)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.ConvergeConcurrent, segment);
        }
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment ConvergeConcurrent(Func<Task> segment)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// 创建一个尝试应用业务 ID，并在成功后收敛到 Exclusive 权限的业务段。
        /// </summary>
        /// <param name="segment">在成功持有 Exclusive 权限时执行的业务代码。</param>
        /// <param name="contextOrEpochID">要切换到的业务上下文 ID，或要推进到的业务阶段 ID。</param>
        /// <param name="idType">表示 <paramref name="contextOrEpochID"/> 的具体业务 ID 类型。</param>
        public static ConcurrentExclusiveLockSegment TryApplyIDConvergeExclusive(Action segment, int contextOrEpochID, IDType idType)
        {
            return new ConcurrentExclusiveLockSegment(ConcurrentExclusiveAccessMode.TryApplyIDConvergeExclusive, segment, contextOrEpochID, idType);
        }
        [Obsolete("Pipeline segments must be synchronous. Async segments are not supported.", error: true)]
        public static ConcurrentExclusiveLockSegment TryApplyIDConvergeExclusive(Func<Task> segment, int contextOrEpochID, IDType idType)
        {
            throw new NotSupportedException();
        }
    }
}