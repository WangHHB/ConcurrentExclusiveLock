// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock.testandbenchmark;

import com.sun.management.OperatingSystemMXBean;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLock;

import java.lang.management.ManagementFactory;
import java.time.Duration;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.atomic.AtomicReference;
import java.util.concurrent.locks.ReentrantLock;
import java.util.concurrent.locks.ReentrantReadWriteLock;
import java.util.concurrent.locks.StampedLock;

final class BenchmarkRunner {
    private static final double[] READ_PERCENTAGES = {100.0, 99.5, 90.0, 50.0, 30.0, 0.0};
    private static final int PROCESSORS = Math.max(1, Runtime.getRuntime().availableProcessors());
    private static volatile long blackhole;

    private BenchmarkRunner() {
    }

    static void runStandard(CliOptions options) {
        validateWorkload(options.workload);
        printEnvironment(options);

        for (double readPercent : READ_PERCENTAGES) {
            System.out.printf(Locale.ROOT, "%nScenario: read/write %s/%s%n",
                    formatPercent(readPercent), formatPercent(100.0 - readPercent));
            System.out.printf(Locale.ROOT,
                    "  %-26s %10s %10s %14s %14s %12s %13s %13s %15s  %s%n",
                    "lock type", "elapsed", "cpu%", "works/s", "works/s/lock", "work/cpu%",
                    "reads", "writes", "avg write ns", "state");

            String expectedState = null;
            for (Strategy strategy : Strategy.values()) {
                int warmupOperations = Math.min(2_000, Math.max(100, options.operations / 20));
                runScenario(strategy, readPercent, options, warmupOperations, false);
                Result result = runScenario(strategy, readPercent, options, options.operations, true);

                if (expectedState == null) {
                    expectedState = result.state;
                } else if (!expectedState.equals(result.state)) {
                    throw new AssertionError("final work state differs for " + strategy
                            + " expected=" + expectedState + " actual=" + result.state);
                }

                String cpu = Double.isNaN(result.cpuPercent)
                        ? "n/a"
                        : String.format(Locale.ROOT, "%.1f%%", result.cpuPercent);
                String workCpu = Double.isNaN(result.cpuPercent) || result.cpuPercent <= 0.0
                        ? "n/a"
                        : String.format(Locale.ROOT, "%.0f", result.worksPerSecond / result.cpuPercent);

                System.out.printf(Locale.ROOT,
                        "  %-26s %9.3fs %10s %14.0f %14.0f %12s %,13d %,13d %15.1f  %s%n",
                        strategy.displayName,
                        result.elapsedSeconds,
                        cpu,
                        result.worksPerSecond,
                        result.worksPerSecond / options.lockInstances,
                        workCpu,
                        result.reads,
                        result.writes,
                        result.averageWriteNanos,
                        result.state);
            }
        }
        System.out.println();
        System.out.println("sink=" + Long.toUnsignedString(blackhole));
    }

    private static void printEnvironment(CliOptions options) {
        int totalThreads = Math.multiplyExact(options.lockInstances, options.threads);
        long totalOperations = Math.multiplyExact((long) totalThreads, options.operations);

        System.out.println("Lock benchmark");
        System.out.printf("Java=%s, VM=%s, OS=%s %s%n",
                System.getProperty("java.version"),
                System.getProperty("java.vm.name"),
                System.getProperty("os.name"),
                System.getProperty("os.version"));
        System.out.printf("CPU=%d, GC=%s%n", PROCESSORS, gcNames());
        System.out.println();
        System.out.printf(Locale.ROOT,
                "lock-instances=%d, threads/lock=%d, total-threads=%d, works/thread=%,d, read-steps=%d, write-steps=%d%n",
                options.lockInstances, options.threads, totalThreads, options.operations,
                options.readWork, options.writeWork);
        System.out.println(workloadDescription(options));
        System.out.println("Workers use dedicated Thread instances and start from a common gate.");
        System.out.println("Each lock instance owns a fresh Work; every strategy is measured in a separate round.");
        System.out.printf(Locale.ROOT, "total lock operations per strategy/scenario=%,d%n", totalOperations);
    }

    private static String gcNames() {
        StringBuilder builder = new StringBuilder();
        for (var bean : ManagementFactory.getGarbageCollectorMXBeans()) {
            if (builder.length() != 0) {
                builder.append(", ");
            }
            builder.append(bean.getName());
        }
        return builder.isEmpty() ? "unknown" : builder.toString();
    }

