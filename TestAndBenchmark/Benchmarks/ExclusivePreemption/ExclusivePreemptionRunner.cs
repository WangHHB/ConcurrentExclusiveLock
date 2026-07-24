using System.Diagnostics;
using System.Globalization;
using IntomicLib;
using TestAndBenchmark.Common.Workloads;

namespace TestAndBenchmark.Benchmarks.ExclusivePreemption;

internal static class ExclusivePreemptionRunner
{
    public static void Run(ExclusivePreemptionOptions options)
    {
        Console.WriteLine("Exclusive preemption latency test");
        Console.WriteLine($"Profile             : {options.Profile}");
        Console.WriteLine($"Threads             : {options.ConcurrentThreads}");
        Console.WriteLine($"Attempts            : {options.Attempts}");
        Console.WriteLine($"ConcurrentSpin      : {options.ConcurrentSpin}");
        Console.WriteLine($"ExclusiveHoldMs     : {options.ExclusiveHoldMilliseconds}");
        Console.WriteLine($"ExclusivePauseMs    : {options.ExclusivePauseMilliseconds}");
        Console.WriteLine($"ExclusiveTimeoutMs  : {options.ExclusiveTimeoutMilliseconds}");
        Console.WriteLine($"Targets             : {string.Join(", ", options.Targets)}");
        Console.WriteLine($"Workload            : {options.Workload}");
        Console.WriteLine($"ConcurrentWork      : {options.ConcurrentWork}");
        Console.WriteLine($"ExclusiveWork       : {options.ExclusiveWork}");
        Console.WriteLine($"MemoryMb            : {options.MemoryMb}");
        Console.WriteLine($"DictionarySize      : {options.DictionarySize}");
        Console.WriteLine();

        var allAttempts = new List<ExclusivePreemptionAttempt>(options.Attempts * options.Targets.Length);
        foreach (ExclusivePreemptionTarget target in options.Targets)
        {
            TargetRunResult result = RunOneTarget(options, target);
            allAttempts.AddRange(result.Attempts);
            PrintSummary(target, result.Attempts, result.ConcurrentOperations);
        }

        SaveCsv(options, allAttempts);
    }

    private static TargetRunResult RunOneTarget(ExclusivePreemptionOptions options, ExclusivePreemptionTarget target)
    {
        using var context = new TargetContext(options.Workload, options.ConcurrentWork, options.ExclusiveWork, options.MemoryMb, options.DictionarySize);
        using var startGate = new ManualResetEventSlim(false);
        using var stopGate = new ManualResetEventSlim(false);

        var state = new SharedState();
        Thread[] concurrentWorkers = Enumerable.Range(0, options.ConcurrentThreads)
            .Select(index => new Thread(() => RunConcurrentWorker(context, target, options.Workload, options.ConcurrentSpin, startGate, stopGate, state))
            {
                IsBackground = true,
                Name = $"exclusive-preemption-{target}-concurrent-{index}",
            })
            .ToArray();

        foreach (Thread worker in concurrentWorkers)
        {
            worker.Start();
        }

        startGate.Set();
        Thread.Sleep(250);

        var attempts = new List<ExclusivePreemptionAttempt>(options.Attempts);
        for (int i = 1; i <= options.Attempts; i++)
        {
            Thread.Sleep(options.ExclusivePauseMilliseconds);
            attempts.Add(RunAttempt(i, context, target, options, state));
        }

        stopGate.Set();
        foreach (Thread worker in concurrentWorkers)
        {
            worker.Join();
        }

        return new TargetRunResult(attempts, state.ConcurrentOperations);
    }

    private static void RunConcurrentWorker(
        TargetContext context,
        ExclusivePreemptionTarget target,
        WorkloadMode workload,
        int concurrentSpin,
        ManualResetEventSlim startGate,
        ManualResetEventSlim stopGate,
        SharedState state)
    {
        startGate.Wait();

        while (!stopGate.IsSet)
        {
            ExecuteConcurrent(context, target, () =>
            {
                if (Volatile.Read(ref state.ExclusiveWaiting) == 1)
                {
                    Interlocked.Increment(ref state.NewConcurrentAfterExclusiveArrived);
                }

                context.Work.TickConcurrent();

                if (concurrentSpin > 0)
                {
                    Thread.SpinWait(concurrentSpin);
                }
            });

            Interlocked.Increment(ref state.ConcurrentOperations);
        }
    }

    private static ExclusivePreemptionAttempt RunAttempt(
        int attempt,
        TargetContext context,
        ExclusivePreemptionTarget target,
        ExclusivePreemptionOptions options,
        SharedState state)
    {
        long concurrentAtArrival = Volatile.Read(ref state.ConcurrentOperations);
        long newConcurrentBefore = Volatile.Read(ref state.NewConcurrentAfterExclusiveArrived);

        Volatile.Write(ref state.ExclusiveWaiting, 1);
        long start = Stopwatch.GetTimestamp();

        IDisposable? lease = TryEnterExclusive(context, target, options.ExclusiveTimeoutMilliseconds);
        long elapsed = Stopwatch.GetTimestamp() - start;
        Volatile.Write(ref state.ExclusiveWaiting, 0);

        long concurrentAtEntry = Volatile.Read(ref state.ConcurrentOperations);
        long newConcurrentAfter = Volatile.Read(ref state.NewConcurrentAfterExclusiveArrived);
        bool entered = lease is not null;

        if (lease is not null)
        {
            try
            {
                context.Work.TickExclusive();

                if (options.ExclusiveHoldMilliseconds > 0)
                {
                    Thread.Sleep(options.ExclusiveHoldMilliseconds);
                }
            }
            finally
            {
                lease.Dispose();
            }
        }

        double waitNs = elapsed * 1_000_000_000.0 / Stopwatch.Frequency;
        return new ExclusivePreemptionAttempt(
            target,
            attempt,
            entered,
            waitNs,
            newConcurrentAfter - newConcurrentBefore,
            concurrentAtArrival,
            concurrentAtEntry);
    }

