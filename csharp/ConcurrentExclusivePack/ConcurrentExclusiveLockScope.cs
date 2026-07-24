using System;

namespace IntomicLib
{
    /// <summary>
    /// <see cref="ConcurrentExclusiveLock"/> 的便捷使用封装。
    /// </summary>
    /// <remarks>
    /// 在此 scope 的生命周期内，调用者可以按协议在 Concurrent、Exclusive 状态之间切换，
    /// 包括并发进入、排他进入、原地升级为 Exclusive、从 Exclusive 降级回 Concurrent 等操作。
    /// 此锁不提供递归式嵌套特权。
    ///
    /// 调用者可以手动释放当前持有的访问权限；如果未手动释放，
    /// <see cref="Dispose"/> 会根据 scope 当前持有的状态自动执行对应释放。
    ///
    /// Dispose 只会释放当前 Scope 持有的访问权限，不会还原 ContextID 或 EpochID。
    /// ContextID / EpochID 表示锁关联的业务状态，应由业务代码自行设置、切换、推进或恢复。
    ///
    /// 此类型用于简化 using 模式下的锁状态管理，减少因异常路径、升级/降级路径或提前返回导致的释放错误。
    /// Scope 实例仅供单个调用上下文使用，不支持多线程并发操作，也不应被复制。
    /// </remarks>
    public struct ConcurrentExclusiveLockScope : IDisposable
    {
        private readonly ConcurrentExclusiveLock Locker;
        private long CounterMate;

        /// <summary>
        /// 锁当前状态的观察快照。
        /// </summary>
        /// <remarks>
        /// 该值仅表示读取瞬间观察到的锁状态，不能作为准确的锁状态判断，仅用于诊断、监控。
        ///
        /// 当抢占式 Exclusive 请求进入竞争窗口后，即使它仍在等待当前 Concurrent 释放，
        /// 该值也可能观察为 Exclusive。
        /// 因此，ObservedState 表示当前锁的访问倾向或转换状态，
        /// 不表示当前已经有线程正在执行 Exclusive 业务代码。
        /// </remarks>
        public ConcurrentExclusiveLockState ObservedState { get { return Locker.ObservedState; } }

        /// <summary>
        /// 获取当前锁竞争压力的观察指标。
        /// </summary>
        /// <remarks>
        /// 该值是读取瞬间观察到的竞争压力快照，仅用于诊断、监控或调度参考。
        ///
        /// 因此，纯 Concurrent 场景下该值为 0；
        /// 一旦存在 Exclusive 压力，则返回当前观察到的 Concurrent + Exclusive 压力规模。
        /// </remarks>
        public int ObservedContention { get { return Locker.ObservedContention; } }

        /// <summary>
        /// 原子获取或设置与当前锁关联的业务上下文 ID。
        /// </summary>
        /// <remarks>
        /// 此属性用于记录锁协议之外的附加业务状态，例如由业务层标识当前上下文。
        /// 从而识别同一上下文并避免重复获取权限。
        ///
        /// 直接设置此属性会无条件覆盖当前 ContextID。
        /// 如果需要判断是否切换到不同上下文，请使用 <see cref="SwitchContextID(int)"/>。
        ///
        /// 值为 0 表示未设置上下文 ID。
        /// 非零值的含义、分配、校验及清理均由调用方负责。
        /// </remarks>
        public int ContextID { get { return Locker.ContextID; } set { Locker.ContextID = value; } }

        /// <summary>
        /// 原子获取或设置与当前锁关联的业务阶段 ID。
        /// </summary>
        /// <remarks>
        /// 此属性用于记录锁协议之外的附加业务状态，例如标识排他区域的生命周期阶段、
        /// 数据版本、处理批次或其他业务阶段。
        ///
        /// 直接设置此属性会无条件覆盖当前 EpochID，
        /// 不会检查阶段是否向前推进，也不会阻止回退或重置。
        /// 如果需要保证阶段 ID 只能递增，请使用 <see cref="RaiseEpochID(int)"/>。
        ///
        /// 值为 0 表示未设置阶段 ID。
        /// 非零值的含义、分配、校验及清理均由调用方负责。
        /// </remarks>
        public int EpochID { get { return Locker.EpochID; } set { Locker.EpochID = value; } }

        /// <summary>
        /// 设置新的 ContextID，并返回本次设置是否改变了原值。
        /// 如果新值与原值相同，则返回 false。
        /// </summary>
        /// <remarks>
        /// 此方法只切换业务上下文 ID。
        /// </remarks>
        /// <param name="newContextID">新的上下文 ID。</param>
        /// <returns>如果 ContextID 被设置为不同的新值，则返回 true；如果新值与原值相同，则返回 false。</returns>
        public bool SwitchContextID(int newContextID)
        {
            return Locker.SwitchContextID(newContextID);
        }

