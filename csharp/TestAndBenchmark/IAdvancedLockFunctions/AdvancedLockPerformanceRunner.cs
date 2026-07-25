using System;
using System.Diagnostics;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// 只测 ConcurrentExclusiveLock 高级语义自身的成本，不和其他锁横向对比。
/// </summary>
internal static class AdvancedLockPerformanceRunner
{
    private const int WarmupOperationsPerThread = 256;

    public static int Run(int threadCount, int operationsPerThread, int work)
    {
        threadCount = Math.Max(1, threadCount);
        operationsPerThread = Math.Max(1, operationsPerThread);
        work = Math.Max(0, work);

        Console.WriteLine("CEL advanced semantic performance");
        Console.WriteLine($"threads={threadCount:n0}, operations/thread={operationsPerThread:n0}, work={work:n0}");
        Console.WriteLine("Only ConcurrentExclusiveLock is measured; every case uses a fresh lock instance.");
        Console.WriteLine("ops means completed semantic loops across all dedicated worker threads.");
        Console.WriteLine("Every printed case uses exactly the requested operations/thread.");
        Console.WriteLine("Every semantic loop executes exactly one DoWork(work) payload.");
        Console.WriteLine();
        Console.WriteLine("Mixed cases split workers by path; even thread counts are exactly half and half.");
        Console.WriteLine();
        Console.WriteLine(
            $"  {"case",-38} {"loops/thread",12} {"elapsed",10} {"cpu%",9} {"ops/s",14} " +
            $"{"ns/op",10} {"ops/thread/s",14} {"ops/cpu%",12} {"state",18}");

        RunCase("warmup", Math.Min(threadCount, Environment.ProcessorCount), WarmupOperationsPerThread, Math.Min(work, 8), AdvancedPerfKind.Concurrent, print: false);
        RunCase("concurrent enter/release", threadCount, operationsPerThread, work, AdvancedPerfKind.Concurrent, print: true);
        RunCase("exclusive enter -> concurrent", threadCount, operationsPerThread, work, AdvancedPerfKind.ExclusiveToConcurrent, print: true);
        RunCase("concurrent/exclusive->concurrent 50/50", threadCount, operationsPerThread, work, AdvancedPerfKind.MixedConcurrentAndExclusiveToConcurrent, print: true);
        RunCase("exclusive enter/release", threadCount, operationsPerThread, work, AdvancedPerfKind.Exclusive, print: true);
        RunCase("concurrent enter -> exclusive", threadCount, operationsPerThread, work, AdvancedPerfKind.ConcurrentToExclusive, print: true);
        RunCase("exclusive/concurrent->exclusive 50/50", threadCount, operationsPerThread, work, AdvancedPerfKind.MixedExclusiveAndConcurrentToExclusive, print: true);

        Console.WriteLine();
        Console.WriteLine($"sink={AdvancedSemanticSink.Value}");
        return 0;
    }

    private static void RunCase(
        string name,
        int threadCount,
        int operationsPerThread,
        int work,
        AdvancedPerfKind kind,
        bool print)
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        CountdownEvent ready = new CountdownEvent(threadCount);
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        Stopwatch elapsed = new Stopwatch();

        for (int index = 0; index < threadCount; index++)
        {
            int capturedIndex = index;
            threads.Start($"advanced-perf-{kind}-{capturedIndex}", () =>
            {
                ready.Signal();
                start.Wait();
                for (int operation = 0; operation < operationsPerThread; operation++)
                {
                    ExecuteOperation(ref locker, kind, capturedIndex, operation, operationsPerThread, work);
                }
            });
        }

        AdvancedAssert.Wait(ready, $"Advanced performance case {name} workers did not become ready.");
        elapsed.Start();
        start.Set();
        threads.JoinAll($"advanced performance {name}", TimeSpan.FromMinutes(5));
        elapsed.Stop();

        process.Refresh();
        TimeSpan cpuAfter = process.TotalProcessorTime;
        long operations = (long)threadCount * operationsPerThread;
        double elapsedSeconds = Math.Max(0.000001, elapsed.Elapsed.TotalSeconds);
        double cpuPercent =
            (cpuAfter - cpuBefore).TotalSeconds / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100.0;
        double opsPerSecond = operations / elapsedSeconds;
        double nanosecondsPerOperation = elapsedSeconds * 1_000_000_000.0 / operations;
        double opsPerThreadSecond = opsPerSecond / threadCount;
        double opsPerCpuPercent = cpuPercent <= 0 ? 0 : opsPerSecond / cpuPercent;

