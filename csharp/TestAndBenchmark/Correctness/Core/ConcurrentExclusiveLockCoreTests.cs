using IntomicLib;
using TestAndBenchmark.Common.Testing;

namespace TestAndBenchmark.Correctness.Core;

internal static class ConcurrentExclusiveLockCoreTests
{
    public static IEnumerable<TestCase> GetTests()
    {
        yield return TestCase.Sync("Correctness/Core", "created lock starts idle", CreatedLockStartsIdle);
        yield return TestCase.Sync("Correctness/Core", "context id switch reports changes", ContextIdSwitchReportsChanges);
        yield return TestCase.Sync("Correctness/Core", "epoch id only raises forward", EpochIdOnlyRaisesForward);
    }

    private static void CreatedLockStartsIdle()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        TestAssert.Equal(ConcurrentExclusiveLockState.Idle, locker.ObservedState);
        TestAssert.Equal(0, locker.ObservedContention);
        TestAssert.Equal(0, locker.ContextID);
        TestAssert.Equal(0, locker.EpochID);
    }

    private static void ContextIdSwitchReportsChanges()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        TestAssert.True(locker.SwitchContextID(7));
        TestAssert.Equal(7, locker.ContextID);
        TestAssert.False(locker.SwitchContextID(7));
        TestAssert.Equal(7, locker.ContextID);
        TestAssert.True(locker.SwitchContextID(11));
        TestAssert.Equal(11, locker.ContextID);
    }

    private static void EpochIdOnlyRaisesForward()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        TestAssert.True(locker.RaiseEpochID(1));
        TestAssert.Equal(1, locker.EpochID);
        TestAssert.False(locker.RaiseEpochID(1));
        TestAssert.Equal(1, locker.EpochID);
        TestAssert.False(locker.RaiseEpochID(0));
        TestAssert.Equal(1, locker.EpochID);
        TestAssert.True(locker.RaiseEpochID(5));
        TestAssert.Equal(5, locker.EpochID);
    }

}
