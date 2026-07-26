// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock;

import java.util.Objects;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;

/**
 * An access-permission pipeline for {@link ConcurrentExclusiveLock}.
 *
 * <p>The pipeline executes a sequence of synchronous business segments. Each
 * segment declares the access permission required for its execution. Based on
 * the permission successfully held by the preceding segment, the pipeline
 * automatically releases, reacquires, continues, upgrades, or downgrades
 * access.</p>
 *
 * <p>When a try-type segment does not satisfy its execution condition, that
 * segment is skipped. The pipeline releases any permission it still holds and
 * continues processing subsequent segments from the NONE state.</p>
 *
 * <p>All segments are synchronous. A segment must not start asynchronous work
 * and return before that work completes. Exceptions propagate to the caller,
 * subsequent segments are not executed, and any permission still held is
 * released when the pipeline exits.</p>
 */
public final class ConcurrentExclusiveLockPipeline {

    private final ConcurrentExclusiveLock locker;

    /** Creates a pipeline bound to the specified lock. */
    public ConcurrentExclusiveLockPipeline(ConcurrentExclusiveLock locker) {
        this.locker = Objects.requireNonNull(locker, "locker");
    }

    /** Returns the lock bound to this pipeline. */
    public ConcurrentExclusiveLock locker() {
        return locker;
    }

    /**
     * Schedules the synchronous pipeline operation on the common pool.
     *
     * <p>This method does not make individual segments asynchronous. It merely
     * schedules {@link #doPipeline(ConcurrentExclusiveLockSegment...)} on a
     * worker thread.</p>
     */
    public CompletableFuture<Void> doPipelineAsync(
            ConcurrentExclusiveLockSegment... segments) {

        return CompletableFuture.runAsync(() -> doPipeline(segments));
    }

    /**
     * Schedules the synchronous pipeline operation on the supplied executor.
     */
    public CompletableFuture<Void> doPipelineAsync(
            Executor executor,
            ConcurrentExclusiveLockSegment... segments) {

        Objects.requireNonNull(executor, "executor");
        return CompletableFuture.runAsync(() -> doPipeline(segments), executor);
    }

    /** Executes the supplied pipeline segments in sequence. */
    public void doPipeline(ConcurrentExclusiveLockSegment... segments) {
        Objects.requireNonNull(segments, "segments");

        try (ConcurrentExclusiveLockScope scope =
                     new ConcurrentExclusiveLockScope(locker)) {

            ConcurrentExclusiveAccessMode lastSuccessAccess =
                    ConcurrentExclusiveAccessMode.NONE;

            for (ConcurrentExclusiveLockSegment segment : segments) {
                Objects.requireNonNull(segment, "segments contains null");

                switch (segment.accessMode()) {
                    case CONCURRENT ->
                            lastSuccessAccess = executeConcurrent(
                                    scope, segment, lastSuccessAccess);

                    case TRY_CONCURRENT ->
                            lastSuccessAccess = executeTryConcurrent(
                                    scope, segment, lastSuccessAccess);

                    case EXCLUSIVE ->
                            lastSuccessAccess = executeExclusive(
                                    scope, segment, lastSuccessAccess);

                    case TEST_EXCLUSIVE ->
                            lastSuccessAccess = executeTestExclusive(
                                    scope, segment, lastSuccessAccess);

                    case TRY_EXCLUSIVE ->
                            lastSuccessAccess = executeTryExclusive(
                                    scope, segment, lastSuccessAccess);

                    case CONVERGE_CONCURRENT ->
                            lastSuccessAccess = executeConvergeConcurrent(
                                    scope, segment, lastSuccessAccess);

                    case CONVERGE_EXCLUSIVE ->
                            lastSuccessAccess = executeConvergeExclusive(
                                    scope, segment, lastSuccessAccess);

                    case TRY_APPLY_ID_CONVERGE_EXCLUSIVE ->
                            lastSuccessAccess = executeTryApplyIDConvergeExclusive(
                                    scope, segment, lastSuccessAccess);

                    case NONE ->
                            lastSuccessAccess = executeNone(
                                    scope, segment, lastSuccessAccess);
                }
            }
        }
    }

    private static ConcurrentExclusiveAccessMode executeConcurrent(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        switch (last) {
            case CONCURRENT -> {
                scope.releaseConcurrent();
                scope.acquireConcurrent();
            }
            case EXCLUSIVE -> {
                scope.releaseExclusive();
                scope.acquireConcurrent();
            }
            default -> scope.acquireConcurrent();
        }

        segment.segment().run();
        return ConcurrentExclusiveAccessMode.CONCURRENT;
    }

