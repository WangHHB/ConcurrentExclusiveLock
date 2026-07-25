using System;
using System.Diagnostics;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>持续轰击单锁并高频采样诊断用 Contention，不参与锁横向性能排名。</summary>
internal static class ContentionStressRunner
{
    public static int Run(TimeSpan duration, int workerCount)
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        CountdownEvent ready = new CountdownEvent(workerCount);
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        int stop = 0;
        long operations = 0;

        Console.WriteLine("Contention peak stress");
        Console.WriteLine(
            $"duration={duration:c}, dedicated-workers={workerCount:n0}, single-lock, " +
            "mix=75% Concurrent / 25% Exclusive");
        Console.WriteLine("Contention is sampled as a weak diagnostic value, not a waiter count.");
        Console.WriteLine();

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        for (int worker = 0; worker < workerCount; worker++)
        {
            int capturedWorker = worker;
            threads.Start($"contention-stress-{capturedWorker}", () =>
            {
                uint random = CreateSeed(capturedWorker);
                long localOperations = 0;
                ready.Signal();
                AdvancedAssert.Wait(start, "Contention stress worker did not receive the start signal.");

                while (Volatile.Read(ref stop) == 0)
                {
                    random = NextRandom(random);
                    if ((random & 3u) == 0)
                    {
                        locker.AcquireExclusive();
                        try
                        {
                            Thread.SpinWait(32);
                        }
                        finally
                        {
                            locker.ReleaseExclusive();
                        }
                    }
                    else
                    {
                        locker.AcquireConcurrent();
                        try
                        {
                            Thread.SpinWait(32);
                        }
                        finally
                        {
                            locker.ReleaseConcurrent();
                        }
                    }
                    localOperations++;
                }

                Interlocked.Add(ref operations, localOperations);
            });
        }

        try
        {
            AdvancedAssert.Wait(ready, "Contention stress workers did not all become ready.");
            using Process process = Process.GetCurrentProcess();
            TimeSpan cpuStart = process.TotalProcessorTime;
            Stopwatch stopwatch = Stopwatch.StartNew();
            start.Set();

            int maximum = 0;
            long sampleCount = 0;
            long positiveSamples = 0;
            long sampleSum = 0;

            while (stopwatch.Elapsed < duration)
            {
                int value = locker.ObservedContention;
                maximum = Math.Max(maximum, value);
                sampleSum += value;
                sampleCount++;
                if (value > 0)
                {
                    positiveSamples++;
                }
                Thread.SpinWait(16);
            }

            Volatile.Write(ref stop, 1);
            TimeSpan joinTimeout = TimeSpan.FromSeconds(
                Math.Clamp(duration.TotalSeconds + 30.0, 60.0, 600.0));
            threads.JoinAll("Contention peak stress", joinTimeout);
            stopwatch.Stop();
            process.Refresh();
            TimeSpan cpuElapsed = process.TotalProcessorTime - cpuStart;

            WaitForQuiescence(locker);
            double average = sampleCount == 0 ? 0 : sampleSum / (double)sampleCount;
            double positivePercent = sampleCount == 0 ? 0 : positiveSamples * 100.0 / sampleCount;
            double cpuPercent = cpuElapsed.TotalSeconds /
                                Math.Max(0.001, stopwatch.Elapsed.TotalSeconds) /
                                Math.Max(1, Environment.ProcessorCount) * 100.0;

            Console.WriteLine(
                $"result max={maximum:n0}, average={average:0.000}, nonzero={positivePercent:0.0}%, " +
                $"samples={sampleCount:n0}");
            Console.WriteLine(
                $"       operations={operations:n0}, elapsed={stopwatch.Elapsed.TotalSeconds:0.000}s, " +
                $"cpu={cpuPercent:0.0}%, final-contention={locker.ObservedContention}, final-state={locker.ObservedState}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[FAIL] {exception.GetType().Name}: {exception.Message}");
            return 5;
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            start.Set();
        }
    }

    private static void WaitForQuiescence(ConcurrentExclusiveLock locker)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        SpinWait spinner = new SpinWait();
        while (stopwatch.Elapsed < AdvancedTestTiming.Timeout)
        {
            if (locker.ObservedContention == 0 && locker.ObservedState == ConcurrentExclusiveLockState.Idle)
            {
                return;
            }
            spinner.SpinOnce();
        }

        throw new TimeoutException(
            $"Lock did not become quiescent. Contention={locker.ObservedContention}, State={locker.ObservedState}.");
    }

    private static uint CreateSeed(int worker)
    {
        unchecked
        {
            uint value = 0x9E3779B9u ^ (uint)worker * 0x85EBCA6Bu;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            return value == 0 ? 1u : value;
        }
    }

    private static uint NextRandom(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return value;
    }
}
