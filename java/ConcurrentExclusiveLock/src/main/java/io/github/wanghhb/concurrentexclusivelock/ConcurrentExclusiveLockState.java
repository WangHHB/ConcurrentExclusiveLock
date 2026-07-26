// SPDX-License-Identifier: MIT OR Apache-2.0
// Copyright 2026 YiBo Wang

package io.github.wanghhb.concurrentexclusivelock;

/** Observational state of a {@link ConcurrentExclusiveLock}. */
public enum ConcurrentExclusiveLockState {
    IDLE,
    CONCURRENT,
    EXCLUSIVE
}
