using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

internal sealed class ExclusiveToConcurrentCorrectnessCase : IAdvancedLockCorrectnessCase
{
    public string Name => "ExclusiveToConcurrent keeps a continuous Concurrent context";

    public void Run()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim writerAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim writerEntered = new ManualResetEventSlim(false);

        locker.AcquireExclusive();
        try
        {
            threads.Start("exclusive-to-concurrent-writer", () =>
            {
                writerAttempting.Set();
                locker.AcquireExclusive();
                try
                {
                    writerEntered.Set();
                }
                finally
                {
                    locker.ReleaseExclusive();
                }
            });

            AdvancedAssert.Wait(writerAttempting, "Queued Exclusive did not start.");
            AdvancedAssert.RemainsBlocked(writerEntered, "Queued Exclusive entered while caller held Exclusive.");

            locker.ExclusiveToConcurrent();
            AdvancedAssert.RemainsBlocked(
                writerEntered,
                "Queued Exclusive inserted before downgraded Concurrent was released.");

            locker.ReleaseConcurrent();
        }
        finally
        {
            // Do not attempt speculative cleanup here: later reusability assertions detect a missing release,
            // while an extra release could corrupt the lock state.
        }

        AdvancedAssert.Wait(writerEntered, "Queued Exclusive did not enter after downgraded Concurrent released.");
        threads.JoinAll(Name);
        AssertReusable(locker, Name);
    }

    private static void AssertReusable(ConcurrentExclusiveLock locker, string operation)
    {
        locker.AcquireConcurrent();
        locker.ReleaseConcurrent();
        locker.AcquireExclusive();
        locker.ReleaseExclusive();
    }
}

internal sealed class ConcurrentToExclusiveCorrectnessCase : IAdvancedLockCorrectnessCase
{
    private const int ParticipantCount = 8;
    private const int ContextId = 123456;

    public string Name => "ContextID Concurrent-to-Exclusive upgrades are isolated and release failed Concurrent contexts";

    public void Run()
    {
        VerifyExactlyOneContextSwitchWinner();
        VerifyDifferentContextIdsRunIsolated();
        VerifyContextSwitchUpgradeWaitsForOtherActiveConcurrent();
        VerifyRaiseEpochIdRejectsStaleStages();
        VerifyRaiseEpochIdRunsHigherStagesIsolated();
        VerifyRaiseEpochUpgradeWaitsForOtherActiveConcurrent();
        VerifyMultipleRaiseEpochUpgradesWaitForOtherActiveConcurrent();
        VerifyContextUpgradeWaitsForDowngradedConcurrent();
        VerifyEpochUpgradeWaitsForDowngradedConcurrent();
        VerifyContextUpgradeWaitsForUpgradeThenDowngradeConcurrent();
        VerifyEpochUpgradeWaitsForUpgradeThenDowngradeConcurrent();
        VerifyRepeatedContextUpgradeDowngradeCyclesAreIsolated();
        VerifyWinnerHasExclusivePriority();
        VerifyScopeAccounting();
        VerifySwitchContextIdAndRaiseEpochId();
    }

    private static void VerifyExactlyOneContextSwitchWinner()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(ParticipantCount);
        int winners = 0;
        int losers = 0;
        int activeExclusive = 0;

