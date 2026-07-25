using System;
using System.Diagnostics;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>覆盖 Concurrent 获取重载、ID 上限、容量、立即失败、超时和无限等待。</summary>
internal sealed class ConcurrentAcquireFullSemanticCase : IAdvancedLockCorrectnessCase
{
    public string Name => "Concurrent acquire IDs, limits, immediate attempts, timeouts, and release semantics";

    public void Run()
    {
        VerifyIdleAcquisitionAndBounds();
        VerifyMaximumConcurrentCapacity();
        VerifyExclusiveBlockingAndInfiniteTimeout();
        VerifyInvalidMaximumContracts();
    }

    private static void VerifyIdleAcquisitionAndBounds()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        int acquired = locker.AcquireConcurrent();
        AdvancedAssert.True(acquired != 0, "AcquireConcurrent default returned the reserved failure ID 0.");
        locker.ReleaseConcurrent();

        int limited = locker.AcquireConcurrent(maxConcurrent: 7);
        AssertConcurrentId(limited, 7, "AcquireConcurrent(maxConcurrent)");
        locker.ReleaseConcurrent();

        int immediate = locker.TryAcquireConcurrent(maxConcurrent: 9);
        AssertConcurrentId(immediate, 9, "TryAcquireConcurrent(maxConcurrent)");
        locker.ReleaseConcurrent();

