using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Runs dedicated OS threads from a common start gate and propagates the first worker failure.
/// Thread creation and arrival at the ready barrier are outside the measured interval.
/// </summary>
/// <remarks>
/// Porting contract: do not replace this with a thread pool, tasks, goroutines, async jobs, or
/// another scheduler-managed abstraction. The measured topology is exactly one dedicated worker
/// per requested benchmark thread. Elapsed time starts immediately before opening the common gate
/// and stops after every worker has terminated.
/// </remarks>
internal static class DedicatedThreadHarness
{
    public static ThreadRunMeasurement Run(
        int threadCount,
        string threadNamePrefix,
        Action<int> workerBody)
    {
        ExceptionDispatchInfo? firstFailure = null;
        int abortBeforeStart = 0;
        using ManualResetEventSlim startGate = new ManualResetEventSlim(false);
        using CountdownEvent ready = new CountdownEvent(threadCount);
        Thread[] workers = new Thread[threadCount];
        int startedWorkers = 0;

        try
        {
            for (int workerIndex = 0; workerIndex < workers.Length; workerIndex++)
            {
                int capturedWorkerIndex = workerIndex;
                workers[workerIndex] = new Thread(() =>
                {
                    try
                    {
                        ready.Signal();
                        startGate.Wait();
                        if (Volatile.Read(ref abortBeforeStart) == 0)
                        {
                            workerBody(capturedWorkerIndex);
                        }
                    }
                    catch (Exception exception)
                    {
                        Interlocked.CompareExchange(
                            ref firstFailure,
                            ExceptionDispatchInfo.Capture(exception),
                            null);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"{threadNamePrefix}-{capturedWorkerIndex}"
                };

                workers[workerIndex].Start();
                startedWorkers++;
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref abortBeforeStart, 1);
            startGate.Set();

            for (int workerIndex = 0; workerIndex < startedWorkers; workerIndex++)
            {
                workers[workerIndex].Join();
            }

            throw new InvalidOperationException(
                $"Failed after starting {startedWorkers:n0} of {threadCount:n0} dedicated worker threads.",
                exception);
        }

        ready.Wait();

        using Process process = Process.GetCurrentProcess();
        TimeSpan cpuStart = process.TotalProcessorTime;
        Stopwatch stopwatch = Stopwatch.StartNew();
        startGate.Set();

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        stopwatch.Stop();
        process.Refresh();
        TimeSpan cpuElapsed = process.TotalProcessorTime - cpuStart;

        firstFailure?.Throw();
        return new ThreadRunMeasurement(stopwatch.Elapsed, cpuElapsed);
    }
}

/// <summary>Raw wall/process-CPU measurement from common-gate release until all dedicated workers terminate.</summary>
internal readonly struct ThreadRunMeasurement
{
    public TimeSpan Elapsed { get; }
    public TimeSpan CpuTime { get; }

    public ThreadRunMeasurement(TimeSpan elapsed, TimeSpan cpuTime)
    {
        Elapsed = elapsed;
        CpuTime = cpuTime;
    }
}
