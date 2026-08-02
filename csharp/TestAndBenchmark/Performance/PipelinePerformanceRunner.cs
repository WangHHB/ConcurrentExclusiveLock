using IntomicLib;
using System.Threading;

namespace LockBenchmark;

/// <summary>CEL-specific staged-operation evaluation with internal ablations and portable baselines.</summary>
/// <remarks>
/// Porting contract:
/// - Every logical operation is prepare under Concurrent, exactly one commit under Exclusive,
///   then post-processing under Concurrent. The same StageWork function and step counts are used.
/// - Core/Scope/Pipeline converge preserve a continuous permission context through in-place
///   upgrade/downgrade. Handoff baselines release and reacquire between stages and are therefore
///   explicitly weaker semantics, not equivalent implementations.
/// - Each lock owns independent synchronization and SharedPipelineState; no global lock/state is allowed.
/// - Expected commits per lock are threads * operations and are validated for every strategy.
/// </remarks>
internal static class PipelinePerformanceRunner
{
    private sealed record StrategyDefinition(string Name, string Semantics, Func<BenchmarkOptions, PipelinePerformanceResult> Run);

    public static int Run(BenchmarkOptions options, BenchmarkSession session)
    {
        IReadOnlyList<StrategyDefinition> strategies = CreateStrategies();
        BenchmarkOptions warmupOptions = JitWarmup.Pipeline(options);
        Console.WriteLine("CEL Pipeline performance evaluation");
        Console.WriteLine(
            $"lock-instances={options.LockInstances:n0}, threads/lock={options.Threads:n0}, total-threads={options.TotalWorkerThreads:n0}, " +
            $"operations/thread={options.OperationsPerThread:n0}, prepare={options.PrepareSteps:n0}, " +
            $"commit={options.CommitSteps:n0}, post={options.PostSteps:n0}");
        Console.WriteLine();

        foreach (StrategyDefinition strategy in strategies)
        {
            BenchmarkRunner.ForceCollection();
            _ = strategy.Run(warmupOptions);
        }

        long? expectedCommits = null;
        PrintGroup("CEL internal ablation", strategies.Take(4), options, session, ref expectedCommits);
        Console.WriteLine();
        PrintGroup("Portable baselines", strategies.Skip(4), options, session, ref expectedCommits);

        return 0;
    }

    private static void PrintGroup(
        string title,
        IEnumerable<StrategyDefinition> strategies,
        BenchmarkOptions options,
        BenchmarkSession session,
        ref long? expectedCommits)
    {
        Console.WriteLine($"{title}:");
        Console.WriteLine(
            $"  {"strategy",-26}  {"elapsed",9}  {"cpu%",8}  {"ops/s",13}  {"ops/s/lock",12}  {"ns/op",11}  {"commits",12}");

        foreach (StrategyDefinition strategy in strategies)
        {
            BenchmarkRunner.ForceCollection();
            PipelinePerformanceResult result = strategy.Run(options);
            if (!expectedCommits.HasValue) expectedCommits = result.CommitCount;
            else if (expectedCommits.Value != result.CommitCount)
            {
                throw new InvalidOperationException($"Commit count mismatch: strategy={result.Strategy}.");
            }

            Console.WriteLine(
                $"  {result.Strategy,-26}  {result.Elapsed.TotalSeconds,8:0.000}s  {result.CpuPercent,7:0.0}%  " +
                $"{result.OperationsPerSecond,13:0}  {result.OperationsPerSecond / options.LockInstances,12:0}  " +
                $"{result.NanosecondsPerOperation,11:0}  {result.CommitCount,12:n0}");

            session.Write("staged-pipeline-performance", new
            {
                group = title,
                options.LockInstances,
                threadsPerLock = options.Threads,
                totalThreads = options.TotalWorkerThreads,
                options.OperationsPerThread,
                options.PrepareSteps,
                options.CommitSteps,
                options.PostSteps,
                result.Strategy,
                result.Semantics,
                elapsedSeconds = result.Elapsed.TotalSeconds,
                result.CpuPercent,
                operationsPerSecond = result.OperationsPerSecond,
                operationsPerSecondPerLock = result.OperationsPerSecond / options.LockInstances,
                nanosecondsPerOperation = result.NanosecondsPerOperation,
                result.CommitCount,
                stateHash = unchecked((ulong)result.StateHash).ToString("X16"),
                result.Sink
            });
        }
    }