        /// <summary>
        /// 尝试将 EpochID 推进到新的阶段，并返回本次推进是否成功。
        /// 如果新值小于或等于当前值，则返回 false。
        /// </summary>
        /// <remarks>
        /// 此方法只推进业务阶段 ID。
        /// </remarks>
        /// <param name="newEpochID">新的阶段 ID，必须大于当前 EpochID 才能推进成功。</param>
        /// <returns>如果 EpochID 成功推进到更大的新值，则返回 true；否则返回 false。</returns>
        public bool RaiseEpochID(int newEpochID)
        {
            return Locker.RaiseEpochID(newEpochID);
        }

        /// <summary>
        /// 初始化一个绑定到指定 <see cref="ConcurrentExclusiveLock"/> 的使用 scope。
        /// </summary>
        /// <remarks>
        /// 构造函数本身不会获得任何 Concurrent 或 Exclusive 访问权限。
        /// 调用者需要在此 scope 上显式进入 Concurrent、Exclusive、原地升级或原地降级等状态。
        ///
        /// <see cref="Dispose"/> 会根据 scope 当前最终持有的状态自动执行对应释放，
        /// 用于简化 using 范围内的锁状态管理。
        /// </remarks>
        /// <param name="locker">
        /// 要由此 scope 管理的 <see cref="ConcurrentExclusiveLock"/> 实例。
        /// </param>
        public ConcurrentExclusiveLockScope(ConcurrentExclusiveLock locker)
        {
            Locker = locker;
            CounterMate = 0;
        }


        /// <summary>
        /// 等待获得 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法会在允许进入 Concurrent 状态时返回。
        ///
        /// 获得 Concurrent 后，调用者可以在后续调用 <c>ReleaseConcurrent()</c> 手动释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        /// 如果之后通过其他协议转换了当前持有状态，则应按转换后的状态释放。
        ///
        /// <paramref name="maxConcurrent"/> 用于限制本次允许进入的最大 Concurrent ID。
        /// <paramref name="maxConcurrent"/> 不能小于 1。
        /// </remarks>
        /// <param name="maxConcurrent">
        /// 最大允许的 Concurrent ID。
        /// 有效返回值范围为 [1, maxConcurrent]。
        /// </param>
        /// <returns>
        /// 返回本次获得的 Concurrent ID，范围为 [1, maxConcurrent]。
        /// </returns>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="ConcurrentExclusiveLockCapacityExceededException">
        /// 当运行时同时存在的 Concurrent 数量超过内部限制时抛出。
        /// 当前实现支持 31-bit 并发计数空间，该限制在实际运行环境中基本不可能触发。
        /// </exception>
        public int AcquireConcurrent(int maxConcurrent = ConcurrentExclusiveLock.MaxConcurrent)
        {
            int concurrentID = Locker.AcquireConcurrent(maxConcurrent);
            CounterMate++;
            return concurrentID;
        }

        /// <summary>
        /// 尝试获得 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法只尝试一次当前是否可以进入 Concurrent 状态，不会等待锁状态变化。
        ///
        /// 返回值不为 0 表示已获得 Concurrent，调用者可以在后续调用 <c>ReleaseConcurrent()</c> 手动释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        /// 如果之后通过其他协议转换了当前持有状态，则应按转换后的状态释放。
        ///
        /// <paramref name="maxConcurrent"/> 用于限制本次允许进入的最大 Concurrent ID。
        /// <paramref name="maxConcurrent"/> 不能小于 1。
        /// </remarks>
        /// <param name="maxConcurrent">
        /// 最大允许的 Concurrent ID。
        /// 有效成功返回值范围为 [1, maxConcurrent]。
        /// </param>
        /// <returns>
        /// 返回本次获得的 Concurrent ID，范围为 [1, maxConcurrent]；
        /// 返回 0 表示未获得 Concurrent。
        /// </returns>
        /// <exception cref="ArgumentException"></exception>
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
        /// 在指定时间内尝试获得 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法会在指定超时时间内等待锁进入可获得 Concurrent 的状态。
        ///
        /// 返回值不为 0 表示已获得 Concurrent，调用者可以在后续调用 <c>ReleaseConcurrent()</c> 手动释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        /// 如果之后通过其他协议转换了当前持有状态，则应按转换后的状态释放。
        ///
        /// <paramref name="millisecondsTimeout"/> 为 0 时只尝试一次，不会等待；小于 0 时表示无限等待。
        ///
        /// <paramref name="maxConcurrent"/> 用于限制本次允许进入的最大 Concurrent ID。
        /// <paramref name="maxConcurrent"/> 不能小于 1。
        /// 如果 <paramref name="millisecondsTimeout"/> 大于等于 0，则最多等待到超时并返回 0；
        /// 如果 <paramref name="millisecondsTimeout"/> 小于 0，则会一直等待。
        /// </remarks>
        /// <param name="millisecondsTimeout">
        /// 等待获得 Concurrent 的最长毫秒数。
        /// 0 表示只尝试一次，不等待；小于 0 表示无限等待。
        /// </param>
        /// <param name="maxConcurrent">
        /// 最大允许的 Concurrent ID。
        /// 有效成功返回值范围为 [1, maxConcurrent]。
        /// </param>
        /// <returns>
        /// 返回本次获得的 Concurrent ID，范围为 [1, maxConcurrent]；
        /// 返回 0 表示未在指定时间内获得 Concurrent。
        /// </returns>
        /// <exception cref="ArgumentException"></exception>
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
        /// 释放当前持有的一次 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法必须在当前 scope 已持有 Concurrent 的上下文中调用。
        ///
        /// 调用后，当前 scope 不再持有该次 Concurrent 访问权限，
        /// 不应继续执行任何依赖该次 Concurrent 权限的业务代码。
        ///
        /// 已手动释放的权限不会再由 <see cref="Dispose"/> 释放。
        /// </remarks>
        public void ReleaseConcurrent()
        {
            Locker.ReleaseConcurrent();
            CounterMate--;
        }

