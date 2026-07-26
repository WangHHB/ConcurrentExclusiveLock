// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock;

import java.util.Objects;

/**
 * A synchronous business segment in a
 * {@link ConcurrentExclusiveLockPipeline}.
 *
 * <p>Each segment declares the access permission required to run it and stores
 * the synchronous business code to execute. Pipeline business code must finish
 * before {@link Runnable#run()} returns. Starting asynchronous work and
 * returning before that work completes is not supported.</p>
 */
public final class ConcurrentExclusiveLockSegment {

    /** Specifies the business-ID type used by an ID-conditioned segment. */
    public enum IDType {
        /** Uses ContextID to switch the business context. */
        CONTEXT_ID(0),

        /** Uses EpochID to advance the business stage monotonically. */
        EPOCH_ID(1);

        private final int code;

        IDType(int code) {
            this.code = code;
        }

        /** Returns the stable numeric code corresponding to the original API. */
        public int code() {
            return code;
        }
    }

    private final Runnable segment;
    private final int contextOrEpochID;
    private final IDType idType;
    private final ConcurrentExclusiveAccessMode accessMode;

    private ConcurrentExclusiveLockSegment(
            ConcurrentExclusiveAccessMode accessMode,
            Runnable segment,
            int contextOrEpochID,
            IDType idType) {

        this.accessMode = Objects.requireNonNull(accessMode, "accessMode");
        this.segment = Objects.requireNonNull(segment, "segment");
        this.contextOrEpochID = contextOrEpochID;
        this.idType = Objects.requireNonNull(idType, "idType");
    }

    /** Returns the synchronous business code executed by this segment. */
    public Runnable segment() {
        return segment;
    }

    /** Returns the ContextID or EpochID used by this segment. */
    public int contextOrEpochID() {
        return contextOrEpochID;
    }

    /** Returns the business-ID type used by this segment. */
    public IDType idType() {
        return idType;
    }

    /** Returns the access-permission mode declared by this segment. */
    public ConcurrentExclusiveAccessMode accessMode() {
        return accessMode;
    }

    /** Creates a segment that runs without access permission. */
    public static ConcurrentExclusiveLockSegment none(Runnable segment) {
        return create(ConcurrentExclusiveAccessMode.NONE, segment);
    }

    /** Creates a segment that acquires independent Concurrent permission. */
    public static ConcurrentExclusiveLockSegment concurrent(Runnable segment) {
        return create(ConcurrentExclusiveAccessMode.CONCURRENT, segment);
    }

    /** Creates a segment that attempts independent Concurrent permission. */
    public static ConcurrentExclusiveLockSegment tryConcurrent(Runnable segment) {
        return create(ConcurrentExclusiveAccessMode.TRY_CONCURRENT, segment);
    }

    /** Creates a segment that acquires independent Exclusive permission. */
    public static ConcurrentExclusiveLockSegment exclusive(Runnable segment) {
        return create(ConcurrentExclusiveAccessMode.EXCLUSIVE, segment);
    }

    /**
     * Creates a segment that attempts Exclusive permission only while the lock
     * is Idle.
     */
    public static ConcurrentExclusiveLockSegment testExclusive(Runnable segment) {
        return create(ConcurrentExclusiveAccessMode.TEST_EXCLUSIVE, segment);
    }

    /** Creates a segment that attempts preemptive Exclusive permission. */
    public static ConcurrentExclusiveLockSegment tryExclusive(Runnable segment) {
        return create(ConcurrentExclusiveAccessMode.TRY_EXCLUSIVE, segment);
    }

    /**
     * Creates a segment that continues, downgrades to, or acquires Concurrent
     * permission.
     */
    public static ConcurrentExclusiveLockSegment convergeConcurrent(Runnable segment) {
        return create(ConcurrentExclusiveAccessMode.CONVERGE_CONCURRENT, segment);
    }

    /**
     * Creates a segment that continues, upgrades to, or acquires Exclusive
     * permission.
     */
    public static ConcurrentExclusiveLockSegment convergeExclusive(Runnable segment) {
        return create(ConcurrentExclusiveAccessMode.CONVERGE_EXCLUSIVE, segment);
    }

    /**
     * Creates a segment that converges to Exclusive permission only when the
     * supplied business ID is applied successfully.
     */
    public static ConcurrentExclusiveLockSegment tryApplyIDConvergeExclusive(
            Runnable segment,
            int contextOrEpochID,
            IDType idType) {

        return new ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode.TRY_APPLY_ID_CONVERGE_EXCLUSIVE,
                segment,
                contextOrEpochID,
                idType);
    }

    private static ConcurrentExclusiveLockSegment create(
            ConcurrentExclusiveAccessMode accessMode,
            Runnable segment) {

        return new ConcurrentExclusiveLockSegment(
                accessMode,
                segment,
                0,
                IDType.CONTEXT_ID);
    }
}
