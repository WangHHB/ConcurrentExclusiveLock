// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock;

import java.time.Duration;
import java.util.Objects;

/**
 * A convenience wrapper for using {@link ConcurrentExclusiveLock}.
 *
 * <p>During the lifetime of this scope, the caller may transition between
 * Concurrent and Exclusive states according to the protocol, including
 * Concurrent acquisition, Exclusive acquisition, in-place upgrade to
 * Exclusive, and downgrade from Exclusive to Concurrent.</p>
 *
 * <p>The caller may explicitly release the currently held access permission.
 * If it is not released explicitly, {@link #close()} performs the corresponding
 * release based on the state ultimately held by the scope.</p>
 *
 * <p>{@code close()} releases only the access permission ultimately held by the
 * scope. It does not restore or clear ContextID or EpochID. These IDs represent
 * business state associated with the lock and remain the responsibility of the
 * caller.</p>
 *
 * <p>This scope is intended for one calling context, is not thread-safe, must
 * not be shared across threads, and must not be used after it has been closed.
 * Exclusive permission remains thread-affine and therefore must be released,
 * downgraded, or closed by the thread that acquired it.</p>
 */
public final class ConcurrentExclusiveLockScope implements AutoCloseable {

    private final ConcurrentExclusiveLock locker;
    private long counterMate;
    private boolean closed;

    /**
     * Creates a scope bound to the specified lock.
     *
     * <p>The constructor itself does not acquire Concurrent or Exclusive
     * permission. Permission must be acquired explicitly through this scope.</p>
     *
     * @param locker the lock managed by this scope
     */
    public ConcurrentExclusiveLockScope(ConcurrentExclusiveLock locker) {
        this.locker = Objects.requireNonNull(locker, "locker");
    }

    /** Returns an observational snapshot of the lock's current state. */
    public ConcurrentExclusiveLockState observedState() {
        ensureOpen();
        return locker.observedState();
    }

    /** Returns an observational indicator of current contention pressure. */
    public int observedContention() {
        ensureOpen();
        return locker.observedContention();
    }

    /** Returns the current business context ID. */
    public int getContextID() {
        ensureOpen();
        return locker.getContextID();
    }

    /** Unconditionally sets the current business context ID. */
    public void setContextID(int value) {
        ensureOpen();
        locker.setContextID(value);
    }

    /** Returns the current business epoch ID. */
    public int getEpochID() {
        ensureOpen();
        return locker.getEpochID();
    }

    /** Unconditionally sets the current business epoch ID. */
    public void setEpochID(int value) {
        ensureOpen();
        locker.setEpochID(value);
    }

    /**
     * Sets a new ContextID and returns whether the previous value changed.
     */
    public boolean switchContextID(int newContextID) {
        ensureOpen();
        return locker.switchContextID(newContextID);
    }

    /**
     * Attempts to advance EpochID and returns whether the advance succeeded.
     */
    public boolean raiseEpochID(int newEpochID) {
        ensureOpen();
        return locker.raiseEpochID(newEpochID);
    }

    /** Waits to acquire Concurrent permission. */
    public int acquireConcurrent() {
        return acquireConcurrent(ConcurrentExclusiveLock.MAX_CONCURRENT);
    }

    /**
     * Waits to acquire Concurrent permission and returns a Concurrent ID in
     * {@code [1, maxConcurrent]}.
     */
    public int acquireConcurrent(int maxConcurrent) {
        ensureOpen();
        int concurrentID = locker.acquireConcurrent(maxConcurrent);
        counterMate++;
        return concurrentID;
    }

    /** Attempts once to acquire Concurrent permission. */
    public int tryAcquireConcurrent() {
        return tryAcquireConcurrent(ConcurrentExclusiveLock.MAX_CONCURRENT);
    }

    /**
     * Attempts once to acquire Concurrent permission and returns 0 on failure.
     */
    public int tryAcquireConcurrent(int maxConcurrent) {
        ensureOpen();
        int concurrentID = locker.tryAcquireConcurrent(maxConcurrent);
        if (concurrentID != 0) {
            counterMate++;
        }
        return concurrentID;
    }

    /**
     * Attempts to acquire Concurrent permission within the supplied timeout.
     * A negative duration means an infinite wait.
     */
    public int tryAcquireConcurrent(Duration timeout) {
        return tryAcquireConcurrent(timeout, ConcurrentExclusiveLock.MAX_CONCURRENT);
    }

