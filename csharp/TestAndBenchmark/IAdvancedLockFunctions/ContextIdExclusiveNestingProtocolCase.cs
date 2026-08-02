using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// Verifies a business-level nesting protocol in which a stable ContextID identifies the same
/// synchronization context, allowing duplicate Exclusive acquisition to be skipped without releasing the outer permission early.
/// </summary>
internal sealed class ContextIdExclusiveNestingProtocolCase : IAdvancedLockCorrectnessCase
{
    private const int OuterContextId = 1239;
    private const int OtherContextId = 5678;

    public string Name => "ContextID safely supports the documented same-context Exclusive nesting protocol";

    public void Run()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim contenderAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim contenderEntered = new ManualResetEventSlim(false);
        bool nestedBodyExecuted = false;
        bool nestedExceptionObserved = false;

        ExecuteContextAwareExclusive(locker, OuterContextId, () =>
        {
            AdvancedAssert.Equal(
                OuterContextId,
                locker.ContextID,
                "Outer Exclusive region did not publish its fixed ContextID.");

            threads.Start("context-id-different-context-contender", () =>
            {
                contenderAttempting.Set();
                ExecuteContextAwareExclusive(locker, OtherContextId, () =>
                {
                    AdvancedAssert.Equal(
                        OtherContextId,
                        locker.ContextID,
                        "Different context did not publish its ContextID after acquiring Exclusive.");
                    contenderEntered.Set();
                });
            });

            AdvancedAssert.Wait(contenderAttempting, "Different ContextID contender did not start.");
            AdvancedAssert.RemainsBlocked(
                contenderEntered,
                "Different ContextID bypassed the outer Exclusive region.");

            try
            {
                ExecuteContextAwareExclusive(locker, OuterContextId, () =>
                {
                    nestedBodyExecuted = true;
                    AdvancedAssert.Equal(
                        OuterContextId,
                        locker.ContextID,
                        "Nested same-context code did not observe the outer ContextID.");
                    throw new InjectedNestedContextException();
                });
            }
            catch (InjectedNestedContextException)
            {
                nestedExceptionObserved = true;
            }

            AdvancedAssert.True(nestedBodyExecuted, "Nested same-context body did not execute.");
            AdvancedAssert.True(nestedExceptionObserved, "Nested same-context exception was not propagated.");
            AdvancedAssert.Equal(
                OuterContextId,
                locker.ContextID,
                "Nested same-context exit cleared the outer ContextID.");
            AdvancedAssert.RemainsBlocked(
                contenderEntered,
                "Nested same-context exit released the outer Exclusive permission.");
        });

        // A different context may enter only after the outer owner clears ContextID and releases Exclusive.
        AdvancedAssert.Wait(
            contenderEntered,
            "Different ContextID did not enter after the outer context released Exclusive.");
        threads.JoinAll(Name);
        AdvancedAssert.Equal(0, locker.ContextID, "ContextID was not cleared after the final owner exited.");

        bool reusable = locker.TryAcquireExclusive(preemptConcurrent: false);
        AdvancedAssert.True(reusable, "Lock was not reusable after ContextID nesting protocol completed.");
        if (reusable)
        {
            locker.ReleaseExclusive();
        }
    }

    private static void ExecuteContextAwareExclusive(
        ConcurrentExclusiveLock locker,
        int contextId,
        Action body)
    {
        bool ownsExclusive = false;
        if (locker.ContextID != contextId)
        {
            locker.AcquireExclusive();
            ownsExclusive = true;
            locker.ContextID = contextId;
        }

        try
        {
            body();
        }
        finally
        {
            if (ownsExclusive)
            {
                // Clear while still holding Exclusive so the next owner cannot observe a stale-context window.
                locker.ContextID = 0;
                locker.ReleaseExclusive();
            }
        }
    }

    private sealed class InjectedNestedContextException : Exception
    {
    }
}
