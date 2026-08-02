namespace LockBenchmark;

/// <summary>
/// Builds the single tiny, fixed, unreported JIT warmup used by every performance strategy.
/// </summary>
/// <remarks>
/// Porting contract:
/// - Warmup count is not configurable: each compared strategy is invoked exactly once.
/// - Warmup is not a statistical repetition and must never execute the user's requested topology.
/// - Lock count is 1; thread/operation counts are the fixed values below; data sizes are minimal.
/// - A non-zero requested work step becomes exactly 1 so the same workload code path is compiled.
/// - Runtimes without JIT compilation may retain this tiny call for code-path initialization, but
///   must not turn it into a full hidden benchmark run.
/// </remarks>
internal static class JitWarmup
{
    public static BenchmarkOptions Comparable(BenchmarkOptions source) => Create(source, threads: 2, operations: 8);

    public static BenchmarkOptions ExclusiveProgress(BenchmarkOptions source) => Create(source, threads: 1, operations: 8);

    public static BenchmarkOptions Pipeline(BenchmarkOptions source) => Create(source, threads: 1, operations: 1);

    private static BenchmarkOptions Create(BenchmarkOptions source, int threads, int operations)
    {
        BenchmarkOptions result = new()
        {
            Mode = source.Mode,
            LockInstances = 1,
            Threads = threads,
            OperationsPerThread = operations,
            ConcurrentWorkSteps = WarmupSteps(source.ConcurrentWorkSteps),
            ExclusiveWorkSteps = WarmupSteps(source.ExclusiveWorkSteps),
            MemoryWorkingSetMb = 1,
            DictionaryEntries = 2,
            PayloadFrames = 1,
            Workload = source.Workload,
            ConcurrentPermille = 500,
            LatencySampleEvery = 1,
            PrepareSteps = WarmupSteps(source.PrepareSteps),
            CommitSteps = WarmupSteps(source.CommitSteps),
            PostSteps = WarmupSteps(source.PostSteps),
            UpgradeContentionConcurrentThreads = 1,
            UpgradeContentionExclusiveThreads = 1,
            MachineId = source.MachineId,
            ExperimentId = source.ExperimentId
        };
        result.Validate();
        return result;
    }

    private static int WarmupSteps(int requestedSteps) => requestedSteps == 0 ? 0 : 1;
}
