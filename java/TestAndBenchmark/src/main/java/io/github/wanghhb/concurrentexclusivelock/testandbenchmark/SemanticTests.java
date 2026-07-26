// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock.testandbenchmark;

import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLock;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockPipeline;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockScope;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockSegment;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockState;

import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.SplittableRandom;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

final class SemanticTests {
    private SemanticTests() {
    }

    static void runFull(CliOptions options) {
        System.out.println("[full-semantics] deterministic core contracts");
        testBasicCore();
        testPreemptiveExclusive();
        testUpgradeSerialization(Math.max(2, Math.min(options.semanticWorkers, 32)));
        testConditionalUpgradeWinner(Math.max(2, Math.min(options.semanticWorkers, 32)));

        System.out.println("[full-semantics] scope lifecycle");
        testScope();

        System.out.println("[full-semantics] pipeline contracts");
        testPipelineFixed();

        System.out.printf("[full-semantics] random legal paths locks=%d workers/lock=%d operations/lock=%d seed=%d%n",
                options.lockInstances,
                options.semanticWorkers,
                options.semanticOperations,
                options.semanticSeed);
        runRandomLegalPaths(
                options.lockInstances,
                options.semanticWorkers,
                options.semanticOperations,
                options.semanticSeed);

        System.out.println("[full-semantics] PASS");
    }

    static void runAdvanced(CliOptions options) {
        int workers = Math.max(2, Math.min(options.semanticWorkers, 64));
        System.out.printf("[advanced-correctness] workers=%d locks=%d operations=%d seed=%d%n",
                workers,
                options.lockInstances,
                options.advancedOperations,
                options.advancedSeed);

        testUpgradeSerialization(workers);
        testConditionalUpgradeWinner(workers);
        testDowngradeContinuity(workers);
        runRandomLegalPaths(
                options.lockInstances,
                workers,
                options.advancedOperations,
                options.advancedSeed);

        System.out.println("[advanced-correctness] PASS");
    }

    static void runPipelineSemantics(CliOptions options) {
        System.out.println("[pipeline-semantics] fixed transition contracts");
        testPipelineFixed();

        int lockCount = Math.max(1, options.lockInstances);
        int workers = Math.max(2, options.semanticWorkers);
        int rounds = Math.max(1, options.semanticOperations);
        System.out.printf("[pipeline-semantics] concurrent fixed batches locks=%d workers/lock=%d rounds/lock=%d seed=%d%n",
                lockCount, workers, rounds, options.semanticSeed);

        PipelineStress.runFixedBatches(lockCount, workers, rounds, options.semanticSeed);
        System.out.println("[pipeline-semantics] PASS");
    }

    private static void testBasicCore() {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();
        TestSupport.equal(ConcurrentExclusiveLockState.IDLE, lock.observedState(), "new lock must be idle");
        TestSupport.equal(0, lock.observedContention(), "idle contention must be zero");

        int first = lock.acquireConcurrent();
        int second = lock.tryAcquireConcurrent();
        TestSupport.equal(1, first, "first Concurrent ID");
        TestSupport.equal(2, second, "second Concurrent ID");
        TestSupport.equal(ConcurrentExclusiveLockState.CONCURRENT, lock.observedState(), "Concurrent state");
        lock.releaseConcurrent();
        lock.releaseConcurrent();
        assertIdle(lock, "basic Concurrent release");

        int zeroTimeout = lock.tryAcquireConcurrent(Duration.ZERO);
        TestSupport.check(zeroTimeout != 0, "zero-timeout Concurrent must perform one immediate attempt");
        lock.releaseConcurrent();

        int limited = lock.acquireConcurrent(1);
        TestSupport.equal(1, limited, "limited Concurrent ID");
        TestSupport.equal(0, lock.tryAcquireConcurrent(1), "maxConcurrent must be enforced");
        lock.releaseConcurrent();

        lock.acquireExclusive();
        TestSupport.equal(ConcurrentExclusiveLockState.EXCLUSIVE, lock.observedState(), "Exclusive state");
        lock.releaseExclusive();
        assertIdle(lock, "basic Exclusive release");

        TestSupport.check(lock.tryAcquireExclusive(false), "idle TestExclusive must succeed");
        lock.releaseExclusive();

        lock.acquireConcurrent();
        TestSupport.check(!lock.tryAcquireExclusive(false), "TestExclusive must fail while Concurrent is held");
        lock.releaseConcurrent();

        TestSupport.equal(0, lock.getContextID(), "default ContextID");
        TestSupport.check(lock.switchContextID(7), "first ContextID switch must succeed");
        TestSupport.check(!lock.switchContextID(7), "same ContextID switch must fail");
        TestSupport.equal(7, lock.getContextID(), "ContextID value");

        TestSupport.check(lock.raiseEpochID(2), "EpochID must advance");
        TestSupport.check(!lock.raiseEpochID(2), "EpochID must not remain equal");
        TestSupport.check(!lock.raiseEpochID(1), "EpochID must not move backward");
        TestSupport.check(lock.raiseEpochID(3), "EpochID must advance again");
        TestSupport.equal(3, lock.getEpochID(), "EpochID value");
    }

