using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// 验证 Scope 在普通释放、异常路径、升降级路径里都能按当前持有状态自动收尾。
/// </summary>
internal sealed class ConcurrentExclusiveLockScopeLifecycleCase : IAdvancedLockCorrectnessCase
{
    private const int ContextId = 0x515151;

    public string Name => "ConcurrentExclusiveLockScope releases every legal final state and exception path";

    public void Run()
    {
        VerifyConstructorContract();
        VerifyNormalFinalStates();
        VerifyExceptionFinalStates();
        VerifyFailedContextUpgradeIsNotDoubleReleased();
        VerifyContextUpgradeWaitsForOtherActiveConcurrent();
        VerifyEpochUpgradeWaitsForOtherActiveConcurrent();
        VerifyMultipleEpochUpgradesWaitForOtherActiveConcurrent();
        VerifyContextUpgradeWaitsForScopeUpgradeThenDowngradeConcurrent();
        VerifyEpochUpgradeWaitsForScopeUpgradeThenDowngradeConcurrent();
        VerifyRandomScopePaths();
    }

    private static void VerifyConstructorContract()
    {
        //try
        //{
        //    _ = new ConcurrentExclusiveLockScope(null);
        //}
        //catch (ArgumentNullException)
        //{
        //    return;
        //}

        //throw new InvalidOperationException("Scope constructor must reject a null lock.");
    }