        /// <summary>
        /// 等待获得 Exclusive 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法用于请求抢占式 Exclusive。
        ///
        /// 请求发起后，锁会阻止新的 Concurrent 进入，
        /// 并等待当前已进入的 Concurrent 释放。
        /// 当锁进入 Exclusive 状态后，此方法返回。
        ///
        /// 获得 Exclusive 后，调用者可以在后续调用 <c>ReleaseExclusive()</c> 手动释放，
        /// 或通过 <c>ExclusiveToConcurrent()</c> 降级为 Concurrent 后再按 Concurrent 协议释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        /// </remarks>
        public void AcquireExclusive()
        {
            Locker.AcquireExclusive();
            CounterMate += ConcurrentExclusiveLock.Exclusive_Add;
        }

        /// <summary>
        /// 尝试获得 Exclusive 访问权限。
        /// </summary>
        /// <remarks>
        /// 当 <paramref name="preemptConcurrent"/> 为 true 时，
        /// 此方法会以抢占式方式请求 Exclusive。
        /// 如果当前存在 Concurrent，锁会阻止新的 Concurrent 进入，
        /// 并参与排他竞争；
        /// 成功的 Exclusive 请求会等待当前已进入的 Concurrent 释放后获得 Exclusive。
        ///
        /// 当 <paramref name="preemptConcurrent"/> 为 false 时，
        /// 此方法不会抢占 Concurrent。
        /// 只要当前存在 Concurrent 或 Exclusive，就会立即返回 false。
        ///
        /// 返回 true 表示已获得 Exclusive，调用者可以在后续调用 <c>ReleaseExclusive()</c> 手动释放，
        /// 或通过 <c>ExclusiveToConcurrent()</c> 降级为 Concurrent 后再按 Concurrent 协议释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        ///
        /// 返回 false 表示未获得 Exclusive，调用者不应执行任何依赖 Exclusive 权限的业务代码。
        /// </remarks>
        /// <param name="preemptConcurrent">
        /// true 表示允许抢占 Concurrent；
        /// false 表示不抢占 Concurrent，只有锁处于 Idle 状态时才可能获得 Exclusive。
        /// </param>
        /// <returns>
        /// true 表示已获得 Exclusive；
        /// false 表示未获得 Exclusive。
        /// </returns>
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
        /// 在指定时间内尝试获得 Exclusive 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法会在指定超时时间内尝试获得抢占式 Exclusive。
        ///
        /// <paramref name="millisecondsTimeout"/> 为 0 时只尝试一次，不会等待；
        /// 小于 0 时表示无限等待。
        ///
        /// 返回 true 表示已获得 Exclusive，调用者可以在后续调用 <c>ReleaseExclusive()</c> 手动释放，
        /// 或通过 <c>ExclusiveToConcurrent()</c> 降级为 Concurrent 后再按 Concurrent 协议释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        ///
        /// 返回 false 表示未在指定时间内获得 Exclusive，
        /// 调用者不应执行任何依赖 Exclusive 权限的业务代码。
        /// </remarks>
        /// <param name="millisecondsTimeout">
        /// 等待获得 Exclusive 的最长毫秒数。
        /// 0 表示只尝试一次，不等待；小于 0 表示无限等待。
        /// </param>
        /// <returns>
        /// true 表示已获得 Exclusive；
        /// false 表示未在指定时间内获得 Exclusive。
        /// </returns>
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
        /// 释放当前持有的 Exclusive 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法必须在当前 scope 已持有 Exclusive 的上下文中调用。
        ///
        /// 调用后，当前 scope 不再持有 Exclusive 访问权限，
        /// 不应继续执行任何依赖 Exclusive 权限的业务代码。
        ///
        /// 释放完成后，锁会恢复正常竞争状态，
        /// 等待中的 Concurrent 或 Exclusive 将按照锁的竞争规则继续尝试进入。
        ///
        /// 如果调用者希望在结束 Exclusive 后继续保持 Concurrent 访问权限，
        /// 应调用 <c>ExclusiveToConcurrent()</c>，
        /// 而不是调用此方法。
        ///
        /// 已手动释放的权限不会再由 <see cref="Dispose"/> 释放。
        /// </remarks>
        public void ReleaseExclusive()
        {
            Locker.ReleaseExclusive();
            CounterMate -= ConcurrentExclusiveLock.Exclusive_Add;
        }