    private static IReadOnlyList<StrategyDefinition> CreateStrategies() => new[]
    {
        new StrategyDefinition("CEL Core converge", "continuous in-place upgrade/downgrade", RunCelCoreConverge),
        new StrategyDefinition("CEL Scope converge", "continuous in-place upgrade/downgrade with Scope cleanup", RunCelScopeConverge),
        new StrategyDefinition("CEL Pipeline converge", "continuous in-place upgrade/downgrade declared as segments", RunCelPipelineConverge),
        new StrategyDefinition("CEL Core handoff", "release/reacquire gaps between stages", RunCelCoreHandoff),
        new StrategyDefinition("RWLS handoff", "release/reacquire gaps between read and write stages", RunRwlsHandoff),
        new StrategyDefinition("Monitor serialized", "all three stages serialized under one monitor per lock", RunMonitorSerialized)
    };

    private sealed class SharedPipelineState
    {
        public long CommitCount;
        public long State;
        public long CommitSink;
        public long WorkerSink;

        public void Commit(int worker, int operation, int steps)
        {
            long value = StageWork(worker, operation, 1, steps);
            State++;
            CommitCount++;
            CommitSink = unchecked(CommitSink + value);
        }

        public long CombinedSink => unchecked(CommitSink ^ WorkerSink);
    }

    private sealed class CelHolder
    {
        public ConcurrentExclusiveLock Lock = ConcurrentExclusiveLock.Create();
    }

    private sealed class PipelineWorkerState
    {
        private readonly int worker;
        private readonly BenchmarkOptions options;
        private readonly SharedPipelineState shared;
        public int Operation;
        public long LocalSink;

        public PipelineWorkerState(int worker, BenchmarkOptions options, SharedPipelineState shared)
        {
            this.worker = worker;
            this.options = options;
            this.shared = shared;
        }

        public void Prepare() => LocalSink = unchecked(LocalSink + StageWork(worker, Operation, 0, options.PrepareSteps));
        public void Commit() => shared.Commit(worker, Operation, options.CommitSteps);
        public void Post() => LocalSink = unchecked(LocalSink + StageWork(worker, Operation, 2, options.PostSteps));
    }

    private static PipelinePerformanceResult RunCelCoreConverge(BenchmarkOptions options)
    {
        CelHolder[] holders = CreateCelHolders(options.LockInstances);
        SharedPipelineState[] states = CreateStates(options.LockInstances);
        return RunThreads("CEL Core converge", "continuous in-place upgrade/downgrade", options, states,
            (int lockIndex, int _, int worker, int operation, ref long localSink) =>
            {
                ConcurrentExclusiveLock cel = holders[lockIndex].Lock;
                cel.AcquireConcurrent();
                localSink = unchecked(localSink + StageWork(worker, operation, 0, options.PrepareSteps));
                cel.ConcurrentToExclusive();
                states[lockIndex].Commit(worker, operation, options.CommitSteps);
                cel.ExclusiveToConcurrent();
                localSink = unchecked(localSink + StageWork(worker, operation, 2, options.PostSteps));
                cel.ReleaseConcurrent();
            });
    }

    private static PipelinePerformanceResult RunCelScopeConverge(BenchmarkOptions options)
    {
        CelHolder[] holders = CreateCelHolders(options.LockInstances);
        SharedPipelineState[] states = CreateStates(options.LockInstances);
        return RunThreads("CEL Scope converge", "continuous in-place upgrade/downgrade with Scope cleanup", options, states,
            (int lockIndex, int _, int worker, int operation, ref long localSink) =>
            {
                using ConcurrentExclusiveLockScope scope = new(holders[lockIndex].Lock);
                scope.AcquireConcurrent();
                localSink = unchecked(localSink + StageWork(worker, operation, 0, options.PrepareSteps));
                scope.ConcurrentToExclusive();
                states[lockIndex].Commit(worker, operation, options.CommitSteps);
                scope.ExclusiveToConcurrent();
                localSink = unchecked(localSink + StageWork(worker, operation, 2, options.PostSteps));
            });
    }