    private static ConcurrentExclusiveAccessMode executeTryConcurrent(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        switch (last) {
            case CONCURRENT -> scope.releaseConcurrent();
            case EXCLUSIVE -> scope.releaseExclusive();
            default -> { }
        }

        if (scope.tryAcquireConcurrent() != 0) {
            segment.segment().run();
            return ConcurrentExclusiveAccessMode.CONCURRENT;
        }

        return ConcurrentExclusiveAccessMode.NONE;
    }

    private static ConcurrentExclusiveAccessMode executeExclusive(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        switch (last) {
            case CONCURRENT -> scope.releaseConcurrent();
            case EXCLUSIVE -> scope.releaseExclusive();
            default -> { }
        }

        scope.acquireExclusive();
        segment.segment().run();
        return ConcurrentExclusiveAccessMode.EXCLUSIVE;
    }

    private static ConcurrentExclusiveAccessMode executeTestExclusive(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        switch (last) {
            case CONCURRENT -> scope.releaseConcurrent();
            case EXCLUSIVE -> scope.releaseExclusive();
            default -> { }
        }

        if (scope.tryAcquireExclusive(false)) {
            segment.segment().run();
            return ConcurrentExclusiveAccessMode.EXCLUSIVE;
        }

        return ConcurrentExclusiveAccessMode.NONE;
    }

    private static ConcurrentExclusiveAccessMode executeTryExclusive(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        switch (last) {
            case CONCURRENT -> scope.releaseConcurrent();
            case EXCLUSIVE -> scope.releaseExclusive();
            default -> { }
        }

        if (scope.tryAcquireExclusive(true)) {
            segment.segment().run();
            return ConcurrentExclusiveAccessMode.EXCLUSIVE;
        }

        return ConcurrentExclusiveAccessMode.NONE;
    }

    private static ConcurrentExclusiveAccessMode executeConvergeConcurrent(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        switch (last) {
            case CONCURRENT -> { }
            case EXCLUSIVE -> scope.exclusiveToConcurrent();
            default -> scope.acquireConcurrent();
        }

        segment.segment().run();
        return ConcurrentExclusiveAccessMode.CONCURRENT;
    }

    private static ConcurrentExclusiveAccessMode executeConvergeExclusive(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        switch (last) {
            case CONCURRENT -> scope.concurrentToExclusive();
            case EXCLUSIVE -> { }
            default -> scope.acquireExclusive();
        }

        segment.segment().run();
        return ConcurrentExclusiveAccessMode.EXCLUSIVE;
    }

    private static ConcurrentExclusiveAccessMode executeTryApplyIDConvergeExclusive(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        boolean success;

        switch (last) {
            case CONCURRENT -> {
                success = tryUpgradeWithID(scope, segment);
                if (success) {
                    segment.segment().run();
                    return ConcurrentExclusiveAccessMode.EXCLUSIVE;
                }
                return ConcurrentExclusiveAccessMode.NONE;
            }

            case EXCLUSIVE -> {
                success = applyID(scope, segment);
                if (success) {
                    segment.segment().run();
                    return ConcurrentExclusiveAccessMode.EXCLUSIVE;
                }

                scope.releaseExclusive();
                return ConcurrentExclusiveAccessMode.NONE;
            }

            default -> {
                success = applyID(scope, segment);
                if (success) {
                    scope.acquireExclusive();
                    segment.segment().run();
                    return ConcurrentExclusiveAccessMode.EXCLUSIVE;
                }
                return ConcurrentExclusiveAccessMode.NONE;
            }
        }
    }

    private static ConcurrentExclusiveAccessMode executeNone(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment,
            ConcurrentExclusiveAccessMode last) {

        switch (last) {
            case CONCURRENT -> scope.releaseConcurrent();
            case EXCLUSIVE -> scope.releaseExclusive();
            default -> { }
        }

        segment.segment().run();
        return ConcurrentExclusiveAccessMode.NONE;
    }

    private static boolean tryUpgradeWithID(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment) {

        return switch (segment.idType()) {
            case CONTEXT_ID ->
                    scope.tryConcurrentToExclusiveWithSwitchContextID(
                            segment.contextOrEpochID());
            case EPOCH_ID ->
                    scope.tryConcurrentToExclusiveWithRaiseEpochID(
                            segment.contextOrEpochID());
        };
    }

    private static boolean applyID(
            ConcurrentExclusiveLockScope scope,
            ConcurrentExclusiveLockSegment segment) {

        return switch (segment.idType()) {
            case CONTEXT_ID -> scope.switchContextID(segment.contextOrEpochID());
            case EPOCH_ID -> scope.raiseEpochID(segment.contextOrEpochID());
        };
    }
}
