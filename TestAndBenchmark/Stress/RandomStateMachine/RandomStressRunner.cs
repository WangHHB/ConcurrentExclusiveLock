using System.Diagnostics;
using IntomicLib;

namespace TestAndBenchmark.Stress.RandomStateMachine;

internal static class RandomStressRunner
{
    private const int MaxConcurrent = int.MaxValue;

    public static int Run(RandomStressOptions options)
    {
        Console.WriteLine("Random Pipeline-focused stress test");
        Console.WriteLine($"Profile             : {options.Profile}");
        Console.WriteLine($"Seed                : {options.Seed}");
        Console.WriteLine($"Workers             : {options.Workers}");
        Console.WriteLine($"LockInstances       : {options.EntityCount}");
        Console.WriteLine($"Duration            : {(options.Duration is null ? "until stopped" : options.Duration.ToString())}");
        Console.WriteLine($"ProgressSeconds     : {options.ProgressSeconds}");
        Console.WriteLine($"PipelinePercent     : {options.PipelinePercent}");
        Console.WriteLine($"Spin                : {options.Spin}");
        Console.WriteLine($"Segments            : {options.SegmentsMin}..{options.SegmentsMax}");
        Console.WriteLine($"ExceptionPercent    : {options.ExceptionPercent}");
        Console.WriteLine($"YieldPercent        : {options.YieldPercent}");
        Console.WriteLine();

        StressEntity[] entities = Enumerable.Range(0, options.EntityCount).Select(_ => new StressEntity()).ToArray();
        using var startGate = new ManualResetEventSlim(false);
        using var stopGate = new ManualResetEventSlim(false);
        using var cancelGate = new ManualResetEventSlim(false);

        var shared = new SharedState();
        Thread[] workers = Enumerable.Range(0, options.Workers)
            .Select(workerId => new Thread(() => RunWorker(workerId, options, entities, startGate, stopGate, shared))
            {
                IsBackground = true,
                Name = $"random-stress-{workerId}",
            })
            .ToArray();

        foreach (Thread worker in workers)
        {
            worker.Start();
        }

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancelGate.Set();
        };

        Console.CancelKeyPress += cancelHandler;
        var stopwatch = Stopwatch.StartNew();
        startGate.Set();

        TimeSpan nextProgress = options.ProgressSeconds <= 0
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(options.ProgressSeconds);

        while (Volatile.Read(ref shared.Failed) == 0 && !cancelGate.IsSet)
        {
            if (options.Duration is not null && stopwatch.Elapsed >= options.Duration.Value)
            {
                break;
            }

            if (stopwatch.Elapsed >= nextProgress)
            {
                PrintProgress(stopwatch.Elapsed, shared);
                nextProgress += TimeSpan.FromSeconds(options.ProgressSeconds);
            }

            Thread.Sleep(100);
        }

        Console.CancelKeyPress -= cancelHandler;
        stopGate.Set();

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        CollectEntityCounters(entities, shared);

        string? failure = shared.Failure;
        bool leaked = entities.Any(static entity => entity.ConcurrentInside != 0 || entity.ExclusiveInside != 0);

        Console.WriteLine($"Elapsed             : {stopwatch.Elapsed}");
        Console.WriteLine($"Operations          : {shared.TotalOperations}");
        Console.WriteLine($"Scope operations    : {shared.ScopeOperations}");
        Console.WriteLine($"Pipeline operations : {shared.PipelineOperations}");
        Console.WriteLine($"Pipeline segments   : {shared.PipelineSegments}");
        Console.WriteLine($"Expected exceptions : {shared.ExpectedExceptions}");
        Console.WriteLine($"Reproduce           : {options.ToReproductionCommand()}");
        Console.WriteLine();

        if (failure is not null)
        {
            Console.WriteLine("Result: FAIL");
            Console.WriteLine(failure);
            return 1;
        }

        if (leaked)
        {
            Console.WriteLine("Result: FAIL");
            Console.WriteLine("Final state leak detected in stress observer.");
            return 1;
        }

