// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock.testandbenchmark;

import com.sun.management.OperatingSystemMXBean;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLock;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockState;

import java.lang.management.GarbageCollectorMXBean;
import java.lang.management.ManagementFactory;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.SplittableRandom;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import java.util.concurrent.atomic.AtomicReference;

final class StressRunners {
    private StressRunners() {
    }

    static void runEndurance(Duration duration, CliOptions options) {
        int processors = Runtime.getRuntime().availableProcessors();
        int lockCount = Math.max(1, Math.min(32, processors / 2));
        int workersPerLock = 4;
        int totalWorkers = lockCount * workersPerLock;
        ConcurrentExclusiveLock[] locks = new ConcurrentExclusiveLock[lockCount];
        TestSupport.AccessProbe[] probes = new TestSupport.AccessProbe[lockCount];
        AtomicInteger[] epochs = new AtomicInteger[lockCount];
        for (int index = 0; index < lockCount; index++) {
            locks[index] = ConcurrentExclusiveLock.create();
            probes[index] = new TestSupport.AccessProbe();
            epochs[index] = new AtomicInteger();
        }

        long deadline = TestSupport.deadlineAfter(duration);
        AtomicLong operations = new AtomicLong();
        AtomicReference<Throwable> failure = new AtomicReference<>();
        CountDownLatch ready = new CountDownLatch(totalWorkers);
        CountDownLatch start = new CountDownLatch(1);
        CountDownLatch done = new CountDownLatch(totalWorkers);
        List<Thread> threads = new ArrayList<>(totalWorkers);

        System.out.printf("[endurance] duration=%s locks=%d workers/lock=%d totalWorkers=%d seed=%d%n",
                duration, lockCount, workersPerLock, totalWorkers, options.semanticSeed);

        for (int workerIndex = 0; workerIndex < totalWorkers; workerIndex++) {
            final int index = workerIndex;
            final int lockIndex = workerIndex / workersPerLock;
            Thread thread = new Thread(() -> {
                SplittableRandom random = new SplittableRandom(options.semanticSeed + TestSupport.mix64(index + 1L));
                int operation = 0;
                ready.countDown();
                try {
                    start.await();
                    while (failure.get() == null && TestSupport.beforeDeadline(deadline)) {
                        SemanticTests.executeRandomLegalPath(
                                locks[lockIndex], probes[lockIndex], epochs[lockIndex], random, operation++);
                        operations.incrementAndGet();
                    }
                } catch (Throwable exception) {
                    failure.compareAndSet(null, exception);
                } finally {
                    done.countDown();
                }
            }, "endurance-" + workerIndex);
            thread.setDaemon(true);
            threads.add(thread);
            thread.start();
        }

        TestSupport.await(ready, Duration.ofSeconds(20), "endurance workers did not become ready");
        long wallStart = System.nanoTime();
        long cpuStart = processCpuTime();
        long lastReport = wallStart;
        start.countDown();

        while (done.getCount() != 0 && TestSupport.beforeDeadline(deadline) && failure.get() == null) {
            TestSupport.sleepMillis(200L);
            long now = System.nanoTime();
            if (now - lastReport >= Duration.ofSeconds(10).toNanos()) {
                reportResources("endurance", operations.get(), wallStart, cpuStart);
                lastReport = now;
            }
        }

        TestSupport.await(done, Duration.ofSeconds(60), "endurance workers did not stop");
        Throwable exception = failure.get();
        if (exception != null) {
            rethrow(exception);
        }

        for (int index = 0; index < lockCount; index++) {
            probes[index].assertIdle();
            TestSupport.equal(ConcurrentExclusiveLockState.IDLE, locks[index].observedState(),
                    "endurance lock " + index + " must return to IDLE");
        }

        reportResources("endurance-final", operations.get(), wallStart, cpuStart);
        System.out.println("[endurance] PASS");
    }

    static void runContention(Duration duration, CliOptions options) {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();
        TestSupport.AccessProbe probe = new TestSupport.AccessProbe();
        int workers = Math.max(2, options.threads);
        long deadline = TestSupport.deadlineAfter(duration);
        AtomicLong operations = new AtomicLong();

        System.out.printf("[contention-stress] duration=%s workers=%d seed=%d%n",
                duration, workers, options.semanticSeed);

        TestSupport.runThreads("contention", workers,
                duration.plusSeconds(30), workerIndex -> {
                    SplittableRandom random = new SplittableRandom(
                            options.semanticSeed + TestSupport.mix64(workerIndex + 1L));
                    while (TestSupport.beforeDeadline(deadline)) {
                        if (random.nextInt(100) < 70) {
                            lock.acquireConcurrent();
                            probe.enterConcurrent();
                            TestSupport.busyWork(random.nextLong(), 2);
                            probe.exitConcurrent();
                            lock.releaseConcurrent();
                        } else {
                            lock.acquireExclusive();
                            probe.enterExclusive();
                            TestSupport.busyWork(random.nextLong(), 2);
                            probe.exitExclusive();
                            lock.releaseExclusive();
                        }
                        operations.incrementAndGet();
                    }
                });

        probe.assertIdle();
        TestSupport.equal(ConcurrentExclusiveLockState.IDLE, lock.observedState(),
                "contention lock must return to IDLE");
        System.out.printf("[contention-stress] PASS operations=%d%n", operations.get());
    }

    private static void reportResources(String name, long operations, long wallStart, long cpuStart) {
        long now = System.nanoTime();
        double seconds = Math.max(1e-9, (now - wallStart) / 1_000_000_000.0);
        long cpuNow = processCpuTime();
        double cpuPercent = cpuStart >= 0 && cpuNow >= cpuStart
                ? (cpuNow - cpuStart) * 100.0 / Math.max(1L, now - wallStart)
                : Double.NaN;
        Runtime runtime = Runtime.getRuntime();
        long used = runtime.totalMemory() - runtime.freeMemory();
        long gcCount = 0L;
        for (GarbageCollectorMXBean collector : ManagementFactory.getGarbageCollectorMXBeans()) {
            long count = collector.getCollectionCount();
            if (count > 0) {
                gcCount += count;
            }
        }

        System.out.printf("[%s] elapsed=%.1fs operations=%d ops/s=%.0f cpu%%=%s heapUsed=%.1fMiB threads=%d gc=%d%n",
                name,
                seconds,
                operations,
                operations / seconds,
                Double.isNaN(cpuPercent) ? "n/a" : String.format(java.util.Locale.ROOT, "%.1f", cpuPercent),
                used / 1048576.0,
                ManagementFactory.getThreadMXBean().getThreadCount(),
                gcCount);
    }

    private static long processCpuTime() {
        java.lang.management.OperatingSystemMXBean bean = ManagementFactory.getOperatingSystemMXBean();
        if (bean instanceof OperatingSystemMXBean osBean) {
            return osBean.getProcessCpuTime();
        }
        return -1L;
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
