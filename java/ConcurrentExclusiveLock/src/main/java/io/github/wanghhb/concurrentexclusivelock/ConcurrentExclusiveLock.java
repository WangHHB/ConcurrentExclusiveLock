// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock;

import java.lang.invoke.MethodHandles;
import java.lang.invoke.VarHandle;
import java.time.Duration;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.ReentrantLock;

/**
 * Provides a high-performance, non-recursive synchronization lock based on
 * Concurrent / Exclusive access permissions.
 *
 * <p>This lock expresses access permissions rather than read/write intent.
 * Concurrent means that the current operation may run together with other
 * Concurrent operations. Exclusive means that the current operation must run
 * alone.</p>
 *
 * <p>Ordinary Concurrent acquisition and release primarily use lightweight
 * atomic counting. Ordinary Exclusive acquisition and in-place conversion from
 * Concurrent to Exclusive use an internal non-fair {@link ReentrantLock} for
 * exclusive scheduling, waiting, and thread ownership.</p>
 *
 * <p>Exclusive permission is thread-affine. It must be released or downgraded
 * by the same thread that acquired it.</p>
 *
 * <p>This class is the first Java port of the C# implementation and intentionally
 * keeps the original counter protocol. The 128-bit protocol state is embedded
 * directly in this object; the non-fair ReentrantLock is also allocated directly
 * with the lock instance. It targets Java 17 or later.</p>
 */
public final class ConcurrentExclusiveLock {

    /** Maximum supported Concurrent holder count. */
    public static final int MAX_CONCURRENT = Integer.MAX_VALUE;

    static final long EXCLUSIVE_ADD = 1L << 32;
    static final long CONVERGE_ADD = EXCLUSIVE_ADD - 1L;
    static final int SHIFT_COUNT = 32;

    private static final long TWO_EXCLUSIVE = EXCLUSIVE_ADD * 2L;

    private static final VarHandle COUNTER;
    private static final VarHandle CONTEXT_ID;
    private static final VarHandle EPOCH_ID;

    static {
        try {
            MethodHandles.Lookup lookup = MethodHandles.lookup();
            COUNTER = lookup.findVarHandle(ConcurrentExclusiveLock.class, "counter", long.class);
            CONTEXT_ID = lookup.findVarHandle(ConcurrentExclusiveLock.class, "contextID", int.class);
            EPOCH_ID = lookup.findVarHandle(ConcurrentExclusiveLock.class, "epochID", int.class);
        } catch (ReflectiveOperationException exception) {
            throw new ExceptionInInitializerError(exception);
        }
    }

    // The protocol state occupies 128 bits: 64-bit counter + two 32-bit business IDs.
    private volatile long counter;
    private volatile int contextID;
    private volatile int epochID;

    // Java has no public cross-method Monitor.Enter / Monitor.Exit API, so the
    // exclusive scheduler is stored directly on the lock object.
    private final ReentrantLock monitor = new ReentrantLock(false);

    private ConcurrentExclusiveLock() {
    }

    /** Creates a correctly initialized lock instance. */
    public static ConcurrentExclusiveLock create() {
        return new ConcurrentExclusiveLock();
    }

    /** Returns an observational snapshot of the current lock state. */
    public ConcurrentExclusiveLockState observedState() {
        long counter = readCounter();
        if (counter >= EXCLUSIVE_ADD) {
            return ConcurrentExclusiveLockState.EXCLUSIVE;
        }
        if (counter > 0L) {
            return ConcurrentExclusiveLockState.CONCURRENT;
        }
        return ConcurrentExclusiveLockState.IDLE;
    }

    /**
     * Returns an observational indicator of current contention pressure.
     * A purely Concurrent state reports 0.
     */
    public int observedContention() {
        long counter = readCounter();
        int exclusive = (int) (counter >> SHIFT_COUNT);
        return exclusive == 0 ? 0 : (int) counter + exclusive;
    }