    private static void testPreemptiveExclusive() {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();
        TestSupport.AccessProbe probe = new TestSupport.AccessProbe();
        int readerCount = 4;
        CountDownLatch readersEntered = new CountDownLatch(readerCount);
        CountDownLatch releaseReaders = new CountDownLatch(1);
        CountDownLatch allDone = new CountDownLatch(readerCount + 1);
        AtomicReference<Throwable> failure = new AtomicReference<>();
        List<Thread> threads = new ArrayList<>();

        for (int index = 0; index < readerCount; index++) {
            Thread reader = new Thread(() -> {
                try {
                    lock.acquireConcurrent();
                    probe.enterConcurrent();
                    readersEntered.countDown();
                    releaseReaders.await();
                    probe.exitConcurrent();
                    lock.releaseConcurrent();
                } catch (Throwable exception) {
                    failure.compareAndSet(null, exception);
                } finally {
                    allDone.countDown();
                }
            }, "preempt-reader-" + index);
            reader.setDaemon(true);
            threads.add(reader);
            reader.start();
        }

        TestSupport.await(readersEntered, Duration.ofSeconds(10), "readers did not enter");

        Thread writer = new Thread(() -> {
            try {
                lock.acquireExclusive();
                probe.enterExclusive();
                TestSupport.busyWork(1, 64);
                probe.exitExclusive();
                lock.releaseExclusive();
            } catch (Throwable exception) {
                failure.compareAndSet(null, exception);
            } finally {
                allDone.countDown();
            }
        }, "preempt-writer");
        writer.setDaemon(true);
        writer.start();

        TestSupport.spinUntil(
                () -> lock.observedState() == ConcurrentExclusiveLockState.EXCLUSIVE,
                Duration.ofSeconds(10),
                "preemptive Exclusive did not enter the contention window");

        TestSupport.equal(0, lock.tryAcquireConcurrent(), "new Concurrent must be blocked by Exclusive pressure");
        releaseReaders.countDown();
        TestSupport.await(allDone, Duration.ofSeconds(30), "preemptive Exclusive test timed out");

        Throwable exception = failure.get();
        if (exception != null) {
            rethrow(exception);
        }
        probe.assertIdle();
        assertIdle(lock, "preemptive Exclusive test");
    }

    private static void testUpgradeSerialization(int workers) {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();
        TestSupport.AccessProbe probe = new TestSupport.AccessProbe();
        CountDownLatch acquired = new CountDownLatch(workers);
        CountDownLatch startUpgrade = new CountDownLatch(1);
        CountDownLatch done = new CountDownLatch(workers);
        AtomicReference<Throwable> failure = new AtomicReference<>();
        AtomicInteger completed = new AtomicInteger();

        for (int index = 0; index < workers; index++) {
            final int workerIndex = index;
            Thread thread = new Thread(() -> {
                try {
                    lock.acquireConcurrent();
                    acquired.countDown();
                    startUpgrade.await();
                    lock.concurrentToExclusive();
                    probe.enterExclusive();
                    completed.incrementAndGet();
                    TestSupport.busyWork(workerIndex + 1L, 64);
                    probe.exitExclusive();
                    lock.releaseExclusive();
                } catch (Throwable exception) {
                    failure.compareAndSet(null, exception);
                } finally {
                    done.countDown();
                }
            }, "upgrade-" + index);
            thread.setDaemon(true);
            thread.start();
        }

        TestSupport.await(acquired, Duration.ofSeconds(10), "upgrade workers did not acquire Concurrent");
        startUpgrade.countDown();
        TestSupport.await(done, Duration.ofSeconds(45), "upgrade serialization test timed out");

        Throwable exception = failure.get();
        if (exception != null) {
            rethrow(exception);
        }
        TestSupport.equal(workers, completed.get(), "all upgrade workers must complete");
        probe.assertIdle();
        assertIdle(lock, "upgrade serialization test");
    }

