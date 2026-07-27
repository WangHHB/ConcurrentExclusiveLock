#ifndef CONCURRENT_EXCLUSIVE_LOCK_INTERNAL_H
#define CONCURRENT_EXCLUSIVE_LOCK_INTERNAL_H

#include "ConcurrentExclusiveLock.h"

#ifdef __cplusplus
extern "C" {
#endif

/* Used by the C++ Scope to reproduce the C# CounterMate final release. */
cel_result cel_lock_free_release(cel_lock* lock, int64_t counter_delta);

#ifdef __cplusplus
}
#endif

#endif