    /** Returns the current business context ID. */
    public int getContextID() {
        return (int) CONTEXT_ID.getVolatile(this);
    }

    /** Unconditionally sets the business context ID. */
    public void setContextID(int value) {
        CONTEXT_ID.setVolatile(this, value);
    }

    /** Returns the current business epoch ID. */
    public int getEpochID() {
        return (int) EPOCH_ID.getVolatile(this);
    }

    /** Unconditionally sets the business epoch ID. */
    public void setEpochID(int value) {
        EPOCH_ID.setVolatile(this, value);
    }

    /**
     * Sets a new ContextID and returns whether the previous value changed.
     */
    public boolean switchContextID(int newContextID) {
        return (int) CONTEXT_ID.getAndSet(this, newContextID) != newContextID;
    }

    /**
     * Attempts to advance EpochID. Returns false when the supplied value is not
     * greater than the current value.
     */
    public boolean raiseEpochID(int newEpochID) {
        for (;;) {
            int oldEpochID = (int) EPOCH_ID.getVolatile(this);
            if (newEpochID <= oldEpochID) {
                return false;
            }
            if (EPOCH_ID.compareAndSet(this, oldEpochID, newEpochID)) {
                return true;
            }
        }
    }

    /** Waits to acquire Concurrent permission. */
    public int acquireConcurrent() {
        return acquireConcurrent(MAX_CONCURRENT);
    }

    /**
     * Waits to acquire Concurrent permission and returns a Concurrent ID in
     * {@code [1, maxConcurrent]}.
     */
    public int acquireConcurrent(int maxConcurrent) {
        validateMaxConcurrent(maxConcurrent);

        int adjustTurn = 0;

        retry:
        for (;;) {
            long counter = readCounter();
            if (counter >= maxConcurrent) {
                adjustTurn++;
                if (adjustTurn == 1) {
                    if (counter < TWO_EXCLUSIVE) {
                        monitor.lock();
                        monitor.unlock();
                    } else {
                        Thread.yield();
                    }
                } else if (adjustTurn < 33) {
                    Thread.yield();
                } else {
                    adjustTurn = 1;
                    sleepMillisUninterruptibly(5L);
                }
                continue;
            }

            for (;;) {
                counter = addCounter(1L);
                if ((int) counter < 0) {
                    addCounter(-1L);
                    throw new ConcurrentExclusiveLockCapacityExceededException();
                }
                if (counter <= maxConcurrent) {
                    return (int) counter;
                }

                counter = addCounter(-1L);
                if (counter < EXCLUSIVE_ADD) {
                    continue;
                }
                continue retry;
            }
        }
    }

    /** Attempts once to acquire Concurrent permission. */
    public int tryAcquireConcurrent() {
        return tryAcquireConcurrent(MAX_CONCURRENT);
    }

    /**
     * Attempts once to acquire Concurrent permission and returns 0 on failure.
     */
    public int tryAcquireConcurrent(int maxConcurrent) {
        validateMaxConcurrent(maxConcurrent);

        long counter = readCounter();
        if (counter >= maxConcurrent) {
            return 0;
        }

        counter = addCounter(1L);
        if ((int) counter < 0) {
            addCounter(-1L);
            return 0;
        }
        if (counter <= maxConcurrent) {
            return (int) counter;
        }

        addCounter(-1L);
        return 0;
    }

    /**
     * Attempts to acquire Concurrent permission within the supplied timeout.
     * A negative duration means an infinite wait.
     */
    public int tryAcquireConcurrent(Duration timeout) {
        return tryAcquireConcurrent(timeout, MAX_CONCURRENT);
    }