    private static String workloadDescription(CliOptions options) {
        return switch (options.workload) {
            case "cpu" -> String.format(Locale.ROOT,
                    "workload=cpu (read-steps=%d, write-steps=%d)", options.readWork, options.writeWork);
            case "memory" -> String.format(Locale.ROOT,
                    "workload=memory (%d MiB shared per lock, read-steps=%d, write-steps=%d)",
                    options.memoryMb, options.readWork, options.writeWork);
            case "dictionary" -> String.format(Locale.ROOT,
                    "workload=dictionary (size=%d per lock, read-steps=%d, write-steps=%d)",
                    options.dictionarySize, options.readWork, options.writeWork);
            case "ledger" -> String.format(Locale.ROOT,
                    "workload=ledger (accounts=%d per lock, read-steps=%d, write-steps=%d)",
                    options.dictionarySize, options.readWork, options.writeWork);
            case "payload" -> String.format(Locale.ROOT,
                    "workload=payload (bytes=%d per lock, read-steps=%d, write-steps=%d)",
                    options.dictionarySize, options.readWork, options.writeWork);
            default -> throw new IllegalArgumentException("unsupported workload: " + options.workload);
        };
    }

    private static String formatPercent(double value) {
        if (value == Math.rint(value)) {
            return String.format(Locale.ROOT, "%.0f", value);
        }
        return String.format(Locale.ROOT, "%.1f", value);
    }

    static void runAdvanced(CliOptions options) {
        System.out.printf("[advanced-perf] threads=%d operations/thread=%d work=%d%n",
                options.threads, options.operations, options.work);
        for (AdvancedPath path : AdvancedPath.values()) {
            AdvancedResult result = runAdvancedPath(path, options);
            System.out.printf(Locale.ROOT, "%-34s elapsed=%8.3fs operations/s=%12.0f%n",
                    path.displayName, result.elapsedSeconds, result.operationsPerSecond);
        }
    }

    private static Result runScenario(
            Strategy strategy,
            double readPercent,
            CliOptions options,
            int operations,
            boolean measured) {

        int totalThreads = Math.multiplyExact(options.lockInstances, options.threads);
        BenchInstance[] instances = new BenchInstance[options.lockInstances];
        for (int index = 0; index < instances.length; index++) {
            instances[index] = new BenchInstance(createWork(options, index));
        }

        WorkerResult[] workerResults = new WorkerResult[totalThreads];
        CountDownLatch ready = new CountDownLatch(totalThreads);
        CountDownLatch start = new CountDownLatch(1);
        CountDownLatch done = new CountDownLatch(totalThreads);
        AtomicReference<Throwable> failure = new AtomicReference<>();
        int readBasis = (int) Math.round(readPercent * 100.0);

        for (int workerIndex = 0; workerIndex < totalThreads; workerIndex++) {
            final int index = workerIndex;
            final int lockIndex = workerIndex / options.threads;
            Thread thread = new Thread(() -> {
                long local = 0L;
                long reads = 0L;
                long writes = 0L;
                long writeNanos = 0L;
                BenchInstance instance = instances[lockIndex];
                ready.countDown();
                try {
                    start.await();
                    for (int operation = 0; operation < operations; operation++) {
                        int selector = Math.floorMod((int) TestSupport.mix64(
                                (((long) index) << 32) ^ operation), 10_000);
                        boolean read = selector < readBasis;
                        if (read) {
                            local ^= executeRead(strategy, instance, index, operation, options.readWork);
                            reads++;
                        } else {
                            if (measured) {
                                long writeStart = System.nanoTime();
                                local ^= executeWrite(strategy, instance, index, operation, options.writeWork);
                                writeNanos += System.nanoTime() - writeStart;
                            } else {
                                local ^= executeWrite(strategy, instance, index, operation, options.writeWork);
                            }
                            writes++;
                        }
                    }
                    workerResults[index] = new WorkerResult(reads, writes, writeNanos, local);
                } catch (Throwable exception) {
                    failure.compareAndSet(null, exception);
                } finally {
                    done.countDown();
                }
            }, "benchmark-" + strategy.name().toLowerCase(Locale.ROOT) + "-" + workerIndex);
            thread.setDaemon(true);
            thread.start();
        }

        TestSupport.await(ready, Duration.ofSeconds(30), "benchmark workers did not become ready");
        long cpuStart = measured ? processCpuTime() : -1L;
        long startNanos = System.nanoTime();
        start.countDown();
        TestSupport.await(done, Duration.ofMinutes(15), "benchmark scenario timed out");
        long elapsedNanos = System.nanoTime() - startNanos;
        long cpuEnd = measured ? processCpuTime() : -1L;

        Throwable exception = failure.get();
        if (exception != null) {
            rethrow(exception);
        }

        long reads = 0L;
        long writes = 0L;
        long writeNanos = 0L;
        long local = 0L;
        for (WorkerResult result : workerResults) {
            reads += result.reads;
            writes += result.writes;
            writeNanos += result.writeNanos;
            local ^= result.blackhole;
        }
        blackhole ^= local;

        StringBuilder state = new StringBuilder();
        for (BenchInstance instance : instances) {
            state.append(instance.work.state()).append(';');
        }

        double seconds = elapsedNanos / 1_000_000_000.0;
        double cpuPercent = cpuStart >= 0 && cpuEnd >= cpuStart
                ? (cpuEnd - cpuStart) * 100.0 / (Math.max(1L, elapsedNanos) * PROCESSORS)
                : Double.NaN;
        double worksPerSecond = (reads + writes) / Math.max(seconds, 1e-9);
        double averageWriteNanos = writes == 0L ? 0.0 : (double) writeNanos / writes;
        return new Result(seconds, cpuPercent, worksPerSecond, reads, writes,
                averageWriteNanos, state.toString());
    }

