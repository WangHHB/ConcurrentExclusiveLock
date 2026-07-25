using System;
using System.Collections.Generic;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// 验证 Scope 确实绑定调用方提供的 struct 锁存储，而不是管理按值复制的锁副本。
/// 这是后续自动释放和状态转换测试成立的前置条件。
/// </summary>
internal sealed class ConcurrentExclusiveLockScopeBindingCase : IAdvancedLockCorrectnessCase
{
    public string Name => "ConcurrentExclusiveLockScope binds the original lock storage";

    public void Run()
    {
        List<string> failures = new List<string>();
        VerifyExclusiveVisibility(failures);
        VerifyConcurrentVisibility(failures);
        VerifyContextIdVisibility(failures);

        if (failures.Count != 0)
        {
            throw new InvalidOperationException(string.Join(" ", failures));
        }
    }

    private static void VerifyExclusiveVisibility(List<string> failures)
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
        scope.AcquireExclusive();

        bool originalEntered = locker.TryAcquireExclusive(preemptConcurrent: false);
        if (originalEntered)
        {
            locker.ReleaseExclusive();
            failures.Add("The original lock remained Idle while its scope held Exclusive.");
        }

        scope.Dispose();
        bool enteredAfterDispose = locker.TryAcquireExclusive(preemptConcurrent: false);
        if (enteredAfterDispose)
        {
            locker.ReleaseExclusive();
        }
        else
        {
            failures.Add("The original lock was not available after disposing an Exclusive scope.");
        }
    }

    private static void VerifyConcurrentVisibility(List<string> failures)
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
        scope.AcquireConcurrent();

        bool originalEntered = locker.TryAcquireExclusive(preemptConcurrent: false);
        if (originalEntered)
        {
            locker.ReleaseExclusive();
            failures.Add("The original lock remained Idle while its scope held Concurrent.");
        }

        scope.Dispose();
        bool enteredAfterDispose = locker.TryAcquireExclusive(preemptConcurrent: false);
        if (enteredAfterDispose)
        {
            locker.ReleaseExclusive();
        }
        else
        {
            failures.Add("The original lock was not available after disposing a Concurrent scope.");
        }
    }

    private static void VerifyContextIdVisibility(List<string> failures)
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(locker);
        const int expectedContextId = 0x13572468;

        scope.ContextID = expectedContextId;
        if (locker.ContextID != expectedContextId)
        {
            failures.Add(
                $"Scope ContextID was not written to the original lock. " +
                $"Expected={expectedContextId}, actual={locker.ContextID}.");
        }

        scope.ContextID = 0;
        scope.Dispose();
    }
}