    /**
     * Attempts to acquire Concurrent permission within the supplied timeout.
     * Returns 0 on failure.
     *
     * <p>{@link Duration} is used because Java cannot overload both the original
     * one-argument maxConcurrent form and the original one-argument millisecond
     * timeout form with the same {@code int} signature.</p>
     */
    public int tryAcquireConcurrent(Duration timeout, int maxConcurrent) {
        if (timeout == null) {
            throw new NullPointerException("timeout");
        }
        validateMaxConcurrent(maxConcurrent);

        if (timeout.isNegative()) {
            return acquireConcurrent(maxConcurrent);
        }
        if (timeout.isZero()) {
            return tryAcquireConcurrent(maxConcurrent);
        }

        long deadline = deadlineAfter(timeout);
        int adjustTurn = 0;

        retry:
        for (;;) {
            if (isExpired(deadline)) {
                return 0;
            }

            long counter = readCounter();
            if (counter >= maxConcurrent) {
                adjustTurn++;
                if (adjustTurn == 1) {
                    if (counter < TWO_EXCLUSIVE) {
                        if (!tryLockUntil(monitor, deadline)) {
                            return 0;
                        }
                        monitor.unlock();
                    } else {
                        Thread.yield();
                    }
                } else if (adjustTurn < 33) {
                    Thread.yield();
                } else {
                    adjustTurn = 1;
                    sleepUntilOrFor(deadline, 5L);
                }
                continue;
            }

            for (;;) {
                counter = addCounter(1L);
                if ((int) counter < 0) {
                    addCounter(-1L);
                    return 0;
                }
                if (counter <= maxConcurrent) {
                    return (int) counter;
                }

                counter = addCounter(-1L);
                if (counter < EXCLUSIVE_ADD) {
                    if (!isExpired(deadline)) {
                        continue;
                    }
                    return 0;
                }
                continue retry;
            }
        }
    }

    /** Releases one currently held Concurrent permission. */
    public void releaseConcurrent() {
        addCounter(-1L);
    }

    /** Waits to acquire preemptive Exclusive permission. */
    public void acquireExclusive() {
        int adjustTurn = 0;

        retry:
        for (;;) {
            monitor.lock();
            long counter = addCounter(EXCLUSIVE_ADD);

            if (counter != EXCLUSIVE_ADD) {
                if (counter < TWO_EXCLUSIVE) {
                    while ((counter = readCounter()) != EXCLUSIVE_ADD) {
                        if (counter < TWO_EXCLUSIVE) {
                            adjustTurn = adjustWait(adjustTurn);
                        } else {
                            addCounter(-EXCLUSIVE_ADD);
                            monitor.unlock();
                            Thread.yield();
                            continue retry;
                        }
                    }
                } else {
                    addCounter(-EXCLUSIVE_ADD);
                    monitor.unlock();
                    Thread.yield();
                    continue;
                }
            }
            return;
        }
    }

    /** Attempts to acquire preemptive Exclusive permission. */
    public boolean tryAcquireExclusive() {
        return tryAcquireExclusive(true);
    }

    /**
     * Attempts to acquire Exclusive permission.
     *
     * @param preemptConcurrent true to preempt new Concurrent entries and wait;
     *                          false to succeed only when the lock is idle
     */
    public boolean tryAcquireExclusive(boolean preemptConcurrent) {
        int adjustTurn = 0;

        if (preemptConcurrent) {
            if (readCounter() >= EXCLUSIVE_ADD) {
                return false;
            }

            monitor.lock();
            long counter = addCounter(EXCLUSIVE_ADD);
            if (counter != EXCLUSIVE_ADD) {
                if (counter < TWO_EXCLUSIVE) {
                    while ((counter = readCounter()) != EXCLUSIVE_ADD) {
                        if (counter < TWO_EXCLUSIVE) {
                            adjustTurn = adjustWait(adjustTurn);
                        } else {
                            addCounter(-EXCLUSIVE_ADD);
                            monitor.unlock();
                            return false;
                        }
                    }
                    return true;
                }

                addCounter(-EXCLUSIVE_ADD);
                monitor.unlock();
                return false;
            }
            return true;
        }

        if (!monitor.tryLock()) {
            return false;
        }
        if (COUNTER.compareAndSet(this, 0L, EXCLUSIVE_ADD)) {
            return true;
        }

        monitor.unlock();
        return false;
    }

