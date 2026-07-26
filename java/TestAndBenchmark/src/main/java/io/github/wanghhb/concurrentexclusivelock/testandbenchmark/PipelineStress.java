// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock.testandbenchmark;

import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveAccessMode;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLock;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockPipeline;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockSegment;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockState;

import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.SplittableRandom;
import java.util.concurrent.atomic.AtomicLong;

final class PipelineStress {
    private PipelineStress() {
    }

    static void run(Duration duration, CliOptions options) {
        long deadline = TestSupport.deadlineAfter(duration);
        long rootSeed = options.semanticSeed;
        int batch = 0;
        long totalPipelines = 0L;
        boolean first = true;

        System.out.printf("[pipeline-stress] duration=%s maxLocks=%d maxWorkers/lock=%d maxRounds/lock=%d seed=%d%n",
                duration,
                options.lockInstances,
                options.semanticWorkers,
                options.semanticOperations,
                rootSeed);

        while (first || TestSupport.beforeDeadline(deadline)) {
            first = false;
            batch++;
            long batchSeed = rootSeed + TestSupport.mix64(batch);
            SplittableRandom shape = new SplittableRandom(batchSeed);
            int locks = shape.nextInt(1, options.lockInstances + 1);
            int workers = shape.nextInt(2, options.semanticWorkers + 1);
            int rounds = shape.nextInt(1, options.semanticOperations + 1);

            try {
                long completed = runRandomBatch(locks, workers, rounds, batchSeed);
                totalPipelines += completed;
                if (batch == 1 || batch % 10 == 0) {
                    System.out.printf("[pipeline-stress] batch=%d seed=%d locks=%d workers/lock=%d rounds/lock=%d totalPipelines=%d%n",
                            batch, batchSeed, locks, workers, rounds, totalPipelines);
                }
            } catch (Throwable exception) {
                System.err.printf("[pipeline-stress] FAILED batch=%d seed=%d locks=%d workers/lock=%d rounds/lock=%d%n",
                        batch, batchSeed, locks, workers, rounds);
                throw exception;
            }
        }

        System.out.printf("[pipeline-stress] PASS batches=%d pipelines=%d%n", batch, totalPipelines);
    }

    static void runFixedBatches(int lockCount, int workersPerLock, int rounds, long seed) {
        ConcurrentExclusiveLock[] locks = createLocks(lockCount);
        TestSupport.AccessProbe[] probes = createProbes(lockCount);
        int totalWorkers = Math.multiplyExact(lockCount, workersPerLock);
        Duration timeout = Duration.ofSeconds(Math.max(30L, Math.min(300L,
                (long) lockCount * rounds / 250L + 30L)));

        TestSupport.runThreads("pipeline-fixed", totalWorkers, timeout, workerIndex -> {
            int lockIndex = workerIndex / workersPerLock;
            ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locks[lockIndex]);
            TestSupport.AccessProbe probe = probes[lockIndex];
            int localWorker = workerIndex % workersPerLock;
            for (int round = localWorker; round < rounds; round += workersPerLock) {
                final int currentRound = round;
                int contextID = (int) ((seed + workerIndex + currentRound) & 0x7fffffff);
                pipeline.doPipeline(
                        ConcurrentExclusiveLockSegment.none(() -> TestSupport.busyWork(currentRound, 1)),
                        ConcurrentExclusiveLockSegment.concurrent(() -> concurrentAction(probe, currentRound, 2)),
                        ConcurrentExclusiveLockSegment.convergeConcurrent(() -> concurrentAction(probe, currentRound, 2)),
                        ConcurrentExclusiveLockSegment.convergeExclusive(() -> exclusiveAction(probe, currentRound, 2)),
                        ConcurrentExclusiveLockSegment.convergeConcurrent(() -> concurrentAction(probe, currentRound, 2)),
                        ConcurrentExclusiveLockSegment.tryApplyIDConvergeExclusive(
                                () -> exclusiveAction(probe, currentRound, 2),
                                contextID,
                                ConcurrentExclusiveLockSegment.IDType.CONTEXT_ID),
                        ConcurrentExclusiveLockSegment.none(() -> TestSupport.busyWork(currentRound, 1)));
            }
        });