    private static long executeRead(Strategy strategy, BenchInstance instance, int worker, int operation, int steps) {
        return switch (strategy) {
            case SYNCHRONIZED -> {
                synchronized (instance.monitor) {
                    yield instance.work.read(worker, operation, steps);
                }
            }
            case REENTRANT_LOCK -> {
                instance.mutex.lock();
                try {
                    yield instance.work.read(worker, operation, steps);
                } finally {
                    instance.mutex.unlock();
                }
            }
            case REENTRANT_READ_WRITE_LOCK -> {
                instance.rw.readLock().lock();
                try {
                    yield instance.work.read(worker, operation, steps);
                } finally {
                    instance.rw.readLock().unlock();
                }
            }
            case STAMPED_LOCK -> {
                long stamp = instance.stamped.readLock();
                try {
                    yield instance.work.read(worker, operation, steps);
                } finally {
                    instance.stamped.unlockRead(stamp);
                }
            }
            case CONCURRENT_EXCLUSIVE_LOCK -> {
                instance.cel.acquireConcurrent();
                try {
                    yield instance.work.read(worker, operation, steps);
                } finally {
                    instance.cel.releaseConcurrent();
                }
            }
            case CEL_EXCLUSIVE_ONLY -> {
                instance.cel.acquireExclusive();
                try {
                    yield instance.work.read(worker, operation, steps);
                } finally {
                    instance.cel.releaseExclusive();
                }
            }
        };
    }

    private static long executeWrite(Strategy strategy, BenchInstance instance, int worker, int operation, int steps) {
        return switch (strategy) {
            case SYNCHRONIZED -> {
                synchronized (instance.monitor) {
                    yield instance.work.write(worker, operation, steps);
                }
            }
            case REENTRANT_LOCK -> {
                instance.mutex.lock();
                try {
                    yield instance.work.write(worker, operation, steps);
                } finally {
                    instance.mutex.unlock();
                }
            }
            case REENTRANT_READ_WRITE_LOCK -> {
                instance.rw.writeLock().lock();
                try {
                    yield instance.work.write(worker, operation, steps);
                } finally {
                    instance.rw.writeLock().unlock();
                }
            }
            case STAMPED_LOCK -> {
                long stamp = instance.stamped.writeLock();
                try {
                    yield instance.work.write(worker, operation, steps);
                } finally {
                    instance.stamped.unlockWrite(stamp);
                }
            }
            case CONCURRENT_EXCLUSIVE_LOCK, CEL_EXCLUSIVE_ONLY -> {
                instance.cel.acquireExclusive();
                try {
                    yield instance.work.write(worker, operation, steps);
                } finally {
                    instance.cel.releaseExclusive();
                }
            }
        };
    }

