using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// 楠岃瘉 Pipeline 鎸夊０鏄庣殑璁块棶鏉冮檺鍒囨崲 Scope 鐘舵€侊紝骞惰兘鍦ㄥ紓甯歌矾寰勮嚜鍔ㄩ噴鏀俱€?/// </summary>
internal sealed class ConcurrentExclusiveLockPipelineCorrectnessCase : IAdvancedLockCorrectnessCase
{
    private const int ContextId = 0x9001;
    private readonly int lockInstances;
    private readonly int workersPerLock;
    private readonly int roundsPerLock;
    private readonly int seed;
    private readonly bool printSummary;
    private readonly TimeSpan? randomPipelineNoProgressTimeout;
    private int nextPipelineContextId = 0x10000000;
    private int nextPipelineEpochId = 0x20000000;

    public ConcurrentExclusiveLockPipelineCorrectnessCase()
        : this(1, 8, 200, 90210)
    {
    }

    public ConcurrentExclusiveLockPipelineCorrectnessCase(
        int lockInstances,
        int workersPerLock,
        int roundsPerLock,
        int seed,
        bool printSummary = true,
        TimeSpan? randomPipelineNoProgressTimeout = null)
    {
        this.lockInstances = Math.Max(1, lockInstances);
        this.workersPerLock = Math.Max(2, workersPerLock);
        this.roundsPerLock = Math.Max(1, roundsPerLock);
        this.seed = seed;
        this.printSummary = printSummary;
        this.randomPipelineNoProgressTimeout = randomPipelineNoProgressTimeout;
    }

    public string Name => "ConcurrentExclusiveLockPipeline preserves declared segment access semantics";

    public void Run()
    {
        VerifyBasicStateMachine();
        VerifySuccessfulTryModesAreNormalized();
        VerifyTryApplyIDConvergeExclusiveAppliesIdBeforeExclusive();
        VerifyTryApplyIDConvergeExclusiveCanConvergeBackToConcurrent();
        VerifyTryApplyIDConvergeExclusiveWaitsForPipelineDowngradedConcurrent();
        VerifyTryApplyIDConvergeExclusiveUsesActualConcurrentState();
        VerifyTryApplyIDConvergeExclusiveWaitsForActiveConcurrentBusiness();
        VerifyTryApplyIDConvergeExclusiveDistinctContextsWaitForActiveConcurrentBusiness();
        VerifyTryApplyIDConvergeExclusiveHasSingleWinner();
        VerifyTryApplyIDConvergeExclusiveDistinctContextIdsAreIsolated();
        VerifyTryApplyIDConvergeExclusiveEpochUpgradesAreIsolated();
        VerifyTryApplyIDConvergeExclusiveFromNoneIsIsolated();
        VerifyTryApplyIDConvergeExclusiveRepeatedCyclesAreIsolated();
        VerifyExceptionPathReleases();
        VerifyRandomPipelines();
    }

