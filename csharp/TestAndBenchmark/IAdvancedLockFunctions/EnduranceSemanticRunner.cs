using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// 长时间复用同一批锁对象，持续执行随机合法权限路径并观察进程健康状态。
/// 拓扑由逻辑处理器数量自动生成，命令行只需给出测试时长。
/// </summary>
internal static class EnduranceSemanticRunner
{
    private const int WorkersPerLock = 4;
    private const int RoundsPerBatch = 256;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(1);

    public static int Run(TimeSpan duration)
    {
        int processorCount = Math.Max(1, Environment.ProcessorCount);
        int lockCount = Math.Clamp((processorCount + 1) / 2, 8, 128);
        int numaNodes = GetNumaNodeCount();
        SharedConcurrentExclusiveLock[] locks = new SharedConcurrentExclusiveLock[lockCount];
        for (int index = 0; index < locks.Length; index++)
        {
            locks[index] = new SharedConcurrentExclusiveLock();
        }

        Console.WriteLine("Semantic endurance validation");
        Console.WriteLine(
            $"duration={FormatDuration(duration)}, logical-cpu={processorCount:n0}, numa-nodes={numaNodes:n0}");
        Console.WriteLine(
            $"persistent-locks={lockCount:n0}, workers/lock={WorkersPerLock:n0}, " +
            $"batch-threads={lockCount * WorkersPerLock:n0}, rounds/lock/batch={RoundsPerBatch:n0}");
        Console.WriteLine(
            "The same lock objects remain alive for the entire run; every generated call path is contract-valid.");
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
        long completedRounds = 0;
        long[] totals = new long[5];

        try
        {
            while (elapsed.Elapsed < duration && !Volatile.Read(ref cancellationRequested))
            {
                int seed = Random.Shared.Next();
                RandomizedValidSemanticPathsCase batch = new RandomizedValidSemanticPathsCase(
                    lockCount,
                    WorkersPerLock,
                    RoundsPerBatch,
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
                completedRounds += (long)lockCount * RoundsPerBatch;
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
                        $"batches={completedBatches:n0}, rounds={completedRounds:n0}, cpu={cpuPercent:0.0}%, " +
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

            Console.WriteLine();
            Console.WriteLine(
                $"[PASS] endurance completed: elapsed={FormatDuration(elapsed.Elapsed)}, " +
                $"batches={completedBatches:n0}, rounds={completedRounds:n0}, " +
                $"persistent-locks={lockCount:n0}.");
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

    private static int GetNumaNodeCount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        try
        {
            return GetNumaHighestNodeNumber(out uint highestNode) ? checked((int)highestNode + 1) : 1;
        }
        catch (DllNotFoundException)
        {
            return 1;
        }
        catch (EntryPointNotFoundException)
        {
            return 1;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumaHighestNodeNumber(out uint highestNodeNumber);
}