        int timedImmediate = locker.TryAcquireConcurrent(millisecondsTimeout: 0, maxConcurrent: 11);
        AssertConcurrentId(timedImmediate, 11, "TryAcquireConcurrent(0, maxConcurrent)");
        locker.ReleaseConcurrent();
    }

    private static void VerifyMaximumConcurrentCapacity()
    {
        const int capacity = 4;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        CountdownEvent holdersAcquired = new CountdownEvent(capacity);
        ManualResetEventSlim[] releaseHolders = new ManualResetEventSlim[capacity];
        int[] holderIds = new int[capacity];

        for (int holderIndex = 0; holderIndex < capacity; holderIndex++)
        {
            releaseHolders[holderIndex] = new ManualResetEventSlim(false);
            int capturedIndex = holderIndex;
            threads.Start($"stage5-concurrent-capacity-holder-{capturedIndex}", () =>
            {
                int id = locker.AcquireConcurrent(maxConcurrent: capacity);
                holderIds[capturedIndex] = id;
                holdersAcquired.Signal();
                try
                {
                    AdvancedAssert.Wait(
                        releaseHolders[capturedIndex],
                        $"Concurrent capacity holder {capturedIndex} was not released.");
                }
                finally
                {
                    locker.ReleaseConcurrent();
                }
            });
        }

        AdvancedAssert.Wait(holdersAcquired, "Concurrent capacity was not filled by four holders.");
        for (int first = 0; first < capacity; first++)
        {
            AssertConcurrentId(holderIds[first], capacity, $"Concurrent holder {first}");
            for (int second = first + 1; second < capacity; second++)
            {
                AdvancedAssert.True(
                    holderIds[first] != holderIds[second],
                    $"Active Concurrent holders {first} and {second} received the same ID {holderIds[first]}.");
            }
        }

        int immediateResult = int.MaxValue;
        int zeroTimeoutResult = int.MaxValue;
        int timedResult = int.MaxValue;
        TimeSpan timedElapsed = TimeSpan.Zero;
        int waitingId = 0;
        ManualResetEventSlim probesCompleted = new ManualResetEventSlim(false);
        ManualResetEventSlim waitingStarted = new ManualResetEventSlim(false);
        ManualResetEventSlim waitingEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseWaiting = new ManualResetEventSlim(false);

        threads.Start("stage5-concurrent-capacity-waiter", () =>
        {
            immediateResult = locker.TryAcquireConcurrent(maxConcurrent: capacity);
            zeroTimeoutResult = locker.TryAcquireConcurrent(millisecondsTimeout: 0, maxConcurrent: capacity);
            Stopwatch stopwatch = Stopwatch.StartNew();
            timedResult = locker.TryAcquireConcurrent(millisecondsTimeout: 50, maxConcurrent: capacity);
            stopwatch.Stop();
            timedElapsed = stopwatch.Elapsed;
            probesCompleted.Set();

            waitingStarted.Set();
            waitingId = locker.AcquireConcurrent(maxConcurrent: capacity);
            try
            {
                waitingEntered.Set();
                AdvancedAssert.Wait(releaseWaiting, "Capacity waiter was not released.");
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        });

        AdvancedAssert.Wait(probesCompleted, "Concurrent capacity probes did not finish.");
        AdvancedAssert.True(immediateResult == 0, "TryAcquireConcurrent succeeded after maxConcurrent IDs were exhausted.");
        AdvancedAssert.True(zeroTimeoutResult == 0, "TryAcquireConcurrent(0) succeeded after maxConcurrent IDs were exhausted.");
        AdvancedAssert.True(timedResult == 0, "Timed TryAcquireConcurrent succeeded after maxConcurrent IDs were exhausted.");
        AdvancedAssert.True(
            timedElapsed >= TimeSpan.FromMilliseconds(20),
            $"Timed Concurrent attempt returned too early: {timedElapsed.TotalMilliseconds:0.0} ms.");
        AdvancedAssert.Wait(waitingStarted, "Blocking capacity waiter did not start.");
        AdvancedAssert.RemainsBlocked(
            waitingEntered,
            "AcquireConcurrent entered while every allowed Concurrent ID was still occupied.");

        releaseHolders[0].Set();
        AdvancedAssert.Wait(waitingEntered, "Capacity waiter did not enter after one Concurrent ID was released.");
        AssertConcurrentId(waitingId, capacity, "Capacity waiter");
        releaseWaiting.Set();
        for (int holderIndex = 1; holderIndex < capacity; holderIndex++)
        {
            releaseHolders[holderIndex].Set();
        }

        threads.JoinAll("Concurrent maxConcurrent capacity");
    }

    private static void VerifyExclusiveBlockingAndInfiniteTimeout()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        int immediateResult = int.MaxValue;
        int timedResult = int.MaxValue;
        int infiniteResult = 0;
        ManualResetEventSlim timedCompleted = new ManualResetEventSlim(false);
        ManualResetEventSlim infiniteStarted = new ManualResetEventSlim(false);
        ManualResetEventSlim infiniteEntered = new ManualResetEventSlim(false);

        locker.AcquireExclusive();
        threads.Start("stage5-concurrent-blocked-by-exclusive", () =>
        {
            immediateResult = locker.TryAcquireConcurrent();
            timedResult = locker.TryAcquireConcurrent(millisecondsTimeout: 50);
            timedCompleted.Set();
            infiniteStarted.Set();
            infiniteResult = locker.TryAcquireConcurrent(millisecondsTimeout: -1);
            try
            {
                infiniteEntered.Set();
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        });

        AdvancedAssert.Wait(timedCompleted, "Concurrent immediate/timed probes did not finish under Exclusive.");
        AdvancedAssert.True(immediateResult == 0, "Immediate Concurrent attempt entered under Exclusive.");
        AdvancedAssert.True(timedResult == 0, "Timed Concurrent attempt entered under Exclusive.");
        AdvancedAssert.Wait(infiniteStarted, "Infinite Concurrent attempt did not start.");
        AdvancedAssert.RemainsBlocked(infiniteEntered, "Infinite Concurrent attempt returned before Exclusive released.");

        locker.ReleaseExclusive();
        AdvancedAssert.Wait(infiniteEntered, "Infinite Concurrent attempt did not enter after Exclusive released.");
        AdvancedAssert.True(infiniteResult != 0, "Infinite Concurrent attempt returned the failure ID after entering.");
        threads.JoinAll("Concurrent attempts blocked by Exclusive");
    }

    private static void VerifyInvalidMaximumContracts()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        int[] invalidMaximums = { 0, -1 };
        foreach (int invalidMaximum in invalidMaximums)
        {
            ExpectArgumentException(
                () => locker.AcquireConcurrent(maxConcurrent: invalidMaximum),
                $"AcquireConcurrent(maxConcurrent={invalidMaximum})");
            ExpectArgumentException(
                () => locker.TryAcquireConcurrent(maxConcurrent: invalidMaximum),
                $"TryAcquireConcurrent(maxConcurrent={invalidMaximum})");
            ExpectArgumentException(
                () => locker.TryAcquireConcurrent(millisecondsTimeout: 0, maxConcurrent: invalidMaximum),
                $"TryAcquireConcurrent(0, maxConcurrent={invalidMaximum})");
            ExpectArgumentException(
                () => locker.TryAcquireConcurrent(millisecondsTimeout: 50, maxConcurrent: invalidMaximum),
                $"TryAcquireConcurrent(50, maxConcurrent={invalidMaximum})");
            ExpectArgumentException(
                () => locker.TryAcquireConcurrent(millisecondsTimeout: -1, maxConcurrent: invalidMaximum),
                $"TryAcquireConcurrent(-1, maxConcurrent={invalidMaximum})");
        }
    }

    private static void ExpectArgumentException(Action operation, string operationName)
    {
        try
        {
            operation();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{operationName} must reject maxConcurrent < 1 with ArgumentException.");
    }

    private static void AssertConcurrentId(int id, int maximum, string operation)
    {
        AdvancedAssert.True(
            id >= 1 && id <= maximum,
            $"{operation} returned Concurrent ID {id}, expected range [1, {maximum}].");
    }
}

