using System;
using System.Diagnostics;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Repeats the complete Pipeline semantic suite for a requested duration; any failure stops
/// immediately and reports the exact replay seed.
/// </summary>
/// <remarks>
/// Each batch preserves the literal lock/worker/round topology. Only its deterministic batch seed
/// changes. Hardware information may be reported and used for CPU normalization, never for scaling.
/// </remarks>
internal static class PipelineSemanticStressRunner
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    public static int Run(
        TimeSpan duration,
        int lockInstances,
        int workersPerLock,
        int operationsPerLock,
        int? requestedSeed,
        int pipelineExceptionPermille)
    {
        PortableRandomContract.Validate();
        int baseSeed = requestedSeed ?? SeedSource.Create();
        PortableRandom seedSource = new PortableRandom(baseSeed);
        int processorCount = Math.Max(1, Environment.ProcessorCount);

        Console.WriteLine("Pipeline semantic stress");
        Console.WriteLine(
            $"duration={FormatDuration(duration)}, locks={lockInstances:n0}, workers/lock={workersPerLock:n0}, " +
            $"rounds/lock/batch={operationsPerLock:n0}, total-threads/batch={lockInstances * (long)workersPerLock:n0}, " +
            $"base-seed={baseSeed}, pipeline-exception-permille={pipelineExceptionPermille}.");
        Console.WriteLine();

        bool cancellationRequested = false;
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Volatile.Write(ref cancellationRequested, true);
        };
        Console.CancelKeyPress += cancelHandler;

        Stopwatch elapsed = Stopwatch.StartNew();
        Process process = Process.GetCurrentProcess();
        TimeSpan lastCpu = process.TotalProcessorTime;
        TimeSpan lastHeartbeat = TimeSpan.Zero;
        long completedBatches = 0;
        long totalInjectedExceptions = 0;

        try
        {
            do
            {
                int batchSeed = seedSource.NextSeed();
                int batchLockInstances = lockInstances;
                int batchWorkersPerLock = workersPerLock;
                int batchOperationsPerLock = operationsPerLock;

                ConcurrentExclusiveLockPipelineCorrectnessCase testCase = new ConcurrentExclusiveLockPipelineCorrectnessCase(
                    batchLockInstances,
                    batchWorkersPerLock,
                    batchOperationsPerLock,
                    batchSeed,
                    printSummary: false,
                    randomPipelineNoProgressTimeout: TimeSpan.FromMinutes(10),
                    randomExceptionPermille: pipelineExceptionPermille);

                try
                {
                    testCase.Run();
                }
                catch (Exception exception)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"[FAIL] pipeline stress batch={completedBatches + 1:n0}, seed={batchSeed}, " +
                        $"locks={batchLockInstances:n0}, workers/lock={batchWorkersPerLock:n0}, " +
                        $"rounds/lock={batchOperationsPerLock:n0}, elapsed={FormatDuration(elapsed.Elapsed)}");
                    Console.WriteLine($"       {FormatException(exception)}");

                    return 4;
                }

                completedBatches++;
                totalInjectedExceptions += testCase.RandomCaughtInjectedExceptions;

                TimeSpan now = elapsed.Elapsed;
                if (now - lastHeartbeat >= HeartbeatInterval ||
                    now >= duration ||
                    Volatile.Read(ref cancellationRequested))
                {
                    process.Refresh();
                    TimeSpan currentCpu = process.TotalProcessorTime;
                    double wallSeconds = Math.Max(0.001, (now - lastHeartbeat).TotalSeconds);
                    double cpuPercent =
                        (currentCpu - lastCpu).TotalSeconds / wallSeconds / processorCount * 100.0;
                    TimeSpan remaining = duration > now ? duration - now : TimeSpan.Zero;

                    Console.WriteLine(
                        $"[OK] elapsed={FormatDuration(now)}, remaining={FormatDuration(remaining)}, " +
                        $"batches={completedBatches:n0}, last-seed={batchSeed}, " +
                        $"last-shape={batchLockInstances:n0}x{batchWorkersPerLock:n0}x{batchOperationsPerLock:n0}, " +
                        $"last-injected={testCase.RandomCaughtInjectedExceptions:n0}, total-injected={totalInjectedExceptions:n0}, " +
                        $"cpu={cpuPercent:0.0}%, managed={ToMiB(GC.GetTotalMemory(false)):n0}MiB, " +
                        $"threads={process.Threads.Count:n0}, gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

                    lastHeartbeat = now;
                    lastCpu = currentCpu;
                }
            }
            while (elapsed.Elapsed < duration && !Volatile.Read(ref cancellationRequested));

            Console.WriteLine();
            Console.WriteLine(
                $"[PASS] pipeline semantic stress completed: elapsed={FormatDuration(elapsed.Elapsed)}, " +
                $"batches={completedBatches:n0}, injected-exceptions={totalInjectedExceptions:n0}, base-seed={baseSeed}.");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static long ToMiB(long bytes) => bytes / (1024L * 1024L);

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays}.{value:hh\\:mm\\:ss}";
        }

        if (value.TotalMinutes < 1)
        {
            return $"{value.TotalSeconds:0.000}s";
        }

        return value.ToString(@"hh\:mm\:ss");
    }

    private static string FormatException(Exception exception)
    {
        string result = $"{exception.GetType().Name}: {exception.Message}";
        for (Exception? inner = exception.InnerException; inner != null; inner = inner.InnerException)
        {
            result += $" -> {inner.GetType().Name}: {inner.Message}";
        }

        return result;
    }
}
