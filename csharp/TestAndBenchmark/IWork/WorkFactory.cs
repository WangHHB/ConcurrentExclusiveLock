namespace LockBenchmark;

internal static class WorkFactory
{
    public static WorkDefinition Create(BenchmarkOptions options) => options.Workload switch
    {
        WorkloadKind.Cpu => new WorkDefinition(
            $"cpu (concurrent-work={options.ConcurrentWorkSteps}, exclusive-work={options.ExclusiveWorkSteps})",
            () => new CpuWork(options.ConcurrentWorkSteps, options.ExclusiveWorkSteps)),

        WorkloadKind.Memory => new WorkDefinition(
            $"memory ({options.MemoryWorkingSetMb} MiB shared, concurrent-work={options.ConcurrentWorkSteps}, exclusive-work={options.ExclusiveWorkSteps})",
            () => new MemoryWork(options.ConcurrentWorkSteps, options.ExclusiveWorkSteps, options.MemoryWorkingSetMb)),

        WorkloadKind.Dictionary => new WorkDefinition(
            $"dictionary cache ({options.DictionaryEntries:n0} entries, concurrent-work={options.ConcurrentWorkSteps}, exclusive-work={options.ExclusiveWorkSteps})",
            () => new DictionaryWork(options.ConcurrentWorkSteps, options.ExclusiveWorkSteps, options.DictionaryEntries)),

        WorkloadKind.Ledger => new WorkDefinition(
            $"account ledger ({options.DictionaryEntries:n0} accounts, concurrent-work={options.ConcurrentWorkSteps}, exclusive-work={options.ExclusiveWorkSteps})",
            () => new LedgerWork(options.ConcurrentWorkSteps, options.ExclusiveWorkSteps, options.DictionaryEntries)),

        WorkloadKind.Payload => new WorkDefinition(
            $"binary payload ({options.PayloadFrames:n0} frames, concurrent-work={options.ConcurrentWorkSteps}, exclusive-work={options.ExclusiveWorkSteps})",
            () => new BinaryPayloadWork(options.ConcurrentWorkSteps, options.ExclusiveWorkSteps, options.PayloadFrames)),

        _ => throw new ArgumentOutOfRangeException(nameof(options.Workload))
    };
}

internal readonly record struct WorkDefinition(string Name, Func<IWork> Create);
