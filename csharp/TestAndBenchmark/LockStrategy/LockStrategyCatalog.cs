using System;
using System.Collections.Generic;

namespace LockBenchmark;

/// <summary>
/// 标准横向压测使用的锁策略目录及固定运行顺序。
/// </summary>
internal static class LockStrategyCatalog
{
    private static readonly LockStrategyDefinition[] Definitions =
    {
        new LockStrategyDefinition(() => new MonitorLockStrategy()),
        new LockStrategyDefinition(() => new ReaderWriterLockSlimStrategy()),
        new LockStrategyDefinition(() => new ConcurrentExclusiveLockStrategy(false)),
        new LockStrategyDefinition(() => new ConcurrentExclusiveLockStrategy(true)),
    };

    public static IReadOnlyList<LockStrategyDefinition> All => Definitions;
}

/// <summary>
/// 锁策略的无状态创建定义。每个测试案例都通过它获得一个全新的锁实例。
/// </summary>
internal readonly struct LockStrategyDefinition
{
    public Func<ILockStrategy> Create { get; }

    public LockStrategyDefinition(Func<ILockStrategy> create)
    {
        Create = create;
    }
}