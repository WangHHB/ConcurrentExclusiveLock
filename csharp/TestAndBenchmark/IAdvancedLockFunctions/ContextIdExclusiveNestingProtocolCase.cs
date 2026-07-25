using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// 验证业务层使用固定 ContextID 识别同一同步调用上下文时，
/// 可以跳过重复 Exclusive 获取，同时不提前释放外层权限。
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

        // 外层先清零 ContextID 再释放 Exclusive 后，不同上下文才允许进入。
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
                // 必须在仍持有 Exclusive 时清除，不能给下一任所有者留下旧上下文窗口。
                locker.ContextID = 0;
                locker.ReleaseExclusive();
            }
        }
    }

    private sealed class InjectedNestedContextException : Exception
    {
    }
}
