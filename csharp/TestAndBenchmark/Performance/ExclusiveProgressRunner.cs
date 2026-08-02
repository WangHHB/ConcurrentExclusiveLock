using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Counts how many Exclusive operations complete while a fixed total of Concurrent operations is processed.
/// </summary>
/// <remarks>
/// Porting contract:
/// - The test uses only AcquireConcurrent, ReleaseConcurrent, AcquireExclusive, and ReleaseExclusive.
/// - --threads is the number of Concurrent workers per lock.
/// - --operations is the fixed number of Concurrent operations performed by every Concurrent worker.
/// - Every lock has exactly one Exclusive writer. After each completed Exclusive operation, that writer must observe at least
///   one new Concurrent completion on the same lock before it may start the next Exclusive acquisition.
/// - This per-lock progress gate is part of the benchmark definition. It prevents a fast writer from repeatedly reacquiring,
///   extending the Concurrent flood, and thereby creating more time in which to count additional Exclusive operations.
/// - Before measurement, every Concurrent worker acquires once and waits at a common flood gate. This establishes the same
///   initial Concurrent-holder topology without inspecting implementation-specific state.
/// - An Exclusive operation is counted only when the writer acquires while at least one Concurrent worker for that lock remains active.
/// - Strategies that serialize their Concurrent path are excluded because they cannot establish the required initial topology.
/// - Ports must preserve the ordering used here: record the per-lock Concurrent-completion snapshot while Exclusive is still held,
///   release Exclusive, then wait until that completion counter advances (or all Concurrent workers finish) before reacquiring.
/// </remarks>
internal static class ExclusiveProgressRunner
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WriterArmInterval = TimeSpan.FromMilliseconds(10);

    private sealed class ExclusiveProgressGroup : IDisposable
    {
        public ILockStrategy Strategy { get; }
        public IWork Work { get; }
        public int RemainingConcurrentWorkers;
        // Incremented after every completed Concurrent acquire/work/release cycle.
        // This is both the final operation count and the portable per-lock progress signal used to gate the writer.
        public long ConcurrentOperations;
        public long ExclusiveOperations;
        public long ReaderSink;
        public long WriterSink;

        public ExclusiveProgressGroup(
            LockStrategyDefinition strategyDefinition,
            WorkDefinition workDefinition,
            int concurrentWorkers)
        {
            Strategy = strategyDefinition.Create();
            Work = workDefinition.Create();
            Work.Init();
            RemainingConcurrentWorkers = concurrentWorkers;
        }

        public void Dispose()
        {
            Work.Dispose();
            Strategy.Dispose();
        }
    }

    public static int Run(BenchmarkOptions options, BenchmarkSession session)
    {
        WorkDefinition workDefinition = WorkFactory.Create(options);
        BenchmarkOptions warmupOptions = JitWarmup.ExclusiveProgress(options);
        WorkDefinition warmupWorkDefinition = WorkFactory.Create(warmupOptions);
        IReadOnlyList<LockStrategyDefinition> strategies = LockStrategyCatalog.ExclusiveProgress;
        long totalConcurrentThreads = options.TotalWorkerThreads;
        long totalConcurrentOperations = checked(totalConcurrentThreads * options.OperationsPerThread);
        long totalExclusiveWriters = options.LockInstances;

        Console.WriteLine("Exclusive progress during a fixed Concurrent flood");
        Console.WriteLine(
            $"lock-instances={options.LockInstances:n0}, concurrent-threads/lock={options.Threads:n0}, " +
            $"total-concurrent-threads={totalConcurrentThreads:n0}, operations/concurrent-thread={options.OperationsPerThread:n0}, " +
            $"total-concurrent-operations={totalConcurrentOperations:n0}, exclusive-writers={totalExclusiveWriters:n0}");
        Console.WriteLine($"concurrent-work={options.ConcurrentWorkSteps:n0}, exclusive-work={options.ExclusiveWorkSteps:n0}");
        Console.WriteLine($"workload={workDefinition.Name}");
        Console.WriteLine(
            $"topology=1 Exclusive writer/lock; writer-arm={WriterArmInterval.TotalMilliseconds:0}ms once before measurement; " +
            "reentry-gate=at least 1 new same-lock Concurrent completion after each Exclusive completion; " +
            "measurement=Exclusive completions before that lock's Concurrent flood finishes");
        Console.WriteLine();

        foreach (LockStrategyDefinition strategy in strategies)
        {
            BenchmarkRunner.ForceCollection();
            _ = RunCase(strategy, warmupWorkDefinition, warmupOptions);
        }

        Console.WriteLine(
            $"  {"lock type",-26}  {"elapsed",9}  {"cpu%",8}  {"Concurrent ops/s",17}  " +
            $"{"Exclusive entries",17} {"entries/1M C",14} {"Exclusive ops/s",16} {"min-lock entries",17} {"max-lock entries",17}");

        foreach (LockStrategyDefinition strategy in strategies)
        {
            BenchmarkRunner.ForceCollection();
            ExclusiveProgressResult result = RunCase(strategy, workDefinition, options);
            if (result.ConcurrentOperations != totalConcurrentOperations)
            {
                throw new InvalidOperationException(
                    $"Concurrent operation mismatch in exclusive-progress: strategy={result.LockName}, " +
                    $"expected={totalConcurrentOperations}, actual={result.ConcurrentOperations}.");
            }

            // One initial Exclusive completion is possible per lock. Every later completion requires at least one
            // new same-lock Concurrent completion, so this bound also validates the reentry-gate implementation.
            long maximumExclusiveOperations = checked(totalConcurrentOperations + options.LockInstances);
            if (result.ExclusiveOperations > maximumExclusiveOperations)
            {
                throw new InvalidOperationException(
                    $"Exclusive progress exceeded its gated maximum: strategy={result.LockName}, " +
                    $"maximum={maximumExclusiveOperations}, actual={result.ExclusiveOperations}.");
            }

            PrintResult(result);
            session.Write("exclusive-progress", new
            {
                options.LockInstances,
                concurrentThreadsPerLock = options.Threads,
                totalConcurrentThreads,
                concurrentOperationsPerThread = options.OperationsPerThread,
                totalConcurrentOperations,
                exclusiveWriterThreads = totalExclusiveWriters,
                writerArmMilliseconds = WriterArmInterval.TotalMilliseconds,
                exclusiveReentryGate = "one-new-same-lock-concurrent-completion",
                options.ConcurrentWorkSteps,
                options.ExclusiveWorkSteps,
                workload = workDefinition.Name,
                result.LockName,
                elapsedSeconds = result.Elapsed.TotalSeconds,
                result.CpuPercent,
                result.ConcurrentOperations,
                concurrentOperationsPerSecond = result.ConcurrentOperationsPerSecond,
                result.ExclusiveOperations,
                exclusiveOperationsPerSecond = result.ExclusiveOperationsPerSecond,
                exclusivePerMillionConcurrent = result.ExclusivePerMillionConcurrent,
                perLockExclusiveOperations = result.PerLockExclusiveOperations,
                result.MinLockExclusiveOperations,
                result.MaxLockExclusiveOperations,
                stateHash = unchecked((ulong)result.StateHash).ToString("X16")
            });
        }

        return 0;
    }

    private static ExclusiveProgressResult RunCase(
        LockStrategyDefinition strategyDefinition,
        WorkDefinition workDefinition,
        BenchmarkOptions options)
    {
        ExclusiveProgressGroup[] groups = new ExclusiveProgressGroup[options.LockInstances];
        int concurrentThreadCount = checked((int)options.TotalWorkerThreads);
        int writerThreadCount = options.LockInstances;
        int totalThreadCount = checked(concurrentThreadCount + writerThreadCount);
        ExceptionDispatchInfo? firstFailure = null;
        Thread[] concurrentWorkers = new Thread[concurrentThreadCount];
        Thread[] writers = new Thread[writerThreadCount];
        using CountdownEvent ready = new(totalThreadCount);
        using CountdownEvent initialConcurrentHolders = new(concurrentThreadCount);
        using CountdownEvent writerAttempts = new(writerThreadCount);
        using ManualResetEventSlim setupGate = new(false);
        using ManualResetEventSlim writerArmGate = new(false);
        using ManualResetEventSlim floodGate = new(false);

        try
        {
            for (int lockIndex = 0; lockIndex < groups.Length; lockIndex++)
            {
                groups[lockIndex] = new ExclusiveProgressGroup(
                    strategyDefinition,
                    workDefinition,
                    options.Threads);
            }

            for (int workerIndex = 0; workerIndex < concurrentWorkers.Length; workerIndex++)
            {
                int capturedWorkerIndex = workerIndex;
                int lockIndex = workerIndex / options.Threads;
                int localWorkerIndex = workerIndex % options.Threads;
                ExclusiveProgressGroup group = groups[lockIndex];
                concurrentWorkers[workerIndex] = new Thread(() =>
                {
                    long localOperations = 0;
                    long localSink = 0;
                    bool activeWorker = true;
                    try
                    {
                        ready.Signal();
                        setupGate.Wait();

                        group.Strategy.AcquireConcurrent();
                        try
                        {
                            initialConcurrentHolders.Signal();
                            floodGate.Wait();
                            localSink = unchecked(localSink + group.Work.TickRead());
                        }
                        finally
                        {
                            group.Strategy.ReleaseConcurrent();
                        }
                        localOperations++;
                        Interlocked.Increment(ref group.ConcurrentOperations);

                        for (int operation = 1; operation < options.OperationsPerThread; operation++)
                        {
                            group.Strategy.AcquireConcurrent();
                            try
                            {
                                localSink = unchecked(localSink + group.Work.TickRead());
                            }
                            finally
                            {
                                group.Strategy.ReleaseConcurrent();
                            }
                            localOperations++;
                            Interlocked.Increment(ref group.ConcurrentOperations);
                        }

                        Interlocked.Decrement(ref group.RemainingConcurrentWorkers);
                        activeWorker = false;
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref firstFailure, ExceptionDispatchInfo.Capture(exception), null);
                    }
                    finally
                    {
                        if (activeWorker)
                        {
                            Interlocked.Decrement(ref group.RemainingConcurrentWorkers);
                        }
                        Interlocked.Add(ref group.ReaderSink, localSink ^ localOperations ^ localWorkerIndex ^ capturedWorkerIndex);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"ExclusiveProgress-Concurrent-L{lockIndex}-W{localWorkerIndex}"
                };
                concurrentWorkers[workerIndex].Start();
            }

            for (int lockIndex = 0; lockIndex < writers.Length; lockIndex++)
            {
                int capturedLockIndex = lockIndex;
                ExclusiveProgressGroup group = groups[lockIndex];
                writers[lockIndex] = new Thread(() =>
                {
                    long localOperations = 0;
                    long localSink = 0;
                    bool exclusiveHeld = false;
                    try
                    {
                        ready.Signal();
                        setupGate.Wait();
                        writerArmGate.Wait();

                        writerAttempts.Signal();
                        group.Strategy.AcquireExclusive();
                        exclusiveHeld = true;
                        while (true)
                        {
                            if (Volatile.Read(ref group.RemainingConcurrentWorkers) == 0)
                            {
                                break;
                            }

                            localSink = unchecked(localSink + group.Work.TickWrite());
                            localOperations++;

                            // Capture the progress baseline while Exclusive is still held. No Concurrent operation can
                            // complete between this snapshot and ReleaseExclusive(), so the next observed increment
                            // necessarily represents Concurrent progress made after this Exclusive operation.
                            //
                            // Porting requirement: do not move this snapshot after ReleaseExclusive(). Doing so can miss
                            // the first valid Concurrent completion and unnecessarily require an additional completion.
                            long concurrentCompletionBaseline = Volatile.Read(ref group.ConcurrentOperations);
                            group.Strategy.ReleaseExclusive();
                            exclusiveHeld = false;

                            // A writer may not immediately reacquire forever. It must allow at least one new Concurrent
                            // operation on this same lock to complete before issuing the next Exclusive request. This
                            // removes the positive feedback in which more Exclusive entries prolong the flood and create
                            // still more time for Exclusive entries. The all-workers-finished condition guarantees exit.
                            // Ports may use their normal yield/backoff primitive here; they must not inspect lock internals.
                            SpinWait progressWait = new();
                            while (Volatile.Read(ref group.ConcurrentOperations) == concurrentCompletionBaseline &&
                                   Volatile.Read(ref group.RemainingConcurrentWorkers) != 0)
                            {
                                progressWait.SpinOnce();
                            }

                            if (Volatile.Read(ref group.RemainingConcurrentWorkers) == 0)
                            {
                                break;
                            }

                            group.Strategy.AcquireExclusive();
                            exclusiveHeld = true;
                        }
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(ref firstFailure, ExceptionDispatchInfo.Capture(exception), null);
                    }
                    finally
                    {
                        if (exclusiveHeld)
                        {
                            group.Strategy.ReleaseExclusive();
                        }
                        Volatile.Write(ref group.ExclusiveOperations, localOperations);
                        Interlocked.Add(ref group.WriterSink, localSink ^ localOperations ^ capturedLockIndex);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"ExclusiveProgress-Exclusive-L{lockIndex}"
                };
                writers[lockIndex].Start();
            }

            if (!ready.Wait(CoordinationTimeout))
            {
                firstFailure?.Throw();
                throw new TimeoutException("Exclusive-progress workers did not become ready.");
            }

            setupGate.Set();
            if (!initialConcurrentHolders.Wait(CoordinationTimeout))
            {
                firstFailure?.Throw();
                throw new TimeoutException("Concurrent workers did not establish the initial holder topology.");
            }

            writerArmGate.Set();
            if (!writerAttempts.Wait(CoordinationTimeout))
            {
                firstFailure?.Throw();
                throw new TimeoutException("Exclusive writers did not start their initial acquisition attempts.");
            }
            Thread.Sleep(WriterArmInterval);

            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            TimeSpan cpuBefore = process.TotalProcessorTime;
            Stopwatch elapsed = Stopwatch.StartNew();
            floodGate.Set();

            foreach (Thread worker in concurrentWorkers) worker.Join();
            foreach (Thread writer in writers) writer.Join();

            elapsed.Stop();
            process.Refresh();
            TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
            firstFailure?.Throw();

            long concurrentOperations = groups.Sum(group => Volatile.Read(ref group.ConcurrentOperations));
            long[] perLockExclusiveOperations = groups
                .Select(group => Volatile.Read(ref group.ExclusiveOperations))
                .ToArray();
            long exclusiveOperations = perLockExclusiveOperations.Sum();
            long stateHash = BenchmarkCaseRunner.CombineStateHashes(groups.Select(group => group.Work).ToArray());
            long sink = groups.Aggregate(0L, (value, group) => unchecked(value ^ group.ReaderSink ^ group.WriterSink));
            GC.KeepAlive(sink);

            return new ExclusiveProgressResult(
                strategyDefinition.Name,
                elapsed.Elapsed,
                MeasurementMath.CpuPercent(cpu, elapsed.Elapsed),
                concurrentOperations,
                exclusiveOperations,
                perLockExclusiveOperations,
                stateHash);
        }
        finally
        {
            setupGate.Set();
            writerArmGate.Set();
            floodGate.Set();
            for (int i = groups.Length - 1; i >= 0; i--) groups[i]?.Dispose();
        }
    }

    private static void PrintResult(ExclusiveProgressResult result)
    {
        Console.WriteLine(
            $"  {result.LockName,-26}  {result.Elapsed.TotalSeconds,8:0.000}s  {result.CpuPercent,7:0.0}%  " +
            $"{result.ConcurrentOperationsPerSecond,17:0}  {result.ExclusiveOperations,17:n0} " +
            $"{result.ExclusivePerMillionConcurrent,14:0.###} {result.ExclusiveOperationsPerSecond,16:0} " +
            $"{result.MinLockExclusiveOperations,17:n0} {result.MaxLockExclusiveOperations,17:n0}");
    }
}