    private static void VerifyBasicStateMachine()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        int none = 0;
        int concurrent = 0;
        int exclusive = 0;
        int converged = 0;

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.None(() => none++),
            ConcurrentExclusiveLockSegment.Concurrent(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline Concurrent segment did not hold Concurrent.");
                concurrent++;
            }),
            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline TryApplyIDConvergeExclusive segment did not hold Exclusive.");
                AdvancedAssert.True(locker.ContextID == ContextId, "Pipeline TryApplyIDConvergeExclusive did not switch ContextID.");
                converged++;
            }, ContextId, ConcurrentExclusiveLockSegment.IDType.ContextID),
            ConcurrentExclusiveLockSegment.Concurrent(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline downgraded Concurrent segment did not hold Concurrent.");
                concurrent++;
            }),
            ConcurrentExclusiveLockSegment.Exclusive(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline Exclusive segment did not hold Exclusive.");
                exclusive++;
            }));

        AdvancedAssert.Equal(1, none, "Pipeline None segment count mismatch.");
        AdvancedAssert.Equal(2, concurrent, "Pipeline Concurrent segment count mismatch.");
        AdvancedAssert.Equal(1, exclusive, "Pipeline Exclusive segment count mismatch.");
        AdvancedAssert.Equal(1, converged, "Pipeline TryApplyIDConvergeExclusive segment count mismatch.");
        AssertReusable(locker, "Pipeline basic state machine");
    }

    private static void VerifySuccessfulTryModesAreNormalized()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        int tryExclusiveThenConcurrent = 0;
        int tryConcurrentThenExclusive = 0;

        RunWithTimeout("Pipeline successful TryExclusive normalization", () =>
        {
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.TryExclusive(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline TryExclusive did not hold Exclusive.");
                }),
                ConcurrentExclusiveLockSegment.Concurrent(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline Concurrent did not replace successful TryExclusive with Concurrent.");
                    tryExclusiveThenConcurrent++;
                }),
                ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline TryApplyIDConvergeExclusive after TryExclusive/Concurrent did not hold Exclusive.");
                }, ContextId + 40, ConcurrentExclusiveLockSegment.IDType.ContextID));
        });

        RunWithTimeout("Pipeline successful TryConcurrent normalization", () =>
        {
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.TryConcurrent(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline TryConcurrent did not hold Concurrent.");
                }),
                ConcurrentExclusiveLockSegment.Exclusive(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline Exclusive did not replace successful TryConcurrent with Exclusive.");
                    tryConcurrentThenExclusive++;
                }));
        });

        AdvancedAssert.Equal(1, tryExclusiveThenConcurrent, "Pipeline must normalize successful TryExclusive as actual Exclusive.");
        AdvancedAssert.Equal(1, tryConcurrentThenExclusive, "Pipeline must normalize successful TryConcurrent as actual Concurrent.");
        AssertReusable(locker, "Pipeline successful Try mode normalization");
    }

    private static void VerifyTryApplyIDConvergeExclusiveAppliesIdBeforeExclusive()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        int fromNone = 0;
        int fromExclusive = 0;
        int staleEpochSkipped = 0;

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline ID-first segment from None did not hold Exclusive.");
                AdvancedAssert.Equal(ContextId + 10, locker.ContextID, "Pipeline ID-first segment from None did not apply ContextID.");
                fromNone++;
            }, ContextId + 10, ConcurrentExclusiveLockSegment.IDType.ContextID),
            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline ID-first segment from Exclusive did not keep Exclusive.");
                AdvancedAssert.Equal(ContextId + 20, locker.EpochID, "Pipeline ID-first segment from Exclusive did not raise EpochID.");
                fromExclusive++;
            }, ContextId + 20, ConcurrentExclusiveLockSegment.IDType.EpochID),
            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
            {
                staleEpochSkipped++;
            }, ContextId + 19, ConcurrentExclusiveLockSegment.IDType.EpochID),
            ConcurrentExclusiveLockSegment.Concurrent(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline did not release failed TryApplyIDConvergeExclusive state.");
            }));

        AdvancedAssert.Equal(1, fromNone, "Pipeline TryApplyIDConvergeExclusive from None segment count mismatch.");
        AdvancedAssert.Equal(1, fromExclusive, "Pipeline TryApplyIDConvergeExclusive from Exclusive segment count mismatch.");
        AdvancedAssert.Equal(0, staleEpochSkipped, "Pipeline stale EpochID segment must be skipped.");
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive ID-first transitions");
    }

    private static void VerifyTryApplyIDConvergeExclusiveCanConvergeBackToConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim concurrentSegmentEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseConcurrentSegment = new ManualResetEventSlim(false);
        ManualResetEventSlim writerEntered = new ManualResetEventSlim(false);
        int downgraded = 0;

        threads.Start("pipeline-writer-behind-try-apply-downgrade", () =>
        {
            AdvancedAssert.Wait(concurrentSegmentEntered, "Pipeline downgraded Concurrent segment did not start.");
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

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.Concurrent(() => { }),
            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline TryApplyIDConvergeExclusive did not hold Exclusive before downgrade.");
            }, ContextId + 60, ConcurrentExclusiveLockSegment.IDType.ContextID),
            ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline ConvergeConcurrent after TryApplyIDConvergeExclusive did not hold Concurrent.");
                Interlocked.Increment(ref downgraded);
                concurrentSegmentEntered.Set();
                AdvancedAssert.RemainsBlocked(
                    writerEntered,
                    "Pipeline writer entered while downgraded Concurrent segment was still running.");
                releaseConcurrentSegment.Set();
            }));

        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment.Concurrent(() => { }),
            ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline TryApplyIDConvergeExclusive EpochID did not hold Exclusive before downgrade.");
            }, ContextId + 61, ConcurrentExclusiveLockSegment.IDType.EpochID),
            ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
            {
                AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline ConvergeConcurrent after EpochID TryApplyIDConvergeExclusive did not hold Concurrent.");
                Interlocked.Increment(ref downgraded);
            }));

        AdvancedAssert.Wait(releaseConcurrentSegment, "Pipeline downgraded Concurrent segment did not finish.");
        AdvancedAssert.Wait(writerEntered, "Pipeline writer did not enter after downgraded Concurrent segment completed.");
        threads.JoinAll("Pipeline TryApplyIDConvergeExclusive can converge back to Concurrent");
        AdvancedAssert.Equal(2, Volatile.Read(ref downgraded), "Pipeline TryApplyIDConvergeExclusive downgrade segment count mismatch.");
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive downgrade to Concurrent");
    }

    private static void VerifyTryApplyIDConvergeExclusiveWaitsForPipelineDowngradedConcurrent()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim downgradedEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseDowngraded = new ManualResetEventSlim(false);
        ManualResetEventSlim upgraderEntered = new ManualResetEventSlim(false);
        int activeDowngradedConcurrent = 0;
        int entered = 0;

        threads.Start("pipeline-downgraded-concurrent-holder", () =>
        {
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.Concurrent(() => { }),
                ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() => { }, ContextId + 70, ConcurrentExclusiveLockSegment.IDType.ContextID),
                ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
                {
                    Interlocked.Increment(ref activeDowngradedConcurrent);
                    downgradedEntered.Set();
                    AdvancedAssert.Wait(releaseDowngraded, "Pipeline downgraded holder was not released.");
                    Interlocked.Decrement(ref activeDowngradedConcurrent);
                }));
        });

        threads.Start("pipeline-upgrader-behind-downgraded-concurrent", () =>
        {
            AdvancedAssert.Wait(downgradedEntered, "Pipeline downgraded Concurrent holder did not start.");
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.Concurrent(() => { }),
                ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                {
                    AdvancedAssert.Equal(
                        0,
                        Volatile.Read(ref activeDowngradedConcurrent),
                        "Pipeline TryApplyIDConvergeExclusive entered while downgraded Concurrent business was still active.");
                    Interlocked.Increment(ref entered);
                    upgraderEntered.Set();
                }, ContextId + 71, ConcurrentExclusiveLockSegment.IDType.ContextID));
        });

        AdvancedAssert.Wait(downgradedEntered, "Pipeline downgraded Concurrent holder did not enter.");
        AdvancedAssert.RemainsBlocked(
            upgraderEntered,
            "Pipeline TryApplyIDConvergeExclusive did not wait for downgraded Concurrent business.");
        releaseDowngraded.Set();
        AdvancedAssert.Wait(upgraderEntered, "Pipeline TryApplyIDConvergeExclusive did not enter after downgraded Concurrent released.");
        threads.JoinAll("Pipeline TryApplyIDConvergeExclusive waits for pipeline downgraded Concurrent");
        AdvancedAssert.Equal(1, Volatile.Read(ref entered), "Pipeline ContextID TryApplyIDConvergeExclusive behind downgraded Concurrent count mismatch.");

        locker.EpochID = ContextId + 80;
        activeDowngradedConcurrent = 0;
        entered = 0;
        downgradedEntered.Reset();
        releaseDowngraded.Reset();
        upgraderEntered.Reset();
        threads = new AdvancedTestThreadGroup();

        threads.Start("pipeline-downgraded-concurrent-holder-epoch", () =>
        {
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.Concurrent(() => { }),
                ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() => { }, ContextId + 81, ConcurrentExclusiveLockSegment.IDType.ContextID),
                ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
                {
                    Interlocked.Increment(ref activeDowngradedConcurrent);
                    downgradedEntered.Set();
                    AdvancedAssert.Wait(releaseDowngraded, "Pipeline downgraded Epoch holder was not released.");
                    Interlocked.Decrement(ref activeDowngradedConcurrent);
                }));
        });

        threads.Start("pipeline-epoch-upgrader-behind-downgraded-concurrent", () =>
        {
            AdvancedAssert.Wait(downgradedEntered, "Pipeline downgraded Concurrent holder did not start for EpochID.");
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.Concurrent(() => { }),
                ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                {
                    AdvancedAssert.Equal(
                        0,
                        Volatile.Read(ref activeDowngradedConcurrent),
                        "Pipeline EpochID TryApplyIDConvergeExclusive entered while downgraded Concurrent business was still active.");
                    Interlocked.Increment(ref entered);
                    upgraderEntered.Set();
                }, ContextId + 82, ConcurrentExclusiveLockSegment.IDType.EpochID));
        });

        AdvancedAssert.Wait(downgradedEntered, "Pipeline downgraded Concurrent holder did not enter for EpochID.");
        AdvancedAssert.RemainsBlocked(
            upgraderEntered,
            "Pipeline EpochID TryApplyIDConvergeExclusive did not wait for downgraded Concurrent business.");
        releaseDowngraded.Set();
        AdvancedAssert.Wait(upgraderEntered, "Pipeline EpochID TryApplyIDConvergeExclusive did not enter after downgraded Concurrent released.");
        threads.JoinAll("Pipeline EpochID TryApplyIDConvergeExclusive waits for pipeline downgraded Concurrent");
        AdvancedAssert.Equal(1, Volatile.Read(ref entered), "Pipeline EpochID TryApplyIDConvergeExclusive behind downgraded Concurrent count mismatch.");
        locker.EpochID = 0;
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive waits for downgraded Concurrent");
    }

    private static void VerifyTryApplyIDConvergeExclusiveUsesActualConcurrentState()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        int afterConvergeConcurrent = 0;
        int afterTryConcurrent = 0;

        RunWithTimeout("Pipeline TryApplyIDConvergeExclusive after ConvergeConcurrent", () =>
        {
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.Exclusive(() => { }),
                ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline ConvergeConcurrent did not leave a Concurrent state.");
                }),
                ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline TryApplyIDConvergeExclusive after ConvergeConcurrent did not hold Exclusive.");
                    AdvancedAssert.Equal(ContextId + 30, locker.ContextID, "Pipeline TryApplyIDConvergeExclusive after ConvergeConcurrent did not apply ContextID.");
                    afterConvergeConcurrent++;
                }, ContextId + 30, ConcurrentExclusiveLockSegment.IDType.ContextID));
        });

        RunWithTimeout("Pipeline TryApplyIDConvergeExclusive after TryConcurrent", () =>
        {
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.TryConcurrent(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Concurrent, "Pipeline TryConcurrent did not leave a Concurrent state.");
                }),
                ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                {
                    AdvancedAssert.True(locker.ObservedState == ConcurrentExclusiveLockState.Exclusive, "Pipeline TryApplyIDConvergeExclusive after TryConcurrent did not hold Exclusive.");
                    AdvancedAssert.Equal(ContextId + 31, locker.ContextID, "Pipeline TryApplyIDConvergeExclusive after TryConcurrent did not apply ContextID.");
                    afterTryConcurrent++;
                }, ContextId + 31, ConcurrentExclusiveLockSegment.IDType.ContextID));
        });

        AdvancedAssert.Equal(1, afterConvergeConcurrent, "Pipeline TryApplyIDConvergeExclusive must treat ConvergeConcurrent success as current Concurrent.");
        AdvancedAssert.Equal(1, afterTryConcurrent, "Pipeline TryApplyIDConvergeExclusive must treat TryConcurrent success as current Concurrent.");
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive actual Concurrent state");
    }

    private static void VerifyTryApplyIDConvergeExclusiveWaitsForActiveConcurrentBusiness()
    {
        const int participants = 4;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier concurrentEntered = new Barrier(participants);
        ManualResetEventSlim upgraderReady = new ManualResetEventSlim(false);
        ManualResetEventSlim winnerEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseReaders = new ManualResetEventSlim(false);
        int activeConcurrent = 0;
        int overlaps = 0;

        for (int i = 0; i < participants; i++)
        {
            int captured = i;
            threads.Start($"pipeline-try-apply-id-waits-for-concurrent-{captured}", () =>
            {
                bool isUpgrader = captured == 0;
                pipeline.DoPipeline(
                    ConcurrentExclusiveLockSegment.Concurrent(() =>
                    {
                        Interlocked.Increment(ref activeConcurrent);
                        if (!concurrentEntered.SignalAndWait(AdvancedTestTiming.Timeout))
                        {
                            throw new TimeoutException("Pipeline active Concurrent participants did not align before TryApplyIDConvergeExclusive.");
                        }

                        if (isUpgrader)
                        {
                            Interlocked.Decrement(ref activeConcurrent);
                            upgraderReady.Set();
                        }
                        else
                        {
                            AdvancedAssert.Wait(releaseReaders, "Pipeline held Concurrent reader was not released.");
                            Interlocked.Decrement(ref activeConcurrent);
                        }
                    }),
                    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                    {
                        if (Volatile.Read(ref activeConcurrent) != 0)
                        {
                            Interlocked.Increment(ref overlaps);
                        }

                        winnerEntered.Set();
                    }, ContextId + 900, ConcurrentExclusiveLockSegment.IDType.ContextID));
            });
        }

        AdvancedAssert.Wait(upgraderReady, "Pipeline upgrader did not leave its Concurrent segment.");
        AdvancedAssert.RemainsBlocked(
            winnerEntered,
            "Pipeline TryApplyIDConvergeExclusive entered Exclusive while other Concurrent business was still running.");
        releaseReaders.Set();
        AdvancedAssert.Wait(winnerEntered, "Pipeline TryApplyIDConvergeExclusive did not enter after Concurrent business released.");
        threads.JoinAll("Pipeline TryApplyIDConvergeExclusive waits for active Concurrent business");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Pipeline TryApplyIDConvergeExclusive overlapped active Concurrent business.");
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive active Concurrent wait");
    }

    private static void VerifyTryApplyIDConvergeExclusiveDistinctContextsWaitForActiveConcurrentBusiness()
    {
        const int participants = 4;
        const int upgraders = 2;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier concurrentEntered = new Barrier(participants);
        using CountdownEvent upgradersReady = new CountdownEvent(upgraders);
        ManualResetEventSlim winnerEntered = new ManualResetEventSlim(false);
        ManualResetEventSlim releaseReaders = new ManualResetEventSlim(false);
        int activeConcurrent = 0;
        int activeExclusive = 0;
        int overlaps = 0;

        for (int i = 0; i < participants; i++)
        {
            int captured = i;
            threads.Start($"pipeline-try-apply-id-distinct-waits-for-concurrent-{captured}", () =>
            {
                bool isUpgrader = captured < upgraders;
                pipeline.DoPipeline(
                    ConcurrentExclusiveLockSegment.Concurrent(() =>
                    {
                        Interlocked.Increment(ref activeConcurrent);
                        if (!concurrentEntered.SignalAndWait(AdvancedTestTiming.Timeout))
                        {
                            throw new TimeoutException("Pipeline distinct ContextID active Concurrent participants did not align.");
                        }

                        if (isUpgrader)
                        {
                            Interlocked.Decrement(ref activeConcurrent);
                            upgradersReady.Signal();
                        }
                        else
                        {
                            AdvancedAssert.Wait(releaseReaders, "Pipeline held distinct ContextID reader was not released.");
                            Interlocked.Decrement(ref activeConcurrent);
                        }
                    }),
                    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                    {
                        int exclusive = Interlocked.Increment(ref activeExclusive);
                        int concurrent = Volatile.Read(ref activeConcurrent);
                        if (exclusive != 1 || concurrent != 0)
                        {
                            Interlocked.Increment(ref overlaps);
                        }

                        winnerEntered.Set();
                        Thread.Yield();
                        Interlocked.Decrement(ref activeExclusive);
                    }, ContextId + 950 + captured, ConcurrentExclusiveLockSegment.IDType.ContextID));
            });
        }

        if (!upgradersReady.Wait(AdvancedTestTiming.Timeout))
        {
            throw new TimeoutException("Pipeline distinct ContextID upgraders did not leave their Concurrent segments.");
        }

        AdvancedAssert.RemainsBlocked(
            winnerEntered,
            "Pipeline distinct ContextID TryApplyIDConvergeExclusive entered while other Concurrent business was still running.");
        releaseReaders.Set();
        threads.JoinAll("Pipeline distinct ContextID TryApplyIDConvergeExclusive waits for active Concurrent business");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Pipeline distinct ContextID upgrades overlapped active Concurrent business or each other.");
        AssertReusable(locker, "Pipeline distinct ContextID TryApplyIDConvergeExclusive active Concurrent wait");
    }

    private void VerifyTryApplyIDConvergeExclusiveHasSingleWinner()
    {
        int participants = Math.Max(2, Math.Min(workersPerLock, 128));
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier barrier = new Barrier(participants);
        int winners = 0;
        int losersContinued = 0;
        int activeExclusive = 0;

        for (int i = 0; i < participants; i++)
        {
            int captured = i;
            threads.Start($"pipeline-try-apply-id-converge-{captured}", () =>
            {
                bool won = false;
                pipeline.DoPipeline(
                    ConcurrentExclusiveLockSegment.Concurrent(() =>
                    {
                        if (!barrier.SignalAndWait(AdvancedTestTiming.Timeout))
                        {
                            throw new TimeoutException("Pipeline participants did not align before TryApplyIDConvergeExclusive.");
                        }
                    }),
                    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                    {
                        won = true;
                        Interlocked.Increment(ref winners);
                        int active = Interlocked.Increment(ref activeExclusive);
                        AdvancedAssert.Equal(1, active, "Pipeline TryApplyIDConvergeExclusive business segments overlapped.");
                        Thread.Yield();
                        Interlocked.Decrement(ref activeExclusive);
                    }, ContextId, ConcurrentExclusiveLockSegment.IDType.ContextID),
                    ConcurrentExclusiveLockSegment.None(() =>
                    {
                        if (!won)
                        {
                            Interlocked.Increment(ref losersContinued);
                        }
                    }));
            });
        }

        threads.JoinAll("Pipeline TryApplyIDConvergeExclusive single winner");
        AdvancedAssert.Equal(1, winners, "Pipeline TryApplyIDConvergeExclusive must execute exactly one winner segment.");
        AdvancedAssert.Equal(participants - 1, losersContinued, "Pipeline TryApplyIDConvergeExclusive losers must continue after auto-release.");
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive single winner");
    }

    private void VerifyTryApplyIDConvergeExclusiveDistinctContextIdsAreIsolated()
    {
        int participants = Math.Max(2, Math.Min(workersPerLock, 128));
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier barrier = new Barrier(participants);
        int executed = 0;
        int continued = 0;
        int activeConcurrent = 0;
        int activeExclusive = 0;
        int overlaps = 0;

        for (int i = 0; i < participants; i++)
        {
            int captured = i;
            threads.Start($"pipeline-try-apply-id-distinct-context-{captured}", () =>
            {
                bool ran = false;
                pipeline.DoPipeline(
                    ConcurrentExclusiveLockSegment.Concurrent(() =>
                    {
                        Interlocked.Increment(ref activeConcurrent);
                        if (!barrier.SignalAndWait(AdvancedTestTiming.Timeout))
                        {
                            throw new TimeoutException("Pipeline distinct ContextID participants did not align before TryApplyIDConvergeExclusive.");
                        }

                        Thread.Yield();
                        Interlocked.Decrement(ref activeConcurrent);
                    }),
                    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                    {
                        ran = true;
                        Interlocked.Increment(ref executed);
                        int active = Interlocked.Increment(ref activeExclusive);
                        int concurrent = Volatile.Read(ref activeConcurrent);
                        if (active != 1 || concurrent != 0)
                        {
                            Interlocked.Increment(ref overlaps);
                        }

                        Thread.Yield();
                        Interlocked.Decrement(ref activeExclusive);
                    }, ContextId + 500 + captured, ConcurrentExclusiveLockSegment.IDType.ContextID),
                    ConcurrentExclusiveLockSegment.None(() =>
                    {
                        if (ran)
                        {
                            Interlocked.Increment(ref continued);
                        }
                    }));
            });
        }

        threads.JoinAll("Pipeline TryApplyIDConvergeExclusive distinct ContextID isolation");
        AdvancedAssert.Equal(participants, Volatile.Read(ref executed), "Every distinct ContextID Pipeline upgrade must execute.");
        AdvancedAssert.Equal(participants, Volatile.Read(ref continued), "Every distinct ContextID Pipeline must continue after execution.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Distinct ContextID Pipeline upgrades must not overlap.");
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive distinct ContextID isolation");
    }

    private void VerifyTryApplyIDConvergeExclusiveEpochUpgradesAreIsolated()
    {
        int participants = Math.Max(2, Math.Min(workersPerLock, 128));
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier barrier = new Barrier(participants);
        const int baseEpoch = ContextId + 1000;
        int executed = 0;
        int skipped = 0;
        int activeConcurrent = 0;
        int activeExclusive = 0;
        int overlaps = 0;

        locker.EpochID = baseEpoch;
        for (int i = 0; i < participants; i++)
        {
            int captured = i;
            threads.Start($"pipeline-try-apply-id-epoch-upgrade-{captured}", () =>
            {
                bool ran = false;
                pipeline.DoPipeline(
                    ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
                    {
                        Interlocked.Increment(ref activeConcurrent);
                        if (!barrier.SignalAndWait(AdvancedTestTiming.Timeout))
                        {
                            throw new TimeoutException("Pipeline EpochID participants did not align before TryApplyIDConvergeExclusive.");
                        }

                        Thread.Yield();
                        Interlocked.Decrement(ref activeConcurrent);
                    }),
                    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                    {
                        ran = true;
                        Interlocked.Increment(ref executed);
                        int active = Interlocked.Increment(ref activeExclusive);
                        int concurrent = Volatile.Read(ref activeConcurrent);
                        if (active != 1 || concurrent != 0)
                        {
                            Interlocked.Increment(ref overlaps);
                        }

                        Thread.Yield();
                        Interlocked.Decrement(ref activeExclusive);
                    }, baseEpoch + 1 + captured, ConcurrentExclusiveLockSegment.IDType.EpochID),
                    ConcurrentExclusiveLockSegment.None(() =>
                    {
                        if (!ran)
                        {
                            Interlocked.Increment(ref skipped);
                        }
                    }));
            });
        }

        threads.JoinAll("Pipeline TryApplyIDConvergeExclusive EpochID upgrade isolation");
        AdvancedAssert.True(Volatile.Read(ref executed) > 0, "At least one Pipeline EpochID upgrade must execute.");
        AdvancedAssert.Equal(participants, Volatile.Read(ref executed) + Volatile.Read(ref skipped), "Every Pipeline EpochID participant must execute or skip.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "Pipeline EpochID upgrades must not overlap.");
        locker.EpochID = 0;
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive EpochID upgrade isolation");
    }

    private void VerifyTryApplyIDConvergeExclusiveFromNoneIsIsolated()
    {
        int participants = Math.Max(2, Math.Min(workersPerLock, 128));
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        using Barrier barrier = new Barrier(participants);
        int executed = 0;
        int activeExclusive = 0;
        int overlaps = 0;

        for (int i = 0; i < participants; i++)
        {
            int captured = i;
            threads.Start($"pipeline-try-apply-id-none-context-{captured}", () =>
            {
                if (!barrier.SignalAndWait(AdvancedTestTiming.Timeout))
                {
                    throw new TimeoutException("Pipeline None-state ContextID participants did not align before TryApplyIDConvergeExclusive.");
                }

                pipeline.DoPipeline(
                    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                    {
                        Interlocked.Increment(ref executed);
                        int active = Interlocked.Increment(ref activeExclusive);
                        if (active != 1)
                        {
                            Interlocked.Increment(ref overlaps);
                        }

                        Thread.Yield();
                        Interlocked.Decrement(ref activeExclusive);
                    }, ContextId + 800 + captured, ConcurrentExclusiveLockSegment.IDType.ContextID));
            });
        }

        threads.JoinAll("Pipeline TryApplyIDConvergeExclusive from None isolation");
        AdvancedAssert.Equal(participants, Volatile.Read(ref executed), "Every None-state Pipeline ID application must execute.");
        AdvancedAssert.Equal(0, Volatile.Read(ref overlaps), "None-state Pipeline Exclusive executions must not overlap.");
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive from None isolation");
    }

    private void VerifyTryApplyIDConvergeExclusiveRepeatedCyclesAreIsolated()
    {
        int participants = Math.Max(2, Math.Min(workersPerLock, 16));
        const int cycles = 96;
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);
        AccessTracker tracker = new AccessTracker(0);
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        CountdownEvent ready = new CountdownEvent(participants);
        ManualResetEventSlim start = new ManualResetEventSlim(false);

        for (int participant = 0; participant < participants; participant++)
        {
            int capturedParticipant = participant;
            threads.Start($"pipeline-repeated-try-apply-cycle-{capturedParticipant}", () =>
            {
                ready.Signal();
                start.Wait();

                ConcurrentExclusiveLockSegment[] segments =
                    new ConcurrentExclusiveLockSegment[cycles * 2 + 1];

                segments[0] = ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
                {
                    string operation =
                        $"Pipeline repeated initial ConvergeConcurrent (worker={capturedParticipant}, state={locker.ObservedState})";
                    tracker.EnterConcurrent(operation);
                    Thread.Yield();
                    tracker.ExitConcurrent(operation);
                });

                for (int cycle = 0; cycle < cycles; cycle++)
                {
                    int capturedCycle = cycle;
                    ConcurrentExclusiveLockSegment.IDType idType =
                        ((capturedParticipant + capturedCycle) & 1) == 0
                            ? ConcurrentExclusiveLockSegment.IDType.ContextID
                            : ConcurrentExclusiveLockSegment.IDType.EpochID;
                    int id = idType == ConcurrentExclusiveLockSegment.IDType.ContextID
                        ? CreateContextId()
                        : CreateEpochId();
                    string idLabel =
                        $"worker={capturedParticipant}, cycle={capturedCycle}, idType={idType}, id={id}";

                    segments[1 + cycle * 2] = ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                    {
                        string operation =
                            $"Pipeline repeated TryApplyIDConvergeExclusive ({idLabel}, state={locker.ObservedState})";
                        tracker.EnterExclusive(operation);
                        Thread.Yield();
                        tracker.ExitExclusive(operation);
                    }, id, idType);

                    segments[2 + cycle * 2] = ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
                    {
                        string operation =
                            $"Pipeline repeated ConvergeConcurrent ({idLabel}, state={locker.ObservedState})";
                        tracker.EnterConcurrent(operation);
                        Thread.Yield();
                        tracker.ExitConcurrent(operation);
                    });
                }

                pipeline.DoPipeline(segments);
            });
        }

        AdvancedAssert.Wait(ready, "Pipeline repeated TryApplyIDConvergeExclusive participants did not become ready.");
        start.Set();
        threads.JoinAll("Pipeline repeated TryApplyIDConvergeExclusive cycles");
        tracker.AssertIdle("Pipeline repeated TryApplyIDConvergeExclusive cycles");
        AssertReusable(locker, "Pipeline repeated TryApplyIDConvergeExclusive cycles");
    }

    private static void VerifyExceptionPathReleases()
    {
        ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locker);

        ExpectPipelineException(() =>
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.Exclusive((Action)(() =>
                    {
                        throw new PipelineInjectedException("Exclusive");
                    }))));
        AssertReusable(locker, "Pipeline Exclusive exception");

        ExpectPipelineException(() =>
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.Concurrent(() => { }),
                ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive((Action)(() =>
                    throw new PipelineInjectedException("TryApplyIDConvergeExclusive")), ContextId + 100, ConcurrentExclusiveLockSegment.IDType.ContextID)));
        AssertReusable(locker, "Pipeline TryApplyIDConvergeExclusive exception");

        ExpectPipelineException(() =>
            pipeline.DoPipeline(
                ConcurrentExclusiveLockSegment.Concurrent((Action)(() =>
                    throw new PipelineInjectedException("Concurrent")))));
        AssertReusable(locker, "Pipeline Concurrent exception");
    }

    private void VerifyRandomPipelines()
    {
        ConcurrentExclusiveLock[] lockers = new ConcurrentExclusiveLock[lockInstances];
        ConcurrentExclusiveLockPipeline[] pipelines = new ConcurrentExclusiveLockPipeline[lockInstances];
        AccessTracker[] trackers = new AccessTracker[lockInstances];
        for (int lockIndex = 0; lockIndex < lockInstances; lockIndex++)
        {
            lockers[lockIndex] = ConcurrentExclusiveLock.Create();
            pipelines[lockIndex] = new ConcurrentExclusiveLockPipeline(lockers[lockIndex]);
            trackers[lockIndex] = new AccessTracker(lockIndex);
        }

        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        int totalWorkers = checked(lockInstances * workersPerLock);
        CountdownEvent ready = new CountdownEvent(totalWorkers);
        long totalSegments = 0;
        long completedRounds = 0;

        for (int lockIndex = 0; lockIndex < lockInstances; lockIndex++)
        {
            int capturedLockIndex = lockIndex;
            for (int worker = 0; worker < workersPerLock; worker++)
            {
                int capturedWorker = worker;
                threads.Start($"pipeline-random-{capturedLockIndex}-{capturedWorker}", () =>
                {
                    Random random = new Random(seed + capturedLockIndex * 1009 + capturedWorker * 17);
                    ready.Signal();
                    start.Wait();
                    for (int round = 0; round < roundsPerLock; round++)
                    {
                        ConcurrentExclusiveLockSegment[] segments = BuildRandomSegments(
                            lockers[capturedLockIndex],
                            trackers[capturedLockIndex],
                            random,
                            capturedWorker,
                            round);
                        Interlocked.Add(ref totalSegments, segments.Length);
                        pipelines[capturedLockIndex].DoPipeline(segments);
                        Interlocked.Increment(ref completedRounds);
                    }
                });
            }
        }

        AdvancedAssert.Wait(ready, "Pipeline random workers did not become ready.");
        start.Set();
        if (randomPipelineNoProgressTimeout.HasValue)
        {
            threads.JoinAllWhileProgressing(
                "Pipeline randomized complex state machine",
                () => Volatile.Read(ref completedRounds),
                randomPipelineNoProgressTimeout.Value);
        }
        else
        {
            int timeoutSeconds = Math.Clamp(totalWorkers / 8 + roundsPerLock / 100 + 20, 20, 300);
            threads.JoinAll("Pipeline randomized complex state machine", TimeSpan.FromSeconds(timeoutSeconds));
        }

        for (int lockIndex = 0; lockIndex < lockInstances; lockIndex++)
        {
            trackers[lockIndex].AssertIdle("Pipeline randomized completion");
            AssertReusable(lockers[lockIndex], $"Pipeline randomized complex state machine lock {lockIndex}");
        }

        if (printSummary)
        {
            Console.WriteLine(
                $"       pipeline random locks={lockInstances:n0}, workers/lock={workersPerLock:n0}, " +
                $"rounds/lock={roundsPerLock:n0}, segments={totalSegments:n0}, seed={seed}");
        }
    }

    private ConcurrentExclusiveLockSegment[] BuildRandomSegments(
        ConcurrentExclusiveLock locker,
        AccessTracker tracker,
        Random random,
        int worker,
        int round)
    {
        int length = random.Next(3, 8);
        ConcurrentExclusiveLockSegment[] segments =
            new ConcurrentExclusiveLockSegment[length];

        for (int index = 0; index < length; index++)
        {
            int capturedIndex = index;
            bool emptyBusiness = random.Next(4) == 0;
            string label = $"worker={worker}, round={round}, segment={capturedIndex}";
            switch (random.Next(7))
            {
                case 0:
                    segments[index] = ConcurrentExclusiveLockSegment.None(() =>
                        DoRandomWork(emptyBusiness, worker, round, capturedIndex));
                    break;
                case 1:
                    segments[index] = ConcurrentExclusiveLockSegment.Concurrent(() =>
                    {
                        tracker.EnterConcurrent($"Pipeline random Concurrent ({label}, state={locker.ObservedState})");
                        DoRandomWork(emptyBusiness, worker, round, capturedIndex);
                        tracker.ExitConcurrent($"Pipeline random Concurrent ({label}, state={locker.ObservedState})");
                    });
                    break;
                case 2:
                    segments[index] = ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
                    {
                        tracker.EnterConcurrent($"Pipeline random ConvergeConcurrent ({label}, state={locker.ObservedState})");
                        DoRandomWork(emptyBusiness, worker, round, capturedIndex);
                        tracker.ExitConcurrent($"Pipeline random ConvergeConcurrent ({label}, state={locker.ObservedState})");
                    });
                    break;
                case 3:
                    segments[index] = ConcurrentExclusiveLockSegment.Exclusive(() =>
                    {
                        tracker.EnterExclusive($"Pipeline random Exclusive ({label}, state={locker.ObservedState})");
                        DoRandomWork(emptyBusiness, worker, round, capturedIndex);
                        tracker.ExitExclusive($"Pipeline random Exclusive ({label}, state={locker.ObservedState})");
                    });
                    break;
                case 4:
                    ConcurrentExclusiveLockSegment.IDType idType = ((round + capturedIndex) & 1) == 0
                        ? ConcurrentExclusiveLockSegment.IDType.ContextID
                        : ConcurrentExclusiveLockSegment.IDType.EpochID;
                    int id = idType == ConcurrentExclusiveLockSegment.IDType.ContextID
                        ? CreateContextId()
                        : CreateEpochId();
                    string idLabel = $"{label}, idType={idType}, id={id}";
                    segments[index] = ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
                    {
                        tracker.EnterExclusive($"Pipeline random TryApplyIDConvergeExclusive ({idLabel}, state={locker.ObservedState})");
                        DoRandomWork(emptyBusiness, worker, round, capturedIndex);
                        tracker.ExitExclusive($"Pipeline random TryApplyIDConvergeExclusive ({idLabel}, state={locker.ObservedState})");
                    }, id, idType);
                    break;
                case 5:
                    segments[index] = ConcurrentExclusiveLockSegment.TryExclusive(() =>
                    {
                        tracker.EnterExclusive($"Pipeline random TryExclusive ({label}, state={locker.ObservedState})");
                        DoRandomWork(emptyBusiness, worker, round, capturedIndex);
                        tracker.ExitExclusive($"Pipeline random TryExclusive ({label}, state={locker.ObservedState})");
                    });
                    break;
                default:
                    segments[index] = ConcurrentExclusiveLockSegment.TryConcurrent(() =>
                    {
                        tracker.EnterConcurrent($"Pipeline random TryConcurrent ({label}, state={locker.ObservedState})");
                        DoRandomWork(emptyBusiness, worker, round, capturedIndex);
                        tracker.ExitConcurrent($"Pipeline random TryConcurrent ({label}, state={locker.ObservedState})");
                    });
                    break;
            }
        }

        return segments;
    }

    private static void ExpectPipelineException(Action action)
    {
        try
        {
            action();
        }
        catch (PipelineInjectedException)
        {
            return;
        }

        throw new InvalidOperationException("Expected Pipeline injected exception was not thrown.");
    }

    private static void AssertReusable(ConcurrentExclusiveLock locker, string operation)
    {
        if (!locker.TryAcquireExclusive(preemptConcurrent: false))
        {
            throw new InvalidOperationException($"{operation}: lock was not reusable after Pipeline run.");
        }

        locker.ReleaseExclusive();
    }

    private static void RunWithTimeout(string operation, Action action)
    {
        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        threads.Start(operation, action);
        threads.JoinAll(operation);
    }

    private static void DoTinyWork(int worker, int round, int segment)
    {
        AdvancedSemanticSink.Add(unchecked((long)(((ulong)worker << 32) ^ (uint)(round * 31 + segment))));
    }

    private static void DoRandomWork(bool emptyBusiness, int worker, int round, int segment)
    {
        if (!emptyBusiness)
        {
            DoTinyWork(worker, round, segment);
        }
    }

    private int CreateContextId()
    {
        return Interlocked.Increment(ref nextPipelineContextId);
    }

    private int CreateEpochId()
    {
        return Interlocked.Increment(ref nextPipelineEpochId);
    }

    private sealed class AccessTracker
    {
        private readonly int lockIndex;
        private int activeConcurrent;
        private int activeExclusive;
        private string lastConcurrentOperation = "";
        private string lastExclusiveOperation = "";

        public AccessTracker(int lockIndex)
        {
            this.lockIndex = lockIndex;
        }

        public void EnterConcurrent(string operation)
        {
            int concurrent = Interlocked.Increment(ref activeConcurrent);
            Volatile.Write(ref lastConcurrentOperation, operation);
            int exclusive = Volatile.Read(ref activeExclusive);
            if (exclusive != 0)
            {
                Interlocked.Decrement(ref activeConcurrent);
                throw new InvalidOperationException(
                    $"Lock {lockIndex}: {operation}: Concurrent business overlapped Exclusive. concurrent={concurrent}, exclusive={exclusive}, " +
                    $"activeConcurrent='{Volatile.Read(ref lastConcurrentOperation)}', activeExclusive='{Volatile.Read(ref lastExclusiveOperation)}'.");
            }
        }

        public void ExitConcurrent(string operation)
        {
            int concurrent = Interlocked.Decrement(ref activeConcurrent);
            if (concurrent < 0)
            {
                throw new InvalidOperationException($"Lock {lockIndex}: {operation}: Concurrent tracker underflow.");
            }
        }

        public void EnterExclusive(string operation)
        {
            int exclusive = Interlocked.Increment(ref activeExclusive);
            Volatile.Write(ref lastExclusiveOperation, operation);
            int concurrent = Volatile.Read(ref activeConcurrent);
            if (exclusive != 1 || concurrent != 0)
            {
                Interlocked.Decrement(ref activeExclusive);
                throw new InvalidOperationException(
                    $"Lock {lockIndex}: {operation}: Exclusive business overlapped. concurrent={concurrent}, exclusive={exclusive}, " +
                    $"activeConcurrent='{Volatile.Read(ref lastConcurrentOperation)}', activeExclusive='{Volatile.Read(ref lastExclusiveOperation)}'.");
            }
        }

        public void ExitExclusive(string operation)
        {
            int exclusive = Interlocked.Decrement(ref activeExclusive);
            if (exclusive != 0)
            {
                throw new InvalidOperationException($"Lock {lockIndex}: {operation}: Exclusive tracker underflow/overlap.");
            }
        }

        public void AssertIdle(string operation)
        {
            int concurrent = Volatile.Read(ref activeConcurrent);
            int exclusive = Volatile.Read(ref activeExclusive);
            if (concurrent != 0 || exclusive != 0)
            {
                throw new InvalidOperationException(
                    $"Lock {lockIndex}: {operation}: tracker not idle. concurrent={concurrent}, exclusive={exclusive}.");
            }
        }
    }

    private sealed class PipelineInjectedException : Exception
    {
        public PipelineInjectedException(string message)
            : base(message)
        {
        }
    }
}

