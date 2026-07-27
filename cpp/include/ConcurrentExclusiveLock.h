#ifndef CONCURRENT_EXCLUSIVE_LOCK_H
#define CONCURRENT_EXCLUSIVE_LOCK_H

/*
 * ConcurrentExclusiveLock C core
 *
 * The synchronization semantics are ported from the original C# reference
 * implementation by YiBoWang. Concurrent / Exclusive describe access
 * permission rather than read / write intent.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#if defined(_MSC_VER)
#  define CEL_ALIGNAS(value) __declspec(align(value))
#elif defined(__cplusplus)
#  define CEL_ALIGNAS(value) alignas(value)
#else
#  include <stdalign.h>
#  define CEL_ALIGNAS(value) _Alignas(value)
#endif

#define CEL_VERSION_MAJOR 1
#define CEL_VERSION_MINOR 0
#define CEL_VERSION_PATCH 0
#define CEL_MAX_CONCURRENT INT32_MAX
#define CEL_INFINITE_TIMEOUT_MS INT64_C(-1)

/*
 * cel_lock is caller-owned and does not allocate a separate lock object.
 *
 * The fields are public only because C requires the complete type for stack
 * and embedded storage. They are implementation details and must never be
 * read, written, copied, moved, or initialized directly by callers.
 */
#if defined(_WIN32)
#  if !defined(WIN32_LEAN_AND_MEAN)
#    define WIN32_LEAN_AND_MEAN
#    define CEL_DEFINED_WIN32_LEAN_AND_MEAN
#  endif
#  if !defined(NOMINMAX)
#    define NOMINMAX
#    define CEL_DEFINED_NOMINMAX
#  endif
#  include <windows.h>
#  if defined(CEL_DEFINED_NOMINMAX)
#    undef NOMINMAX
#    undef CEL_DEFINED_NOMINMAX
#  endif
#  if defined(CEL_DEFINED_WIN32_LEAN_AND_MEAN)
#    undef WIN32_LEAN_AND_MEAN
#    undef CEL_DEFINED_WIN32_LEAN_AND_MEAN
#  endif
typedef struct cel_platform_monitor {
    SRWLOCK value;
} cel_platform_monitor;
#elif defined(__unix__) || defined(__APPLE__) || defined(__ANDROID__)
#  include <pthread.h>
typedef struct cel_platform_monitor {
    pthread_mutex_t value;
} cel_platform_monitor;
#else
#  error "ConcurrentExclusiveLock: unsupported platform monitor backend"
#endif

typedef struct cel_lock {
    CEL_ALIGNAS(16) uint64_t cel_internal_counter;
    int32_t cel_internal_context_id;
    int32_t cel_internal_epoch_id;
    uint32_t cel_internal_initialized_magic;
    cel_platform_monitor cel_internal_monitor;
} cel_lock;

typedef enum cel_result {
    CEL_RESULT_SUCCESS = 0,
    CEL_RESULT_NOT_ACQUIRED = 1,
    CEL_RESULT_TIMEOUT = 2,
    CEL_RESULT_INVALID_ARGUMENT = 3,
    CEL_RESULT_NOT_INITIALIZED = 4,
    CEL_RESULT_BUSY = 5,
    CEL_RESULT_CAPACITY_EXCEEDED = 6,
    CEL_RESULT_PLATFORM_ERROR = 7
} cel_result;

typedef enum cel_lock_state {
    CEL_LOCK_STATE_IDLE = 0,
    CEL_LOCK_STATE_CONCURRENT = 1,
    CEL_LOCK_STATE_EXCLUSIVE = 2
} cel_lock_state;

#if defined(__cplusplus)
extern "C" {
#endif

/* Lifecycle. Destroy only after every user and waiter has stopped. */
cel_result cel_lock_init(cel_lock* lock);
cel_result cel_lock_destroy(cel_lock* lock);

/* Observational snapshots. Do not use them as synchronization predicates. */
cel_lock_state cel_lock_observed_state(const cel_lock* lock);
int32_t cel_lock_observed_contention(const cel_lock* lock);

/* Business identifiers outside the permission protocol. */
int32_t cel_lock_get_context_id(const cel_lock* lock);
void cel_lock_set_context_id(cel_lock* lock, int32_t value);

int32_t cel_lock_get_epoch_id(const cel_lock* lock);
void cel_lock_set_epoch_id(cel_lock* lock, int32_t value);

bool cel_lock_switch_context_id(cel_lock* lock, int32_t new_context_id);
bool cel_lock_raise_epoch_id(cel_lock* lock, int32_t new_epoch_id);

/*
 * Concurrent acquisition returns an ID through out_concurrent_id.
 * The ID is in [1, max_concurrent]. It is not a release token.
 */
cel_result cel_lock_acquire_concurrent(
    cel_lock* lock,
    int32_t max_concurrent,
    int32_t* out_concurrent_id);

cel_result cel_lock_try_acquire_concurrent(
    cel_lock* lock,
    int32_t max_concurrent,
    int32_t* out_concurrent_id);

cel_result cel_lock_try_acquire_concurrent_for(
    cel_lock* lock,
    int64_t timeout_milliseconds,
    int32_t max_concurrent,
    int32_t* out_concurrent_id);

cel_result cel_lock_release_concurrent(cel_lock* lock);

/* Exclusive is preemptive and thread-affine. */
cel_result cel_lock_acquire_exclusive(cel_lock* lock);

/*
 * preempt_concurrent=true follows the C# TryAcquireExclusive(true) semantics:
 * it may wait for existing Concurrent holders, but fails if another ordinary
 * Exclusive is already pending or an upgrade request takes priority.
 *
 * preempt_concurrent=false succeeds only if the lock is immediately Idle.
 */
cel_result cel_lock_try_acquire_exclusive(
    cel_lock* lock,
    bool preempt_concurrent);

cel_result cel_lock_try_acquire_exclusive_for(
    cel_lock* lock,
    int64_t timeout_milliseconds);

cel_result cel_lock_release_exclusive(cel_lock* lock);

/* In-place permission conversion. */
cel_result cel_lock_exclusive_to_concurrent(cel_lock* lock);
cel_result cel_lock_concurrent_to_exclusive(cel_lock* lock);

/*
 * On success, the caller holds Exclusive.
 * On failure, the previously held Concurrent permission has already been
 * released and must not be released again.
 */
cel_result cel_lock_try_concurrent_to_exclusive_with_switch_context_id(
    cel_lock* lock,
    int32_t new_context_id);

cel_result cel_lock_try_concurrent_to_exclusive_with_raise_epoch_id(
    cel_lock* lock,
    int32_t new_epoch_id);

const char* cel_result_string(cel_result result);

#if defined(__cplusplus)
} /* extern "C" */
#endif

#undef CEL_ALIGNAS

#endif /* CONCURRENT_EXCLUSIVE_LOCK_H */
