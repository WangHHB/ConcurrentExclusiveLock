using IntomicLib;

namespace TestAndBenchmark.Sample;

internal static class PipelineSample
{
    public static void ConcurrentThenExclusiveThenNone()
    {
        var room = new MatchRoom();
        int playerId = 1001;

        var pipeline = new ConcurrentExclusiveLockPipeline(room.Lock);

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.Concurrent(() =>
            {
                // 独立 Concurrent 段：收集允许并发访问的状态。
                // Independent Concurrent segment: collect state that may be accessed concurrently.
                room.PendingPlayerId = playerId;
                room.HasPendingInput = room.ActivePlayerId == playerId;
            }),

            ConcurrentExclusiveLockSegment.Exclusive(() =>
            {
                // 独立 Exclusive 段：会重新申请一段独占权限。
                // Independent Exclusive segment: reacquires a standalone Exclusive access region.
                if (room.HasPendingInput)
                {
                    room.Score += 10;
                    room.EpochID++;
                }
            }),

            ConcurrentExclusiveLockSegment.None(() =>
            {
                // None 段：释放上一段仍持有的权限，在无锁状态下通知外部系统。
                // None segment: releases any access still held by the previous segment, then runs outside the lock.
                room.LastNotifiedPlayerId = playerId;
            }));
    }

    public static void ExclusiveDowngradeWithConvergeConcurrent()
    {
        var room = new MatchRoom();

        var pipeline = new ConcurrentExclusiveLockPipeline(room.Lock);

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.Exclusive(() =>
            {
                // 先独占提交核心状态。
                // First commit the core state under Exclusive access.
                room.Score += 50;
                room.EpochID++;
            }),

            ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
            {
                // 上一段成功持有 Exclusive 时，这里会原地降级到 Concurrent。
                // If the previous segment holds Exclusive, this segment downgrades in place to Concurrent.
                room.VisibleEpochID = room.EpochID;
                room.VisibleScore = room.Score;
            }));
    }

    public static void ConcurrentUpgradeWithEpoch()
    {
        var room = new MatchRoom();
        int nextEpochID = room.EpochID + 1;

        var pipeline = new ConcurrentExclusiveLockPipeline(room.Lock);

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.Concurrent(() =>
            {
                // 先以 Concurrent 权限检查是否需要进入独占提交。
                // First check under Concurrent access whether an isolated commit is needed.
                room.HasPendingInput = room.PendingScoreDelta != 0;
            }),

            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(
                () =>
                {
                    // 如果上一段仍持有 Concurrent，会按 EpochID 协议原地升级到 Exclusive。
                    // 只有 EpochID 推进成功并获得 Exclusive 后，本段才会执行。
                    // If the previous segment still holds Concurrent, Pipeline upgrades in place through EpochID.
                    // This segment runs only after EpochID is raised and Exclusive access is acquired.
                    room.Score += room.PendingScoreDelta;
                    room.PendingScoreDelta = 0;
                    room.EpochID = nextEpochID;
                },
                contextOrEpochID: nextEpochID,
                idType: ConcurrentExclusiveLockSegment.IDType.EpochID),

            ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
            {
                // 上一段成功后，这里会从 Exclusive 原地降级到 Concurrent。
                // After the previous segment succeeds, this downgrades in place from Exclusive to Concurrent.
                room.VisibleEpochID = room.EpochID;
                room.VisibleScore = room.Score;
            }));
    }

    public static void ConcurrentUpgradeWithContext()
    {
        var room = new MatchRoom();
        int matchContextID = 20260722;

        var pipeline = new ConcurrentExclusiveLockPipeline(room.Lock);

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.Concurrent(() =>
            {
                // 先以 Concurrent 权限确认当前业务上下文是否仍然有效。
                // First confirm under Concurrent access that the current business context is still valid.
                room.HasPendingInput = room.CurrentMatchID == matchContextID;
            }),

            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(
                () =>
                {
                    // 使用 ContextID 时，只有成功切换业务上下文并获得 Exclusive，本段才会执行。
                    // With ContextID, this segment runs only after the business context is switched and Exclusive is acquired.
                    room.MatchSettlementCount++;
                    room.EpochID++;
                },
                contextOrEpochID: matchContextID,
                idType: ConcurrentExclusiveLockSegment.IDType.ContextID));
    }

    public static void TrySegmentsCanSkipWork()
    {
        var room = new MatchRoom();

        var pipeline = new ConcurrentExclusiveLockPipeline(room.Lock);

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.TryConcurrent(() =>
            {
                // TryConcurrent 未获得 Concurrent 时，本段不会执行，后续段继续处理。
                // If TryConcurrent does not acquire Concurrent access, this segment is skipped and later segments continue.
                room.TryConcurrentExecuted = true;
            }),

            ConcurrentExclusiveLockSegment.TestExclusive(() =>
            {
                // TestExclusive 只在锁处于 Idle 时尝试获取 Exclusive，不抢占已有 Concurrent。
                // TestExclusive tries Exclusive only when the lock is Idle; it does not preempt existing Concurrent access.
                room.TestExclusiveExecuted = true;
            }),

            ConcurrentExclusiveLockSegment.TryExclusive(() =>
            {
                // TryExclusive 允许抢占 Concurrent，但如果未获得 Exclusive，本段也会被跳过。
                // TryExclusive may preempt Concurrent, but this segment is skipped if Exclusive is not acquired.
                room.TryExclusiveExecuted = true;
            }));
    }

    public static async Task RunPipelineOnThreadPoolAsync()
    {
        var room = new MatchRoom();
        int nextEpochID = room.EpochID + 1;

        var pipeline = new ConcurrentExclusiveLockPipeline(room.Lock);

        await pipeline.DoPipelineAsync(
            ConcurrentExclusiveLockSegment.Concurrent(() =>
            {
                // DoPipelineAsync 会把同步 Pipeline 调度到线程池执行。
                // Segment 本身仍然是同步代码，不要在段内部做异步等待。
                // DoPipelineAsync schedules the synchronous Pipeline on the thread pool.
                // Segments are still synchronous; do not perform async waits inside a segment.
                room.HasPendingInput = room.PendingScoreDelta != 0;
            }),

            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(
                () =>
                {
                    room.Score += room.PendingScoreDelta;
                    room.PendingScoreDelta = 0;
                    room.EpochID = nextEpochID;
                },
                contextOrEpochID: nextEpochID,
                idType: ConcurrentExclusiveLockSegment.IDType.EpochID),

            ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
            {
                room.VisibleEpochID = room.EpochID;
                room.VisibleScore = room.Score;
            }));
    }

    private sealed class MatchRoom
    {
        public readonly ConcurrentExclusiveLock Lock = ConcurrentExclusiveLock.Create();

        public int ActivePlayerId = 1001;
        public int CurrentMatchID = 20260722;
        public int PendingPlayerId;
        public int LastNotifiedPlayerId;
        public int PendingScoreDelta = 20;
        public int Score;
        public int VisibleScore;
        public int EpochID;
        public int VisibleEpochID;
        public int MatchSettlementCount;
        public bool HasPendingInput;
        public bool TryConcurrentExecuted;
        public bool TestExclusiveExecuted;
        public bool TryExclusiveExecuted;
    }
}
