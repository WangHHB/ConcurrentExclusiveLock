using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 创建专用 Thread，在全部线程就绪后统一放行，并收集时间和异常。
/// 线程创建及到达 ready 屏障的时间不进入计时区间。
/// </summary>
internal static class DedicatedThreadHarness
{
    public static ThreadRunMeasurement Run(
        int threadCount,
        string threadNamePrefix,
        Action<int> workerBody)
    {
        ExceptionDispatchInfo firstFailure = null;
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

/// <summary>统一放行到全部专用线程结束之间的原始计量。</summary>
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
