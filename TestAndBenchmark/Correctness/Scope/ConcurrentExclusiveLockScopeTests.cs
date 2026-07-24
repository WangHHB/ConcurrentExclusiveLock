using IntomicLib;
using TestAndBenchmark.Common.Testing;

namespace TestAndBenchmark.Correctness.Scope;

internal static class ConcurrentExclusiveLockScopeTests
{
    private const int MaxConcurrent = int.MaxValue;
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(2);

    public static IEnumerable<TestCase> GetTests()
    {
        yield return TestCase.Sync("Correctness/Scope", "invalid max concurrent throws", InvalidMaxConcurrentThrows);
        yield return TestCase.Async("Correctness/Scope", "concurrent access allows another entrant", ConcurrentAccessAllowsAnotherEntrant);
        yield return TestCase.Sync("Correctness/Scope", "exclusive access blocks all other access", ExclusiveAccessBlocksAllOtherAccess);
        yield return TestCase.Sync("Correctness/Scope", "non-preemptive exclusive requires idle", NonPreemptiveExclusiveRequiresIdle);
        yield return TestCase.Async("Correctness/Scope", "preemptive exclusive blocks new concurrent entrants", PreemptiveExclusiveBlocksNewConcurrentEntrants);
        yield return TestCase.Sync("Correctness/Scope", "dispose releases concurrent", DisposeReleasesConcurrent);
        yield return TestCase.Sync("Correctness/Scope", "dispose releases exclusive", DisposeReleasesExclusive);
        yield return TestCase.Sync("Correctness/Scope", "manual release is not released again by dispose", ManualReleaseIsNotReleasedAgainByDispose);
        yield return TestCase.Sync("Correctness/Scope", "exception path releases exclusive", ExceptionPathReleasesExclusive);
        yield return TestCase.Sync("Correctness/Scope", "scope can downgrade exclusive to concurrent", ScopeCanDowngradeExclusiveToConcurrent);
        yield return TestCase.Sync("Correctness/Scope", "context upgrade success holds exclusive", ContextUpgradeSuccessHoldsExclusive);
        yield return TestCase.Sync("Correctness/Scope", "context upgrade failure releases concurrent", ContextUpgradeFailureReleasesConcurrent);
        yield return TestCase.Sync("Correctness/Scope", "epoch upgrade success holds exclusive", EpochUpgradeSuccessHoldsExclusive);
        yield return TestCase.Sync("Correctness/Scope", "epoch upgrade failure releases concurrent", EpochUpgradeFailureReleasesConcurrent);
    }

    private static void InvalidMaxConcurrentThrows()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        using var scope = new ConcurrentExclusiveLockScope(locker);

