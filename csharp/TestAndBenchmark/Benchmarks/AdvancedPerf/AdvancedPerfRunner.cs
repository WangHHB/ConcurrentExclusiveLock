using System.Diagnostics;
using System.Globalization;
using IntomicLib;
using TestAndBenchmark.Common.Workloads;

namespace TestAndBenchmark.Benchmarks.AdvancedPerf;

internal static class AdvancedPerfRunner
{
    public static void Run(AdvancedPerfOptions options)
    {
        Console.WriteLine("Advanced semantic performance");
        Console.WriteLine($"threads={options.Threads}, operations/thread={options.OperationsPerThread:N0}, workload={options.Workload}, concurrent-steps={options.ConcurrentWork}, exclusive-steps={options.ExclusiveWork}");
        Console.WriteLine($"targets={string.Join(", ", options.Targets)}");
        Console.WriteLine("Every printed case uses exactly the requested operations/thread.");
        Console.WriteLine("Every semantic loop executes exactly one shared BenchmarkWork payload.");
        Console.WriteLine("RWLS and Monitor use nearest comparable paths for CEL-only in-place semantics.");
        Console.WriteLine();
        Console.WriteLine("Mixed cases split workers by path; even thread counts are exactly half and half.");
        Console.WriteLine();

        PrintHeader();

        ulong sink = 0;
        foreach (AdvancedPerfTarget target in options.Targets)
        {
            sink ^= RunCase(options, target, "concurrent enter/release", ExecuteConcurrent);
            sink ^= RunCase(options, target, "exclusive enter -> concurrent", ExecuteExclusiveToConcurrent);
            sink ^= RunMixedCase(options, target, "concurrent/exclusive->concurrent 50/50", ExecuteConcurrent, ExecuteExclusiveToConcurrent);
            sink ^= RunCase(options, target, "exclusive enter/release", ExecuteExclusive);
            sink ^= RunCase(options, target, "concurrent enter -> exclusive", ExecuteConcurrentToExclusive);
            sink ^= RunMixedCase(options, target, "exclusive/concurrent->exclusive 50/50", ExecuteExclusive, ExecuteConcurrentToExclusive);
        }

        Console.WriteLine();
        Console.WriteLine($"sink={sink}");
    }

    private static ulong RunCase(
        AdvancedPerfOptions options,
        AdvancedPerfTarget target,
        string name,
        Func<TargetContext, AdvancedPerfTarget, int, ulong, int, ulong> operation)
    {
        return RunWorkers(options, target, name, _ => operation);
    }

    private static ulong RunMixedCase(
        AdvancedPerfOptions options,
        AdvancedPerfTarget target,
        string name,
        Func<TargetContext, AdvancedPerfTarget, int, ulong, int, ulong> first,
        Func<TargetContext, AdvancedPerfTarget, int, ulong, int, ulong> second)
    {
        int split = options.Threads / 2;
        return RunWorkers(options, target, name, workerId => workerId < split ? first : second);
    }

    private static ulong RunWorkers(
        AdvancedPerfOptions options,
        AdvancedPerfTarget target,
        string name,
        Func<int, Func<TargetContext, AdvancedPerfTarget, int, ulong, int, ulong>> operationSelector)
    {
        using var context = new TargetContext(options.Workload, options.ConcurrentWork, options.ExclusiveWork, options.MemoryMb, options.DictionarySize);
        using var startGate = new ManualResetEventSlim(false);
        var localSinks = new ulong[options.Threads];
        int nextContextId = 0;

        Thread[] workers = Enumerable.Range(0, options.Threads)
            .Select(workerId => new Thread(() =>
            {
                Func<TargetContext, AdvancedPerfTarget, int, ulong, int, ulong> operation = operationSelector(workerId);
                ulong state = unchecked((ulong)(workerId + 1) * 0x9E3779B185EBCA87UL);

                startGate.Wait();

                for (int i = 0; i < options.OperationsPerThread; i++)
                {
                    int contextId = Interlocked.Increment(ref nextContextId);
                    state = operation(context, target, contextId, state, options.Work);
                }

                localSinks[workerId] = state;
            })
            {
                IsBackground = true,
                Name = $"advanced-perf-{target}-{workerId}",
            })
            .ToArray();

        foreach (Thread worker in workers)
        {
            worker.Start();
        }

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();

        startGate.Set();

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        stopwatch.Stop();
        process.Refresh();

        long loops = (long)options.Threads * options.OperationsPerThread;
        double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
        double cpuPercent = (process.TotalProcessorTime - cpuStart).TotalSeconds / elapsedSeconds / Environment.ProcessorCount * 100.0;
        double opsPerSecond = loops / elapsedSeconds;
        double nsPerOp = elapsedSeconds * 1_000_000_000.0 / loops;
        double opsPerThreadSecond = opsPerSecond / options.Threads;

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {target,-7} {name,-40} {options.OperationsPerThread,12:N0} {elapsedSeconds,9:N3}s {cpuPercent,8:N1}% {opsPerSecond,14:N0} {nsPerOp,10:N0} {opsPerThreadSecond,14:N0} {GetFinalState(context, target),18}"));

        ulong sink = 0;
        foreach (ulong localSink in localSinks)
        {
            sink ^= localSink;
        }

        return sink;
    }

