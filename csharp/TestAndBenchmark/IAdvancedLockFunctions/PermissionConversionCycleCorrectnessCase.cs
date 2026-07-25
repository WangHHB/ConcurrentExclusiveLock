using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// 验证 Exclusive -> Concurrent -> TryConcurrentToExclusiveWithSwitchContextID 的连续转换不会给普通 Exclusive 插队窗口。
/// </summary>
internal sealed class PermissionConversionCycleCorrectnessCase : IAdvancedLockCorrectnessCase
{
    private const int ContextId = 0x4001;

    public string Name => "Exclusive/Concurrent permission conversion can cycle without an insertion window";

    public void Run()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim ordinaryWriterAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim ordinaryWriterEntered = new ManualResetEventSlim(false);
        int cycleCount = 32;

        locker.AcquireExclusive();
        try
        {
            threads.Start("conversion-cycle-ordinary-exclusive", () =>
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
                "Ordinary Exclusive entered while conversion cycle held Exclusive.");

            for (int cycle = 0; cycle < cycleCount; cycle++)
            {
                locker.ExclusiveToConcurrent();
                AdvancedAssert.RemainsBlocked(
                    ordinaryWriterEntered,
                    $"Queued ordinary Exclusive inserted after ExclusiveToConcurrent cycle {cycle}.");

                bool upgraded = locker.TryConcurrentToExclusiveWithSwitchContextID(ContextId + cycle);
                AdvancedAssert.True(upgraded, $"Single conversion cycle holder failed to upgrade at cycle {cycle}.");
                AdvancedAssert.RemainsBlocked(
                    ordinaryWriterEntered,
                    $"Queued ordinary Exclusive inserted before context-upgrade winner cycle {cycle} released.");
            }
        }
        finally
        {
            locker.ReleaseExclusive();
        }

        AdvancedAssert.Wait(ordinaryWriterEntered, "Ordinary Exclusive did not enter after conversion cycle released.");
        threads.JoinAll(Name);
        AssertReusable(locker);
    }

    private static void AssertReusable(ConcurrentExclusiveLock locker)
    {
        locker.AcquireConcurrent();
        locker.ReleaseConcurrent();
        locker.AcquireExclusive();
        locker.ReleaseExclusive();
    }
}