        if (print)
        {
            Console.WriteLine(
                $"  {name,-38} {operationsPerThread,12:n0} {elapsed.Elapsed.TotalSeconds,9:0.000}s {cpuPercent,8:0.0}% " +
                $"{opsPerSecond,14:0} {nanosecondsPerOperation,10:0} {opsPerThreadSecond,14:0} {opsPerCpuPercent,12:0} " +
                $"{locker.ObservedState,18}");
        }
    }

    private static void ExecuteOperation(
        ref ConcurrentExclusiveLock locker,
        AdvancedPerfKind kind,
        int workerIndex,
        int operation,
        int operationsPerThread,
        int work)
    {
        switch (kind)
        {
            case AdvancedPerfKind.Concurrent:
                locker.AcquireConcurrent();
                DoWork(workerIndex, operation, work);
                locker.ReleaseConcurrent();
                break;

            case AdvancedPerfKind.Exclusive:
                locker.AcquireExclusive();
                DoWork(workerIndex, operation, work);
                locker.ReleaseExclusive();
                break;

            case AdvancedPerfKind.ExclusiveToConcurrent:
                locker.AcquireExclusive();
                locker.ExclusiveToConcurrent();
                DoWork(workerIndex, operation, work);
                locker.ReleaseConcurrent();
                break;

            case AdvancedPerfKind.ConcurrentToExclusive:
                locker.AcquireConcurrent();
                if (!locker.TryConcurrentToExclusiveWithSwitchContextID(
                    CreateUpgradeContextId(workerIndex, operation, operationsPerThread)))
                {
                    throw new InvalidOperationException("Unique ContextID Concurrent-to-Exclusive upgrade failed.");
                }

                DoWork(workerIndex, operation, work);
                locker.ReleaseExclusive();
                break;

            case AdvancedPerfKind.MixedConcurrentAndExclusiveToConcurrent:
                if ((workerIndex & 1) == 0)
                {
                    locker.AcquireConcurrent();
                    DoWork(workerIndex, operation, work);
                    locker.ReleaseConcurrent();
                }
                else
                {
                    locker.AcquireExclusive();
                    locker.ExclusiveToConcurrent();
                    DoWork(workerIndex, operation, work);
                    locker.ReleaseConcurrent();
                }
                break;

            case AdvancedPerfKind.MixedExclusiveAndConcurrentToExclusive:
                if ((workerIndex & 1) == 0)
                {
                    locker.AcquireExclusive();
                    DoWork(workerIndex, operation, work);
                    locker.ReleaseExclusive();
                }
                else
                {
                    locker.AcquireConcurrent();
                    if (!locker.TryConcurrentToExclusiveWithSwitchContextID(
                        CreateUpgradeContextId(workerIndex, operation, operationsPerThread)))
                    {
                        throw new InvalidOperationException("Unique ContextID Concurrent-to-Exclusive upgrade failed.");
                    }

                    DoWork(workerIndex, operation, work);
                    locker.ReleaseExclusive();
                }
                break;
        }
    }

    private static int CreateUpgradeContextId(int workerIndex, int operation, int operationsPerThread)
    {
        return unchecked(0x10000000 + workerIndex * operationsPerThread + operation);
    }

    private static void DoWork(int workerIndex, int operation, int work)
    {
        ulong value = ((ulong)workerIndex << 32) ^ (uint)operation ^ 0x9E3779B97F4A7C15UL;
        for (int i = 0; i < work; i++)
        {
            value ^= value << 7;
            value ^= value >> 9;
            value *= 0xBF58476D1CE4E5B9UL;
        }

        AdvancedSemanticSink.Add(unchecked((long)value));
    }

    private enum AdvancedPerfKind
    {
        Concurrent,
        Exclusive,
        ExclusiveToConcurrent,
        ConcurrentToExclusive,
        MixedConcurrentAndExclusiveToConcurrent,
        MixedExclusiveAndConcurrentToExclusive
    }
}
