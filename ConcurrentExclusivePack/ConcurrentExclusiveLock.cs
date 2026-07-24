/*设计说明：
 * 本锁表达的是访问权限，而不是读写意图。
 *
 * Concurrent 表示当前操作可以与其他 Concurrent 操作同时进入；
 * Exclusive 表示当前操作必须独占进入，不能与任何其他操作并发。
 *
 * 业务代码在 Exclusive 区域内仍然可能包含大量读取逻辑；
 * Concurrent 区域内也可能执行由业务保证互不冲突的修改。
 *
 * 因此，Concurrent / Exclusive 描述的是“是否允许并发访问”，
 * 而不是“代码内部是否读取或写入数据”。
 *
 * Concurrent 普通获取与释放以轻量原子计数为主，不进入 Monitor 排序队列。
 *
 * Exclusive 是抢占式的。Exclusive 请求进入竞争窗口后，会阻止新的
 * Concurrent 继续进入，并等待已经持有的 Concurrent 自然退出。
 * 这是本锁的最大特点。
 *
 * 普通 Exclusive 获取，以及从 Concurrent 原地升级到 Exclusive 的转换，
 * 会借用 Monitor(lock) 的互斥、等待、唤醒和排序机制参与排他调度。
 *
 * 本锁不额外承诺严格 FIFO 公平，也不承诺比 Monitor 更强的调度公平性。
 * 线程实际运行顺序仍会受到操作系统调度、CPU 拓扑、缓存状态、
 * 系统负载以及调用方业务时长影响。
 *
 * ContextID 用于表达业务上下文身份，可用于同一上下文下避免重复获取 Exclusive。
 * EpochID 用于表达单调推进的生命周期、版本或阶段。
 * 两者都属于锁协议之外的业务标识，其含义、分配、校验和清理由调用方负责。
 *
 * Concurrent 不提供递归式嵌套语义。
 * 不应在已持有 Concurrent 时直接请求普通 Exclusive；需要使用原地升级协议。
 * 不应在已持有 Exclusive 时直接请求普通 Concurrent；需要使用原地降级协议。
 * Exclusive 不提供递归计数；如需兼容同一业务上下文的重复进入，可通过 ContextID
 * 在业务层建立协议。
 *
 * ConcurrentExclusiveLock 是值类型，默认初始化的实例不可用。
 * 必须使用 ConcurrentExclusiveLock.Create() 创建实例。
 * 真实状态存放在内部 CELToken 中，复制 ConcurrentExclusiveLock 值不会复制锁状态。
 *
 * CELToken 的核心状态字段为 128bit：
 * Counter 记录 Concurrent / Exclusive 计数，ContextID 记录业务上下文，
 * EpochID 记录单调推进的生命周期版本。
 * Monitor 直接作用于 CELToken 实例，不额外创建独立同步对象。
 *
 * 理论边界：
 * 本锁使用 31bit 计数空间记录同一时刻的 Concurrent 持有数量。
 * 该数量不是调用者发起请求的总数量，实际业务中基本不会触达；
 * 此说明仅用于明确设计边界。
 */

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace IntomicLib
{
    internal class CELToken
    {
        public long Counter;  //低32位为并发计数，高32位为排他计数

        public int _ContextID;  //上下文ID

        public int _EpochID;  //单调推进的生命周期版本ID
    }


    /// <summary>
    /// 提供基于 Concurrent / Exclusive 访问权限的高性能非递归同步锁。
    /// </summary>
    /// <remarks>
    /// 此锁表达的是访问权限，而不是读写意图。
    /// Concurrent 表示当前操作可以与其他 Concurrent 操作同时进入；
    /// Exclusive 表示当前操作必须独占进入，不能与任何其他操作并发。
    /// 此锁不提供递归式嵌套特权。
    ///
    /// 此锁支持抢占式 Exclusive、Concurrent 原地升级为 Exclusive、
    /// Exclusive 原地降级为 Concurrent，以及状态和竞争度快照观察。
    ///
    /// Concurrent 普通获取与释放以轻量原子计数为主，不进入 Monitor 排序队列。
    /// 普通 Exclusive 获取，以及从 Concurrent 原地升级到 Exclusive 的转换，
    /// 会借用 Monitor 的互斥、等待、唤醒和排序机制参与排他调度。
    ///
    /// 此锁不保证严格 FIFO 顺序，也不承诺比 Monitor 更强的调度公平性。
    /// 线程实际运行顺序仍会受到操作系统调度、CPU 拓扑、缓存状态、系统负载
    /// 以及调用方业务时长影响。
    ///
    /// 此类型适用于同步、非递归、高并发共享状态场景，
    /// 尤其适合大量细粒度锁对象并存的服务端场景。
    ///
    /// 此类型是值类型，默认初始化的实例不可用。
    /// 请使用 <see cref="Create"/> 创建实例。
    /// </remarks>
    public readonly struct ConcurrentExclusiveLock
    {
        /// <summary>
        /// 锁能支持的最大并发能力
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
        /// 创建一个已正确初始化的 <see cref="ConcurrentExclusiveLock"/> 实例。
        /// </summary>
        /// <remarks>
        /// <see cref="ConcurrentExclusiveLock"/> 是值类型，默认初始化得到的实例不可用。
        /// 请始终使用此方法创建新的实例。
        ///
        /// 复制返回的 <see cref="ConcurrentExclusiveLock"/> 值不会复制锁状态；
        /// 复制后的值仍会引用同一个内部锁状态。
        /// </remarks>
        public static ConcurrentExclusiveLock Create()
        {
            return new ConcurrentExclusiveLock(new CELToken());
        }

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
        /// 获取当前锁竞争压力的观察指标。
        /// </summary>
        /// <remarks>
        /// 该值是读取瞬间观察到的竞争压力快照，仅用于诊断、监控或调度参考。
        ///
        /// 因此，纯 Concurrent 场景下该值为 0；
        /// 一旦存在 Exclusive 压力，则返回当前观察到的 Concurrent + Exclusive 压力规模。
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
        /// 原子获取或设置与当前锁关联的业务上下文 ID。
        /// </summary>
        /// <remarks>
        /// 此属性用于记录锁协议之外的附加业务状态，例如由业务层标识当前上下文。
        /// 从而识别同一上下文并避免重复权限。
        ///
        /// 直接设置此属性会无条件覆盖当前 ContextID。
        /// 如果需要判断是否切换到不同上下文，请使用 <see cref="SwitchContextID(int)"/>。
        ///
        /// 值为 0 表示未设置上下文 ID。
        /// 非零值的含义、分配、校验及清理均由调用方负责。
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
        /// 设置新的 ContextID，并返回本次设置是否改变了原值。
        /// 如果新值与原值相同，则返回 false。
        /// </summary>
        /// <remarks>
        /// 此方法只切换业务上下文 ID。
        /// </remarks>
        /// <param name="newContextID">新的上下文 ID。</param>
        /// <returns>如果 ContextID 被设置为不同的新值，则返回 true；如果新值与原值相同，则返回 false。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool SwitchContextID(int newContextID)
        {
            return Interlocked.Exchange(ref GetToken()._ContextID, newContextID) != newContextID;
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
        /// 等待获得 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法会在允许进入 Concurrent 状态时返回。
        ///
        /// 获得 Concurrent 后，调用者可以在后续调用 <c>ReleaseConcurrent()</c> 手动释放；
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
            if ((int)counter < 0)  //运行时并发溢出int.MaxValue，基本不可能发生
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
        /// 尝试获得 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法只尝试一次当前是否可以进入 Concurrent 状态，不会等待锁状态变化。
        ///
        /// 返回值不为 0 表示已获得 Concurrent，调用者可以在后续调用 <c>ReleaseConcurrent()</c> 手动释放；
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
            if ((int)counter < 0)  //运行时并发溢出int.MaxValue，基本不可能发生
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
        /// 在指定时间内尝试获得 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法会在指定超时时间内等待锁进入可获得 Concurrent 的状态。
        ///
        /// 返回值不为 0 表示已获得 Concurrent，调用者可以在后续调用 <c>ReleaseConcurrent()</c> 手动释放；
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
                if ((int)counter < 0)  //运行时并发溢出int.MaxValue，基本不可能发生
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
        /// 释放当前持有的一次 Concurrent 访问权限。
        /// </summary>
        /// <remarks>
        /// 此方法必须在当前 scope 已持有 Concurrent 的上下文中调用。
        ///
        /// 调用后，当前 scope 不再持有该次 Concurrent 访问权限，
        /// 不应继续执行任何依赖该次 Concurrent 权限的业务代码。
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseConcurrent()
        {
            Interlocked.Add(ref GetToken().Counter, -1);
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
                        else   //回退给TryConcurrentToExclusive
                        {
                            Interlocked.Add(ref token.Counter, -Exclusive_Add);
                            Monitor.Exit(token);
                            Thread.Yield();
                            goto ReDo;
                        }
                    }
                }
                else  //回退给TryConcurrentToExclusive
                {
                    Interlocked.Add(ref token.Counter, -Exclusive_Add);
                    Monitor.Exit(token);
                    Thread.Yield();
                    goto ReDo;
                }
            }
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
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseExclusive()
        {
            CELToken token = GetToken();
            Interlocked.Add(ref token.Counter, -Exclusive_Add);
            Monitor.Exit(token);
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
        /// 
        /// 此方法用于在完成独占修改后，继续保持一个连续的 Concurrent 访问上下文，
        /// 避免先释放 Exclusive 再重新申请 Concurrent 造成访问窗口。
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExclusiveToConcurrent()
        {
            long counter;

            CELToken token = GetToken();
            counter = Interlocked.Add(ref token.Counter, -Converge_Add);
            Monitor.Exit(token);

            if (counter >= Exclusive_Add)  //在竞争激烈时，需要切段重新申请
            {
                Interlocked.Add(ref token.Counter, -1);
                AcquireConcurrent();
            }
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
        /// 失败后，原 Concurrent 权限已经释放，不应再调用 <c>ReleaseConcurrent()</c>。
        /// </remarks>
        public bool TryConcurrentToExclusiveWithSwitchContextID(int newContextID)
        {
            int adjustTurn = 0;
            CELToken token = GetToken();
            if ((int)Interlocked.Add(ref token.Counter, Converge_Add) != 0)  //语义相当于在并发内部直接升级为多个排他信号
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
        /// 失败后，原 Concurrent 权限已经释放，不应再调用 <c>ReleaseConcurrent()</c>。
        /// </remarks>
        public bool TryConcurrentToExclusiveWithRaiseEpochID(int newEpochID)
        {
            int adjustTurn = 0;
            CELToken token = GetToken();
            if ((int)Interlocked.Add(ref token.Counter, Converge_Add) != 0)  //语义相当于在并发内部直接升级为多个排他信号
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
        /// 辅助方法
        /// </summary>
        /// <param name="counter"></param>
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
    /// ConcurrentExclusiveLock锁状态
    /// </summary>
    public enum ConcurrentExclusiveLockState : byte
    {
        /// <summary>
        /// 空闲
        /// </summary>
        Idle = 0,

        /// <summary>
        /// 并发中
        /// </summary>
        Concurrent = 1,

        /// <summary>
        /// 排他中
        /// </summary>
        Exclusive = 2,
    }

    /// <summary>
    /// ConcurrentExclusiveLock运行时超出最大并发处理能力。
    /// 一般绝无可能发生。
    /// </summary>
    public class ConcurrentExclusiveLockCapacityExceededException : Exception
    {
        /// <summary>
        /// 
        /// </summary>
        public ConcurrentExclusiveLockCapacityExceededException() : base()
        { }
    }
}