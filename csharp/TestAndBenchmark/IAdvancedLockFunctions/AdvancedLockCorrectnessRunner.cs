using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LockBenchmark;

/// <summary>一个可独立执行的高级锁语义正确性案例。</summary>
internal interface IAdvancedLockCorrectnessCase
{
    string Name { get; }

    void Run();
}

/// <summary>阶段3总控：只报告语义正确性，不参与标准锁性能排名。</summary>
internal static class AdvancedLockCorrectnessRunner
{
    public static int Run(
        int lockInstances,
        int advancedOperationsPerLock,
        int? requestedSeed)
    {
        int randomSeed = requestedSeed ?? Random.Shared.Next();
        List<IAdvancedLockCorrectnessCase> cases = new List<IAdvancedLockCorrectnessCase>
        {
            new ExclusiveToConcurrentCorrectnessCase(),
            new ConcurrentToExclusiveCorrectnessCase()
        };

        if (lockInstances > 1)
        {
            cases.Add(new AdvancedMassiveIndependentLocksCase(
                lockInstances,
                advancedOperationsPerLock,
                randomSeed));
        }

        Console.WriteLine("Stage 3: advanced lock correctness");
        Console.WriteLine("Dedicated Thread instances are used; every blocking assertion has a timeout.");
        if (lockInstances > 1)
        {
            Console.WriteLine(
                $"Mass-independent mode: locks={lockInstances:n0}, participants/lock=2, " +
                $"maximum simultaneous threads={lockInstances * 2L:n0}.");
            Console.WriteLine(
                $"Base operations/lock/kind={advancedOperationsPerLock:n0}, " +
                $"random jitter=+/-25%, seed={randomSeed}.");
        }
        Console.WriteLine();

        int passed = 0;
        foreach (IAdvancedLockCorrectnessCase testCase in cases)
        {
            try
            {
                testCase.Run();
                passed++;
                Console.WriteLine($"[PASS] {testCase.Name}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[FAIL] {testCase.Name}");
                Console.WriteLine($"       {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: passed={passed}, failed={cases.Count - passed}, total={cases.Count}");
        return passed == cases.Count ? 0 : 2;
    }
}

/// <summary>高级语义测试共用的超时和短暂阻塞观察窗口。</summary>
internal static class AdvancedTestTiming
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MustRemainBlocked = TimeSpan.FromMilliseconds(100);
}

/// <summary>为正确性测试创建专用后台线程，并集中传播线程异常。</summary>
internal sealed class AdvancedTestThreadGroup
{
    private readonly List<Thread> threads = new List<Thread>();
    private ExceptionDispatchInfo firstFailure;

    public void Start(string name, Action body)
    {
        Thread thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(
                    ref firstFailure,
                    ExceptionDispatchInfo.Capture(exception),
                    null);
                Console.WriteLine(
                    $"[THREAD FAIL] {Thread.CurrentThread.Name}: " +
                    FormatException(exception));
            }
        })
        {
            IsBackground = true,
            Name = name
        };

        threads.Add(thread);
        thread.Start();
    }

    private static string FormatException(Exception exception)
    {
        string result = $"{exception.GetType().Name}: {exception.Message}";
        Exception inner = exception.InnerException;
        while (inner != null)
        {
            result += $" -> {inner.GetType().Name}: {inner.Message}";
            inner = inner.InnerException;
        }

        return result;
    }

    public void JoinAll(string operation)
    {
        JoinAll(operation, AdvancedTestTiming.Timeout);
    }

    public void JoinAll(string operation, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        foreach (Thread thread in threads)
        {
            TimeSpan remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero || !thread.Join(remaining))
            {
                throw new TimeoutException(
                    $"{operation}: thread '{thread.Name}' did not finish within {timeout.TotalSeconds:0} seconds.");
            }
        }

        firstFailure?.Throw();
    }

    public void JoinAllWhileProgressing(
        string operation,
        Func<long> readProgress,
        TimeSpan noProgressTimeout)
    {
        bool[] joined = new bool[threads.Count];
        int remaining = threads.Count;
        long lastProgress = readProgress();
        Stopwatch noProgress = Stopwatch.StartNew();

        while (remaining > 0)
        {
            firstFailure?.Throw();

            bool joinedAny = false;
            for (int i = 0; i < threads.Count; i++)
            {
                if (joined[i])
                {
                    continue;
                }

                if (threads[i].Join(0))
                {
                    joined[i] = true;
                    remaining--;
                    joinedAny = true;
                }
            }

            if (remaining == 0)
            {
                break;
            }

            long progress = readProgress();
            if (joinedAny || progress != lastProgress)
            {
                lastProgress = progress;
                noProgress.Restart();
            }
            else if (noProgress.Elapsed >= noProgressTimeout)
            {
                throw new TimeoutException(
                    $"{operation}: no worker progress for {noProgressTimeout.TotalSeconds:0} seconds. " +
                    $"progress={progress:n0}, remaining-threads={remaining:n0}.");
            }

            Thread.Sleep(50);
        }

        firstFailure?.Throw();
    }
}

/// <summary>小型断言集，避免引入测试框架和线程池调度。</summary>
internal static class AdvancedAssert
{
    public static void Wait(ManualResetEventSlim signal, string message)
    {
        Wait(signal, AdvancedTestTiming.Timeout, message);
    }

    public static void Wait(ManualResetEventSlim signal, TimeSpan timeout, string message)
    {
        if (!signal.Wait(timeout))
        {
            throw new TimeoutException(message);
        }
    }

    public static void Wait(CountdownEvent signal, string message)
    {
        Wait(signal, AdvancedTestTiming.Timeout, message);
    }

    public static void Wait(CountdownEvent signal, TimeSpan timeout, string message)
    {
        if (!signal.Wait(timeout))
        {
            throw new TimeoutException(message);
        }
    }

    public static void RemainsBlocked(ManualResetEventSlim signal, string message)
    {
        if (signal.Wait(AdvancedTestTiming.MustRemainBlocked))
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Equal(int expected, int actual, string message)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException($"{message} Expected={expected}, actual={actual}.");
        }
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