    private static void ExecuteConcurrent(TargetContext context, ExclusivePreemptionTarget target, Action body)
    {
        switch (target)
        {
            case ExclusivePreemptionTarget.Scope:
                using (var scope = new ConcurrentExclusiveLockScope(context.Locker))
                {
                    scope.AcquireConcurrent();
                    body();
                }

                break;

            case ExclusivePreemptionTarget.Rwls:
                context.Rwls.EnterReadLock();
                try
                {
                    body();
                }
                finally
                {
                    context.Rwls.ExitReadLock();
                }

                break;

            case ExclusivePreemptionTarget.Monitor:
                lock (context.MonitorGate)
                {
                    body();
                }

                break;
        }
    }

    private static IDisposable? TryEnterExclusive(TargetContext context, ExclusivePreemptionTarget target, int timeoutMilliseconds)
    {
        switch (target)
        {
            case ExclusivePreemptionTarget.Scope:
                var scope = new ConcurrentExclusiveLockScope(context.Locker);
                if (!scope.TryAcquireExclusive(timeoutMilliseconds))
                {
                    scope.Dispose();
                    return null;
                }

                return scope;

            case ExclusivePreemptionTarget.Rwls:
                if (!context.Rwls.TryEnterWriteLock(timeoutMilliseconds))
                {
                    return null;
                }

                return new ReleaseAction(context.Rwls.ExitWriteLock);

            case ExclusivePreemptionTarget.Monitor:
                bool entered = false;
                Monitor.TryEnter(context.MonitorGate, timeoutMilliseconds, ref entered);
                if (!entered)
                {
                    return null;
                }

                return new ReleaseAction(() => Monitor.Exit(context.MonitorGate));

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private static void PrintSummary(ExclusivePreemptionTarget target, IReadOnlyCollection<ExclusivePreemptionAttempt> attempts, long totalConcurrentOperations)
    {
        ExclusivePreemptionAttempt[] entered = attempts.Where(static item => item.Entered).OrderBy(static item => item.ExclusiveWaitNs).ToArray();
        int failed = attempts.Count - entered.Length;

        Console.WriteLine($"Target              : {target}");
        Console.WriteLine($"Exclusive attempts  : {attempts.Count}");
        Console.WriteLine($"Exclusive entered   : {entered.Length}");
        Console.WriteLine($"Exclusive failed    : {failed}");
        Console.WriteLine($"Concurrent ops      : {totalConcurrentOperations}");

        if (entered.Length > 0)
        {
            Console.WriteLine($"Exclusive wait p50  : {Percentile(entered, 0.50):N1} ns");
            Console.WriteLine($"Exclusive wait p95  : {Percentile(entered, 0.95):N1} ns");
            Console.WriteLine($"Exclusive wait p99  : {Percentile(entered, 0.99):N1} ns");
            Console.WriteLine($"Exclusive wait max  : {entered[^1].ExclusiveWaitNs:N1} ns");
            Console.WriteLine($"New Concurrent max  : {entered.Max(static item => item.NewConcurrentAfterExclusiveArrived)}");
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "Result: PASS" : "Result: FAIL");
        Console.WriteLine();
    }

    private static double Percentile(ExclusivePreemptionAttempt[] sortedAttempts, double percentile)
    {
        int index = (int)Math.Ceiling(percentile * sortedAttempts.Length) - 1;
        return sortedAttempts[Math.Clamp(index, 0, sortedAttempts.Length - 1)].ExclusiveWaitNs;
    }

    private static void SaveCsv(ExclusivePreemptionOptions options, IReadOnlyCollection<ExclusivePreemptionAttempt> attempts)
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "BenchmarkDotNet.Artifacts", "ExclusivePreemption");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, $"exclusive-preemption-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.csv");
        using var output = new StreamWriter(path);

        output.WriteLine("profile,target,workload,concurrentThreads,concurrentSpin,concurrentSteps,exclusiveSteps,memoryMb,dictionarySize,attempt,entered,exclusiveWaitNs,newConcurrentAfterExclusiveArrived,concurrentOperationsAtArrival,concurrentOperationsAtEntry");
        foreach (ExclusivePreemptionAttempt attempt in attempts)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{options.Profile},{attempt.Target},{options.Workload},{options.ConcurrentThreads},{options.ConcurrentSpin},{options.ConcurrentWork},{options.ExclusiveWork},{options.MemoryMb},{options.DictionarySize},{attempt.Attempt},{attempt.Entered},{attempt.ExclusiveWaitNs:R},{attempt.NewConcurrentAfterExclusiveArrived},{attempt.ConcurrentOperationsAtArrival},{attempt.ConcurrentOperationsAtEntry}"));
        }

        Console.WriteLine($"CSV: {path}");
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
        public IBenchmarkWork Work { get; }

        public void Dispose()
        {
            Rwls.Dispose();
            Work.Dispose();
        }
    }

    private sealed record TargetRunResult(List<ExclusivePreemptionAttempt> Attempts, long ConcurrentOperations);

    private sealed class ReleaseAction(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    private sealed class SharedState
    {
        public int ExclusiveWaiting;
        public long ConcurrentOperations;
        public long NewConcurrentAfterExclusiveArrived;
    }
}