        TestAssert.Throws<ArgumentException>(() => scope.AcquireConcurrent(0));
        TestAssert.Throws<ArgumentException>(() => scope.TryAcquireConcurrent(0));
        TestAssert.Throws<ArgumentException>(() => scope.TryAcquireConcurrent(0, 0));
    }

    private static async Task ConcurrentAccessAllowsAnotherEntrant(CancellationToken cancellationToken)
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        using var scope = new ConcurrentExclusiveLockScope(locker);
        scope.AcquireConcurrent(MaxConcurrent);

        int secondConcurrentId = 0;
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var secondScope = new ConcurrentExclusiveLockScope(locker);
            secondConcurrentId = secondScope.TryAcquireConcurrent(MaxConcurrent);
        }, cancellationToken);

        TestAssert.InRange(secondConcurrentId, 1, MaxConcurrent);
    }

    private static void ExclusiveAccessBlocksAllOtherAccess()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        using var scope = new ConcurrentExclusiveLockScope(locker);
        scope.AcquireExclusive();

        using var competitor = new ConcurrentExclusiveLockScope(locker);
        TestAssert.Equal(0, competitor.TryAcquireConcurrent(0, MaxConcurrent));
        TestAssert.False(competitor.TryAcquireExclusive(false));
        TestAssert.False(competitor.TryAcquireExclusive(0));
    }

    private static void NonPreemptiveExclusiveRequiresIdle()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using (var scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent(MaxConcurrent);
            using var competitor = new ConcurrentExclusiveLockScope(locker);
            TestAssert.False(competitor.TryAcquireExclusive(false));
        }

        using (var competitor = new ConcurrentExclusiveLockScope(locker))
        {
            TestAssert.True(competitor.TryAcquireExclusive(false));
        }
    }

    private static async Task PreemptiveExclusiveBlocksNewConcurrentEntrants(CancellationToken cancellationToken)
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        using var exclusiveEntered = new ManualResetEventSlim(false);
        using var exclusiveMayRelease = new ManualResetEventSlim(false);

        using var concurrentScope = new ConcurrentExclusiveLockScope(locker);
        concurrentScope.AcquireConcurrent(MaxConcurrent);

        Task exclusiveTask = Task.Run(() =>
        {
            using var exclusiveScope = new ConcurrentExclusiveLockScope(locker);
            exclusiveScope.AcquireExclusive();
            exclusiveEntered.Set();
            exclusiveMayRelease.Wait(cancellationToken);
        }, cancellationToken);

        try
        {
            TestAssert.Eventually(
                () => locker.ObservedState == ConcurrentExclusiveLockState.Exclusive || locker.ObservedContention > 0,
                ShortWait,
                "The pending exclusive request was not observed.");

            using (var blockedConcurrent = new ConcurrentExclusiveLockScope(locker))
            {
                TestAssert.Equal(0, blockedConcurrent.TryAcquireConcurrent(0, MaxConcurrent));
            }

            concurrentScope.ReleaseConcurrent();
            TestAssert.True(exclusiveEntered.Wait(ShortWait), "The Exclusive request did not enter after Concurrent holders drained.");

            using (var blockedConcurrent = new ConcurrentExclusiveLockScope(locker))
            {
                TestAssert.Equal(0, blockedConcurrent.TryAcquireConcurrent(0, MaxConcurrent));
            }
        }
        finally
        {
            exclusiveMayRelease.Set();
            await exclusiveTask.WaitAsync(ShortWait);
        }
    }

    private static void DisposeReleasesConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using (var scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent(MaxConcurrent);
            TestAssert.False(locker.TryAcquireExclusive(false));
        }

        TestAssert.True(locker.TryAcquireExclusive(false));
        locker.ReleaseExclusive();
    }

    private static void DisposeReleasesExclusive()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using (var scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireExclusive();
            TestAssert.Equal(0, locker.TryAcquireConcurrent(0, MaxConcurrent));
        }

        int concurrentId = locker.TryAcquireConcurrent(MaxConcurrent);
        TestAssert.InRange(concurrentId, 1, MaxConcurrent);
        locker.ReleaseConcurrent();
    }

    private static void ManualReleaseIsNotReleasedAgainByDispose()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using (var scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent(MaxConcurrent);
            scope.ReleaseConcurrent();
        }

        TestAssert.True(locker.TryAcquireExclusive(false));
        locker.ReleaseExclusive();
    }

    private static void ExceptionPathReleasesExclusive()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        try
        {
            using var scope = new ConcurrentExclusiveLockScope(locker);
            scope.AcquireExclusive();
            throw new InvalidOperationException("expected test exception");
        }
        catch (InvalidOperationException ex) when (ex.Message == "expected test exception")
        {
        }

        TestAssert.True(locker.TryAcquireExclusive(false));
        locker.ReleaseExclusive();
    }

    private static void ScopeCanDowngradeExclusiveToConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using (var scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireExclusive();
            scope.ExclusiveToConcurrent();

            int concurrentId = locker.TryAcquireConcurrent(MaxConcurrent);
            TestAssert.InRange(concurrentId, 1, MaxConcurrent);
            locker.ReleaseConcurrent();
            TestAssert.False(locker.TryAcquireExclusive(false));
        }

        TestAssert.True(locker.TryAcquireExclusive(false));
        locker.ReleaseExclusive();
    }

    private static void ContextUpgradeSuccessHoldsExclusive()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using var scope = new ConcurrentExclusiveLockScope(locker);
        scope.AcquireConcurrent(MaxConcurrent);

        TestAssert.True(scope.TryConcurrentToExclusiveWithSwitchContextID(100));
        TestAssert.Equal(100, scope.ContextID);

        using var competitor = new ConcurrentExclusiveLockScope(locker);
        TestAssert.Equal(0, competitor.TryAcquireConcurrent(0, MaxConcurrent));
    }

    private static void ContextUpgradeFailureReleasesConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        locker.ContextID = 100;

        using (var scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent(MaxConcurrent);
            TestAssert.False(scope.TryConcurrentToExclusiveWithSwitchContextID(100));
        }

        using var competitor = new ConcurrentExclusiveLockScope(locker);
        TestAssert.True(competitor.TryAcquireExclusive(false));
    }

    private static void EpochUpgradeSuccessHoldsExclusive()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        using var scope = new ConcurrentExclusiveLockScope(locker);
        scope.AcquireConcurrent(MaxConcurrent);

        TestAssert.True(scope.TryConcurrentToExclusiveWithRaiseEpochID(3));
        TestAssert.Equal(3, scope.EpochID);

        using var competitor = new ConcurrentExclusiveLockScope(locker);
        TestAssert.Equal(0, competitor.TryAcquireConcurrent(0, MaxConcurrent));
    }

    private static void EpochUpgradeFailureReleasesConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        locker.EpochID = 3;

        using (var scope = new ConcurrentExclusiveLockScope(locker))
        {
            scope.AcquireConcurrent(MaxConcurrent);
            TestAssert.False(scope.TryConcurrentToExclusiveWithRaiseEpochID(3));
        }

        using var competitor = new ConcurrentExclusiveLockScope(locker);
        TestAssert.True(competitor.TryAcquireExclusive(false));
    }
}
