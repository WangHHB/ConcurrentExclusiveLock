using System.Threading;

namespace LockBenchmark;

internal static class BenchmarkRunner
{
    private const int WarmupConcurrentPermille = 500;
    private static long sink;

    public static int Run(BenchmarkOptions options, BenchmarkSession session)
    {
        WorkDefinition workDefinition = WorkFactory.Create(options);
        BenchmarkOptions warmupOptions = JitWarmup.Comparable(options);
        WorkDefinition warmupWorkDefinition = WorkFactory.Create(warmupOptions);
        Console.WriteLine("Throughput benchmark");
        Console.WriteLine(
            $"lock-instances={options.LockInstances:n0}, threads/lock={options.Threads:n0}, " +
            $"total-threads={options.TotalWorkerThreads:n0}, works/thread={options.OperationsPerThread:n0}, " +
            $"concurrent-work={options.ConcurrentWorkSteps:n0}, exclusive-work={options.ExclusiveWorkSteps:n0}");
        Console.WriteLine($"workload={workDefinition.Name}");
        Console.WriteLine("Exclusive-op timing=acquire+work+release");
        Console.WriteLine();

        IReadOnlyList<LockStrategyDefinition> strategies = LockStrategyCatalog.Throughput;
        Warmup(strategies, warmupWorkDefinition, warmupOptions);

        foreach (BenchmarkScenario scenario in BenchmarkScenarioCatalog.Resolve(options.ConcurrentPermille))
        {
            RunScenario(options, session, workDefinition, strategies, scenario);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return 0;
    }

    private static void Warmup(
        IReadOnlyList<LockStrategyDefinition> strategies,
        WorkDefinition workDefinition,
        BenchmarkOptions options)
    {
        foreach (LockStrategyDefinition strategy in strategies)
        {
            ForceCollection();
            ThroughputResult result = BenchmarkCaseRunner.Run(strategy, workDefinition, options, WarmupConcurrentPermille);
            Volatile.Write(ref sink, result.Checksum ^ result.StateHash);
        }
    }

    private static void RunScenario(
        BenchmarkOptions options,
        BenchmarkSession session,
        WorkDefinition workDefinition,
        IReadOnlyList<LockStrategyDefinition> strategies,
        BenchmarkScenario scenario)
    {
        Console.WriteLine($"Scenario: {scenario.Name}");
        Console.WriteLine(
            $"  {"lock type",-26}  {"elapsed",9}  {"cpu%",9}  {"works/s",12}  {"works/s/lock",12}  " +
            $"{"work/cpu%",11}  {"concurrent",12}  {"exclusive",12}  {"avg Exclusive op ns",20}  {"state",16}");

        long? expectedState = null;
        foreach (LockStrategyDefinition strategy in strategies)
        {
            ForceCollection();
            ThroughputResult result = BenchmarkCaseRunner.Run(strategy, workDefinition, options, scenario.ConcurrentPermille);
            Volatile.Write(ref sink, result.Checksum ^ result.StateHash);

            if (!expectedState.HasValue) expectedState = result.StateHash;
            else if (expectedState.Value != result.StateHash)
            {
                throw new InvalidOperationException(
                    $"Final work state differs: expected={expectedState.Value:X16}, actual={result.StateHash:X16}, strategy={result.LockName}.");
            }

            PrintResult(result, options.LockInstances);
            session.Write("throughput", new
            {
                scenario = scenario.Name,
                concurrentPermille = scenario.ConcurrentPermille,
                options.LockInstances,
                threadsPerLock = options.Threads,
                totalThreads = options.TotalWorkerThreads,
                options.OperationsPerThread,
                options.ConcurrentWorkSteps,
                options.ExclusiveWorkSteps,
                workload = workDefinition.Name,
                result.LockName,
                elapsedSeconds = result.Elapsed.TotalSeconds,
                result.CpuPercent,
                worksPerSecond = result.OperationsPerSecond,
                worksPerSecondPerLock = result.OperationsPerSecond / options.LockInstances,
                concurrentOperations = result.ConcurrentOperations,
                exclusiveOperations = result.ExclusiveOperations,
                averageExclusiveOperationNs = result.AverageExclusiveOperationNs,
                stateHash = unchecked((ulong)result.StateHash).ToString("X16"),
                result.Checksum
            });
        }

        Console.WriteLine();
    }

    private static void PrintResult(ThroughputResult result, int lockInstances)
    {
        double workPerCpuPercent = result.CpuPercent > 0.000001 ? result.OperationsPerSecond / result.CpuPercent : 0;
        Console.WriteLine(
            $"  {result.LockName,-26}  {result.Elapsed.TotalSeconds,8:0.000}s  {result.CpuPercent,8:0.0}%  " +
            $"{result.OperationsPerSecond,12:0}  {result.OperationsPerSecond / lockInstances,12:0}  {workPerCpuPercent,11:0}  " +
            $"{result.ConcurrentOperations,12:n0}  {result.ExclusiveOperations,12:n0}  {result.AverageExclusiveOperationNs,20:0.0}  " +
            $"{unchecked((ulong)result.StateHash),16:X16}");
    }

    internal static void ForceCollection()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
