using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// 验证 State 只作为诊断快照暴露基本状态，不把它当成强一致计数器使用。
/// </summary>
internal sealed class LockStateSnapshotCorrectnessCase : IAdvancedLockCorrectnessCase
{
    public string Name => "State snapshot exposes Idle, Concurrent, and Exclusive";

    public void Run()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);

        AssertState(locker, scope, ConcurrentExclusiveLockState.Idle, "initial Idle");

        scope.AcquireConcurrent();
        AssertState(locker, scope, ConcurrentExclusiveLockState.Concurrent, "Concurrent");
        scope.ReleaseConcurrent();
        AssertState(locker, scope, ConcurrentExclusiveLockState.Idle, "Idle after Concurrent release");

        scope.AcquireExclusive();
        AssertState(locker, scope, ConcurrentExclusiveLockState.Exclusive, "Exclusive");
        scope.ReleaseExclusive();
        AssertState(locker, scope, ConcurrentExclusiveLockState.Idle, "Idle after Exclusive release");

        scope.Dispose();
    }

    private static void AssertState(
        ConcurrentExclusiveLock locker,
        ConcurrentExclusiveLockScope scope,
        ConcurrentExclusiveLockState expected,
        string operation)
    {
        AdvancedAssert.True(
            locker.ObservedState == expected,
            $"{operation}: raw lock State expected {expected}, actual {locker.ObservedState}.");
        AdvancedAssert.True(
            scope.ObservedState == expected,
            $"{operation}: scope State expected {expected}, actual {scope.ObservedState}.");
    }
}

/// <summary>
/// 验证 Contention 在压力下可观察到非零，并在压力释放后回到 0。
/// </summary>
internal sealed class LockContentionSnapshotCorrectnessCase : IAdvancedLockCorrectnessCase
{
    public string Name => "Contention snapshot becomes observable under pressure and returns to zero";

    public void Run()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        ManualResetEventSlim stop = new ManualResetEventSlim(false);

        locker.AcquireExclusive();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                threads.Start($"contention-snapshot-waiter-{i}", () =>
                {
                    start.Wait();
                    int id = locker.AcquireConcurrent();
                    try
                    {
                        AdvancedAssert.True(id != 0, "Contention waiter acquired an invalid Concurrent ID.");
                    }
                    finally
                    {
                        locker.ReleaseConcurrent();
                    }
                });
            }

            start.Set();
            int maxObserved = 0;
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
            while (DateTime.UtcNow < deadline)
            {
                int observed = Math.Max(locker.ObservedContention, scope.ObservedContention);
                if (observed > maxObserved)
                {
                    maxObserved = observed;
                }

                if (maxObserved > 0)
                {
                    break;
                }

                Thread.Yield();
            }

            AdvancedAssert.True(
                maxObserved > 0,
                "Contention remained zero while multiple threads were blocked.");
        }
        finally
        {
            locker.ReleaseExclusive();
        }

        threads.JoinAll("Contention snapshot waiters");
        AdvancedAssert.Equal(0, locker.ObservedContention, "Raw lock Contention did not return to zero.");
        AdvancedAssert.Equal(0, scope.ObservedContention, "Scope Contention did not return to zero.");
        Console.WriteLine("       contention waiters=8, final=0");
    }
}
