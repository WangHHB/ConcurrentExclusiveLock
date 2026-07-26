// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock.testandbenchmark;

import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;

final class TestSupport {
    private TestSupport() {
    }

    static void check(boolean condition, String message) {
        if (!condition) {
            throw new AssertionError(message);
        }
    }

    static void equal(long expected, long actual, String message) {
        if (expected != actual) {
            throw new AssertionError(message + " expected=" + expected + " actual=" + actual);
        }
    }

    static void equal(Object expected, Object actual, String message) {
        if (!java.util.Objects.equals(expected, actual)) {
            throw new AssertionError(message + " expected=" + expected + " actual=" + actual);
        }
    }

    static Duration parseDuration(String text) {
        if (text == null || text.isBlank()) {
            throw new IllegalArgumentException("duration must not be empty");
        }

        String value = text.trim().toLowerCase(Locale.ROOT);
        if (value.indexOf(':') >= 0) {
            String[] parts = value.split(":", -1);
            if (parts.length != 3) {
                throw new IllegalArgumentException("invalid duration: " + text);
            }
            long hours = Long.parseLong(parts[0]);
            long minutes = Long.parseLong(parts[1]);
            long seconds = Long.parseLong(parts[2]);
            if (hours < 0 || minutes < 0 || minutes > 59 || seconds < 0 || seconds > 59) {
                throw new IllegalArgumentException("invalid duration: " + text);
            }
            return Duration.ofHours(hours).plusMinutes(minutes).plusSeconds(seconds);
        }

        char suffix = value.charAt(value.length() - 1);
        String numberPart = Character.isLetter(suffix)
                ? value.substring(0, value.length() - 1)
                : value;
        long amount = Long.parseLong(numberPart);
        if (amount < 0) {
            throw new IllegalArgumentException("duration must not be negative: " + text);
        }

        return switch (suffix) {
            case 's' -> Duration.ofSeconds(amount);
            case 'm' -> Duration.ofMinutes(amount);
            case 'h' -> Duration.ofHours(amount);
            case 'd' -> Duration.ofDays(amount);
            default -> {
                if (Character.isLetter(suffix)) {
                    throw new IllegalArgumentException("invalid duration suffix: " + text);
                }
                yield Duration.ofSeconds(amount);
            }
        };
    }

    static long deadlineAfter(Duration duration) {
        long nanos;
        try {
            nanos = duration.toNanos();
        } catch (ArithmeticException exception) {
            return Long.MAX_VALUE;
        }
        long now = System.nanoTime();
        long deadline = now + nanos;
        if (nanos > 0 && deadline < now) {
            return Long.MAX_VALUE;
        }
        return deadline;
    }

    static boolean beforeDeadline(long deadline) {
        return deadline == Long.MAX_VALUE || deadline - System.nanoTime() > 0L;
    }

    static void await(CountDownLatch latch, Duration timeout, String message) {
        try {
            if (!latch.await(Math.max(1L, timeout.toMillis()), TimeUnit.MILLISECONDS)) {
                throw new AssertionError(message);
            }
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new AssertionError(message, exception);
        }
    }

    static void sleepMillis(long milliseconds) {
        try {
            Thread.sleep(milliseconds);
        } catch (InterruptedException exception) {
            Thread.currentThread().interrupt();
            throw new RuntimeException(exception);
        }
    }

    static void spinUntil(BooleanSupplier condition, Duration timeout, String message) {
        long deadline = deadlineAfter(timeout);
        while (!condition.getAsBoolean()) {
            if (!beforeDeadline(deadline)) {
                throw new AssertionError(message);
            }
            Thread.onSpinWait();
        }
    }

    static void runThreads(String name, int count, Duration timeout, IndexedTask task) {
        check(count > 0, "thread count must be positive");

        AtomicReference<Throwable> failure = new AtomicReference<>();
        CountDownLatch ready = new CountDownLatch(count);
        CountDownLatch start = new CountDownLatch(1);
        CountDownLatch done = new CountDownLatch(count);
        List<Thread> threads = new ArrayList<>(count);

        for (int index = 0; index < count; index++) {
            final int workerIndex = index;
            Thread thread = new Thread(() -> {
                ready.countDown();
                try {
                    start.await();
                    if (failure.get() == null) {
                        task.run(workerIndex);
                    }
                } catch (Throwable exception) {
                    failure.compareAndSet(null, exception);
                } finally {
                    done.countDown();
                }
            }, name + "-" + index);
            thread.setDaemon(true);
            threads.add(thread);
            thread.start();
        }

        await(ready, Duration.ofSeconds(10), name + " workers did not become ready");
        start.countDown();
        await(done, timeout, name + " timed out; possible deadlock");

        Throwable exception = failure.get();
        if (exception != null) {
            if (exception instanceof RuntimeException runtimeException) {
                throw runtimeException;
            }
            if (exception instanceof Error error) {
                throw error;
            }
            throw new RuntimeException(exception);
        }
    }

    static long mix64(long value) {
        value ^= value >>> 33;
        value *= 0xff51afd7ed558ccdl;
        value ^= value >>> 33;
        value *= 0xc4ceb9fe1a85ec53l;
        value ^= value >>> 33;
        return value;
    }

    static long busyWork(long seed, int steps) {
        long value = seed;
        for (int index = 0; index < steps; index++) {
            value ^= value << 13;
            value ^= value >>> 7;
            value ^= value << 17;
            value += 0x9e3779b97f4a7c15L + index;
        }
        return value;
    }

    @FunctionalInterface
    interface IndexedTask {
        void run(int index) throws Exception;
    }

    @FunctionalInterface
    interface BooleanSupplier {
        boolean getAsBoolean();
    }

    static final class AccessProbe {
        private final AtomicInteger concurrent = new AtomicInteger();
        private final AtomicInteger exclusive = new AtomicInteger();

        void enterConcurrent() {
            check(exclusive.get() == 0, "Concurrent entered while Exclusive was active");
            concurrent.incrementAndGet();
            if (exclusive.get() != 0) {
                concurrent.decrementAndGet();
                throw new AssertionError("Concurrent overlapped Exclusive");
            }
        }

        void exitConcurrent() {
            int value = concurrent.decrementAndGet();
            check(value >= 0, "Concurrent probe underflow");
        }

        void enterExclusive() {
            check(exclusive.compareAndSet(0, 1), "Exclusive overlapped another Exclusive");
            if (concurrent.get() != 0) {
                exclusive.set(0);
                throw new AssertionError("Exclusive overlapped Concurrent");
            }
        }

        void exitExclusive() {
            check(exclusive.compareAndSet(1, 0), "Exclusive probe was not active");
        }

        void assertIdle() {
            equal(0, concurrent.get(), "Concurrent probe did not return to zero");
            equal(0, exclusive.get(), "Exclusive probe did not return to zero");
        }
    }
}
