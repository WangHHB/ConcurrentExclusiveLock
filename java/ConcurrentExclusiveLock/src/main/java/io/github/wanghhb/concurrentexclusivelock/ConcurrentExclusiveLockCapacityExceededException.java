// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock;

/**
 * Indicates that the runtime number of Concurrent holders exceeded the
 * internal 31-bit count capacity.
 */
public final class ConcurrentExclusiveLockCapacityExceededException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    public ConcurrentExclusiveLockCapacityExceededException() {
        super();
    }
}
