using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// Verifies unconditional ConcurrentToExclusive(): single-thread semantics, multi-thread convergence isolation, and Scope cleanup.
/// </summary>
internal sealed class UnconditionalConcurrentToExclusiveCase : IAdvancedLockCorrectnessCase
{
    public string Name => "Unconditional ConcurrentToExclusive serializes every participant and releases correctly";

    public void Run()
    {
        VerifySingleHolderUpgrade();
        VerifyMultiHolderConvergence();
        VerifyUpgradeThenDowngrade();
        VerifyNoInsertionWindow();
        VerifyScopeNormalRelease();
        VerifyScopeExceptionRelease();
        VerifyScopeMultiThreadAccounting();
    }

    private static void VerifySingleHolderUpgrade()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        locker.AcquireConcurrent();
        locker.ConcurrentToExclusive();

        AdvancedAssert.True(
            locker.ObservedState == ConcurrentExclusiveLockState.Exclusive,
            "Single holder ConcurrentToExclusive did not enter Exclusive.");
        AdvancedAssert.Equal(
            0, locker.TryAcquireConcurrent(0, ConcurrentExclusiveLock.MaxConcurrent),
            "Concurrent entry allowed while Exclusive was held after ConcurrentToExclusive.");

        locker.ReleaseExclusive();
        AssertReusable(locker, "single holder upgrade");
    }

    private static void VerifyMultiHolderConvergence()
    {
        const int participants = 8;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(participants);
        int activeExclusive = 0;
        int overlaps = 0;
        int executed = 0;

        for (int i = 0; i < participants; i++)
        {
            threads.Start($"uncond-upgrade-{i}", () =>
            {
                locker.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Unconditional upgrade participants did not acquire together.");
                }

                locker.ConcurrentToExclusive();
                int active = Interlocked.Increment(ref activeExclusive);
                if (active != 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                Interlocked.Increment(ref executed);
                Thread.Yield();
                Interlocked.Decrement(ref activeExclusive);
                locker.ReleaseExclusive();
            });
        }

        threads.JoinAll("Unconditional ConcurrentToExclusive multi-holder convergence");
        AdvancedAssert.Equal(participants, Volatile.Read(ref executed), "Every participant must execute Exclusive.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Unconditional ConcurrentToExclusive must serialize Exclusive regions.");
        AssertReusable(locker, "multi-holder convergence");
    }

    private static void VerifyUpgradeThenDowngrade()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        locker.AcquireConcurrent();
        locker.ConcurrentToExclusive();
        locker.ExclusiveToConcurrent();

        AdvancedAssert.True(
            locker.ObservedState == ConcurrentExclusiveLockState.Concurrent,
            "ExclusiveToConcurrent after ConcurrentToExclusive did not hold Concurrent.");
        AdvancedAssert.True(!locker.TryAcquireExclusive(preemptConcurrent: false),
            "Non-preemptive Exclusive entered while Concurrent was held after downgrade.");

        locker.ReleaseConcurrent();
        AssertReusable(locker, "upgrade then downgrade");
    }

    private static void VerifyNoInsertionWindow()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim ordinaryWriterAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim ordinaryWriterEntered = new ManualResetEventSlim(false);

        locker.AcquireConcurrent();
        try
        {
            threads.Start("uncond-upgrade-no-insertion", () =>
            {
                ordinaryWriterAttempting.Set();
                locker.AcquireExclusive();
                try
                {
                    ordinaryWriterEntered.Set();
                }
                finally
                {
                    locker.ReleaseExclusive();
                }
            });

            AdvancedAssert.Wait(ordinaryWriterAttempting, "Ordinary Exclusive did not start.");
            AdvancedAssert.RemainsBlocked(
                ordinaryWriterEntered,
                "Ordinary Exclusive entered while Concurrent was held before upgrade.");

            locker.ConcurrentToExclusive();
            AdvancedAssert.RemainsBlocked(
                ordinaryWriterEntered,
                "Ordinary Exclusive inserted during ConcurrentToExclusive.");

            locker.ReleaseExclusive();
        }
        finally
        {
        }

        AdvancedAssert.Wait(ordinaryWriterEntered, "Ordinary Exclusive did not enter after ConcurrentToExclusive released.");
        threads.JoinAll("Unconditional ConcurrentToExclusive no insertion window");
        AssertReusable(locker, "no insertion window");
    }

    private static void VerifyScopeNormalRelease()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent();
            scope.ConcurrentToExclusive();
        }

        AssertReusable(locker, "Scope ConcurrentToExclusive normal dispose");
    }

    private static void VerifyScopeExceptionRelease()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        try
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            scope.ConcurrentToExclusive();
            throw new ScopeUpgradeException();
        }
        catch (ScopeUpgradeException)
        {
        }

        AssertReusable(locker, "Scope ConcurrentToExclusive exception dispose");
    }

    private static void VerifyScopeMultiThreadAccounting()
    {
        const int participants = 4;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(participants);
        int executed = 0;
        int activeExclusive = 0;
        int overlaps = 0;

        for (int i = 0; i < participants; i++)
        {
            threads.Start($"scope-uncond-upgrade-{i}", () =>
            {
                using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
                scope.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Scope unconditional upgrade participants did not acquire together.");
                }

                scope.ConcurrentToExclusive();
                int active = Interlocked.Increment(ref activeExclusive);
                if (active != 1)
                {
                    Interlocked.Increment(ref overlaps);
                }

                Interlocked.Increment(ref executed);
                Thread.Yield();
                Interlocked.Decrement(ref activeExclusive);
                // Dispose releases the Exclusive held by scope.
            });
        }

        threads.JoinAll("Scope unconditional ConcurrentToExclusive accounting");
        AdvancedAssert.Equal(participants, Volatile.Read(ref executed), "Every scope participant must execute.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Scope ConcurrentToExclusive must serialize.");
        AssertReusable(locker, "Scope multi-thread accounting");
    }

    private static void AssertReusable(ConcurrentExclusiveLock locker, string operation)
    {
        locker.AcquireConcurrent();
        locker.ReleaseConcurrent();
        locker.AcquireExclusive();
        locker.ReleaseExclusive();
    }

    private sealed class ScopeUpgradeException : Exception
    {
    }
}
