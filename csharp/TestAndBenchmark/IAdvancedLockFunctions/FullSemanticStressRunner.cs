using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 按时间持续重复运行完整 stage-5 语义测试；任意失败立即停止并输出可复现 seed。
/// </summary>
internal static class FullSemanticStressRunner
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    public static int Run(
        TimeSpan duration,
        int lockInstances,
        int workersPerLock,
        int operationsPerLock,
        int? requestedSeed)
    {
        int baseSeed = requestedSeed ?? Random.Shared.Next();
        Random seedSource = new Random(baseSeed);
        int processorCount = Math.Max(1, Environment.ProcessorCount);

        Console.WriteLine("Full semantic stress validation");
        Console.WriteLine(
            $"duration={FormatDuration(duration)}, locks={lockInstances:n0}, workers/lock={workersPerLock:n0}, " +
            $"rounds/lock/batch={operationsPerLock:n0}, total-threads/batch={lockInstances * (long)workersPerLock:n0}, " +
            $"base-seed={baseSeed}.");
        Console.WriteLine("Every batch runs the complete stage-5 semantic case set. Any failure stops immediately.");
        Console.WriteLine("Press Ctrl+C to stop after the current batch.");
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
        long completedCases = 0;

        try
        {
            while (elapsed.Elapsed < duration && !Volatile.Read(ref cancellationRequested))
            {
                int batchSeed = seedSource.Next();
                int batchLockInstances = NextInRange(seedSource, 1, lockInstances);
                int batchWorkersPerLock = NextInRange(seedSource, 2, workersPerLock);
                int batchOperationsPerLock = NextInRange(seedSource, 1, operationsPerLock);
                List<IAdvancedLockCorrectnessCase> cases = FullSemanticCorrectnessRunner.CreateCases(
                    batchLockInstances,
                    batchWorkersPerLock,
                    batchOperationsPerLock,
                    batchSeed,
                    printRandomSummaries: false);

                try
                {
                    RunBatch(cases);
                }
                catch (Exception exception)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        $"[FAIL] full semantic stress batch={completedBatches + 1:n0}, seed={batchSeed}, " +
                        $"locks={batchLockInstances:n0}, workers/lock={batchWorkersPerLock:n0}, " +
                        $"rounds/lock={batchOperationsPerLock:n0}, elapsed={FormatDuration(elapsed.Elapsed)}");
                    Console.WriteLine($"       {FormatException(exception)}");

                    return 4;
                }

                completedBatches++;
                completedCases += cases.Count;

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
                        $"batches={completedBatches:n0}, cases={completedCases:n0}, last-seed={batchSeed}, " +
                        $"last-shape={batchLockInstances:n0}x{batchWorkersPerLock:n0}x{batchOperationsPerLock:n0}, " +
                        $"cpu={cpuPercent:0.0}%, managed={ToMiB(GC.GetTotalMemory(false)):n0}MiB, " +
                        $"threads={process.Threads.Count:n0}, gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");

                    lastHeartbeat = now;
                    lastCpu = currentCpu;
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                $"[PASS] full semantic stress completed: elapsed={FormatDuration(elapsed.Elapsed)}, " +
                $"batches={completedBatches:n0}, cases={completedCases:n0}, base-seed={baseSeed}.");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void RunBatch(List<IAdvancedLockCorrectnessCase> cases)
    {
        foreach (IAdvancedLockCorrectnessCase testCase in cases)
        {
            try
            {
                testCase.Run();
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Case failed: {testCase.Name}",
                    exception);
            }
        }
    }

    private static long ToMiB(long bytes) => bytes / (1024L * 1024L);

    private static int NextInRange(Random random, int minimum, int maximum)
    {
        maximum = Math.Max(minimum, maximum);
        return random.Next(minimum, maximum + 1);
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return $"{(int)value.TotalDays}.{value:hh\\:mm\\:ss}";
        }

        return value.ToString(@"hh\:mm\:ss");
    }

    private static string FormatException(Exception exception)
    {
        string result = $"{exception.GetType().Name}: {exception.Message}";
        for (Exception inner = exception.InnerException; inner != null; inner = inner.InnerException)
        {
            result += $" -> {inner.GetType().Name}: {inner.Message}";
        }

        return result;
    }
}