        assertAllIdle(locks, probes, "fixed pipeline batch");
    }

    private static long runRandomBatch(int lockCount, int workersPerLock, int rounds, long seed) {
        ConcurrentExclusiveLock[] locks = createLocks(lockCount);
        TestSupport.AccessProbe[] probes = createProbes(lockCount);
        int totalWorkers = Math.multiplyExact(lockCount, workersPerLock);
        AtomicLong completed = new AtomicLong();
        Duration timeout = Duration.ofSeconds(Math.max(30L, Math.min(300L,
                (long) lockCount * rounds / 150L + 30L)));

        TestSupport.runThreads("pipeline-random", totalWorkers, timeout, workerIndex -> {
            int lockIndex = workerIndex / workersPerLock;
            ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(locks[lockIndex]);
            TestSupport.AccessProbe probe = probes[lockIndex];
            SplittableRandom random = new SplittableRandom(seed + TestSupport.mix64(workerIndex + 1L));

            int localWorker = workerIndex % workersPerLock;
            for (int round = localWorker; round < rounds; round += workersPerLock) {
                pipeline.doPipeline(randomSegments(random, probe, workerIndex, round));
                completed.incrementAndGet();
            }
        });

        assertAllIdle(locks, probes, "random pipeline batch");
        return completed.get();
    }

    private static ConcurrentExclusiveLockSegment[] randomSegments(
            SplittableRandom random,
            TestSupport.AccessProbe probe,
            int workerIndex,
            int round) {

        int length = random.nextInt(1, 9);
        List<ConcurrentExclusiveLockSegment> segments = new ArrayList<>(length);
        for (int index = 0; index < length; index++) {
            ConcurrentExclusiveAccessMode mode = ConcurrentExclusiveAccessMode.values()[random.nextInt(9)];
            int steps = random.nextInt(0, 5);
            long seed = TestSupport.mix64((((long) workerIndex) << 32) ^ ((long) round << 8) ^ index);

            Runnable none = () -> TestSupport.busyWork(seed, steps);
            Runnable concurrent = () -> {
                probe.enterConcurrent();
                try {
                    TestSupport.busyWork(seed, steps);
                } finally {
                    probe.exitConcurrent();
                }
            };
            Runnable exclusive = () -> {
                probe.enterExclusive();
                try {
                    TestSupport.busyWork(seed, steps);
                } finally {
                    probe.exitExclusive();
                }
            };

            ConcurrentExclusiveLockSegment segment = switch (mode) {
                case NONE -> ConcurrentExclusiveLockSegment.none(none);
                case CONCURRENT -> ConcurrentExclusiveLockSegment.concurrent(concurrent);
                case TRY_CONCURRENT -> ConcurrentExclusiveLockSegment.tryConcurrent(concurrent);
                case EXCLUSIVE -> ConcurrentExclusiveLockSegment.exclusive(exclusive);
                case TEST_EXCLUSIVE -> ConcurrentExclusiveLockSegment.testExclusive(exclusive);
                case TRY_EXCLUSIVE -> ConcurrentExclusiveLockSegment.tryExclusive(exclusive);
                case CONVERGE_CONCURRENT -> ConcurrentExclusiveLockSegment.convergeConcurrent(concurrent);
                case CONVERGE_EXCLUSIVE -> ConcurrentExclusiveLockSegment.convergeExclusive(exclusive);
                case TRY_APPLY_ID_CONVERGE_EXCLUSIVE -> {
                    boolean context = random.nextBoolean();
                    int id = context
                            ? random.nextInt(1, 33)
                            : Math.max(1, round * 16 + index + random.nextInt(1, 16));
                    yield ConcurrentExclusiveLockSegment.tryApplyIDConvergeExclusive(
                            exclusive,
                            id,
                            context
                                    ? ConcurrentExclusiveLockSegment.IDType.CONTEXT_ID
                                    : ConcurrentExclusiveLockSegment.IDType.EPOCH_ID);
                }
            };
            segments.add(segment);
        }
        return segments.toArray(ConcurrentExclusiveLockSegment[]::new);
    }

    private static ConcurrentExclusiveLock[] createLocks(int count) {
        ConcurrentExclusiveLock[] locks = new ConcurrentExclusiveLock[count];
        for (int index = 0; index < count; index++) {
            locks[index] = ConcurrentExclusiveLock.create();
        }
        return locks;
    }

    private static TestSupport.AccessProbe[] createProbes(int count) {
        TestSupport.AccessProbe[] probes = new TestSupport.AccessProbe[count];
        for (int index = 0; index < count; index++) {
            probes[index] = new TestSupport.AccessProbe();
        }
        return probes;
    }

    private static void assertAllIdle(
            ConcurrentExclusiveLock[] locks,
            TestSupport.AccessProbe[] probes,
            String context) {

        for (int index = 0; index < locks.length; index++) {
            probes[index].assertIdle();
            TestSupport.equal(
                    ConcurrentExclusiveLockState.IDLE,
                    locks[index].observedState(),
                    context + " lock " + index + " must return to IDLE");
        }
    }

    private static void concurrentAction(TestSupport.AccessProbe probe, long seed, int steps) {
        probe.enterConcurrent();
        try {
            TestSupport.busyWork(seed, steps);
        } finally {
            probe.exitConcurrent();
        }
    }

    private static void exclusiveAction(TestSupport.AccessProbe probe, long seed, int steps) {
        probe.enterExclusive();
        try {
            TestSupport.busyWork(seed, steps);
        } finally {
            probe.exitExclusive();
        }
    }
}
