using System.Diagnostics;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Measures blocking acquisition calls while still executing the complete requested workload.
/// </summary>
/// <remarks>
/// Porting contract: every AcquireConcurrent/AcquireExclusive call is timed so sampled and
/// non-sampled operations execute the same instrumented acquisition path. Work and release are never
/// inside the measured interval. --latency-sample-every controls only retained sample count, not the
/// number of timed or executed operations. Retention is worker-local and stratified: each consecutive
/// block of N operations contributes exactly one sample, at a deterministic pseudo-random position
/// chosen from a sampling stream independent of the operation-mix stream. Do not time only retained
/// operations, and do not use operationIndex % N == 0: selective instrumentation and a shared phase
/// both bias the latency distribution under a common-gate start. All retained worker samples are
/// pooled per strategy/scenario before the shared linear-interpolation percentile calculation.
/// </remarks>
internal static class AcquisitionLatencyRunner
{
    private const int WarmupConcurrentPermille = 500;

    public static int Run(BenchmarkOptions options, BenchmarkSession session)
    {
        WorkDefinition workDefinition = WorkFactory.Create(options);
        BenchmarkOptions warmupOptions = JitWarmup.Comparable(options);
        WorkDefinition warmupWorkDefinition = WorkFactory.Create(warmupOptions);
        IReadOnlyList<LockStrategyDefinition> strategies = LockStrategyCatalog.Comparable;
        Console.WriteLine("Acquisition latency benchmark");
        Console.WriteLine(
            $"lock-instances={options.LockInstances:n0}, threads/lock={options.Threads:n0}, total-threads={options.TotalWorkerThreads:n0}, " +
            $"operations/thread={options.OperationsPerThread:n0}, sample-every={options.LatencySampleEvery:n0}");
        Console.WriteLine($"workload={workDefinition.Name}");
        Console.WriteLine("measurement=acquisition; retention=1 sample/worker block");
        Console.WriteLine();

        Warmup(strategies, warmupWorkDefinition, warmupOptions);
        foreach (BenchmarkScenario scenario in BenchmarkScenarioCatalog.Resolve(options.ConcurrentPermille))
        {
            RunScenario(options, session, workDefinition, strategies, scenario);
        }
        return 0;
    }

    private static void Warmup(
        IReadOnlyList<LockStrategyDefinition> strategies,
        WorkDefinition workDefinition,
        BenchmarkOptions options)
    {
        foreach (LockStrategyDefinition strategy in strategies)
        {
            BenchmarkRunner.ForceCollection();
            _ = RunCase(strategy, workDefinition, options, WarmupConcurrentPermille);
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
            $"  {"lock type",-26}  {"elapsed",9}  {"cpu%",8}  {"ops/s",12}  {"ops/s/lock",12}  " +
            $"{"permission",10} {"samples",10} {"mean",12} {"p50",12} {"p95",12} {"p99",12} {"p99.9",12} {"max",12}");

        long? expectedState = null;
        foreach (LockStrategyDefinition strategy in strategies)
        {
            BenchmarkRunner.ForceCollection();
            AcquisitionLatencyResult result = RunCase(strategy, workDefinition, options, scenario.ConcurrentPermille);
            if (!expectedState.HasValue) expectedState = result.StateHash;
            else if (expectedState.Value != result.StateHash)
            {
                throw new InvalidOperationException($"State mismatch in acquisition latency: strategy={result.LockName}.");
            }

            PrintResult(result, options.LockInstances);
            session.Write("acquisition-latency", new
            {
                scenario = scenario.Name,
                concurrentPermille = scenario.ConcurrentPermille,
                options.LockInstances,
                threadsPerLock = options.Threads,
                totalThreads = options.TotalWorkerThreads,
                options.OperationsPerThread,
                options.LatencySampleEvery,
                workload = workDefinition.Name,
                result.LockName,
                elapsedSeconds = result.Elapsed.TotalSeconds,
                result.CpuPercent,
                operationsPerSecond = result.OperationsPerSecond,
                operationsPerSecondPerLock = result.OperationsPerSecond / options.LockInstances,
                result.ConcurrentLatency,
                result.ExclusiveLatency,
                concurrentCount = result.ConcurrentOperations,
                exclusiveCount = result.ExclusiveOperations,
                stateHash = unchecked((ulong)result.StateHash).ToString("X16")
            });
        }

        Console.WriteLine();
    }

