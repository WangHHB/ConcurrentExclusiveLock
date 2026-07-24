using IntomicLib;
using TestAndBenchmark.Common.Testing;

namespace TestAndBenchmark.Correctness.Pipeline;

internal static class ConcurrentExclusiveLockPipelineTests
{
    private const int MaxConcurrent = int.MaxValue;

    public static IEnumerable<TestCase> GetTests()
    {
        yield return TestCase.Sync("Correctness/Pipeline", "segments execute in declared order", SegmentsExecuteInDeclaredOrder);
        yield return TestCase.Sync("Correctness/Pipeline", "concurrent segment holds concurrent access", ConcurrentSegmentHoldsConcurrentAccess);
        yield return TestCase.Sync("Correctness/Pipeline", "exclusive segment holds exclusive access", ExclusiveSegmentHoldsExclusiveAccess);
        yield return TestCase.Sync("Correctness/Pipeline", "converge concurrent downgrades from exclusive", ConvergeConcurrentDowngradesFromExclusive);
        yield return TestCase.Sync("Correctness/Pipeline", "try concurrent segment is skipped when unavailable", TryConcurrentSegmentIsSkippedWhenUnavailable);
    }

    private static void SegmentsExecuteInDeclaredOrder()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        var pipeline = new ConcurrentExclusiveLockPipeline(locker);
        var steps = new List<int>();

        pipeline.DoPipeline(
        [
            ConcurrentExclusiveLockSegment.None(() => steps.Add(1)),
            ConcurrentExclusiveLockSegment.Concurrent(() => steps.Add(2)),
            ConcurrentExclusiveLockSegment.Exclusive(() => steps.Add(3)),
            ConcurrentExclusiveLockSegment.None(() => steps.Add(4)),
        ]);

        TestAssert.Equal("1,2,3,4", string.Join(",", steps));
    }

    private static void ConcurrentSegmentHoldsConcurrentAccess()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        var pipeline = new ConcurrentExclusiveLockPipeline(locker);
        bool executed = false;

        pipeline.DoPipeline(
        [
            ConcurrentExclusiveLockSegment.Concurrent(() =>
            {
                executed = true;
                TestAssert.False(locker.TryAcquireExclusive(false));

                int concurrentId = locker.TryAcquireConcurrent(MaxConcurrent);
                TestAssert.InRange(concurrentId, 1, MaxConcurrent);
                locker.ReleaseConcurrent();
            }),
        ]);

        TestAssert.True(executed);
        TestAssert.True(locker.TryAcquireExclusive(false));
        locker.ReleaseExclusive();
    }

    private static void ExclusiveSegmentHoldsExclusiveAccess()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        var pipeline = new ConcurrentExclusiveLockPipeline(locker);
        bool executed = false;

        pipeline.DoPipeline(
        [
            ConcurrentExclusiveLockSegment.Exclusive(() =>
            {
                executed = true;
                TestAssert.Equal(0, locker.TryAcquireConcurrent(0, MaxConcurrent));
                TestAssert.False(locker.TryAcquireExclusive(false));
            }),
        ]);

        TestAssert.True(executed);

        int concurrentId = locker.TryAcquireConcurrent(MaxConcurrent);
        TestAssert.InRange(concurrentId, 1, MaxConcurrent);
        locker.ReleaseConcurrent();
    }

    private static void ConvergeConcurrentDowngradesFromExclusive()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        var pipeline = new ConcurrentExclusiveLockPipeline(locker);
        bool exclusiveExecuted = false;
        bool concurrentExecuted = false;

        pipeline.DoPipeline(
        [
            ConcurrentExclusiveLockSegment.Exclusive(() =>
            {
                exclusiveExecuted = true;
                TestAssert.Equal(0, locker.TryAcquireConcurrent(0, MaxConcurrent));
            }),
            ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
            {
                concurrentExecuted = true;

                int concurrentId = locker.TryAcquireConcurrent(MaxConcurrent);
                TestAssert.InRange(concurrentId, 1, MaxConcurrent);
                locker.ReleaseConcurrent();
                TestAssert.False(locker.TryAcquireExclusive(false));
            }),
        ]);

        TestAssert.True(exclusiveExecuted);
        TestAssert.True(concurrentExecuted);
        TestAssert.True(locker.TryAcquireExclusive(false));
        locker.ReleaseExclusive();
    }

    private static void TryConcurrentSegmentIsSkippedWhenUnavailable()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        var pipeline = new ConcurrentExclusiveLockPipeline(locker);
        bool executed = false;

        locker.AcquireExclusive();
        try
        {
            pipeline.DoPipeline(
            [
                ConcurrentExclusiveLockSegment.TryConcurrent(() => executed = true),
            ]);
        }
        finally
        {
            locker.ReleaseExclusive();
        }

        TestAssert.False(executed);
        int concurrentId = locker.TryAcquireConcurrent(MaxConcurrent);
        TestAssert.InRange(concurrentId, 1, MaxConcurrent);
        locker.ReleaseConcurrent();
    }
}