    /**
     * Attempts to acquire Concurrent permission within the supplied timeout and
     * returns 0 on failure.
     */
    public int tryAcquireConcurrent(Duration timeout, int maxConcurrent) {
        ensureOpen();
        int concurrentID = locker.tryAcquireConcurrent(timeout, maxConcurrent);
        if (concurrentID != 0) {
            counterMate++;
        }
        return concurrentID;
    }

    /**
     * Releases one Concurrent permission currently held by this scope.
     * A permission released explicitly will not be released again by
     * {@link #close()}.
     */
    public void releaseConcurrent() {
        ensureOpen();
        locker.releaseConcurrent();
        counterMate--;
    }

    /** Waits to acquire preemptive Exclusive permission. */
    public void acquireExclusive() {
        ensureOpen();
        locker.acquireExclusive();
        counterMate += ConcurrentExclusiveLock.EXCLUSIVE_ADD;
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
     * @return true if Exclusive permission was acquired; otherwise false
     */
    public boolean tryAcquireExclusive(boolean preemptConcurrent) {
        ensureOpen();
        boolean success = locker.tryAcquireExclusive(preemptConcurrent);
        if (success) {
            counterMate += ConcurrentExclusiveLock.EXCLUSIVE_ADD;
        }
        return success;
    }

    /**
     * Attempts to acquire preemptive Exclusive permission within the supplied
     * timeout. A negative duration means an infinite wait.
     */
    public boolean tryAcquireExclusive(Duration timeout) {
        ensureOpen();
        boolean success = locker.tryAcquireExclusive(timeout);
        if (success) {
            counterMate += ConcurrentExclusiveLock.EXCLUSIVE_ADD;
        }
        return success;
    }

    /**
     * Releases the Exclusive permission currently held by this scope.
     * A permission released explicitly will not be released again by
     * {@link #close()}.
     */
    public void releaseExclusive() {
        ensureOpen();
        locker.releaseExclusive();
        counterMate -= ConcurrentExclusiveLock.EXCLUSIVE_ADD;
    }

    /**
     * Downgrades the currently held Exclusive permission to Concurrent.
     * The scope continues to hold Concurrent permission after this call.
     */
    public void exclusiveToConcurrent() {
        ensureOpen();
        locker.exclusiveToConcurrent();
        counterMate -= ConcurrentExclusiveLock.CONVERGE_ADD;
    }

    /**
     * Upgrades the currently held Concurrent permission to Exclusive while
     * preserving the continuous access context.
     */
    public void concurrentToExclusive() {
        ensureOpen();
        locker.concurrentToExclusive();
        counterMate += ConcurrentExclusiveLock.CONVERGE_ADD;
    }

    /**
     * While holding Concurrent permission, attempts to switch ContextID and
     * upgrade to Exclusive.
     *
     * <p>On success, this scope holds Exclusive permission. On failure, the
     * previously held Concurrent permission has already been released and must
     * not be released again.</p>
     */
    public boolean tryConcurrentToExclusiveWithSwitchContextID(int newContextID) {
        ensureOpen();
        boolean success = locker.tryConcurrentToExclusiveWithSwitchContextID(newContextID);
        if (success) {
            counterMate += ConcurrentExclusiveLock.CONVERGE_ADD;
        } else {
            counterMate--;
        }
        return success;
    }

    /**
     * While holding Concurrent permission, attempts to raise EpochID and
     * upgrade to Exclusive.
     *
     * <p>On success, this scope holds Exclusive permission. On failure, the
     * previously held Concurrent permission has already been released and must
     * not be released again.</p>
     */
    public boolean tryConcurrentToExclusiveWithRaiseEpochID(int newEpochID) {
        ensureOpen();
        boolean success = locker.tryConcurrentToExclusiveWithRaiseEpochID(newEpochID);
        if (success) {
            counterMate += ConcurrentExclusiveLock.CONVERGE_ADD;
        } else {
            counterMate--;
        }
        return success;
    }

    /**
     * Releases any access permission still held by this scope.
     *
     * <p>Permissions already released explicitly are not released again.
     * ContextID and EpochID are not restored or cleared. Repeated calls after a
     * successful close have no effect.</p>
     */
    @Override
    public void close() {
        if (closed) {
            return;
        }

        if (counterMate != 0L) {
            locker.freeRelease(-counterMate);
            counterMate = 0L;
        }
        closed = true;
    }

    private void ensureOpen() {
        if (closed) {
            throw new IllegalStateException("ConcurrentExclusiveLockScope has already been closed.");
        }
    }
}