    private static void testConditionalUpgradeWinner(int workers) {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();
        TestSupport.AccessProbe probe = new TestSupport.AccessProbe();
        AtomicInteger winners = new AtomicInteger();
        CountDownLatch acquired = new CountDownLatch(workers);
        CountDownLatch startUpgrade = new CountDownLatch(1);
        AtomicReference<Throwable> failure = new AtomicReference<>();
        CountDownLatch done = new CountDownLatch(workers);

        for (int index = 0; index < workers; index++) {
            Thread thread = new Thread(() -> {
                try {
                    lock.acquireConcurrent();
                    acquired.countDown();
                    startUpgrade.await();
                    if (lock.tryConcurrentToExclusiveWithSwitchContextID(12345)) {
                        probe.enterExclusive();
                        winners.incrementAndGet();
                        probe.exitExclusive();
                        lock.releaseExclusive();
                    }
                } catch (Throwable exception) {
                    failure.compareAndSet(null, exception);
                } finally {
                    done.countDown();
                }
            }, "conditional-upgrade-" + index);
            thread.setDaemon(true);
            thread.start();
        }

        TestSupport.await(acquired, Duration.ofSeconds(10), "conditional upgrade workers did not acquire Concurrent");
        startUpgrade.countDown();
        TestSupport.await(done, Duration.ofSeconds(45), "conditional upgrade test timed out");

        Throwable exception = failure.get();
        if (exception != null) {
            rethrow(exception);
        }
        TestSupport.equal(1, winners.get(), "same ContextID must produce exactly one upgrade winner");
        TestSupport.equal(12345, lock.getContextID(), "ContextID after conditional upgrade");
        probe.assertIdle();
        assertIdle(lock, "conditional upgrade test");
    }

    private static void testDowngradeContinuity(int workers) {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();
        TestSupport.AccessProbe probe = new TestSupport.AccessProbe();
        AtomicInteger completed = new AtomicInteger();

        TestSupport.runThreads("downgrade", workers, Duration.ofSeconds(45), index -> {
            lock.acquireExclusive();
            probe.enterExclusive();
            TestSupport.busyWork(index, 16);
            probe.exitExclusive();
            lock.exclusiveToConcurrent();
            probe.enterConcurrent();
            TestSupport.busyWork(index, 16);
            probe.exitConcurrent();
            lock.releaseConcurrent();
            completed.incrementAndGet();
        });

        TestSupport.equal(workers, completed.get(), "all downgrade workers must complete");
        probe.assertIdle();
        assertIdle(lock, "downgrade test");
    }

    private static void testScope() {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();

        try (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(lock)) {
            scope.acquireConcurrent();
        }
        assertIdle(lock, "Scope Concurrent close");

        try (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(lock)) {
            scope.acquireExclusive();
        }
        assertIdle(lock, "Scope Exclusive close");

        try {
            try (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(lock)) {
                scope.acquireExclusive();
                throw new IllegalStateException("injected");
            }
        } catch (IllegalStateException expected) {
            TestSupport.equal("injected", expected.getMessage(), "injected exception");
        }
        assertIdle(lock, "Scope exception close");

        ConcurrentExclusiveLockScope closed = new ConcurrentExclusiveLockScope(lock);
        closed.close();
        boolean rejected = false;
        try {
            closed.acquireConcurrent();
        } catch (IllegalStateException expected) {
            rejected = true;
        }
        TestSupport.check(rejected, "closed Scope must reject reuse");

        lock.setContextID(99);
        try (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(lock)) {
            scope.acquireConcurrent();
            TestSupport.check(!scope.tryConcurrentToExclusiveWithSwitchContextID(99),
                    "same ContextID conditional upgrade must fail");
        }
        assertIdle(lock, "Scope conditional-upgrade failure");
    }