    /**
     * Attempts to acquire preemptive Exclusive permission within the supplied
     * timeout. A negative duration means an infinite wait.
     */
    public boolean tryAcquireExclusive(Duration timeout) {
        if (timeout == null) {
            throw new NullPointerException("timeout");
        }
        if (timeout.isNegative()) {
            acquireExclusive();
            return true;
        }

        long deadline = deadlineAfter(timeout);
        int adjustTurn = 0;

        retry:
        for (;;) {
            if (!tryLockUntil(monitor, deadline)) {
                return false;
            }

            long counter = addCounter(EXCLUSIVE_ADD);
            if (counter != EXCLUSIVE_ADD) {
                if (counter < TWO_EXCLUSIVE) {
                    while ((counter = readCounter()) != EXCLUSIVE_ADD) {
                        if (counter < TWO_EXCLUSIVE) {
                            if (!isExpired(deadline)) {
                                adjustTurn = adjustWait(adjustTurn);
                            } else {
                                addCounter(-EXCLUSIVE_ADD);
                                monitor.unlock();
                                return false;
                            }
                        } else {
                            addCounter(-EXCLUSIVE_ADD);
                            monitor.unlock();
                            Thread.yield();
                            if (!isExpired(deadline)) {
                                continue retry;
                            }
                            return false;
                        }
                    }
                    return true;
                }

                addCounter(-EXCLUSIVE_ADD);
                monitor.unlock();
                Thread.yield();
                if (!isExpired(deadline)) {
                    continue;
                }
                return false;
            }
            return true;
        }
    }

    /** Releases the currently held Exclusive permission. */
    public void releaseExclusive() {
        addCounter(-EXCLUSIVE_ADD);
        monitor.unlock();
    }

    /**
     * Downgrades the currently held Exclusive permission to Concurrent.
     */
    public void exclusiveToConcurrent() {
        long counter = addCounter(-CONVERGE_ADD);
        monitor.unlock();

        if (counter >= EXCLUSIVE_ADD) {
            addCounter(-1L);
            acquireConcurrent();
        }
    }

    /**
     * Upgrades the currently held Concurrent permission to Exclusive.
     */
    public void concurrentToExclusive() {
        int adjustTurn = 0;
        if ((int) addCounter(CONVERGE_ADD) != 0) {
            while ((int) readCounter() != 0) {
                adjustTurn = adjustWait2(adjustTurn);
            }
        }
        monitor.lock();
    }

    /**
     * While holding Concurrent permission, attempts to switch ContextID and
     * upgrade to Exclusive. On failure, the prior Concurrent permission has
     * already been released.
     */
    public boolean tryConcurrentToExclusiveWithSwitchContextID(int newContextID) {
        int adjustTurn = 0;
        if ((int) addCounter(CONVERGE_ADD) != 0) {
            while ((int) readCounter() != 0) {
                adjustTurn = adjustWait2(adjustTurn);
            }
        }

        if (switchContextID(newContextID)) {
            monitor.lock();
            return true;
        }

        addCounter(-EXCLUSIVE_ADD);
        return false;
    }

    /**
     * While holding Concurrent permission, attempts to raise EpochID and upgrade
     * to Exclusive. On failure, the prior Concurrent permission has already been
     * released.
     */
    public boolean tryConcurrentToExclusiveWithRaiseEpochID(int newEpochID) {
        int adjustTurn = 0;
        if ((int) addCounter(CONVERGE_ADD) != 0) {
            while ((int) readCounter() != 0) {
                adjustTurn = adjustWait2(adjustTurn);
            }
        }

        if (raiseEpochID(newEpochID)) {
            monitor.lock();
            return true;
        }

        addCounter(-EXCLUSIVE_ADD);
        return false;
    }