    private static AcquisitionLatencyResult RunCase(
        LockStrategyDefinition strategyDefinition,
        WorkDefinition workDefinition,
        BenchmarkOptions options,
        int concurrentPermille)
    {
        ILockStrategy[] strategies = new ILockStrategy[options.LockInstances];
        IWork[] works = new IWork[options.LockInstances];
        int totalThreads = checked((int)options.TotalWorkerThreads);
        List<long>[] concurrentSamples = new List<long>[totalThreads];
        List<long>[] exclusiveSamples = new List<long>[totalThreads];

        try
        {
            for (int i = 0; i < options.LockInstances; i++)
            {
                strategies[i] = strategyDefinition.Create();
                works[i] = workDefinition.Create();
                works[i].Init();
            }

            long totalConcurrent = 0;
            long totalExclusive = 0;
            ThreadRunMeasurement measurement = DedicatedThreadHarness.Run(totalThreads, "AcquisitionLatency", workerIndex =>
            {
                int lockIndex = workerIndex / options.Threads;
                int localWorkerIndex = workerIndex % options.Threads;
                ILockStrategy strategy = strategies[lockIndex];
                IWork work = works[lockIndex];
                List<long> localConcurrentSamples = new(Math.Max(4, options.OperationsPerThread / options.LatencySampleEvery));
                List<long> localExclusiveSamples = new(Math.Max(4, options.OperationsPerThread / options.LatencySampleEvery / 4));
                concurrentSamples[workerIndex] = localConcurrentSamples;
                exclusiveSamples[workerIndex] = localExclusiveSamples;
                uint random = DeterministicRandom.CreateWorkerSeed(lockIndex, localWorkerIndex);

                // Sampling must use an independent stream. Reusing the operation-mix stream would
                // correlate permission selection with sample retention in some sample intervals.
                // The fixed xor constant is a stream-domain separator, not an entropy source.
                uint sampleRandom = DeterministicRandom.Next(random ^ 0xD1B5_4A35u);
                int sampleBlockIndex = 0;
                int sampleBlockRemaining = Math.Min(options.LatencySampleEvery, options.OperationsPerThread);
                int sampleOffset = (int)(sampleRandom % (uint)sampleBlockRemaining);
                long localConcurrent = 0;
                long localExclusive = 0;
                long localSink = 0;

                for (int operation = 0; operation < options.OperationsPerThread; operation++)
                {
                    random = DeterministicRandom.Next(unchecked(random + (uint)operation));
                    bool isConcurrent = DeterministicRandom.IsConcurrent(random, concurrentPermille);
                    long acquisitionTicks;

                    // Time every acquisition, not only the operation that will be retained. Selective
                    // timing changes the pre-acquire path of sampled operations and can systematically
                    // move them deeper into a contended queue. The second timestamp is taken immediately
                    // after permission is acquired; business work and release remain outside the interval.
                    if (isConcurrent)
                    {
                        long start = Stopwatch.GetTimestamp();
                        strategy.AcquireConcurrent();
                        acquisitionTicks = Stopwatch.GetTimestamp() - start;
                        try { localSink = unchecked(localSink + work.TickRead()); }
                        finally { strategy.ReleaseConcurrent(); }
                        localConcurrent++;
                    }
                    else
                    {
                        long start = Stopwatch.GetTimestamp();
                        strategy.AcquireExclusive();
                        acquisitionTicks = Stopwatch.GetTimestamp() - start;
                        try { localSink = unchecked(localSink + work.TickWrite()); }
                        finally { strategy.ReleaseExclusive(); }
                        localExclusive++;
                    }

                    // Retain exactly one sample in every worker-local block. Sampling bookkeeping is
                    // performed only after release so it cannot extend the measured acquisition or the
                    // permission-hold interval. A fresh offset per block removes common-start phase bias
                    // and fixed periodic correlation with the deterministic permission stream.
                    if (sampleOffset == 0)
                    {
                        if (isConcurrent) localConcurrentSamples.Add(acquisitionTicks);
                        else localExclusiveSamples.Add(acquisitionTicks);
                    }

                    sampleOffset--;
                    sampleBlockRemaining--;
                    if (sampleBlockRemaining == 0 && operation + 1 < options.OperationsPerThread)
                    {
                        sampleBlockIndex++;
                        sampleBlockRemaining = Math.Min(
                            options.LatencySampleEvery,
                            options.OperationsPerThread - operation - 1);
                        sampleRandom = DeterministicRandom.Next(
                            unchecked(sampleRandom + (uint)sampleBlockIndex));
                        sampleOffset = (int)(sampleRandom % (uint)sampleBlockRemaining);
                    }
                }

                Interlocked.Add(ref totalConcurrent, localConcurrent);
                Interlocked.Add(ref totalExclusive, localExclusive);
                GC.KeepAlive(localSink);
            });

            return new AcquisitionLatencyResult(
                strategyDefinition.Name,
                measurement.Elapsed,
                MeasurementMath.CpuPercent(measurement.CpuTime, measurement.Elapsed),
                totalConcurrent,
                totalExclusive,
                BenchmarkCaseRunner.CombineStateHashes(works),
                Statistics.SummarizeTicks(concurrentSamples.SelectMany(x => x ?? Enumerable.Empty<long>())),
                Statistics.SummarizeTicks(exclusiveSamples.SelectMany(x => x ?? Enumerable.Empty<long>())));
        }
        finally
        {
            for (int i = works.Length - 1; i >= 0; i--)
            {
                works[i]?.Dispose();
                strategies[i]?.Dispose();
            }
        }
    }

    private static void PrintResult(AcquisitionLatencyResult result, int lockInstances)
    {
        PrintPermission(result, lockInstances, "Concurrent", result.ConcurrentLatency);
        PrintPermission(result, lockInstances, "Exclusive", result.ExclusiveLatency);
    }

    private static void PrintPermission(
        AcquisitionLatencyResult result,
        int lockInstances,
        string permission,
        LatencySummary latency)
    {
        Console.WriteLine(
            $"  {result.LockName,-26}  {result.Elapsed.TotalSeconds,8:0.000}s  {result.CpuPercent,7:0.0}%  " +
            $"{result.OperationsPerSecond,12:0}  {result.OperationsPerSecond / lockInstances,12:0}  " +
            $"{permission,10} {latency.Count,10:n0} {BenchmarkReporter.FormatLatency(latency.MeanNs),12} " +
            $"{BenchmarkReporter.FormatLatency(latency.P50Ns),12} {BenchmarkReporter.FormatLatency(latency.P95Ns),12} " +
            $"{BenchmarkReporter.FormatLatency(latency.P99Ns),12} {BenchmarkReporter.FormatLatency(latency.P999Ns),12} " +
            $"{BenchmarkReporter.FormatLatency(latency.MaxNs),12}");
    }
}
