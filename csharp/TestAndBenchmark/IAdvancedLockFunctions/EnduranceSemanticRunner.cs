using System;
using System.Diagnostics;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// Reuses the same persistent lock objects while repeatedly executing randomized contract-valid
/// permission paths and reporting process health.
/// </summary>
/// <remarks>
/// Lock count, workers per lock, rounds per batch, and the optional base seed are literal command-line
/// inputs. Hardware discovery never scales the topology. A printed base seed deterministically derives
/// every batch seed through <see cref="PortableRandom"/>.
/// </remarks>
internal static class EnduranceSemanticRunner
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    public static int Run(
        TimeSpan duration,
        int lockCount,
        int workersPerLock,
        int roundsPerBatch,
        int? requestedSeed)
    {
        PortableRandomContract.Validate();
        int baseSeed = requestedSeed ?? SeedSource.Create();
        PortableRandom seedSource = new PortableRandom(baseSeed);
        int processorCount = Math.Max(1, Environment.ProcessorCount);
        SharedConcurrentExclusiveLock[] locks = new SharedConcurrentExclusiveLock[lockCount];
        for (int index = 0; index < locks.Length; index++)
        {
            locks[index] = new SharedConcurrentExclusiveLock();
        }

        Console.WriteLine("Semantic endurance validation");
        Console.WriteLine(
            $"duration={FormatDuration(duration)}, persistent-locks={lockCount:n0}, workers/lock={workersPerLock:n0}, " +
            $"batch-threads={lockCount * (long)workersPerLock:n0}, rounds/lock/batch={roundsPerBatch:n0}, " +
            $"base-seed={baseSeed}");
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
        long completedRounds = 0;
        int lastBatchSeed = baseSeed;
        long[] totals = new long[5];

        try
        {
            do
            {
                int seed = seedSource.NextSeed();
                lastBatchSeed = seed;
                RandomizedValidSemanticPathsCase batch = new RandomizedValidSemanticPathsCase(
                    lockCount,
                    workersPerLock,
                    roundsPerBatch,
                    seed,
                    locks,
                    printSummary: false);

                try
                {
                    batch.Run();
                }
                catch (Exception exception)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"[FAIL] endurance batch={completedBatches + 1:n0}, seed={seed}, " +
                        $"elapsed={FormatDuration(elapsed.Elapsed)}");
                    Console.WriteLine($"       {FormatException(exception)}");
                    return 4;
                }

                completedBatches++;
                completedRounds += (long)lockCount * roundsPerBatch;
                long[] counts = batch.LastOperationCounts;
                for (int index = 0; index < totals.Length; index++)
                {
                    totals[index] += counts[index];
                }

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
                        $"batches={completedBatches:n0}, rounds={completedRounds:n0}, last-seed={lastBatchSeed}, cpu={cpuPercent:0.0}%, " +
                        $"working-set={ToMiB(process.WorkingSet64):n0}MiB, private={ToMiB(process.PrivateMemorySize64):n0}MiB, " +
                        $"managed={ToMiB(GC.GetTotalMemory(false)):n0}MiB, threads={process.Threads.Count:n0}, " +
                        $"gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                    Console.WriteLine(
                        $"     paths concurrent={totals[0]:n0}, exclusive={totals[1]:n0}, " +
                        $"downgrade={totals[2]:n0}, upgrade={totals[3]:n0}, " +
                        $"conversion-cycle={totals[4]:n0}");

                    lastHeartbeat = now;
                    lastCpu = currentCpu;
                }
            }
            while (elapsed.Elapsed < duration && !Volatile.Read(ref cancellationRequested));

            Console.WriteLine();
            Console.WriteLine(
                $"[PASS] endurance completed: elapsed={FormatDuration(elapsed.Elapsed)}, " +
                $"batches={completedBatches:n0}, rounds={completedRounds:n0}, " +
                $"persistent-locks={lockCount:n0}, base-seed={baseSeed}, last-seed={lastBatchSeed}.");
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