/// <summary>覆盖 Exclusive 立即、抢占、非抢占、超时、无限等待和释放协议。</summary>
internal sealed class ExclusiveAcquireFullSemanticCase : IAdvancedLockCorrectnessCase
{
    public string Name => "Exclusive acquire preemption, non-preemption, immediate attempts, timeouts, and release semantics";

    public void Run()
    {
        VerifyIdleAttempts();
        VerifyNonPreemptiveFailureKeepsConcurrentOpen();
        VerifyPreemptiveTryBlocksNewConcurrent();
        VerifyBlockingAcquireExclusive();
        VerifyTimedFailureCleanup();
        VerifyZeroTimeoutAgainstExistingExclusive();
        VerifyInfiniteTimedAcquire();
    }

    private static void VerifyIdleAttempts()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

        AdvancedAssert.True(locker.TryAcquireExclusive(preemptConcurrent: false), "Non-preemptive Exclusive failed while Idle.");
        locker.ReleaseExclusive();

        AdvancedAssert.True(locker.TryAcquireExclusive(preemptConcurrent: true), "Preemptive Exclusive failed while Idle.");
        locker.ReleaseExclusive();

        AdvancedAssert.True(locker.TryAcquireExclusive(millisecondsTimeout: 0), "Timed Exclusive(0) failed while Idle.");
        locker.ReleaseExclusive();
    }

    private static void VerifyNonPreemptiveFailureKeepsConcurrentOpen()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        bool exclusiveResult = true;
        int secondReaderId = 0;

        locker.AcquireConcurrent();
        threads.Start("stage5-exclusive-nonpreemptive", () =>
        {
            exclusiveResult = locker.TryAcquireExclusive(preemptConcurrent: false);
            secondReaderId = locker.TryAcquireConcurrent();
            if (secondReaderId != 0)
            {
                locker.ReleaseConcurrent();
            }
        });
        threads.JoinAll("non-preemptive Exclusive failure");
        locker.ReleaseConcurrent();

        AdvancedAssert.True(!exclusiveResult, "Non-preemptive Exclusive succeeded while Concurrent was active.");
        AdvancedAssert.True(
            secondReaderId != 0,
            "Failed non-preemptive Exclusive incorrectly blocked a following Concurrent acquisition.");
    }

    private static void VerifyPreemptiveTryBlocksNewConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim writerAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim writerEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseWriter = new ManualResetEventSlim(false);
        ManualResetEventSlim lateReaderCompleted = new ManualResetEventSlim(false);
        bool writerResult = false;
        int lateReaderResult = int.MaxValue;

        locker.AcquireConcurrent();
        threads.Start("stage5-exclusive-preemptive-try", () =>
        {
            writerAttempting.Set();
            writerResult = locker.TryAcquireExclusive(preemptConcurrent: true);
            if (writerResult)
            {
                try
                {
                    writerEntered.Set();
                    AdvancedAssert.Wait(releaseWriter, "Preemptive Exclusive winner was not released.");
                }
                finally
                {
                    locker.ReleaseExclusive();
                }
            }
        });

        AdvancedAssert.Wait(writerAttempting, "Preemptive Exclusive attempt did not start.");
        AdvancedAssert.RemainsBlocked(writerEntered, "Preemptive Exclusive entered before current Concurrent released.");

        threads.Start("stage5-exclusive-preemptive-late-reader", () =>
        {
            lateReaderResult = locker.TryAcquireConcurrent();
            if (lateReaderResult != 0)
            {
                locker.ReleaseConcurrent();
            }
            lateReaderCompleted.Set();
        });

        AdvancedAssert.Wait(lateReaderCompleted, "Late Concurrent probe did not complete under preemptive Exclusive.");
        AdvancedAssert.True(lateReaderResult == 0, "New Concurrent entered after preemptive Exclusive closed the gate.");
        locker.ReleaseConcurrent();
        AdvancedAssert.Wait(writerEntered, "Preemptive Exclusive did not enter after Concurrent released.");
        AdvancedAssert.True(writerResult, "Preemptive TryAcquireExclusive returned false after winning the request.");
        releaseWriter.Set();
        threads.JoinAll("preemptive TryAcquireExclusive");
    }

    private static void VerifyBlockingAcquireExclusive()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim writerAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim writerEntered = new ManualResetEventSlim(false);

        locker.AcquireConcurrent();
        threads.Start("stage5-acquire-exclusive", () =>
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

        AdvancedAssert.Wait(writerAttempting, "AcquireExclusive attempt did not start.");
        AdvancedAssert.RemainsBlocked(writerEntered, "AcquireExclusive entered before Concurrent released.");
        locker.ReleaseConcurrent();
        AdvancedAssert.Wait(writerEntered, "AcquireExclusive did not enter after Concurrent released.");
        threads.JoinAll("blocking AcquireExclusive");
    }

    private static void VerifyTimedFailureCleanup()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        bool immediate = true;
        bool timed = true;
        TimeSpan elapsed = TimeSpan.Zero;
        int followingReader = 0;

        locker.AcquireConcurrent();
        threads.Start("stage5-exclusive-timeout", () =>
        {
            immediate = locker.TryAcquireExclusive(millisecondsTimeout: 0);
            Stopwatch stopwatch = Stopwatch.StartNew();
            timed = locker.TryAcquireExclusive(millisecondsTimeout: 50);
            stopwatch.Stop();
            elapsed = stopwatch.Elapsed;
        });
        threads.JoinAll("timed Exclusive failure");

        AdvancedAssert.True(!immediate, "TryAcquireExclusive(0) succeeded while Concurrent was active.");
        AdvancedAssert.True(!timed, "Timed TryAcquireExclusive succeeded while Concurrent remained active.");
        AdvancedAssert.True(
            elapsed >= TimeSpan.FromMilliseconds(20),
            $"Timed Exclusive attempt returned too early: {elapsed.TotalMilliseconds:0.0} ms.");

        threads = new AdvancedTestThreadGroup();
        threads.Start("stage5-exclusive-timeout-cleanup-reader", () =>
        {
            followingReader = locker.TryAcquireConcurrent();
            if (followingReader != 0)
            {
                locker.ReleaseConcurrent();
            }
        });
        threads.JoinAll("Concurrent admission after Exclusive timeout");
        locker.ReleaseConcurrent();

        AdvancedAssert.True(
            followingReader != 0,
            "Timed-out Exclusive request did not restore normal Concurrent admission.");
    }

    private static void VerifyInfiniteTimedAcquire()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim writerAttempting = new ManualResetEventSlim(false);
        ManualResetEventSlim writerEntered = new ManualResetEventSlim(false);
        bool result = false;

        locker.AcquireConcurrent();
        threads.Start("stage5-exclusive-infinite-timeout", () =>
        {
            writerAttempting.Set();
            result = locker.TryAcquireExclusive(millisecondsTimeout: -1);
            if (result)
            {
                try
                {
                    writerEntered.Set();
                }
                finally
                {
                    locker.ReleaseExclusive();
                }
            }
        });

        AdvancedAssert.Wait(writerAttempting, "Infinite Exclusive attempt did not start.");
        AdvancedAssert.RemainsBlocked(writerEntered, "Infinite Exclusive attempt entered before Concurrent released.");
        locker.ReleaseConcurrent();
        AdvancedAssert.Wait(writerEntered, "Infinite Exclusive attempt did not enter after Concurrent released.");
        AdvancedAssert.True(result, "Infinite timed Exclusive returned false after the lock became available.");
        threads.JoinAll("infinite timed Exclusive acquisition");
    }

    private static void VerifyZeroTimeoutAgainstExistingExclusive()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim holderEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseHolder = new ManualResetEventSlim(false);

        threads.Start("stage5-exclusive-zero-timeout-holder", () =>
        {
            locker.AcquireExclusive();
            try
            {
                holderEntered.Set();
                // 即使被测调用错误地等待，也会在短时间后自动释放，避免测试永久阻塞。
                releaseHolder.Wait(TimeSpan.FromMilliseconds(250));
            }
            finally
            {
                locker.ReleaseExclusive();
            }
        });

        AdvancedAssert.Wait(holderEntered, "Exclusive zero-timeout holder did not enter.");
        Stopwatch stopwatch = Stopwatch.StartNew();
        bool acquired = locker.TryAcquireExclusive(millisecondsTimeout: 0);
        stopwatch.Stop();

        if (acquired)
        {
            locker.ReleaseExclusive();
        }
        releaseHolder.Set();
        threads.JoinAll("Exclusive zero-timeout against existing Exclusive");

        AdvancedAssert.True(
            !acquired,
            $"TryAcquireExclusive(0) waited for an existing Exclusive and then succeeded " +
            $"after {stopwatch.Elapsed.TotalMilliseconds:0.0} ms.");
        AdvancedAssert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"TryAcquireExclusive(0) did not return immediately under Exclusive: " +
            $"{stopwatch.Elapsed.TotalMilliseconds:0.0} ms.");
    }
}