    private static void VerifyNormalFinalStates()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent();
        }

        AssertReusable(locker, "Scope Concurrent dispose");

        using (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireExclusive();
        }

        AssertReusable(locker, "Scope Exclusive dispose");

        using (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireExclusive();
            scope.ExclusiveToConcurrent();
        }

        AssertReusable(locker, "Scope ExclusiveToConcurrent dispose");

        using (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithSwitchContextID(ContextId),
                "Single Scope Concurrent holder must upgrade with ContextID.");
        }

        AssertReusable(locker, "Scope TryConcurrentToExclusiveWithSwitchContextID winner dispose");

        using (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithRaiseEpochID(ContextId + 1),
                "Single Scope Concurrent holder must upgrade with raised EpochID.");
        }

        AssertReusable(locker, "Scope TryConcurrentToExclusiveWithRaiseEpochID winner dispose");
    }

    private static void VerifyExceptionFinalStates()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        ExpectInjectedException(() =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            throw new ScopeInjectedException("Concurrent exception");
        });
        AssertReusable(locker, "Scope Concurrent exception");

        ExpectInjectedException(() =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireExclusive();
            throw new ScopeInjectedException("Exclusive exception");
        });
        AssertReusable(locker, "Scope Exclusive exception");

        ExpectInjectedException(() =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireExclusive();
            scope.ExclusiveToConcurrent();
            throw new ScopeInjectedException("Downgraded Concurrent exception");
        });
        AssertReusable(locker, "Scope downgraded Concurrent exception");

        ExpectInjectedException(() =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 1),
                "Single Scope holder must upgrade before injected exception.");
            throw new ScopeInjectedException("Context upgrade exception");
        });
        AssertReusable(locker, "Scope context-upgrade exception");

        ExpectInjectedException(() =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithRaiseEpochID(ContextId + 2),
                "Single Scope holder must raise EpochID before injected exception.");
            throw new ScopeInjectedException("Raise epoch upgrade exception");
        });
        AssertReusable(locker, "Scope raise-epoch-upgrade exception");
    }

    private static void VerifyFailedContextUpgradeIsNotDoubleReleased()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(2);
        int winners = 0;
        int losers = 0;

        for (int i = 0; i < 2; i++)
        {
            threads.Start($"scope-failed-context-upgrade-{i}", () =>
            {
                using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
                scope.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Scope context-upgrade participants did not acquire together.");
                }

                if (scope.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 2))
                {
                    Interlocked.Increment(ref winners);
                }
                else
                {
                    Interlocked.Increment(ref losers);
                    // 失败者的 Concurrent 已由底层释放；Dispose 不能再重复释放。
                }
            });
        }

        threads.JoinAll("Scope failed context-upgrade accounting");
        AdvancedAssert.Equal(1, winners, "Exactly one Scope context-upgrade participant must win.");
        AdvancedAssert.Equal(1, losers, "Exactly one Scope context-upgrade participant must lose.");
        AssertReusable(locker, "Scope failed context-upgrade accounting");
    }

    private static void VerifyContextUpgradeWaitsForOtherActiveConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(2);
        ManualResetEventSlim upgradeAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim upgradeEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseReader = new ManualResetEventSlim(false);
        int activeConcurrentBusiness = 0;

        threads.Start("scope-context-upgrade-waits-reader", () =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            Interlocked.Increment(ref activeConcurrentBusiness);
            if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
            {
                throw new TimeoutException("Scope ContextID upgrade wait participants did not acquire together.");
            }

            AdvancedAssert.Wait(releaseReader, "Scope ContextID held reader was not released.");
            Interlocked.Decrement(ref activeConcurrentBusiness);
        });

        threads.Start("scope-context-upgrade-waits-upgrader", () =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
            {
                throw new TimeoutException("Scope ContextID upgrader did not acquire together.");
            }

            upgradeAttempting.Set();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 400),
                "Scope ContextID upgrader should eventually win.");
            AdvancedAssert.Equal(
                0,
                Volatile.Read(ref activeConcurrentBusiness),
                "Scope ContextID upgrade entered Exclusive while another Concurrent business was still active.");
            upgradeEntered.Set();
        });

        AdvancedAssert.Wait(upgradeAttempting, "Scope ContextID upgrader did not start.");
        AdvancedAssert.RemainsBlocked(
            upgradeEntered,
            "Scope ContextID upgrade entered Exclusive before other Concurrent business released.");
        releaseReader.Set();
        AdvancedAssert.Wait(upgradeEntered, "Scope ContextID upgrade did not enter after other Concurrent business released.");
        threads.JoinAll("Scope ContextID upgrade waits for active Concurrent business");
        AssertReusable(locker, "Scope ContextID upgrade active Concurrent wait");
    }

    private static void VerifyEpochUpgradeWaitsForOtherActiveConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(2);
        ManualResetEventSlim upgradeAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim upgradeEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseReader = new ManualResetEventSlim(false);
        int activeConcurrentBusiness = 0;

        locker.EpochID = ContextId + 500;
        threads.Start("scope-epoch-upgrade-waits-reader", () =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            Interlocked.Increment(ref activeConcurrentBusiness);
            if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
            {
                throw new TimeoutException("Scope EpochID upgrade wait participants did not acquire together.");
            }

            AdvancedAssert.Wait(releaseReader, "Scope EpochID held reader was not released.");
            Interlocked.Decrement(ref activeConcurrentBusiness);
        });

        threads.Start("scope-epoch-upgrade-waits-upgrader", () =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
            {
                throw new TimeoutException("Scope EpochID upgrader did not acquire together.");
            }

            upgradeAttempting.Set();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithRaiseEpochID(ContextId + 501),
                "Scope EpochID upgrader should eventually win.");
            AdvancedAssert.Equal(
                0,
                Volatile.Read(ref activeConcurrentBusiness),
                "Scope EpochID upgrade entered Exclusive while another Concurrent business was still active.");
            upgradeEntered.Set();
        });

        AdvancedAssert.Wait(upgradeAttempting, "Scope EpochID upgrader did not start.");
        AdvancedAssert.RemainsBlocked(
            upgradeEntered,
            "Scope EpochID upgrade entered Exclusive before other Concurrent business released.");
        releaseReader.Set();
        AdvancedAssert.Wait(upgradeEntered, "Scope EpochID upgrade did not enter after other Concurrent business released.");
        threads.JoinAll("Scope EpochID upgrade waits for active Concurrent business");
        locker.EpochID = 0;
        AssertReusable(locker, "Scope EpochID upgrade active Concurrent wait");
    }

    private static void VerifyMultipleEpochUpgradesWaitForOtherActiveConcurrent()
    {
        const int readers = 8;
        const int upgraders = 8;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(readers + upgraders);
        using CountdownEvent upgradersAttempting = new CountdownEvent(upgraders);
        ManualResetEventSlim winnerEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseReaders = new ManualResetEventSlim(false);
        int activeConcurrentBusiness = 0;
        int activeExclusiveBusiness = 0;
        int overlaps = 0;
        int winners = 0;
        int losers = 0;

        locker.EpochID = ContextId + 600;
        for (int i = 0; i < readers; i++)
        {
            int captured = i;
            threads.Start($"scope-multi-epoch-reader-{captured}", () =>
            {
                using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
                scope.AcquireConcurrent();
                Interlocked.Increment(ref activeConcurrentBusiness);
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Scope multi EpochID readers did not acquire together.");
                }

                AdvancedAssert.Wait(releaseReaders, "Scope multi EpochID held reader was not released.");
                Interlocked.Decrement(ref activeConcurrentBusiness);
            });
        }

        for (int i = 0; i < upgraders; i++)
        {
            int captured = i;
            threads.Start($"scope-multi-epoch-upgrader-{captured}", () =>
            {
                using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
                scope.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Scope multi EpochID upgraders did not acquire together.");
                }

                upgradersAttempting.Signal();
                if (scope.TryConcurrentToExclusiveWithRaiseEpochID(ContextId + 601 + captured))
                {
                    Interlocked.Increment(ref winners);
                    int exclusive = Interlocked.Increment(ref activeExclusiveBusiness);
                    int concurrent = Volatile.Read(ref activeConcurrentBusiness);
                    if (exclusive != 1 || concurrent != 0)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    winnerEntered.Set();
                    Thread.Yield();
                    Interlocked.Decrement(ref activeExclusiveBusiness);
                }
                else
                {
                    Interlocked.Increment(ref losers);
                }
            });
        }

        if (!upgradersAttempting.Wait(AdvancedTestTiming.Timeout))
        {
            throw new TimeoutException("Scope multi EpochID upgraders did not start.");
        }

        AdvancedAssert.RemainsBlocked(
            winnerEntered,
            "Scope multi EpochID upgrade entered Exclusive before held Concurrent business released.");
        releaseReaders.Set();
        threads.JoinAll("Scope multi EpochID upgrades wait for active Concurrent business");
        AdvancedAssert.True(Volatile.Read(ref winners) > 0, "At least one Scope multi EpochID upgrader must win.");
        AdvancedAssert.Equal(upgraders, Volatile.Read(ref winners) + Volatile.Read(ref losers), "Every Scope multi EpochID upgrader must win or auto-release.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Scope multi EpochID upgrades overlapped active Concurrent business or each other.");
        locker.EpochID = 0;
        AssertReusable(locker, "Scope multi EpochID upgrade active Concurrent wait");
    }

    private static void VerifyContextUpgradeWaitsForScopeUpgradeThenDowngradeConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim downgradedEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseDowngraded = new ManualResetEventSlim(false);
        ManualResetEventSlim upgraderEntered = new ManualResetEventSlim(false);
        int activeDowngradedConcurrent = 0;

        threads.Start("scope-context-upgrade-then-downgrade-holder", () =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 700),
                "Initial Scope ContextID upgrade should win before downgrade.");
            scope.ExclusiveToConcurrent();
            Interlocked.Increment(ref activeDowngradedConcurrent);
            downgradedEntered.Set();
            AdvancedAssert.Wait(releaseDowngraded, "Scope ContextID upgrade-then-downgrade holder was not released.");
            Interlocked.Decrement(ref activeDowngradedConcurrent);
        });

        threads.Start("scope-context-upgrade-against-upgrade-downgrade", () =>
        {
            AdvancedAssert.Wait(downgradedEntered, "Scope ContextID upgrade-then-downgrade holder did not enter Concurrent.");
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 701),
                "Scope ContextID upgrader should eventually win after upgrade-then-downgrade holder releases.");
            AdvancedAssert.Equal(
                0,
                Volatile.Read(ref activeDowngradedConcurrent),
                "Scope ContextID upgrader entered Exclusive while upgrade-then-downgrade Concurrent was active.");
            upgraderEntered.Set();
        });

        AdvancedAssert.Wait(downgradedEntered, "Scope ContextID upgrade-then-downgrade holder did not start.");
        AdvancedAssert.RemainsBlocked(
            upgraderEntered,
            "Scope ContextID upgrader entered before upgrade-then-downgrade Concurrent released.");
        releaseDowngraded.Set();
        AdvancedAssert.Wait(upgraderEntered, "Scope ContextID upgrader did not enter after upgrade-then-downgrade Concurrent released.");
        threads.JoinAll("Scope ContextID upgrade waits for upgrade-then-downgrade Concurrent");
        AssertReusable(locker, "Scope ContextID upgrade-then-downgrade wait");
    }

    private static void VerifyEpochUpgradeWaitsForScopeUpgradeThenDowngradeConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim downgradedEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseDowngraded = new ManualResetEventSlim(false);
        ManualResetEventSlim upgraderEntered = new ManualResetEventSlim(false);
        int activeDowngradedConcurrent = 0;

        locker.EpochID = ContextId + 800;
        threads.Start("scope-epoch-upgrade-then-downgrade-holder", () =>
        {
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithRaiseEpochID(ContextId + 801),
                "Initial Scope EpochID upgrade should win before downgrade.");
            scope.ExclusiveToConcurrent();
            Interlocked.Increment(ref activeDowngradedConcurrent);
            downgradedEntered.Set();
            AdvancedAssert.Wait(releaseDowngraded, "Scope EpochID upgrade-then-downgrade holder was not released.");
            Interlocked.Decrement(ref activeDowngradedConcurrent);
        });

        threads.Start("scope-epoch-upgrade-against-upgrade-downgrade", () =>
        {
            AdvancedAssert.Wait(downgradedEntered, "Scope EpochID upgrade-then-downgrade holder did not enter Concurrent.");
            using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireConcurrent();
            AdvancedAssert.True(
                scope.TryConcurrentToExclusiveWithRaiseEpochID(ContextId + 802),
                "Scope EpochID upgrader should eventually win after upgrade-then-downgrade holder releases.");
            AdvancedAssert.Equal(
                0,
                Volatile.Read(ref activeDowngradedConcurrent),
                "Scope EpochID upgrader entered Exclusive while upgrade-then-downgrade Concurrent was active.");
            upgraderEntered.Set();
        });

        AdvancedAssert.Wait(downgradedEntered, "Scope EpochID upgrade-then-downgrade holder did not start.");
        AdvancedAssert.RemainsBlocked(
            upgraderEntered,
            "Scope EpochID upgrader entered before upgrade-then-downgrade Concurrent released.");
        releaseDowngraded.Set();
        AdvancedAssert.Wait(upgraderEntered, "Scope EpochID upgrader did not enter after upgrade-then-downgrade Concurrent released.");
        threads.JoinAll("Scope EpochID upgrade waits for upgrade-then-downgrade Concurrent");
        locker.EpochID = 0;
        AssertReusable(locker, "Scope EpochID upgrade-then-downgrade wait");
    }

    private static void VerifyRandomScopePaths()
    {
        const int paths = 2000;
        const int seed = 5357811;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        Random random = new Random(seed);
        int injectedExceptions = 0;
        int normalExits = 0;

        for (int i = 0; i < paths; i++)
        {
            bool inject = random.Next(4) != 0;
            try
            {
                using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
                switch (random.Next(6))
                {
                    case 0:
                        scope.AcquireConcurrent();
                        break;
                    case 1:
                        scope.AcquireExclusive();
                        break;
                    case 2:
                        scope.AcquireExclusive();
                        scope.ExclusiveToConcurrent();
                        break;
                    case 3:
                        scope.AcquireConcurrent();
                        scope.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 100 + i);
                        break;
                    case 4:
                        scope.AcquireConcurrent();
                        scope.TryConcurrentToExclusiveWithRaiseEpochID(ContextId + 200 + i);
                        break;
                    case 5:
                        scope.AcquireExclusive();
                        scope.SwitchContextID(ContextId + 300 + i);
                        break;
                }

                if (inject)
                {
                    injectedExceptions++;
                    throw new ScopeInjectedException("random scope path");
                }

                normalExits++;
            }
            catch (ScopeInjectedException)
            {
            }

            AssertReusable(locker, $"random Scope path {i}");
        }

        Console.WriteLine(
            $"       scope random paths={paths:n0}, injected-exceptions={injectedExceptions:n0}, " +
            $"normal-exits={normalExits:n0}, seed={seed}");
    }

    private static void ExpectInjectedException(Action action)
    {
        try
        {
            action();
        }
        catch (ScopeInjectedException)
        {
            return;
        }

        throw new InvalidOperationException("Expected injected Scope exception was not thrown.");
    }

    private static void AssertReusable(ConcurrentExclusiveLock locker, string operation)
    {
        if (locker.TryAcquireExclusive(preemptConcurrent: false))
        {
            locker.ReleaseExclusive();
            return;
        }

        throw new InvalidOperationException($"{operation}: lock was not reusable after Scope disposal.");
    }

    private sealed class ScopeInjectedException : Exception
    {
        public ScopeInjectedException(string message)
            : base(message)
        {
        }
    }
}
