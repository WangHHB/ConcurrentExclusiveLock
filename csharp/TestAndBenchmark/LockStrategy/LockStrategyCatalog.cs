namespace LockBenchmark;

internal static class LockStrategyCatalog
{
    private static readonly LockStrategyDefinition[] Definitions =
    {
        new("lock", false, () => new MonitorLockStrategy()),
        new("ReaderWriterLockSlim", true, () => new ReaderWriterLockSlimStrategy()),
        new("CEL", true, () => new ConcurrentExclusiveLockStrategy())
    };

    public static IReadOnlyList<LockStrategyDefinition> Throughput => Definitions;
    public static IReadOnlyList<LockStrategyDefinition> Comparable => Definitions;
    public static IReadOnlyList<LockStrategyDefinition> ExclusiveProgress =>
        Definitions.Where(definition => definition.SupportsConcurrentHolders).ToArray();
}

internal readonly record struct LockStrategyDefinition(
    string Name,
    bool SupportsConcurrentHolders,
    Func<ILockStrategy> Create);