    private static PipelinePerformanceResult RunCelPipelineConverge(BenchmarkOptions options)
    {
        CelHolder[] holders = CreateCelHolders(options.LockInstances);
        SharedPipelineState[] states = CreateStates(options.LockInstances);
        int totalWorkers = checked((int)options.TotalWorkerThreads);
        ThreadRunMeasurement measurement = DedicatedThreadHarness.Run(totalWorkers, "PipelinePerf", worker =>
        {
            int lockIndex = worker / options.Threads;
            ConcurrentExclusiveLockPipeline pipeline = new(holders[lockIndex].Lock);
            PipelineWorkerState state = new(worker, options, states[lockIndex]);
            ConcurrentExclusiveLockSegment[] segments =
            {
                ConcurrentExclusiveLockSegment.ConvergeConcurrent(state.Prepare),
                ConcurrentExclusiveLockSegment.ConvergeExclusive(state.Commit),
                ConcurrentExclusiveLockSegment.ConvergeConcurrent(state.Post)
            };

            for (int operation = 0; operation < options.OperationsPerThread; operation++)
            {
                state.Operation = operation;
                pipeline.DoPipeline(segments);
            }
            Interlocked.Add(ref states[lockIndex].WorkerSink, state.LocalSink);
        });

        return Complete(
            "CEL Pipeline converge",
            "continuous in-place upgrade/downgrade declared as segments",
            options,
            measurement,
            states);
    }

    private static PipelinePerformanceResult RunCelCoreHandoff(BenchmarkOptions options)
    {
        CelHolder[] holders = CreateCelHolders(options.LockInstances);
        SharedPipelineState[] states = CreateStates(options.LockInstances);
        return RunThreads("CEL Core handoff", "release/reacquire gaps between stages", options, states,
            (int lockIndex, int _, int worker, int operation, ref long localSink) =>
            {
                ConcurrentExclusiveLock cel = holders[lockIndex].Lock;
                cel.AcquireConcurrent();
                localSink = unchecked(localSink + StageWork(worker, operation, 0, options.PrepareSteps));
                cel.ReleaseConcurrent();
                cel.AcquireExclusive();
                states[lockIndex].Commit(worker, operation, options.CommitSteps);
                cel.ReleaseExclusive();
                cel.AcquireConcurrent();
                localSink = unchecked(localSink + StageWork(worker, operation, 2, options.PostSteps));
                cel.ReleaseConcurrent();
            });
    }