    private static void testPipelineFixed() {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();
        ConcurrentExclusiveLockPipeline pipeline = new ConcurrentExclusiveLockPipeline(lock);
        TestSupport.AccessProbe probe = new TestSupport.AccessProbe();
        StringBuilder order = new StringBuilder();

        pipeline.doPipeline(
                ConcurrentExclusiveLockSegment.none(() -> order.append('N')),
                ConcurrentExclusiveLockSegment.concurrent(() -> withConcurrent(probe, () -> order.append('A'))),
                ConcurrentExclusiveLockSegment.convergeConcurrent(() -> withConcurrent(probe, () -> order.append('B'))),
                ConcurrentExclusiveLockSegment.convergeExclusive(() -> withExclusive(probe, () -> order.append('C'))),
                ConcurrentExclusiveLockSegment.convergeExclusive(() -> withExclusive(probe, () -> order.append('D'))),
                ConcurrentExclusiveLockSegment.convergeConcurrent(() -> withConcurrent(probe, () -> order.append('E'))),
                ConcurrentExclusiveLockSegment.none(() -> order.append('F')),
                ConcurrentExclusiveLockSegment.exclusive(() -> withExclusive(probe, () -> order.append('G'))));

        TestSupport.equal("NABCDEFG", order.toString(), "Pipeline execution order");
        assertIdle(lock, "Pipeline normal completion");

        lock.acquireConcurrent();
        AtomicInteger skipped = new AtomicInteger();
        AtomicInteger continued = new AtomicInteger();
        pipeline.doPipeline(
                ConcurrentExclusiveLockSegment.testExclusive(skipped::incrementAndGet),
                ConcurrentExclusiveLockSegment.none(continued::incrementAndGet));
        lock.releaseConcurrent();
        TestSupport.equal(0, skipped.get(), "failed TestExclusive segment must be skipped");
        TestSupport.equal(1, continued.get(), "Pipeline must continue from NONE after Try failure");
        assertIdle(lock, "Pipeline Try failure");

        AtomicInteger idExecutions = new AtomicInteger();
        pipeline.doPipeline(
                ConcurrentExclusiveLockSegment.concurrent(() -> withConcurrent(probe, () -> { })),
                ConcurrentExclusiveLockSegment.tryApplyIDConvergeExclusive(
                        () -> withExclusive(probe, idExecutions::incrementAndGet),
                        7,
                        ConcurrentExclusiveLockSegment.IDType.CONTEXT_ID),
                ConcurrentExclusiveLockSegment.convergeConcurrent(() -> withConcurrent(probe, () -> { })));
        TestSupport.equal(1, idExecutions.get(), "first ID-conditioned segment must execute");
        assertIdle(lock, "Pipeline ID success");

        AtomicInteger afterSkippedID = new AtomicInteger();
        pipeline.doPipeline(
                ConcurrentExclusiveLockSegment.tryApplyIDConvergeExclusive(
                        idExecutions::incrementAndGet,
                        7,
                        ConcurrentExclusiveLockSegment.IDType.CONTEXT_ID),
                ConcurrentExclusiveLockSegment.none(afterSkippedID::incrementAndGet));
        TestSupport.equal(1, idExecutions.get(), "same ContextID segment must be skipped");
        TestSupport.equal(1, afterSkippedID.get(), "Pipeline must continue after ID failure");
        assertIdle(lock, "Pipeline ID failure");

        boolean propagated = false;
        try {
            pipeline.doPipeline(
                    ConcurrentExclusiveLockSegment.exclusive(() -> {
                        throw new IllegalArgumentException("pipeline-injected");
                    }),
                    ConcurrentExclusiveLockSegment.none(() -> {
                        throw new AssertionError("subsequent segment must not execute");
                    }));
        } catch (IllegalArgumentException expected) {
            propagated = true;
        }
        TestSupport.check(propagated, "Pipeline exception must propagate");
        assertIdle(lock, "Pipeline exception release");
        probe.assertIdle();
    }

