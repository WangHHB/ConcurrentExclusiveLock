using System;

namespace LockBenchmark;

/// <summary>
/// 根据 --workload 单独创建一套工作集。
/// Cpu/Memory 是两个极限基线；Dictionary/Ledger/Payload 是业务模拟工作集。
/// </summary>
internal static class WorkFactory
{
    public static WorkDefinition Create(BenchmarkOptions options)
    {
        return options.Workload switch
        {
            WorkloadKind.Cpu => new WorkDefinition(
                $"cpu (read-steps={options.ReadSteps}, write-steps={options.WriteSteps})",
                () => new CpuWork(options.ReadSteps, options.WriteSteps)),

            WorkloadKind.Memory => new WorkDefinition(
                $"memory ({options.MemoryWorkingSetMb} MiB shared, read-steps={options.ReadSteps}, write-steps={options.WriteSteps})",
                () => new MemoryWork(options.ReadSteps, options.WriteSteps, options.MemoryWorkingSetMb)),

            WorkloadKind.Dictionary => new WorkDefinition(
                $"dictionary cache ({options.DictionaryEntries:n0} entries, read-steps={options.ReadSteps}, write-steps={options.WriteSteps})",
                () => new DictionaryWork(options.ReadSteps, options.WriteSteps, options.DictionaryEntries)),

            WorkloadKind.Ledger => new WorkDefinition(
                $"account ledger ({options.DictionaryEntries:n0} accounts, read-steps={options.ReadSteps}, write-steps={options.WriteSteps})",
                () => new LedgerWork(options.ReadSteps, options.WriteSteps, options.DictionaryEntries)),

            WorkloadKind.Payload => new WorkDefinition(
                $"binary payload ({BinaryPayloadWork.GetFrameCount(options.DictionaryEntries):n0} frames, read-steps={options.ReadSteps}, write-steps={options.WriteSteps})",
                () => new BinaryPayloadWork(options.ReadSteps, options.WriteSteps, BinaryPayloadWork.GetFrameCount(options.DictionaryEntries))),

            _ => throw new ArgumentOutOfRangeException(nameof(options.Workload))
        };
    }
}

/// <summary>
/// 选中工作集的显示名称与创建方法。
/// 每次锁测试都调用 <see cref="Create"/>，确保不同锁不共享已经运行过的数据。
/// </summary>
internal readonly struct WorkDefinition
{
    public string Name { get; }
    public Func<IWork> Create { get; }

    public WorkDefinition(string name, Func<IWork> create)
    {
        Name = name;
        Create = create;
    }
}