    /** Internal helper used by upper-layer Scope / Pipeline implementations. */
    void freeRelease(long counterDelta) {
        addCounter(counterDelta);
        if (counterDelta <= -EXCLUSIVE_ADD) {
            monitor.unlock();
        }
    }

    private long readCounter() {
        return (long) COUNTER.getVolatile(this);
    }

    private long addCounter(long delta) {
        return (long) COUNTER.getAndAdd(this, delta) + delta;
    }

    private static void validateMaxConcurrent(int maxConcurrent) {
        if (maxConcurrent < 1) {
            throw new IllegalArgumentException("maxConcurrent must be greater than 0");
        }
    }

    private static int adjustWait(int adjustTurn) {
        final int threshold = 2048;
        if (adjustTurn < threshold) {
            Thread.onSpinWait();
            return adjustTurn + 1;
        }
        Thread.yield();
        return adjustTurn;
    }

    private static int adjustWait2(int adjustTurn) {
        final int threshold = 48;
        if (adjustTurn < threshold) {
            Thread.onSpinWait();
            return adjustTurn + 1;
        }
        Thread.yield();
        return adjustTurn;
    }

    private static long deadlineAfter(Duration timeout) {
        long nanos;
        try {
            nanos = timeout.toNanos();
        } catch (ArithmeticException exception) {
            return Long.MAX_VALUE;
        }

        long now = System.nanoTime();
        if (nanos <= 0L) {
            return now;
        }
        long deadline = now + nanos;
        if (((now ^ deadline) & (nanos ^ deadline)) < 0L) {
            return Long.MAX_VALUE;
        }
        return deadline;
    }

    private static boolean isExpired(long deadline) {
        return deadline != Long.MAX_VALUE && deadline - System.nanoTime() <= 0L;
    }

    private static boolean tryLockUntil(ReentrantLock lock, long deadline) {
        boolean interrupted = false;
        try {
            for (;;) {
                long remaining = deadline == Long.MAX_VALUE
                        ? Long.MAX_VALUE
                        : deadline - System.nanoTime();

                if (remaining <= 0L) {
                    return lock.tryLock();
                }

                try {
                    return lock.tryLock(remaining, TimeUnit.NANOSECONDS);
                } catch (InterruptedException exception) {
                    interrupted = true;
                }
            }
        } finally {
            if (interrupted) {
                Thread.currentThread().interrupt();
            }
        }
    }

    private static void sleepUntilOrFor(long deadline, long milliseconds) {
        if (deadline == Long.MAX_VALUE) {
            sleepMillisUninterruptibly(milliseconds);
            return;
        }

        long remaining = deadline - System.nanoTime();
        if (remaining <= 0L) {
            return;
        }

        long requested = TimeUnit.MILLISECONDS.toNanos(milliseconds);
        sleepNanosUninterruptibly(Math.min(remaining, requested));
    }

    private static void sleepMillisUninterruptibly(long milliseconds) {
        sleepNanosUninterruptibly(TimeUnit.MILLISECONDS.toNanos(milliseconds));
    }

    private static void sleepNanosUninterruptibly(long nanos) {
        if (nanos <= 0L) {
            return;
        }

        boolean interrupted = false;
        long deadline = System.nanoTime() + nanos;
        try {
            for (;;) {
                long remaining = deadline - System.nanoTime();
                if (remaining <= 0L) {
                    return;
                }

                long millis = TimeUnit.NANOSECONDS.toMillis(remaining);
                int extraNanos = (int) (remaining - TimeUnit.MILLISECONDS.toNanos(millis));
                try {
                    Thread.sleep(millis, extraNanos);
                    return;
                } catch (InterruptedException exception) {
                    interrupted = true;
                }
            }
        } finally {
            if (interrupted) {
                Thread.currentThread().interrupt();
            }
        }
    }
}