    private static void runRandomLegalPaths(int lockCount, int workersPerLock, int operations, long seed) {
        ConcurrentExclusiveLock[] locks = new ConcurrentExclusiveLock[lockCount];
        TestSupport.AccessProbe[] probes = new TestSupport.AccessProbe[lockCount];
        AtomicInteger[] epochs = new AtomicInteger[lockCount];
        for (int index = 0; index < lockCount; index++) {
            locks[index] = ConcurrentExclusiveLock.create();
            probes[index] = new TestSupport.AccessProbe();
            epochs[index] = new AtomicInteger();
        }

        int totalWorkers = Math.multiplyExact(lockCount, workersPerLock);
        Duration timeout = Duration.ofSeconds(Math.max(30L, Math.min(300L, (long) operations * lockCount / 250L + 30L)));
        TestSupport.runThreads("semantic", totalWorkers, timeout, workerIndex -> {
            int lockIndex = workerIndex / workersPerLock;
            SplittableRandom random = new SplittableRandom(seed + TestSupport.mix64(workerIndex + 1L));
            int localWorker = workerIndex % workersPerLock;
            for (int operation = localWorker; operation < operations; operation += workersPerLock) {
                executeRandomLegalPath(
                        locks[lockIndex],
                        probes[lockIndex],
                        epochs[lockIndex],
                        random,
                        operation);
            }
        });

        for (int index = 0; index < lockCount; index++) {
            probes[index].assertIdle();
            assertIdle(locks[index], "random semantic lock " + index);
        }
    }

    static void executeRandomLegalPath(
            ConcurrentExclusiveLock lock,
            TestSupport.AccessProbe probe,
            AtomicInteger epoch,
            SplittableRandom random,
            int operation) {

        int path = random.nextInt(8);
        int steps = random.nextInt(0, 8);
        switch (path) {
            case 0 -> {
                lock.acquireConcurrent();
                withConcurrent(probe, () -> TestSupport.busyWork(operation + 1L, steps));
                lock.releaseConcurrent();
            }
            case 1 -> {
                lock.acquireExclusive();
                withExclusive(probe, () -> TestSupport.busyWork(operation + 3L, steps));
                lock.releaseExclusive();
            }
            case 2 -> {
                try (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(lock)) {
                    scope.acquireConcurrent();
                    withConcurrent(probe, () -> TestSupport.busyWork(operation + 5L, steps));
                }
            }
            case 3 -> {
                try (ConcurrentExclusiveLockScope scope = new ConcurrentExclusiveLockScope(lock)) {
                    scope.acquireExclusive();
                    withExclusive(probe, () -> TestSupport.busyWork(operation + 7L, steps));
                }
            }
            case 4 -> {
                lock.acquireConcurrent();
                withConcurrent(probe, () -> TestSupport.busyWork(operation + 11L, steps));
                lock.concurrentToExclusive();
                withExclusive(probe, () -> TestSupport.busyWork(operation + 13L, steps));
                lock.releaseExclusive();
            }
            case 5 -> {
                lock.acquireExclusive();
                withExclusive(probe, () -> TestSupport.busyWork(operation + 17L, steps));
                lock.exclusiveToConcurrent();
                withConcurrent(probe, () -> TestSupport.busyWork(operation + 19L, steps));
                lock.releaseConcurrent();
            }
            case 6 -> {
                lock.acquireConcurrent();
                int contextID = random.nextInt(1, 17);
                if (lock.tryConcurrentToExclusiveWithSwitchContextID(contextID)) {
                    withExclusive(probe, () -> TestSupport.busyWork(operation + 23L, steps));
                    lock.releaseExclusive();
                }
            }
            case 7 -> {
                lock.acquireConcurrent();
                int candidateEpoch = epoch.incrementAndGet();
                if (lock.tryConcurrentToExclusiveWithRaiseEpochID(candidateEpoch)) {
                    withExclusive(probe, () -> TestSupport.busyWork(operation + 29L, steps));
                    lock.releaseExclusive();
                }
            }
            default -> throw new AssertionError("unreachable path");
        }
    }

    private static void withConcurrent(TestSupport.AccessProbe probe, Runnable action) {
        probe.enterConcurrent();
        try {
            action.run();
        } finally {
            probe.exitConcurrent();
        }
    }

    private static void withExclusive(TestSupport.AccessProbe probe, Runnable action) {
        probe.enterExclusive();
        try {
            action.run();
        } finally {
            probe.exitExclusive();
        }
    }

    private static void assertIdle(ConcurrentExclusiveLock lock, String context) {
        TestSupport.equal(ConcurrentExclusiveLockState.IDLE, lock.observedState(), context + " must end idle");
    }

    private static void rethrow(Throwable exception) {
        if (exception instanceof RuntimeException runtimeException) {
            throw runtimeException;
        }
        if (exception instanceof Error error) {
            throw error;
        }
        throw new RuntimeException(exception);
    }
}