        /// <summary>
        /// 将当前持有的 Exclusive 访问权限降级为 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法必须在当前 scope 已持有 Exclusive 的上下文中调用。
        ///
        /// 调用后，当前 scope 不再持有 Exclusive 访问权限，
        /// 不应继续执行任何依赖 Exclusive 权限的业务代码。
        ///
        /// 当前 scope 会继续持有 Concurrent 访问权限，
        /// 后续可以调用 <c>ReleaseConcurrent()</c> 手动释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        ///
        /// 此方法用于在完成独占修改后，继续保持一个连续的 Concurrent 访问上下文，
        /// 避免先释放 Exclusive 再重新申请 Concurrent 造成访问窗口。
        /// </remarks>
        public void ExclusiveToConcurrent()
        {
            Locker.ExclusiveToConcurrent();
            CounterMate -= ConcurrentExclusiveLock.Converge_Add;
        }

        /// <summary>
        /// 在持有 Concurrent 权限的情况下，尝试通过 SwitchContextID 将当前权限升级为 Exclusive。
        /// 当多个 Concurrent 持有者以相同 newContextID 同时调用时，只有成功切换 ContextID 的持有者能够升级。
        /// 当调用者使用不同 newContextID 时，多个调用者可能分别成功，但成功后的 Exclusive 执行区仍会被隔离排序。
        /// 升级成功后，当前 scope 持有 Exclusive 权限；升级失败时，当前 scope 持有的 Concurrent 权限会被自动释放。
        /// </summary>
        /// <remarks>
        /// 此方法必须在当前 scope 已持有 Concurrent 权限的上下文中调用。
        ///
        /// 调用后，锁会阻止新的 Concurrent 进入当前转换窗口，
        /// 成功升级的调用者会按 Exclusive 语义隔离执行。
        /// ContextID 的切换是否成功由 <c>SwitchContextID(newContextID)</c> 决定。
        ///
        /// 成功后，调用者可以调用 <c>ReleaseExclusive()</c> 手动释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        /// 失败后，原 Concurrent 权限已经释放，不应再调用 <c>ReleaseConcurrent()</c>。
        /// </remarks>
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
        /// 在持有 Concurrent 权限的情况下，尝试通过 RaiseEpochID 将当前权限升级为 Exclusive。
        /// 仅当 newEpochID 大于当前 EpochID 时，调用者才可能推进阶段并升级成功。
        /// 当 newEpochID 小于或等于当前 EpochID 时，升级失败，EpochID 保持不变。
        /// 多个调用者使用递增的不同 newEpochID 时，多个调用者可能分别成功，但成功后的 Exclusive 执行区仍会被隔离排序。
        /// 升级成功后，当前 scope 持有 Exclusive 权限；升级失败时，当前 scope 持有的 Concurrent 权限会被自动释放。
        /// </summary>
        /// <remarks>
        /// 此方法必须在当前 scope 已持有 Concurrent 权限的上下文中调用。
        ///
        /// 此方法适合将 EpochID 用作生命周期、版本号或阶段号的场景。
        /// EpochID 只允许向更大的值推进，不允许保持不变或回退。
        /// 调用后，锁会阻止新的 Concurrent 进入当前转换窗口，
        /// 成功升级的调用者会按 Exclusive 语义隔离执行。
        ///
        /// 成功后，调用者可以调用 <c>ReleaseExclusive()</c> 手动释放；
        /// 如果未手动释放，将由 <see cref="Dispose"/> 按 scope 当前最终状态释放。
        /// 失败后，原 Concurrent 权限已经释放，不应再调用 <c>ReleaseConcurrent()</c>。
        /// </remarks>
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
        /// 释放此 scope 当前仍持有的访问权限。
        /// </summary>
        /// <remarks>
        /// Dispose 只根据 scope 自身记录的最终持有状态释放 Concurrent 或 Exclusive。
        /// 已经手动释放的权限不会再次释放。
        /// Dispose 不会还原或清理 ContextID / EpochID。
        /// </remarks>
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