        Console.WriteLine(cancelGate.IsSet ? "Result: STOPPED" : "Result: PASS");
        return 0;
    }

    private static void PrintProgress(TimeSpan elapsed, SharedState shared)
    {
        Console.WriteLine(
            $"Progress elapsed={elapsed:c} ops={Volatile.Read(ref shared.TotalOperations):N0} pipeline={Volatile.Read(ref shared.PipelineOperations):N0} scope={Volatile.Read(ref shared.ScopeOperations):N0}");
    }

    private static void RunWorker(
        int workerId,
        RandomStressOptions options,
        StressEntity[] entities,
        ManualResetEventSlim startGate,
        ManualResetEventSlim stopGate,
        SharedState shared)
    {
        var random = new Random(unchecked(options.Seed + workerId * 1_000_003));
        startGate.Wait();

        while (!stopGate.IsSet && Volatile.Read(ref shared.Failed) == 0)
        {
            StressEntity entity = entities[random.Next(entities.Length)];

            try
            {
                if (random.Next(100) < options.PipelinePercent)
                {
                    RunPipelineOperation(entity, random, options);
                    Interlocked.Increment(ref shared.PipelineOperations);
                }
                else
                {
                    RunScopeOperation(entity, random, options);
                    Interlocked.Increment(ref shared.ScopeOperations);
                }

                Interlocked.Increment(ref shared.TotalOperations);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref shared.Failed, 1) == 0)
                {
                    shared.Failure = $"Worker {workerId} failed: {ex.GetType().Name}: {ex.Message}";
                }
            }
        }
    }

    private static void RunScopeOperation(StressEntity entity, Random random, RandomStressOptions options)
    {
        int operation = random.Next(5);

        switch (operation)
        {
            case 0:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireConcurrent(MaxConcurrent);
                    ExecuteConcurrentBody(entity, random, options);
                }

                break;

            case 1:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireExclusive();
                    ExecuteExclusiveBody(entity, random, options);
                }

                break;

            case 2:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireExclusive();
                    ExecuteExclusiveBody(entity, random, options);
                    scope.ExclusiveToConcurrent();
                    ExecuteConcurrentBody(entity, random, options);
                }

                break;

            case 3:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireConcurrent(MaxConcurrent);
                    ExecuteConcurrentBody(entity, random, options);

                    if (scope.TryConcurrentToExclusiveWithSwitchContextID(random.Next(1, int.MaxValue)))
                    {
                        ExecuteExclusiveBody(entity, random, options);
                    }
                }

                break;

            default:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireConcurrent(MaxConcurrent);
                    ExecuteConcurrentBody(entity, random, options);

                    int epoch = Interlocked.Increment(ref entity.NextEpoch);
                    if (scope.TryConcurrentToExclusiveWithRaiseEpochID(epoch))
                    {
                        ExecuteExclusiveBody(entity, random, options);
                    }
                }

                break;
        }
    }

    private static void RunPipelineOperation(StressEntity entity, Random random, RandomStressOptions options)
    {
        var pipeline = new ConcurrentExclusiveLockPipeline(entity.Locker);

        if (random.Next(100) < options.ExceptionPercent)
        {
            RunPipelineExpectedException(entity, pipeline, random, options);
            return;
        }

        int segmentCount = random.Next(options.SegmentsMin, options.SegmentsMax + 1);
        var segments = new ConcurrentExclusiveLockSegment[segmentCount];
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = CreateRandomSegment(entity, random, options);
        }

        pipeline.DoPipeline(segments);
    }

    private static ConcurrentExclusiveLockSegment CreateRandomSegment(StressEntity entity, Random random, RandomStressOptions options)
    {
        return random.Next(8) switch
        {
            0 => ConcurrentExclusiveLockSegment.None(() => Burn(random, options)),
            1 => ConcurrentExclusiveLockSegment.Concurrent(() => ExecutePipelineConcurrentSegment(entity, random, options)),
            2 => ConcurrentExclusiveLockSegment.TryConcurrent(() => ExecutePipelineConcurrentSegment(entity, random, options)),
            3 => ConcurrentExclusiveLockSegment.Exclusive(() => ExecutePipelineExclusiveSegment(entity, random, options)),
            4 => ConcurrentExclusiveLockSegment.TestExclusive(() => ExecutePipelineExclusiveSegment(entity, random, options)),
            5 => ConcurrentExclusiveLockSegment.TryExclusive(() => ExecutePipelineExclusiveSegment(entity, random, options)),
            6 => ConcurrentExclusiveLockSegment.ConvergeConcurrent(() => ExecutePipelineConcurrentSegment(entity, random, options)),
            _ => CreateRandomIdConvergeExclusiveSegment(entity, random, options),
        };
    }

    private static ConcurrentExclusiveLockSegment CreateRandomIdConvergeExclusiveSegment(StressEntity entity, Random random, RandomStressOptions options)
    {
        if (random.Next(2) == 0)
        {
            int contextId = random.Next(1, int.MaxValue);
            return ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(
                () => ExecutePipelineExclusiveSegment(entity, random, options),
                contextId,
                ConcurrentExclusiveLockSegment.IDType.ContextID);
        }

        int epochId = Interlocked.Increment(ref entity.NextEpoch);
        return ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(
            () => ExecutePipelineExclusiveSegment(entity, random, options),
            epochId,
            ConcurrentExclusiveLockSegment.IDType.EpochID);
    }

    private static void RunPipelineExpectedException(
        StressEntity entity,
        ConcurrentExclusiveLockPipeline pipeline,
        Random random,
        RandomStressOptions options)
    {
        try
        {
            pipeline.DoPipeline(
            [
                CreateRandomSegment(entity, random, options),
                random.Next(2) == 0
                    ? ConcurrentExclusiveLockSegment.Concurrent(() => ThrowAfterConcurrentSegment(entity, random, options))
                    : ConcurrentExclusiveLockSegment.Exclusive(() => ThrowAfterExclusiveSegment(entity, random, options)),
                CreateRandomSegment(entity, random, options),
            ]);
        }
        catch (ExpectedStressException)
        {
            using var scope = new ConcurrentExclusiveLockScope(entity.Locker);
            if (!scope.TryAcquireExclusive(1000))
            {
                throw new InvalidOperationException("Pipeline did not release access after an expected segment exception.");
            }

            Interlocked.Increment(ref entity.ExpectedExceptions);
            return;
        }

        throw new InvalidOperationException("Expected Pipeline segment exception was not propagated.");
    }

    private static void ExecutePipelineConcurrentSegment(StressEntity entity, Random random, RandomStressOptions options)
    {
        Interlocked.Increment(ref entity.ExecutedPipelineSegments);
        ExecuteConcurrentBody(entity, random, options);
    }

    private static void ExecutePipelineExclusiveSegment(StressEntity entity, Random random, RandomStressOptions options)
    {
        Interlocked.Increment(ref entity.ExecutedPipelineSegments);
        ExecuteExclusiveBody(entity, random, options);
    }

    private static void ExecuteConcurrentBody(StressEntity entity, Random random, RandomStressOptions options)
    {
        entity.EnterConcurrent();
        try
        {
            Burn(random, options);
        }
        finally
        {
            entity.LeaveConcurrent();
        }
    }

    private static void ExecuteExclusiveBody(StressEntity entity, Random random, RandomStressOptions options)
    {
        entity.EnterExclusive();
        try
        {
            Burn(random, options);
        }
        finally
        {
            entity.LeaveExclusive();
        }
    }

    private static void ThrowAfterConcurrentSegment(StressEntity entity, Random random, RandomStressOptions options)
    {
        ExecutePipelineConcurrentSegment(entity, random, options);
        throw new ExpectedStressException();
    }

    private static void ThrowAfterExclusiveSegment(StressEntity entity, Random random, RandomStressOptions options)
    {
        ExecutePipelineExclusiveSegment(entity, random, options);
        throw new ExpectedStressException();
    }

    private static void CollectEntityCounters(StressEntity[] entities, SharedState shared)
    {
        foreach (StressEntity entity in entities)
        {
            shared.PipelineSegments += entity.ExecutedPipelineSegments;
            shared.ExpectedExceptions += entity.ExpectedExceptions;
        }
    }

    private static void Burn(Random random, RandomStressOptions options)
    {
        if (options.Spin > 0)
        {
            Thread.SpinWait(options.Spin);
        }

        if (options.YieldPercent > 0 && random.Next(100) < options.YieldPercent)
        {
            Thread.Yield();
        }
    }

    private sealed class ExpectedStressException : Exception
    {
    }

    private sealed class StressEntity
    {
        public ConcurrentExclusiveLock Locker { get; } = ConcurrentExclusiveLock.Create();
        public int NextEpoch;
        public int ConcurrentInside;
        public int ExclusiveInside;
        public long ExecutedPipelineSegments;
        public long ExpectedExceptions;

        public void EnterConcurrent()
        {
            int concurrent = Interlocked.Increment(ref ConcurrentInside);
            int exclusive = Volatile.Read(ref ExclusiveInside);
            if (exclusive != 0)
            {
                Interlocked.Decrement(ref ConcurrentInside);
                throw new InvalidOperationException($"Concurrent overlapped Exclusive. concurrent={concurrent}, exclusive={exclusive}");
            }
        }

        public void LeaveConcurrent()
        {
            int concurrent = Interlocked.Decrement(ref ConcurrentInside);
            if (concurrent < 0)
            {
                throw new InvalidOperationException("Concurrent observer count went below zero.");
            }
        }

        public void EnterExclusive()
        {
            int exclusive = Interlocked.Increment(ref ExclusiveInside);
            int concurrent = Volatile.Read(ref ConcurrentInside);
            if (exclusive != 1 || concurrent != 0)
            {
                Interlocked.Decrement(ref ExclusiveInside);
                throw new InvalidOperationException($"Exclusive overlap detected. concurrent={concurrent}, exclusive={exclusive}");
            }
        }

        public void LeaveExclusive()
        {
            int exclusive = Interlocked.Decrement(ref ExclusiveInside);
            if (exclusive < 0)
            {
                throw new InvalidOperationException("Exclusive observer count went below zero.");
            }
        }
    }

    private sealed class SharedState
    {
        public int Failed;
        public string? Failure;
        public long TotalOperations;
        public long ScopeOperations;
        public long PipelineOperations;
        public long PipelineSegments;
        public long ExpectedExceptions;
    }
}