    private static ulong ExecuteConcurrent(TargetContext context, AdvancedPerfTarget target, int contextId, ulong state, int work)
    {
        switch (target)
        {
            case AdvancedPerfTarget.Scope:
                using (var scope = new ConcurrentExclusiveLockScope(context.Locker))
                {
                    scope.AcquireConcurrent();
                    return MixSink(state, context.Work.TickConcurrent());
                }

            case AdvancedPerfTarget.Rwls:
                context.Rwls.EnterReadLock();
                try
                {
                    return MixSink(state, context.Work.TickConcurrent());
                }
                finally
                {
                    context.Rwls.ExitReadLock();
                }

            case AdvancedPerfTarget.Monitor:
                lock (context.MonitorGate)
                {
                    return MixSink(state, context.Work.TickConcurrent());
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private static ulong ExecuteExclusive(TargetContext context, AdvancedPerfTarget target, int contextId, ulong state, int work)
    {
        switch (target)
        {
            case AdvancedPerfTarget.Scope:
                using (var scope = new ConcurrentExclusiveLockScope(context.Locker))
                {
                    scope.AcquireExclusive();
                    return MixSink(state, context.Work.TickExclusive());
                }

            case AdvancedPerfTarget.Rwls:
                context.Rwls.EnterWriteLock();
                try
                {
                    return MixSink(state, context.Work.TickExclusive());
                }
                finally
                {
                    context.Rwls.ExitWriteLock();
                }

            case AdvancedPerfTarget.Monitor:
                lock (context.MonitorGate)
                {
                    return MixSink(state, context.Work.TickExclusive());
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private static ulong ExecuteExclusiveToConcurrent(TargetContext context, AdvancedPerfTarget target, int contextId, ulong state, int work)
    {
        switch (target)
        {
            case AdvancedPerfTarget.Scope:
                using (var scope = new ConcurrentExclusiveLockScope(context.Locker))
                {
                    scope.AcquireExclusive();
                    scope.ExclusiveToConcurrent();
                    return MixSink(state, context.Work.TickConcurrent());
                }

            case AdvancedPerfTarget.Rwls:
                context.Rwls.EnterWriteLock();
                context.Rwls.ExitWriteLock();
                context.Rwls.EnterReadLock();
                try
                {
                    return MixSink(state, context.Work.TickConcurrent());
                }
                finally
                {
                    context.Rwls.ExitReadLock();
                }

            case AdvancedPerfTarget.Monitor:
                lock (context.MonitorGate)
                {
                    return MixSink(state, context.Work.TickConcurrent());
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private static ulong ExecuteConcurrentToExclusive(TargetContext context, AdvancedPerfTarget target, int contextId, ulong state, int work)
    {
        switch (target)
        {
            case AdvancedPerfTarget.Scope:
                lock (context.UpgradeGate)
                {
                    using var scope = new ConcurrentExclusiveLockScope(context.Locker);
                    scope.AcquireConcurrent();
                    if (!scope.TryConcurrentToExclusiveWithSwitchContextID(contextId))
                    {
                        throw new InvalidOperationException("Concurrent to Exclusive upgrade unexpectedly failed.");
                    }

                    return MixSink(state, context.Work.TickExclusive());
                }

            case AdvancedPerfTarget.Rwls:
                context.Rwls.EnterUpgradeableReadLock();
                try
                {
                    context.Rwls.EnterWriteLock();
                    try
                    {
                        return MixSink(state, context.Work.TickExclusive());
                    }
                    finally
                    {
                        context.Rwls.ExitWriteLock();
                    }
                }
                finally
                {
                    context.Rwls.ExitUpgradeableReadLock();
                }

            case AdvancedPerfTarget.Monitor:
                lock (context.MonitorGate)
                {
                    return MixSink(state, context.Work.TickExclusive());
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private static void PrintHeader()
    {
        Console.WriteLine("  target  case                                     loops/thread   elapsed     cpu%          ops/s      ns/op   ops/thread/s              state");
    }

    private static string GetFinalState(TargetContext context, AdvancedPerfTarget target)
    {
        return target == AdvancedPerfTarget.Scope
            ? context.Locker.ObservedState.ToString()
            : "Idle";
    }

    private static ulong MixSink(ulong state, long workValue)
    {
        state ^= (ulong)workValue;
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        state += 0x9E3779B97F4A7C15UL;
        return state;
    }

    private sealed class TargetContext : IDisposable
    {
        public TargetContext(WorkloadMode workload, int concurrentWork, int exclusiveWork, int memoryMb, int dictionarySize)
        {
            Work = BenchmarkWorkFactory.Create(workload, concurrentWork, exclusiveWork, memoryMb, dictionarySize);
        }

        public ConcurrentExclusiveLock Locker { get; } = ConcurrentExclusiveLock.Create();
        public ReaderWriterLockSlim Rwls { get; } = new();
        public object MonitorGate { get; } = new();
        public object UpgradeGate { get; } = new();
        public IBenchmarkWork Work { get; }

        public void Dispose()
        {
            Rwls.Dispose();
            Work.Dispose();
        }
    }
}
