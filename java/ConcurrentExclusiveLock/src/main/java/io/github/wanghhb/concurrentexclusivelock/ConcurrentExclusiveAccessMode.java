// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock;

/**
 * The access-permission mode declared by a pipeline segment.
 *
 * <p>Each value defines the access permission required by the current segment
 * and how that permission is handled relative to the state successfully held
 * by the preceding segment.</p>
 */
public enum ConcurrentExclusiveAccessMode {

    /**
     * No access permission.
     *
     * <p>Before the current segment runs, any permission still held by the
     * preceding segment is released. The current segment then runs without
     * access permission.</p>
     */
    NONE(0),

    /**
     * Acquires an independent Concurrent permission segment.
     *
     * <p>If the preceding segment already holds Concurrent permission, it is
     * still released and reacquired. To continue the current Concurrent
     * context, use {@link #CONVERGE_CONCURRENT}.</p>
     */
    CONCURRENT(1),

    /**
     * Attempts to acquire an independent Concurrent permission segment.
     *
     * <p>If Concurrent permission is not acquired, the current segment is not
     * executed and the remaining pipeline continues from the NONE state. If
     * the preceding segment still holds permission, that permission is
     * released before acquisition is attempted.</p>
     */
    TRY_CONCURRENT(2),

    /**
     * Acquires an independent Exclusive permission segment.
     *
     * <p>If the preceding segment already holds Exclusive permission, it is
     * still released and reacquired. To continue an existing Exclusive
     * context, use {@link #CONVERGE_EXCLUSIVE}. To additionally condition the
     * current segment on applying a business ID, use
     * {@link #TRY_APPLY_ID_CONVERGE_EXCLUSIVE}.</p>
     */
    EXCLUSIVE(3),

    /**
     * Attempts to acquire Exclusive permission only while the lock is Idle.
     *
     * <p>This mode does not preempt Concurrent access and does not wait for the
     * lock state to change. If acquisition fails, the current segment is not
     * executed and the remaining pipeline continues from the NONE state.</p>
     */
    TEST_EXCLUSIVE(4),

    /**
     * Attempts to acquire Exclusive permission preemptively.
     *
     * <p>This mode may wait. If a Concurrent-to-Exclusive upgrade request
     * appears during contention, the current request may yield and fail. If
     * acquisition fails, the current segment is not executed and the remaining
     * pipeline continues from the NONE state.</p>
     */
    TRY_EXCLUSIVE(5),

    /**
     * Continues, downgrades to, or acquires Concurrent permission.
     *
     * <p>If the preceding segment already holds Concurrent permission, that
     * context is continued. If it holds Exclusive permission, the pipeline
     * downgrades through {@code exclusiveToConcurrent()}. If no permission is
     * held, ordinary Concurrent permission is acquired.</p>
     */
    CONVERGE_CONCURRENT(6),

    /**
     * Continues, upgrades to, or acquires Exclusive permission.
     *
     * <p>If the preceding segment holds Concurrent permission, the pipeline
     * upgrades in place through {@code concurrentToExclusive()}. If it already
     * holds Exclusive permission, that context is continued. If no permission
     * is held, ordinary Exclusive permission is acquired.</p>
     */
    CONVERGE_EXCLUSIVE(7),

    /**
     * Continues, upgrades to, or acquires Exclusive permission, conditioned on
     * the result of applying a business ID.
     *
     * <p>The Try semantics apply to the business-ID operation; they do not mean
     * that this mode never waits. When the ID is applied successfully, the
     * segment executes with Exclusive permission. Otherwise the segment is
     * skipped, any permission still held is released, and the remaining
     * pipeline continues from the NONE state.</p>
     */
    TRY_APPLY_ID_CONVERGE_EXCLUSIVE(8);

    private final int code;

    ConcurrentExclusiveAccessMode(int code) {
        this.code = code;
    }

    /** Returns the stable numeric code corresponding to the original API. */
    public int code() {
        return code;
    }
}