    private static PipelinePerformanceResult RunRwlsHandoff(BenchmarkOptions options)
    {
        ReaderWriterLockSlim[] locks = Enumerable.Range(0, options.LockInstances)
            .Select(_ => new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion))
            .ToArray();
        SharedPipelineState[] states = CreateStates(options.LockInstances);
        try
        {
            return RunThreads("RWLS handoff", "release/reacquire gaps between read and write stages", options, states,
                (int lockIndex, int _, int worker, int operation, ref long localSink) =>
                {
                    ReaderWriterLockSlim rwls = locks[lockIndex];
                    rwls.EnterReadLock();
                    try { localSink = unchecked(localSink + StageWork(worker, operation, 0, options.PrepareSteps)); }
                    finally { rwls.ExitReadLock(); }
                    rwls.EnterWriteLock();
                    try { states[lockIndex].Commit(worker, operation, options.CommitSteps); }
                    finally { rwls.ExitWriteLock(); }
                    rwls.EnterReadLock();
                    try { localSink = unchecked(localSink + StageWork(worker, operation, 2, options.PostSteps)); }
                    finally { rwls.ExitReadLock(); }
                });
        }
        finally
        {
            foreach (ReaderWriterLockSlim rwls in locks) rwls.Dispose();
        }
    }

    private static PipelinePerformanceResult RunMonitorSerialized(BenchmarkOptions options)
    {
        object[] monitors = Enumerable.Range(0, options.LockInstances).Select(_ => new object()).ToArray();
        SharedPipelineState[] states = CreateStates(options.LockInstances);
        return RunThreads("Monitor serialized", "all three stages serialized under one monitor per lock", options, states,
            (int lockIndex, int _, int worker, int operation, ref long localSink) =>
            {
                lock (monitors[lockIndex])
                {
                    localSink = unchecked(localSink + StageWork(worker, operation, 0, options.PrepareSteps));
                    states[lockIndex].Commit(worker, operation, options.CommitSteps);
                    localSink = unchecked(localSink + StageWork(worker, operation, 2, options.PostSteps));
                }
            });
    }

    private delegate void PipelineOperation(int lockIndex, int localWorker, int globalWorker, int operation, ref long localSink);

    private static PipelinePerformanceResult RunThreads(
        string name,
        string semantics,
        BenchmarkOptions options,
        SharedPipelineState[] states,
        PipelineOperation operation)
    {
        int totalWorkers = checked((int)options.TotalWorkerThreads);
        ThreadRunMeasurement measurement = DedicatedThreadHarness.Run(totalWorkers, "PipelinePerf", globalWorker =>
        {
            int lockIndex = globalWorker / options.Threads;
            int localWorker = globalWorker % options.Threads;
            long localSink = 0;
            for (int index = 0; index < options.OperationsPerThread; index++)
            {
                operation(lockIndex, localWorker, globalWorker, index, ref localSink);
            }
            Interlocked.Add(ref states[lockIndex].WorkerSink, localSink ^ localWorker);
        });
        return Complete(name, semantics, options, measurement, states);
    }

    private static PipelinePerformanceResult Complete(
        string name,
        string semantics,
        BenchmarkOptions options,
        ThreadRunMeasurement measurement,
        SharedPipelineState[] states)
    {
        long expectedPerLock = checked((long)options.Threads * options.OperationsPerThread);
        for (int lockIndex = 0; lockIndex < states.Length; lockIndex++)
        {
            SharedPipelineState state = states[lockIndex];
            if (state.CommitCount != expectedPerLock || state.State != expectedPerLock)
            {
                throw new InvalidOperationException(
                    $"Pipeline commit validation failed for {name}, lock={lockIndex}: expected={expectedPerLock:n0}, " +
                    $"commits={state.CommitCount:n0}, state={state.State:n0}.");
            }
        }

        long expectedTotal = checked(expectedPerLock * options.LockInstances);
        long aggregateState = states.Aggregate(0L, (value, state) => checked(value + state.State));
        long aggregateSink = states.Aggregate(0L, (value, state) => unchecked(value ^ state.CombinedSink));
        return new PipelinePerformanceResult(
            name,
            semantics,
            measurement.Elapsed,
            MeasurementMath.CpuPercent(measurement.CpuTime, measurement.Elapsed),
            expectedTotal,
            states.Sum(state => state.CommitCount),
            aggregateState,
            aggregateSink);
    }

    private static CelHolder[] CreateCelHolders(int count) => Enumerable.Range(0, count).Select(_ => new CelHolder()).ToArray();
    private static SharedPipelineState[] CreateStates(int count) => Enumerable.Range(0, count).Select(_ => new SharedPipelineState()).ToArray();

    private static long StageWork(int worker, int operation, int stage, int steps)
    {
        unchecked
        {
            ulong value = ((ulong)(uint)worker << 32) ^ (uint)operation ^ ((ulong)(uint)stage << 48) ^ 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < steps; i++)
            {
                value ^= value << 7;
                value ^= value >> 9;
                value *= 0xBF58476D1CE4E5B9UL;
            }
            return (long)value;
        }
    }
}