    private static Work createWork(CliOptions options, int instanceIndex) {
        return switch (options.workload) {
            case "cpu" -> new CpuWork(instanceIndex);
            case "memory" -> new MemoryWork(options.memoryMb, instanceIndex);
            case "dictionary" -> new DictionaryWork(options.dictionarySize, instanceIndex);
            case "ledger" -> new LedgerWork(options.dictionarySize, instanceIndex);
            case "payload" -> new PayloadWork(options.dictionarySize, instanceIndex);
            default -> throw new IllegalArgumentException("unsupported workload: " + options.workload);
        };
    }

    private static void validateWorkload(String workload) {
        switch (workload) {
            case "cpu", "memory", "dictionary", "ledger", "payload" -> { }
            default -> throw new IllegalArgumentException(
                    "workload must be cpu, memory, dictionary, ledger, or payload");
        }
    }

    private static AdvancedResult runAdvancedPath(AdvancedPath path, CliOptions options) {
        ConcurrentExclusiveLock lock = ConcurrentExclusiveLock.create();
        int workers = options.threads;
        long totalOperations = Math.multiplyExact((long) workers, options.operations);
        CountDownLatch ready = new CountDownLatch(workers);
        CountDownLatch start = new CountDownLatch(1);
        CountDownLatch done = new CountDownLatch(workers);
        AtomicReference<Throwable> failure = new AtomicReference<>();

        for (int worker = 0; worker < workers; worker++) {
            final int workerIndex = worker;
            Thread thread = new Thread(() -> {
                long local = 0L;
                ready.countDown();
                try {
                    start.await();
                    for (int operation = 0; operation < options.operations; operation++) {
                        long seed = (((long) workerIndex) << 32) ^ operation;
                        switch (path) {
                            case CONCURRENT -> {
                                lock.acquireConcurrent();
                                local ^= TestSupport.busyWork(seed, options.work);
                                lock.releaseConcurrent();
                            }
                            case EXCLUSIVE -> {
                                lock.acquireExclusive();
                                local ^= TestSupport.busyWork(seed, options.work);
                                lock.releaseExclusive();
                            }
                            case EXCLUSIVE_TO_CONCURRENT -> {
                                lock.acquireExclusive();
                                local ^= TestSupport.busyWork(seed, options.work);
                                lock.exclusiveToConcurrent();
                                local ^= TestSupport.busyWork(seed + 1, options.work);
                                lock.releaseConcurrent();
                            }
                            case CONCURRENT_TO_EXCLUSIVE -> {
                                lock.acquireConcurrent();
                                local ^= TestSupport.busyWork(seed, options.work);
                                lock.concurrentToExclusive();
                                local ^= TestSupport.busyWork(seed + 1, options.work);
                                lock.releaseExclusive();
                            }
                            case CONDITIONAL_CONTEXT_UPGRADE -> {
                                lock.acquireConcurrent();
                                int id = workerIndex * options.operations + operation + 1;
                                if (lock.tryConcurrentToExclusiveWithSwitchContextID(id)) {
                                    local ^= TestSupport.busyWork(seed, options.work);
                                    lock.releaseExclusive();
                                }
                            }
                        }
                    }
                    blackhole ^= local;
                } catch (Throwable exception) {
                    failure.compareAndSet(null, exception);
                } finally {
                    done.countDown();
                }
            }, "advanced-perf-" + path.name().toLowerCase(Locale.ROOT) + "-" + worker);
            thread.setDaemon(true);
            thread.start();
        }

        TestSupport.await(ready, Duration.ofSeconds(30), "advanced performance workers did not become ready");
        long startNanos = System.nanoTime();
        start.countDown();
        TestSupport.await(done, Duration.ofMinutes(15), "advanced performance path timed out");
        long elapsed = System.nanoTime() - startNanos;

        Throwable exception = failure.get();
        if (exception != null) {
            rethrow(exception);
        }

        double seconds = elapsed / 1_000_000_000.0;
        return new AdvancedResult(seconds, totalOperations / Math.max(seconds, 1e-9));
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

    private static long processCpuTime() {
        java.lang.management.OperatingSystemMXBean bean = ManagementFactory.getOperatingSystemMXBean();
        if (bean instanceof OperatingSystemMXBean osBean) {
            return osBean.getProcessCpuTime();
        }
        return -1L;
    }

    private enum Strategy {
        SYNCHRONIZED("synchronized"),
        REENTRANT_LOCK("ReentrantLock"),
        REENTRANT_READ_WRITE_LOCK("ReentrantReadWriteLock"),
        STAMPED_LOCK("StampedLock"),
        CONCURRENT_EXCLUSIVE_LOCK("CEL"),
        CEL_EXCLUSIVE_ONLY("CEL(ExclusiveOnly)");

        private final String displayName;

        Strategy(String displayName) {
            this.displayName = displayName;
        }
    }

    private enum AdvancedPath {
        CONCURRENT("Concurrent"),
        EXCLUSIVE_TO_CONCURRENT("Exclusive -> Concurrent"),
        EXCLUSIVE("Exclusive"),
        CONCURRENT_TO_EXCLUSIVE("Concurrent -> Exclusive"),
        CONDITIONAL_CONTEXT_UPGRADE("Conditional ContextID upgrade");

        private final String displayName;

        AdvancedPath(String displayName) {
            this.displayName = displayName;
        }
    }

    private static final class BenchInstance {
        final Object monitor = new Object();
        final ReentrantLock mutex = new ReentrantLock(false);
        final ReentrantReadWriteLock rw = new ReentrantReadWriteLock(false);
        final StampedLock stamped = new StampedLock();
        final ConcurrentExclusiveLock cel = ConcurrentExclusiveLock.create();
        final Work work;

        BenchInstance(Work work) {
            this.work = work;
        }
    }

    private interface Work {
        long read(int worker, int operation, int steps);
        long write(int worker, int operation, int steps);
        String state();
    }

    private static final class CpuWork implements Work {
        private long value;

        CpuWork(int instanceIndex) {
            value = 0x1234abcdL ^ instanceIndex;
        }

        @Override
        public long read(int worker, int operation, int steps) {
            return TestSupport.busyWork(value ^ worker ^ operation, steps);
        }

        @Override
        public long write(int worker, int operation, int steps) {
            long delta = TestSupport.mix64((((long) worker) << 32) ^ operation);
            value += delta;
            return TestSupport.busyWork(delta, steps);
        }

        @Override
        public String state() {
            return Long.toUnsignedString(value, 16);
        }
    }

    private static final class MemoryWork implements Work {
        private final int[] data;

        MemoryWork(int memoryMb, int instanceIndex) {
            int length = Math.max(1, Math.multiplyExact(memoryMb, 1024 * 1024) / Integer.BYTES);
            data = new int[length];
            data[instanceIndex % length] = instanceIndex;
        }

        @Override
        public long read(int worker, int operation, int steps) {
            long value = 0L;
            long seed = TestSupport.mix64((((long) worker) << 32) ^ operation);
            for (int index = 0; index < steps; index++) {
                int slot = Math.floorMod((int) (seed + index * 0x9e3779b9L), data.length);
                value += data[slot];
            }
            return value;
        }

        @Override
        public long write(int worker, int operation, int steps) {
            long seed = TestSupport.mix64((((long) worker) << 32) ^ operation);
            int repeats = Math.max(1, steps);
            long value = 0L;
            for (int index = 0; index < repeats; index++) {
                int slot = Math.floorMod((int) (seed + index * 0x7f4a7c15L), data.length);
                int delta = (int) TestSupport.mix64(seed + index);
                data[slot] += delta;
                value += data[slot];
            }
            return value;
        }

        @Override
        public String state() {
            long sum = 0L;
            long weighted = 0L;
            for (int index = 0; index < data.length; index++) {
                sum += data[index];
                weighted += (long) data[index] * (index + 1L);
            }
            return Long.toUnsignedString(sum, 16) + ':' + Long.toUnsignedString(weighted, 16);
        }
    }

    private static final class DictionaryWork implements Work {
        private final Map<Integer, Integer> map;
        private final int size;

        DictionaryWork(int size, int instanceIndex) {
            this.size = Math.max(1, size);
            map = new HashMap<>(this.size * 2);
            for (int index = 0; index < this.size; index++) {
                map.put(index, index ^ instanceIndex);
            }
        }

        @Override
        public long read(int worker, int operation, int steps) {
            long value = 0L;
            int repeats = Math.max(1, steps);
            for (int index = 0; index < repeats; index++) {
                int key = Math.floorMod((int) TestSupport.mix64((((long) worker) << 32) ^ operation ^ index), size);
                value += map.get(key);
            }
            return value;
        }

        @Override
        public long write(int worker, int operation, int steps) {
            int repeats = Math.max(1, steps);
            long value = 0L;
            for (int index = 0; index < repeats; index++) {
                long seed = TestSupport.mix64((((long) worker) << 32) ^ operation ^ index);
                int key = Math.floorMod((int) seed, size);
                int delta = (int) (seed >>> 32);
                value += map.merge(key, delta, Integer::sum);
            }
            return value;
        }

        @Override
        public String state() {
            long sum = 0L;
            long weighted = 0L;
            for (int key = 0; key < size; key++) {
                int value = map.get(key);
                sum += value;
                weighted += (long) value * (key + 1L);
            }
            return Long.toUnsignedString(sum, 16) + ':' + Long.toUnsignedString(weighted, 16);
        }
    }

    private static final class LedgerWork implements Work {
        private final long[] balances;

        LedgerWork(int size, int instanceIndex) {
            balances = new long[Math.max(2, size)];
            for (int index = 0; index < balances.length; index++) {
                balances[index] = 1_000_000L + instanceIndex + index;
            }
        }

        @Override
        public long read(int worker, int operation, int steps) {
            long value = 0L;
            int repeats = Math.max(1, steps);
            for (int index = 0; index < repeats; index++) {
                int account = Math.floorMod((int) TestSupport.mix64((((long) worker) << 32) ^ operation ^ index), balances.length);
                value ^= balances[account];
            }
            return value;
        }

        @Override
        public long write(int worker, int operation, int steps) {
            int repeats = Math.max(1, steps);
            long value = 0L;
            for (int index = 0; index < repeats; index++) {
                long seed = TestSupport.mix64((((long) worker) << 32) ^ operation ^ index);
                int from = Math.floorMod((int) seed, balances.length);
                int to = Math.floorMod((int) (seed >>> 32), balances.length);
                if (to == from) {
                    to = (to + 1) % balances.length;
                }
                long amount = (seed & 0x3ffL) + 1L;
                balances[from] -= amount;
                balances[to] += amount;
                value ^= balances[from] + balances[to];
            }
            return value;
        }

        @Override
        public String state() {
            long sum = 0L;
            long weighted = 0L;
            for (int index = 0; index < balances.length; index++) {
                sum += balances[index];
                weighted += balances[index] * (index + 1L);
            }
            return Long.toUnsignedString(sum, 16) + ':' + Long.toUnsignedString(weighted, 16);
        }
    }

    private static final class PayloadWork implements Work {
        private final byte[] payload;

        PayloadWork(int size, int instanceIndex) {
            payload = new byte[Math.max(64, size)];
            payload[instanceIndex % payload.length] = (byte) instanceIndex;
        }

        @Override
        public long read(int worker, int operation, int steps) {
            long value = 0L;
            int repeats = Math.max(1, steps);
            for (int index = 0; index < repeats; index++) {
                int slot = Math.floorMod((int) TestSupport.mix64((((long) worker) << 32) ^ operation ^ index), payload.length);
                value = (value << 5) ^ (payload[slot] & 0xffL);
            }
            return value;
        }

        @Override
        public long write(int worker, int operation, int steps) {
            long value = 0L;
            int repeats = Math.max(1, steps);
            for (int index = 0; index < repeats; index++) {
                long seed = TestSupport.mix64((((long) worker) << 32) ^ operation ^ index);
                int slot = Math.floorMod((int) seed, payload.length);
                payload[slot] ^= (byte) (seed >>> 32);
                value += payload[slot] & 0xffL;
            }
            return value;
        }

        @Override
        public String state() {
            long sum = 0L;
            long weighted = 0L;
            for (int index = 0; index < payload.length; index++) {
                int value = payload[index] & 0xff;
                sum += value;
                weighted += (long) value * (index + 1L);
            }
            return Long.toUnsignedString(sum, 16) + ':' + Long.toUnsignedString(weighted, 16);
        }
    }

    private record WorkerResult(long reads, long writes, long writeNanos, long blackhole) { }
    private record Result(
            double elapsedSeconds,
            double cpuPercent,
            double worksPerSecond,
            long reads,
            long writes,
            double averageWriteNanos,
            String state) { }
    private record AdvancedResult(double elapsedSeconds, double operationsPerSecond) { }
}