        for (int participantIndex = 0; participantIndex < ParticipantCount; participantIndex++)
        {
            int capturedIndex = participantIndex;
            threads.Start($"try-context-upgrade-{capturedIndex}", () =>
            {
                locker.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("TryConcurrentToExclusive participants did not acquire together.");
                }

                if (locker.TryConcurrentToExclusiveWithSwitchContextID(ContextId))
                {
                    Interlocked.Increment(ref winners);
                    int active = Interlocked.Increment(ref activeExclusive);
                    AdvancedAssert.Equal(1, active, "More than one ContextID upgrade winner executed Exclusive at once.");
                    try
                    {
                        AdvancedAssert.Equal(ContextId, locker.ContextID, "ContextID was not switched by the winner.");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeExclusive);
                        locker.ReleaseExclusive();
                    }
                }
                else
                {
                    // A failed upgrade must already release Concurrent; the loser must not release it again.
                    Interlocked.Increment(ref losers);
                }
            });
        }

        threads.JoinAll("TryConcurrentToExclusiveWithSwitchContextID unique winner");
        AdvancedAssert.Equal(1, Volatile.Read(ref winners), "Exactly one ContextID upgrade participant must win.");
        AdvancedAssert.Equal(
            ParticipantCount - 1,
            Volatile.Read(ref losers),
            "Every non-winner must lose and auto-release Concurrent.");
        AssertReusable(locker, "unique ContextID upgrade");
    }

    private static void VerifyDifferentContextIdsRunIsolated()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(ParticipantCount);
        int winners = 0;
        int losers = 0;
        int activeExclusive = 0;
        int overlaps = 0;

        for (int participantIndex = 0; participantIndex < ParticipantCount; participantIndex++)
        {
            int capturedIndex = participantIndex;
            threads.Start($"try-context-upgrade-distinct-{capturedIndex}", () =>
            {
                locker.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Distinct ContextID upgrade participants did not acquire together.");
                }

                int contextId = ContextId + 100 + capturedIndex;
                if (locker.TryConcurrentToExclusiveWithSwitchContextID(contextId))
                {
                    Interlocked.Increment(ref winners);
                    int active = Interlocked.Increment(ref activeExclusive);
                    if (active != 1)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    try
                    {
                        Thread.Yield();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeExclusive);
                        locker.ReleaseExclusive();
                    }
                }
                else
                {
                    Interlocked.Increment(ref losers);
                }
            });
        }

        threads.JoinAll("TryConcurrentToExclusiveWithSwitchContextID distinct ContextID isolation");
        AdvancedAssert.True(
            Volatile.Read(ref winners) > 0,
            "Distinct ContextID upgrade race must have at least one successful isolated runner.");
        AdvancedAssert.Equal(
            ParticipantCount,
            Volatile.Read(ref winners) + Volatile.Read(ref losers),
            "Every distinct ContextID participant must either run isolated or lose and auto-release Concurrent.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Distinct ContextID successful runners must not overlap.");
        AssertReusable(locker, "distinct ContextID upgrade isolation");
    }

    private static void VerifyContextSwitchUpgradeWaitsForOtherActiveConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(2);
        ManualResetEventSlim upgradeAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim upgradeEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseReader = new ManualResetEventSlim(false);
        int activeConcurrentBusiness = 0;

        threads.Start("context-upgrade-waits-reader", () =>
        {
            locker.AcquireConcurrent();
            try
            {
                Interlocked.Increment(ref activeConcurrentBusiness);
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("ContextID upgrade wait participants did not acquire together.");
                }

                AdvancedAssert.Wait(releaseReader, "ContextID upgrade held reader was not released.");
                Interlocked.Decrement(ref activeConcurrentBusiness);
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        });

        threads.Start("context-upgrade-waits-upgrader", () =>
        {
            locker.AcquireConcurrent();
            if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
            {
                throw new TimeoutException("ContextID upgrade waiter did not acquire together.");
            }

            upgradeAttempting.Set();
            bool won = locker.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 700);
            AdvancedAssert.True(won, "Distinct ContextID upgrader should eventually win.");
            try
            {
                AdvancedAssert.Equal(0, Volatile.Read(ref activeConcurrentBusiness), "ContextID upgrade entered Exclusive while another Concurrent business was still active.");
                upgradeEntered.Set();
            }
            finally
            {
                locker.ReleaseExclusive();
            }
        });

        AdvancedAssert.Wait(upgradeAttempting, "ContextID upgrader did not start.");
        AdvancedAssert.RemainsBlocked(
            upgradeEntered,
            "ContextID upgrade entered Exclusive before other Concurrent business released.");
        releaseReader.Set();
        AdvancedAssert.Wait(upgradeEntered, "ContextID upgrade did not enter after other Concurrent business released.");
        threads.JoinAll("ContextID upgrade waits for active Concurrent business");
        AssertReusable(locker, "ContextID upgrade active Concurrent wait");
    }

    private static void VerifyRaiseEpochIdRejectsStaleStages()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        const int currentEpoch = ContextId + 300;

        locker.EpochID = currentEpoch;

        locker.AcquireConcurrent();
        AdvancedAssert.True(
            !locker.TryConcurrentToExclusiveWithRaiseEpochID(currentEpoch),
            "Equal EpochID must not win.");
        AdvancedAssert.Equal(
            currentEpoch,
            locker.EpochID,
            "Rejected equal stage upgrade must not change EpochID.");

        locker.AcquireConcurrent();
        AdvancedAssert.True(
            !locker.TryConcurrentToExclusiveWithRaiseEpochID(currentEpoch - 1),
            "Lower EpochID must not win.");
        AdvancedAssert.Equal(currentEpoch, locker.EpochID, "Rejected stage upgrades must not change EpochID.");
        locker.EpochID = 0;
        AssertReusable(locker, "stale RaiseEpochID upgrade rejection");
    }

    private static void VerifyRaiseEpochIdRunsHigherStagesIsolated()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(ParticipantCount);
        const int baseEpoch = ContextId + 400;
        int winners = 0;
        int losers = 0;
        int activeExclusive = 0;
        int overlaps = 0;

        locker.EpochID = baseEpoch;
        for (int participantIndex = 0; participantIndex < ParticipantCount; participantIndex++)
        {
            int capturedIndex = participantIndex;
            threads.Start($"raise-epoch-upgrade-{capturedIndex}", () =>
            {
                locker.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Raise EpochID upgrade participants did not acquire together.");
                }

                int requestedEpoch = baseEpoch + 1 + capturedIndex;
                if (locker.TryConcurrentToExclusiveWithRaiseEpochID(requestedEpoch))
                {
                    Interlocked.Increment(ref winners);
                    int active = Interlocked.Increment(ref activeExclusive);
                    if (active != 1)
                    {
                        Interlocked.Increment(ref overlaps);
                    }

                    try
                    {
                        Thread.Yield();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeExclusive);
                        locker.ReleaseExclusive();
                    }
                }
                else
                {
                    Interlocked.Increment(ref losers);
                }
            });
        }

        threads.JoinAll("TryConcurrentToExclusiveWithRaiseEpochID higher stage isolation");
        AdvancedAssert.True(
            Volatile.Read(ref winners) > 0,
            "At least one higher EpochID upgrade must win.");
        AdvancedAssert.Equal(
            ParticipantCount,
            Volatile.Read(ref winners) + Volatile.Read(ref losers),
            "Every higher stage participant must either run isolated or lose and auto-release Concurrent.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Higher EpochID successful runners must not overlap.");
        AdvancedAssert.True(
            locker.EpochID > baseEpoch,
            "Higher EpochID upgrades must advance EpochID.");
        locker.EpochID = 0;
        AssertReusable(locker, "higher stage RaiseEpochID upgrade isolation");
    }

    private static void VerifyRaiseEpochUpgradeWaitsForOtherActiveConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(2);
        ManualResetEventSlim upgradeAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim upgradeEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseReader = new ManualResetEventSlim(false);
        const int baseEpoch = ContextId + 800;
        int activeConcurrentBusiness = 0;

        locker.EpochID = baseEpoch;
        threads.Start("epoch-upgrade-waits-reader", () =>
        {
            locker.AcquireConcurrent();
            try
            {
                Interlocked.Increment(ref activeConcurrentBusiness);
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("EpochID upgrade wait participants did not acquire together.");
                }

                AdvancedAssert.Wait(releaseReader, "EpochID upgrade held reader was not released.");
                Interlocked.Decrement(ref activeConcurrentBusiness);
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        });

        threads.Start("epoch-upgrade-waits-upgrader", () =>
        {
            locker.AcquireConcurrent();
            if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
            {
                throw new TimeoutException("EpochID upgrade waiter did not acquire together.");
            }

            upgradeAttempting.Set();
            bool won = locker.TryConcurrentToExclusiveWithRaiseEpochID(baseEpoch + 1);
            AdvancedAssert.True(won, "Higher EpochID upgrader should eventually win.");
            try
            {
                AdvancedAssert.Equal(0, Volatile.Read(ref activeConcurrentBusiness), "EpochID upgrade entered Exclusive while another Concurrent business was still active.");
                upgradeEntered.Set();
            }
            finally
            {
                locker.ReleaseExclusive();
            }
        });

        AdvancedAssert.Wait(upgradeAttempting, "EpochID upgrader did not start.");
        AdvancedAssert.RemainsBlocked(
            upgradeEntered,
            "EpochID upgrade entered Exclusive before other Concurrent business released.");
        releaseReader.Set();
        AdvancedAssert.Wait(upgradeEntered, "EpochID upgrade did not enter after other Concurrent business released.");
        threads.JoinAll("EpochID upgrade waits for active Concurrent business");
        locker.EpochID = 0;
        AssertReusable(locker, "EpochID upgrade active Concurrent wait");
    }

    private static void VerifyMultipleRaiseEpochUpgradesWaitForOtherActiveConcurrent()
    {
        const int readers = 4;
        const int upgraders = 4;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier acquired = new Barrier(readers + upgraders);
        using CountdownEvent upgradersAttempting = new CountdownEvent(upgraders);
        ManualResetEventSlim winnerEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseReaders = new ManualResetEventSlim(false);
        const int baseEpoch = ContextId + 900;
        int activeConcurrentBusiness = 0;
        int activeExclusiveBusiness = 0;
        int overlaps = 0;
        int winners = 0;
        int losers = 0;

        locker.EpochID = baseEpoch;
        for (int i = 0; i < readers; i++)
        {
            int captured = i;
            threads.Start($"multi-epoch-upgrade-reader-{captured}", () =>
            {
                locker.AcquireConcurrent();
                try
                {
                    Interlocked.Increment(ref activeConcurrentBusiness);
                    if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                    {
                        throw new TimeoutException("Multi EpochID reader participants did not acquire together.");
                    }

                    AdvancedAssert.Wait(releaseReaders, "Multi EpochID held reader was not released.");
                    Interlocked.Decrement(ref activeConcurrentBusiness);
                }
                finally
                {
                    locker.ReleaseConcurrent();
                }
            });
        }

        for (int i = 0; i < upgraders; i++)
        {
            int captured = i;
            threads.Start($"multi-epoch-upgrade-upgrader-{captured}", () =>
            {
                locker.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Multi EpochID upgrader participants did not acquire together.");
                }

                upgradersAttempting.Signal();
                bool won = locker.TryConcurrentToExclusiveWithRaiseEpochID(baseEpoch + 1 + captured);
                if (won)
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
                    locker.ReleaseExclusive();
                }
                else
                {
                    Interlocked.Increment(ref losers);
                }
            });
        }

        if (!upgradersAttempting.Wait(AdvancedTestTiming.Timeout))
        {
            throw new TimeoutException("Multi EpochID upgraders did not start.");
        }

        AdvancedAssert.RemainsBlocked(
            winnerEntered,
            "Multi EpochID upgrade entered Exclusive before held Concurrent business released.");
        releaseReaders.Set();
        threads.JoinAll("Multi EpochID upgrades wait for active Concurrent business");
        AdvancedAssert.True(Volatile.Read(ref winners) > 0, "At least one multi EpochID upgrader must win.");
        AdvancedAssert.Equal(upgraders, Volatile.Read(ref winners) + Volatile.Read(ref losers), "Every multi EpochID upgrader must win or auto-release.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Multi EpochID upgrades overlapped active Concurrent business or each other.");
        locker.EpochID = 0;
        AssertReusable(locker, "multi EpochID upgrade active Concurrent wait");
    }

    private static void VerifyContextUpgradeWaitsForDowngradedConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier concurrentReady = new Barrier(2);
        ManualResetEventSlim upgradeAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim upgradeEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseDowngraded = new ManualResetEventSlim(false);
        int activeDowngradedConcurrent = 0;

        threads.Start("context-upgrade-downgraded-reader", () =>
        {
            locker.AcquireExclusive();
            locker.ExclusiveToConcurrent();
            try
            {
                Interlocked.Increment(ref activeDowngradedConcurrent);
                if (!concurrentReady.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("ContextID downgraded Concurrent participants did not align.");
                }

                AdvancedAssert.Wait(releaseDowngraded, "ContextID downgraded Concurrent was not released.");
                Interlocked.Decrement(ref activeDowngradedConcurrent);
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        });

        threads.Start("context-upgrade-against-downgrade", () =>
        {
            locker.AcquireConcurrent();
            if (!concurrentReady.SignalAndWait(AdvancedTestTiming.Timeout))
            {
                throw new TimeoutException("ContextID upgrader did not align with downgraded Concurrent.");
            }

            upgradeAttempting.Set();
            bool won = locker.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 1000);
            AdvancedAssert.True(won, "ContextID upgrader should eventually win against downgraded Concurrent.");
            try
            {
                AdvancedAssert.Equal(0, Volatile.Read(ref activeDowngradedConcurrent), "ContextID upgrader entered Exclusive while downgraded Concurrent business was still active.");
                upgradeEntered.Set();
            }
            finally
            {
                locker.ReleaseExclusive();
            }
        });

        AdvancedAssert.Wait(upgradeAttempting, "ContextID upgrader against downgraded Concurrent did not start.");
        AdvancedAssert.RemainsBlocked(
            upgradeEntered,
            "ContextID upgrader entered before downgraded Concurrent business released.");
        releaseDowngraded.Set();
        AdvancedAssert.Wait(upgradeEntered, "ContextID upgrader did not enter after downgraded Concurrent released.");
        threads.JoinAll("ContextID upgrade waits for downgraded Concurrent");
        AssertReusable(locker, "ContextID upgrade downgraded Concurrent wait");
    }

    private static void VerifyEpochUpgradeWaitsForDowngradedConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier concurrentReady = new Barrier(2);
        ManualResetEventSlim upgradeAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim upgradeEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseDowngraded = new ManualResetEventSlim(false);
        const int baseEpoch = ContextId + 1100;
        int activeDowngradedConcurrent = 0;

        locker.EpochID = baseEpoch;
        threads.Start("epoch-upgrade-downgraded-reader", () =>
        {
            locker.AcquireExclusive();
            locker.ExclusiveToConcurrent();
            try
            {
                Interlocked.Increment(ref activeDowngradedConcurrent);
                if (!concurrentReady.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("EpochID downgraded Concurrent participants did not align.");
                }

                AdvancedAssert.Wait(releaseDowngraded, "EpochID downgraded Concurrent was not released.");
                Interlocked.Decrement(ref activeDowngradedConcurrent);
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        });

        threads.Start("epoch-upgrade-against-downgrade", () =>
        {
            locker.AcquireConcurrent();
            if (!concurrentReady.SignalAndWait(AdvancedTestTiming.Timeout))
            {
                throw new TimeoutException("EpochID upgrader did not align with downgraded Concurrent.");
            }

            upgradeAttempting.Set();
            bool won = locker.TryConcurrentToExclusiveWithRaiseEpochID(baseEpoch + 1);
            AdvancedAssert.True(won, "EpochID upgrader should eventually win against downgraded Concurrent.");
            try
            {
                AdvancedAssert.Equal(0, Volatile.Read(ref activeDowngradedConcurrent), "EpochID upgrader entered Exclusive while downgraded Concurrent business was still active.");
                upgradeEntered.Set();
            }
            finally
            {
                locker.ReleaseExclusive();
            }
        });

        AdvancedAssert.Wait(upgradeAttempting, "EpochID upgrader against downgraded Concurrent did not start.");
        AdvancedAssert.RemainsBlocked(
            upgradeEntered,
            "EpochID upgrader entered before downgraded Concurrent business released.");
        releaseDowngraded.Set();
        AdvancedAssert.Wait(upgradeEntered, "EpochID upgrader did not enter after downgraded Concurrent released.");
        threads.JoinAll("EpochID upgrade waits for downgraded Concurrent");
        locker.EpochID = 0;
        AssertReusable(locker, "EpochID upgrade downgraded Concurrent wait");
    }

    private static void VerifyContextUpgradeWaitsForUpgradeThenDowngradeConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim downgradedEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseDowngraded = new ManualResetEventSlim(false);
        ManualResetEventSlim upgraderEntered = new ManualResetEventSlim(false);
        int activeDowngradedConcurrent = 0;

        threads.Start("context-upgrade-then-downgrade-holder", () =>
        {
            locker.AcquireConcurrent();
            AdvancedAssert.True(
                locker.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 1200),
                "Initial ContextID upgrade should win before downgrade.");
            locker.ExclusiveToConcurrent();
            try
            {
                Interlocked.Increment(ref activeDowngradedConcurrent);
                downgradedEntered.Set();
                AdvancedAssert.Wait(releaseDowngraded, "ContextID upgrade-then-downgrade holder was not released.");
                Interlocked.Decrement(ref activeDowngradedConcurrent);
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        });

        threads.Start("context-upgrade-against-upgrade-downgrade", () =>
        {
            AdvancedAssert.Wait(downgradedEntered, "ContextID upgrade-then-downgrade holder did not enter Concurrent.");
            locker.AcquireConcurrent();
            bool won = locker.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 1201);
            AdvancedAssert.True(won, "ContextID upgrader should eventually win after upgrade-then-downgrade holder releases.");
            try
            {
                AdvancedAssert.Equal(0, Volatile.Read(ref activeDowngradedConcurrent), "ContextID upgrader entered Exclusive while upgrade-then-downgrade Concurrent was active.");
                upgraderEntered.Set();
            }
            finally
            {
                locker.ReleaseExclusive();
            }
        });

        AdvancedAssert.Wait(downgradedEntered, "ContextID upgrade-then-downgrade holder did not start.");
        AdvancedAssert.RemainsBlocked(
            upgraderEntered,
            "ContextID upgrader entered before upgrade-then-downgrade Concurrent released.");
        releaseDowngraded.Set();
        AdvancedAssert.Wait(upgraderEntered, "ContextID upgrader did not enter after upgrade-then-downgrade Concurrent released.");
        threads.JoinAll("ContextID upgrade waits for upgrade-then-downgrade Concurrent");
        AssertReusable(locker, "ContextID upgrade-then-downgrade wait");
    }

    private static void VerifyEpochUpgradeWaitsForUpgradeThenDowngradeConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim downgradedEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseDowngraded = new ManualResetEventSlim(false);
        ManualResetEventSlim upgraderEntered = new ManualResetEventSlim(false);
        const int baseEpoch = ContextId + 1300;
        int activeDowngradedConcurrent = 0;

        locker.EpochID = baseEpoch;
        threads.Start("epoch-upgrade-then-downgrade-holder", () =>
        {
            locker.AcquireConcurrent();
            AdvancedAssert.True(
                locker.TryConcurrentToExclusiveWithRaiseEpochID(baseEpoch + 1),
                "Initial EpochID upgrade should win before downgrade.");
            locker.ExclusiveToConcurrent();
            try
            {
                Interlocked.Increment(ref activeDowngradedConcurrent);
                downgradedEntered.Set();
                AdvancedAssert.Wait(releaseDowngraded, "EpochID upgrade-then-downgrade holder was not released.");
                Interlocked.Decrement(ref activeDowngradedConcurrent);
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        });

        threads.Start("epoch-upgrade-against-upgrade-downgrade", () =>
        {
            AdvancedAssert.Wait(downgradedEntered, "EpochID upgrade-then-downgrade holder did not enter Concurrent.");
            locker.AcquireConcurrent();
            bool won = locker.TryConcurrentToExclusiveWithRaiseEpochID(baseEpoch + 2);
            AdvancedAssert.True(won, "EpochID upgrader should eventually win after upgrade-then-downgrade holder releases.");
            try
            {
                AdvancedAssert.Equal(0, Volatile.Read(ref activeDowngradedConcurrent), "EpochID upgrader entered Exclusive while upgrade-then-downgrade Concurrent was active.");
                upgraderEntered.Set();
            }
            finally
            {
                locker.ReleaseExclusive();
            }
        });

        AdvancedAssert.Wait(downgradedEntered, "EpochID upgrade-then-downgrade holder did not start.");
        AdvancedAssert.RemainsBlocked(
            upgraderEntered,
            "EpochID upgrader entered before upgrade-then-downgrade Concurrent released.");
        releaseDowngraded.Set();
        AdvancedAssert.Wait(upgraderEntered, "EpochID upgrader did not enter after upgrade-then-downgrade Concurrent released.");
        threads.JoinAll("EpochID upgrade waits for upgrade-then-downgrade Concurrent");
        locker.EpochID = 0;
        AssertReusable(locker, "EpochID upgrade-then-downgrade wait");
    }

    private static void VerifyRepeatedContextUpgradeDowngradeCyclesAreIsolated()
    {
        const int participants = 8;
        const int cycles = 2000;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        CountdownEvent ready = new CountdownEvent(participants);
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        int nextContextId = ContextId + 2000;
        int activeConcurrent = 0;
        int activeExclusive = 0;
        int exclusiveConcurrentOverlaps = 0;
        int exclusiveExclusiveOverlaps = 0;
        int downgradedConcurrentOverlaps = 0;
        int winners = 0;
        int losers = 0;

        for (int participant = 0; participant < participants; participant++)
        {
            int captured = participant;
            threads.Start($"repeated-context-cycle-{captured}", () =>
            {
                int heldAccess = 0;
                locker.AcquireConcurrent();
                heldAccess = 1;
                ready.Signal();
                start.Wait();

                try
                {
                    for (int cycle = 0; cycle < cycles; cycle++)
                    {
                        bool won = locker.TryConcurrentToExclusiveWithSwitchContextID(
                            Interlocked.Increment(ref nextContextId));
                        if (won)
                        {
                            heldAccess = 2;
                            Interlocked.Increment(ref winners);

                            int exclusive = Interlocked.Increment(ref activeExclusive);
                            int concurrent = Volatile.Read(ref activeConcurrent);
                            if (exclusive != 1)
                            {
                                Interlocked.Increment(ref exclusiveExclusiveOverlaps);
                            }

                            if (concurrent != 0)
                            {
                                Interlocked.Increment(ref exclusiveConcurrentOverlaps);
                            }

                            Thread.Yield();
                            Interlocked.Decrement(ref activeExclusive);

                            locker.ExclusiveToConcurrent();
                            heldAccess = 1;

                            concurrent = Interlocked.Increment(ref activeConcurrent);
                            exclusive = Volatile.Read(ref activeExclusive);
                            if (exclusive != 0)
                            {
                                Interlocked.Increment(ref downgradedConcurrentOverlaps);
                            }

                            Thread.Yield();
                            Interlocked.Decrement(ref activeConcurrent);
                        }
                        else
                        {
                            heldAccess = 0;
                            Interlocked.Increment(ref losers);
                            locker.AcquireConcurrent();
                            heldAccess = 1;
                        }
                    }
                }
                finally
                {
                    if (heldAccess == 1)
                    {
                        locker.ReleaseConcurrent();
                    }
                    else if (heldAccess == 2)
                    {
                        locker.ReleaseExclusive();
                    }
                }
            });
        }

        AdvancedAssert.Wait(ready, "Repeated ContextID cycle participants did not become ready.");
        start.Set();
        threads.JoinAll("Repeated ContextID upgrade/downgrade cycles");
        AdvancedAssert.True(Volatile.Read(ref winners) > 0, "Repeated ContextID cycles must have successful upgrades.");
        AdvancedAssert.True(
            Volatile.Read(ref exclusiveConcurrentOverlaps) == 0 &&
            Volatile.Read(ref exclusiveExclusiveOverlaps) == 0 &&
            Volatile.Read(ref downgradedConcurrentOverlaps) == 0,
            "Repeated ContextID upgrade/downgrade cycles overlapped access. " +
            $"exclusive-vs-concurrent={Volatile.Read(ref exclusiveConcurrentOverlaps)}, " +
            $"exclusive-vs-exclusive={Volatile.Read(ref exclusiveExclusiveOverlaps)}, " +
            $"downgraded-concurrent-vs-exclusive={Volatile.Read(ref downgradedConcurrentOverlaps)}.");
        _ = Volatile.Read(ref losers);
        AssertReusable(locker, "repeated ContextID upgrade/downgrade cycles");
    }

    private static void VerifyWinnerHasExclusivePriority()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim ordinaryWriterAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim ordinaryWriterEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseWinner = new ManualResetEventSlim(false);
        int winnerEntered = 0;

        locker.AcquireConcurrent();
        try
        {
            threads.Start("ordinary-exclusive-behind-context-upgrade", () =>
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
                "Ordinary Exclusive entered before current Concurrent released.");

            bool won = locker.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 1);
            AdvancedAssert.True(won, "Single Concurrent holder must win ContextID upgrade.");
            Interlocked.Increment(ref winnerEntered);
            AdvancedAssert.RemainsBlocked(
                ordinaryWriterEntered,
                "Ordinary Exclusive inserted before ContextID upgrade winner.");
            releaseWinner.Set();
            locker.ReleaseExclusive();
        }
        finally
        {
            releaseWinner.Set();
        }

        AdvancedAssert.Equal(1, Volatile.Read(ref winnerEntered), "ContextID upgrade winner did not execute.");
        AdvancedAssert.Wait(ordinaryWriterEntered, "Ordinary Exclusive did not enter after ContextID upgrade winner released.");
        threads.JoinAll("ContextID upgrade priority");
        AssertReusable(locker, "ContextID upgrade priority");
    }

    private static void VerifyScopeAccounting()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        int winners = 0;
        int losers = 0;

        using Barrier acquired = new Barrier(ParticipantCount);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        for (int participantIndex = 0; participantIndex < ParticipantCount; participantIndex++)
        {
            int capturedIndex = participantIndex;
            threads.Start($"scope-context-upgrade-{capturedIndex}", () =>
            {
                using ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
                scope.AcquireConcurrent();
                if (!acquired.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Scope ContextID upgrade participants did not acquire together.");
                }

                if (scope.TryConcurrentToExclusiveWithSwitchContextID(ContextId + 2))
                {
                    Interlocked.Increment(ref winners);
                    // Dispose must release the winner's Exclusive permission.
                }
                else
                {
                    Interlocked.Increment(ref losers);
                    // Dispose must not re-release the loser's already released Concurrent permission.
                }
            });
        }

        threads.JoinAll("Scope ContextID upgrade accounting");
        AdvancedAssert.Equal(1, Volatile.Read(ref winners), "Scope ContextID upgrade must have one winner.");
        AdvancedAssert.Equal(ParticipantCount - 1, Volatile.Read(ref losers), "Scope ContextID upgrade losers mismatch.");
        AssertReusable(locker, "Scope ContextID upgrade accounting");
    }

    private static void VerifySwitchContextIdAndRaiseEpochId()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedAssert.True(locker.SwitchContextID(10), "SwitchContextID must report a changed value.");
        AdvancedAssert.True(!locker.SwitchContextID(10), "SwitchContextID must report false when value is unchanged.");
        AdvancedAssert.True(locker.RaiseEpochID(11), "RaiseEpochID must raise a smaller value.");
        AdvancedAssert.True(!locker.RaiseEpochID(10), "RaiseEpochID must reject a smaller or equal value.");
        AdvancedAssert.True(locker.SwitchContextID(0), "SwitchContextID must clear a non-zero value.");
        locker.EpochID = 0;
    }

    private static void AssertReusable(ConcurrentExclusiveLock locker, string operation)
    {
        locker.AcquireConcurrent();
        locker.ReleaseConcurrent();
        locker.AcquireExclusive();
        locker.ReleaseExclusive();
    }
}
