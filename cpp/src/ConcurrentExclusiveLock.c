#include "ConcurrentExclusiveLock.h"
#include "ConcurrentExclusiveLockInternal.h"

#include <string.h>
#include <time.h>

#if defined(_WIN32)
#  if defined(_MSC_VER)
#    include <intrin.h>
#  endif
#else
#  include <errno.h>
#  include <pthread.h>
#  include <sched.h>
#  include <unistd.h>
#endif

#define CEL_EXCLUSIVE_ADD UINT64_C(4294967296)
#define CEL_CONVERGE_ADD  UINT64_C(4294967295)
#define CEL_INITIALIZED_MAGIC UINT32_C(0x43454C31)

#if defined(_WIN32)
typedef uint64_t cel_atomic_u64;
typedef int32_t cel_atomic_i32;

typedef cel_platform_monitor cel_monitor;

static void cel_atomic_u64_init(cel_atomic_u64* value, uint64_t initial) {
    *value = initial;
}
static uint64_t cel_atomic_u64_load(const cel_atomic_u64* value) {
    return (uint64_t)InterlockedCompareExchange64(
        (volatile LONG64*)(void*)value, 0, 0);
}
static uint64_t cel_atomic_u64_fetch_add(cel_atomic_u64* value, uint64_t delta) {
    return (uint64_t)InterlockedExchangeAdd64(
        (volatile LONG64*)(void*)value, (LONG64)delta);
}
static uint64_t cel_atomic_u64_fetch_sub(cel_atomic_u64* value, uint64_t delta) {
    return (uint64_t)InterlockedExchangeAdd64(
        (volatile LONG64*)(void*)value, -(LONG64)delta);
}
static bool cel_atomic_u64_compare_exchange(
    cel_atomic_u64* value, uint64_t* expected, uint64_t desired) {
    LONG64 observed = InterlockedCompareExchange64(
        (volatile LONG64*)(void*)value,
        (LONG64)desired,
        (LONG64)*expected);
    if ((uint64_t)observed == *expected) {
        return true;
    }
    *expected = (uint64_t)observed;
    return false;
}

static void cel_atomic_i32_init(cel_atomic_i32* value, int32_t initial) {
    *value = initial;
}
static int32_t cel_atomic_i32_load(const cel_atomic_i32* value) {
    return (int32_t)InterlockedCompareExchange(
        (volatile LONG*)(void*)value, 0, 0);
}
static void cel_atomic_i32_store(cel_atomic_i32* value, int32_t desired) {
    (void)InterlockedExchange((volatile LONG*)(void*)value, (LONG)desired);
}
static int32_t cel_atomic_i32_exchange(cel_atomic_i32* value, int32_t desired) {
    return (int32_t)InterlockedExchange(
        (volatile LONG*)(void*)value, (LONG)desired);
}
static bool cel_atomic_i32_compare_exchange(
    cel_atomic_i32* value, int32_t* expected, int32_t desired) {
    LONG observed = InterlockedCompareExchange(
        (volatile LONG*)(void*)value,
        (LONG)desired,
        (LONG)*expected);
    if ((int32_t)observed == *expected) {
        return true;
    }
    *expected = (int32_t)observed;
    return false;
}

#else
typedef uint64_t cel_atomic_u64;
typedef int32_t cel_atomic_i32;
typedef cel_platform_monitor cel_monitor;

static void cel_atomic_u64_init(cel_atomic_u64* value, uint64_t initial) {
    __atomic_store_n(value, initial, __ATOMIC_RELAXED);
}
static uint64_t cel_atomic_u64_load(const cel_atomic_u64* value) {
    return __atomic_load_n(value, __ATOMIC_ACQUIRE);
}
static uint64_t cel_atomic_u64_fetch_add(cel_atomic_u64* value, uint64_t delta) {
    return __atomic_fetch_add(value, delta, __ATOMIC_SEQ_CST);
}
static uint64_t cel_atomic_u64_fetch_sub(cel_atomic_u64* value, uint64_t delta) {
    return __atomic_fetch_sub(value, delta, __ATOMIC_SEQ_CST);
}
static bool cel_atomic_u64_compare_exchange(
    cel_atomic_u64* value, uint64_t* expected, uint64_t desired) {
    return __atomic_compare_exchange_n(
        value,
        expected,
        desired,
        false,
        __ATOMIC_SEQ_CST,
        __ATOMIC_ACQUIRE);
}

static void cel_atomic_i32_init(cel_atomic_i32* value, int32_t initial) {
    __atomic_store_n(value, initial, __ATOMIC_RELAXED);
}
static int32_t cel_atomic_i32_load(const cel_atomic_i32* value) {
    return __atomic_load_n(value, __ATOMIC_ACQUIRE);
}
static void cel_atomic_i32_store(cel_atomic_i32* value, int32_t desired) {
    __atomic_store_n(value, desired, __ATOMIC_RELEASE);
}
static int32_t cel_atomic_i32_exchange(cel_atomic_i32* value, int32_t desired) {
    return __atomic_exchange_n(value, desired, __ATOMIC_SEQ_CST);
}
static bool cel_atomic_i32_compare_exchange(
    cel_atomic_i32* value, int32_t* expected, int32_t desired) {
    return __atomic_compare_exchange_n(
        value,
        expected,
        desired,
        true,
        __ATOMIC_SEQ_CST,
        __ATOMIC_ACQUIRE);
}
#endif

typedef cel_lock cel_lock_impl;

static cel_lock_impl* cel_impl(cel_lock* lock) {
    return lock;
}

static const cel_lock_impl* cel_impl_const(const cel_lock* lock) {
    return lock;
}

static bool cel_is_initialized(const cel_lock_impl* impl) {
    uint32_t magic = 0;
    memcpy(&magic, &impl->cel_internal_initialized_magic, sizeof(magic));
    return magic == CEL_INITIALIZED_MAGIC;
}

static uint64_t cel_now_ms(void) {
#if defined(_WIN32)
    return (uint64_t)GetTickCount64();
#else
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (uint64_t)ts.tv_sec * UINT64_C(1000)
         + (uint64_t)ts.tv_nsec / UINT64_C(1000000);
#endif
}

static void cel_cpu_pause(void) {
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
    YieldProcessor();
#elif defined(__i386__) || defined(__x86_64__)
    __asm__ __volatile__("pause" ::: "memory");
#elif defined(__aarch64__) || defined(__arm__)
    __asm__ __volatile__("yield" ::: "memory");
#elif defined(_MSC_VER)
    _ReadWriteBarrier();
#else
    __asm__ __volatile__("" ::: "memory");
#endif
}

static void cel_thread_yield(void) {
#if defined(_WIN32)
    if (!SwitchToThread()) {
        Sleep(0);
    }
#else
    sched_yield();
#endif
}

static void cel_sleep_ms(uint32_t milliseconds) {
#if defined(_WIN32)
    Sleep((DWORD)milliseconds);
#else
    struct timespec req;
    req.tv_sec = (time_t)(milliseconds / 1000u);
    req.tv_nsec = (long)(milliseconds % 1000u) * 1000000L;
    while (nanosleep(&req, &req) != 0 && errno == EINTR) {
    }
#endif
}

static cel_result cel_monitor_init(cel_monitor* monitor) {
#if defined(_WIN32)
    InitializeSRWLock(&monitor->value);
    return CEL_RESULT_SUCCESS;
#else
    return pthread_mutex_init(&monitor->value, NULL) == 0
        ? CEL_RESULT_SUCCESS
        : CEL_RESULT_PLATFORM_ERROR;
#endif
}

static cel_result cel_monitor_destroy(cel_monitor* monitor) {
#if defined(_WIN32)
    (void)monitor;
    return CEL_RESULT_SUCCESS;
#else
    int rc = pthread_mutex_destroy(&monitor->value);
    if (rc == 0) {
        return CEL_RESULT_SUCCESS;
    }
    if (rc == EBUSY) {
        return CEL_RESULT_BUSY;
    }
    return CEL_RESULT_PLATFORM_ERROR;
#endif
}

static cel_result cel_monitor_enter(cel_monitor* monitor) {
#if defined(_WIN32)
    AcquireSRWLockExclusive(&monitor->value);
    return CEL_RESULT_SUCCESS;
#else
    return pthread_mutex_lock(&monitor->value) == 0
        ? CEL_RESULT_SUCCESS
        : CEL_RESULT_PLATFORM_ERROR;
#endif
}

static cel_result cel_monitor_try_enter(cel_monitor* monitor) {
#if defined(_WIN32)
    return TryAcquireSRWLockExclusive(&monitor->value)
        ? CEL_RESULT_SUCCESS
        : CEL_RESULT_NOT_ACQUIRED;
#else
    int rc = pthread_mutex_trylock(&monitor->value);
    if (rc == 0) {
        return CEL_RESULT_SUCCESS;
    }
    if (rc == EBUSY) {
        return CEL_RESULT_NOT_ACQUIRED;
    }
    return CEL_RESULT_PLATFORM_ERROR;
#endif
}

static cel_result cel_monitor_try_enter_for(
    cel_monitor* monitor,
    int64_t timeout_milliseconds) {
    if (timeout_milliseconds < 0) {
        return cel_monitor_enter(monitor);
    }
    if (timeout_milliseconds == 0) {
        return cel_monitor_try_enter(monitor);
    }

    const uint64_t deadline = cel_now_ms() + (uint64_t)timeout_milliseconds;
    unsigned turn = 0;
    for (;;) {
        cel_result result = cel_monitor_try_enter(monitor);
        if (result == CEL_RESULT_SUCCESS || result == CEL_RESULT_PLATFORM_ERROR) {
            return result;
        }
        if (cel_now_ms() >= deadline) {
            return CEL_RESULT_TIMEOUT;
        }
        if (turn++ < 64u) {
            cel_cpu_pause();
        } else if (turn < 96u) {
            cel_thread_yield();
        } else {
            cel_sleep_ms(1u);
            turn = 0;
        }
    }
}

static cel_result cel_monitor_exit(cel_monitor* monitor) {
#if defined(_WIN32)
    ReleaseSRWLockExclusive(&monitor->value);
    return CEL_RESULT_SUCCESS;
#else
    return pthread_mutex_unlock(&monitor->value) == 0
        ? CEL_RESULT_SUCCESS
        : CEL_RESULT_PLATFORM_ERROR;
#endif
}

static void cel_adjust_wait(int* adjust_turn) {
    const int threshold = 2048;
    if (*adjust_turn < threshold) {
        ++*adjust_turn;
        cel_cpu_pause();
    } else {
        cel_thread_yield();
    }
}

static void cel_adjust_wait2(int* adjust_turn) {
    const int threshold = 48;
    if (*adjust_turn < threshold) {
        ++*adjust_turn;
        cel_cpu_pause();
    } else {
        cel_thread_yield();
    }
}

static int32_t cel_low_i32(uint64_t counter) {
    uint32_t low = (uint32_t)(counter & UINT64_C(0xffffffff));
    int32_t value;
    memcpy(&value, &low, sizeof(value));
    return value;
}

static uint32_t cel_high_u32(uint64_t counter) {
    return (uint32_t)(counter >> 32);
}

static cel_result cel_validate(cel_lock* lock, cel_lock_impl** out_impl) {
    if (lock == NULL || out_impl == NULL) {
        return CEL_RESULT_INVALID_ARGUMENT;
    }
    cel_lock_impl* impl = cel_impl(lock);
    if (!cel_is_initialized(impl)) {
        return CEL_RESULT_NOT_INITIALIZED;
    }
    *out_impl = impl;
    return CEL_RESULT_SUCCESS;
}

static cel_result cel_validate_const(
    const cel_lock* lock,
    const cel_lock_impl** out_impl) {
    if (lock == NULL || out_impl == NULL) {
        return CEL_RESULT_INVALID_ARGUMENT;
    }
    const cel_lock_impl* impl = cel_impl_const(lock);
    if (!cel_is_initialized(impl)) {
        return CEL_RESULT_NOT_INITIALIZED;
    }
    *out_impl = impl;
    return CEL_RESULT_SUCCESS;
}

cel_result cel_lock_init(cel_lock* lock) {
    if (lock == NULL) {
        return CEL_RESULT_INVALID_ARGUMENT;
    }

    cel_lock_impl* impl = cel_impl(lock);

    /*
     * cel_lock_init accepts an uninitialized caller-owned object. Reading an
     * initialization marker before clearing it would make ordinary stack use
     * depend on indeterminate bytes, so double initialization is deliberately
     * treated as caller misuse rather than detected here.
     */
    memset(lock, 0, sizeof(*lock));
    cel_atomic_u64_init(&impl->cel_internal_counter, UINT64_C(0));
    cel_atomic_i32_init(&impl->cel_internal_context_id, 0);
    cel_atomic_i32_init(&impl->cel_internal_epoch_id, 0);

    cel_result result = cel_monitor_init(&impl->cel_internal_monitor);
    if (result != CEL_RESULT_SUCCESS) {
        memset(lock, 0, sizeof(*lock));
        return result;
    }
    impl->cel_internal_initialized_magic = CEL_INITIALIZED_MAGIC;
    return CEL_RESULT_SUCCESS;
}

cel_result cel_lock_destroy(cel_lock* lock) {
    cel_lock_impl* impl;
    cel_result result = cel_validate(lock, &impl);
    if (result != CEL_RESULT_SUCCESS) {
        return result;
    }
    if (cel_atomic_u64_load(&impl->cel_internal_counter) != 0) {
        return CEL_RESULT_BUSY;
    }
    result = cel_monitor_destroy(&impl->cel_internal_monitor);
    if (result == CEL_RESULT_SUCCESS) {
        impl->cel_internal_initialized_magic = 0;
        memset(lock, 0, sizeof(*lock));
    }
    return result;
}

cel_lock_state cel_lock_observed_state(const cel_lock* lock) {
    const cel_lock_impl* impl;
    if (cel_validate_const(lock, &impl) != CEL_RESULT_SUCCESS) {
        return CEL_LOCK_STATE_IDLE;
    }
    uint64_t counter = cel_atomic_u64_load(&impl->cel_internal_counter);
    if (counter >= CEL_EXCLUSIVE_ADD) {
        return CEL_LOCK_STATE_EXCLUSIVE;
    }
    return counter > 0 ? CEL_LOCK_STATE_CONCURRENT : CEL_LOCK_STATE_IDLE;
}

int32_t cel_lock_observed_contention(const cel_lock* lock) {
    const cel_lock_impl* impl;
    if (cel_validate_const(lock, &impl) != CEL_RESULT_SUCCESS) {
        return 0;
    }
    uint64_t counter = cel_atomic_u64_load(&impl->cel_internal_counter);
    uint32_t exc = cel_high_u32(counter);
    if (exc == 0) {
        return 0;
    }
    uint32_t low = (uint32_t)counter;
    uint32_t sum = low + exc;
    int32_t result;
    memcpy(&result, &sum, sizeof(result));
    return result;
}

int32_t cel_lock_get_context_id(const cel_lock* lock) {
    const cel_lock_impl* impl;
    if (cel_validate_const(lock, &impl) != CEL_RESULT_SUCCESS) {
        return 0;
    }
    return cel_atomic_i32_load(&impl->cel_internal_context_id);
}

void cel_lock_set_context_id(cel_lock* lock, int32_t value) {
    cel_lock_impl* impl;
    if (cel_validate(lock, &impl) == CEL_RESULT_SUCCESS) {
        cel_atomic_i32_store(&impl->cel_internal_context_id, value);
    }
}

bool cel_lock_switch_context_id(cel_lock* lock, int32_t new_context_id) {
    cel_lock_impl* impl;
    if (cel_validate(lock, &impl) != CEL_RESULT_SUCCESS) {
        return false;
    }
    return cel_atomic_i32_exchange(&impl->cel_internal_context_id, new_context_id) != new_context_id;
}

int32_t cel_lock_get_epoch_id(const cel_lock* lock) {
    const cel_lock_impl* impl;
    if (cel_validate_const(lock, &impl) != CEL_RESULT_SUCCESS) {
        return 0;
    }
    return cel_atomic_i32_load(&impl->cel_internal_epoch_id);
}

void cel_lock_set_epoch_id(cel_lock* lock, int32_t value) {
    cel_lock_impl* impl;
    if (cel_validate(lock, &impl) == CEL_RESULT_SUCCESS) {
        cel_atomic_i32_store(&impl->cel_internal_epoch_id, value);
    }
}

bool cel_lock_raise_epoch_id(cel_lock* lock, int32_t new_epoch_id) {
    cel_lock_impl* impl;
    if (cel_validate(lock, &impl) != CEL_RESULT_SUCCESS) {
        return false;
    }
    int32_t old_epoch = cel_atomic_i32_load(&impl->cel_internal_epoch_id);
    for (;;) {
        if (new_epoch_id <= old_epoch) {
            return false;
        }
        if (cel_atomic_i32_compare_exchange(
                &impl->cel_internal_epoch_id, &old_epoch, new_epoch_id)) {
            return true;
        }
    }
}

cel_result cel_lock_acquire_concurrent(
    cel_lock* lock,
    int32_t max_concurrent,
    int32_t* out_concurrent_id) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }
    if (max_concurrent < 1 || out_concurrent_id == NULL) {
        return CEL_RESULT_INVALID_ARGUMENT;
    }

    int adjust_turn = 0;
    uint64_t counter;

redo:
    counter = cel_atomic_u64_load(&impl->cel_internal_counter);
    if (counter >= (uint64_t)(uint32_t)max_concurrent) {
        ++adjust_turn;
        if (adjust_turn == 1) {
            if (counter < CEL_EXCLUSIVE_ADD * UINT64_C(2)) {
                cel_result result = cel_monitor_enter(&impl->cel_internal_monitor);
                if (result != CEL_RESULT_SUCCESS) {
                    return result;
                }
                result = cel_monitor_exit(&impl->cel_internal_monitor);
                if (result != CEL_RESULT_SUCCESS) {
                    return result;
                }
            } else {
                cel_thread_yield();
            }
        } else if (adjust_turn < 33) {
            cel_thread_yield();
        } else {
            adjust_turn = 1;
            cel_sleep_ms(5u);
        }
        goto redo;
    }

redo2:
    counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, UINT64_C(1)) + UINT64_C(1);
    if ((uint32_t)counter > (uint32_t)INT32_MAX) {
        cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, UINT64_C(1));
        return CEL_RESULT_CAPACITY_EXCEEDED;
    }
    if (counter <= (uint64_t)(uint32_t)max_concurrent) {
        *out_concurrent_id = (int32_t)(uint32_t)counter;
        return CEL_RESULT_SUCCESS;
    }
    counter = cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, UINT64_C(1)) - UINT64_C(1);
    if (counter < CEL_EXCLUSIVE_ADD) {
        goto redo2;
    }
    goto redo;
}

cel_result cel_lock_try_acquire_concurrent(
    cel_lock* lock,
    int32_t max_concurrent,
    int32_t* out_concurrent_id) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }
    if (max_concurrent < 1 || out_concurrent_id == NULL) {
        return CEL_RESULT_INVALID_ARGUMENT;
    }

    uint64_t counter = cel_atomic_u64_load(&impl->cel_internal_counter);
    if (counter >= (uint64_t)(uint32_t)max_concurrent) {
        *out_concurrent_id = 0;
        return CEL_RESULT_NOT_ACQUIRED;
    }
    counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, UINT64_C(1)) + UINT64_C(1);
    if ((uint32_t)counter > (uint32_t)INT32_MAX) {
        cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, UINT64_C(1));
        *out_concurrent_id = 0;
        return CEL_RESULT_CAPACITY_EXCEEDED;
    }
    if (counter <= (uint64_t)(uint32_t)max_concurrent) {
        *out_concurrent_id = (int32_t)(uint32_t)counter;
        return CEL_RESULT_SUCCESS;
    }
    cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, UINT64_C(1));
    *out_concurrent_id = 0;
    return CEL_RESULT_NOT_ACQUIRED;
}

cel_result cel_lock_try_acquire_concurrent_for(
    cel_lock* lock,
    int64_t timeout_milliseconds,
    int32_t max_concurrent,
    int32_t* out_concurrent_id) {
    if (timeout_milliseconds < 0) {
        return cel_lock_acquire_concurrent(lock, max_concurrent, out_concurrent_id);
    }

    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }
    if (max_concurrent < 1 || out_concurrent_id == NULL) {
        return CEL_RESULT_INVALID_ARGUMENT;
    }

    const uint64_t deadline = cel_now_ms() + (uint64_t)timeout_milliseconds;
    int adjust_turn = 0;
    uint64_t counter;

redo:
    if (cel_now_ms() <= deadline) {
        counter = cel_atomic_u64_load(&impl->cel_internal_counter);
        if (counter >= (uint64_t)(uint32_t)max_concurrent) {
            ++adjust_turn;
            if (adjust_turn == 1) {
                if (counter < CEL_EXCLUSIVE_ADD * UINT64_C(2)) {
                    uint64_t now = cel_now_ms();
                    if (now > deadline) {
                        *out_concurrent_id = 0;
                        return CEL_RESULT_TIMEOUT;
                    }
                    cel_result result = cel_monitor_try_enter_for(
                        &impl->cel_internal_monitor,
                        (int64_t)(deadline - now));
                    if (result == CEL_RESULT_SUCCESS) {
                        result = cel_monitor_exit(&impl->cel_internal_monitor);
                        if (result != CEL_RESULT_SUCCESS) {
                            return result;
                        }
                    } else if (result == CEL_RESULT_TIMEOUT || result == CEL_RESULT_NOT_ACQUIRED) {
                        *out_concurrent_id = 0;
                        return CEL_RESULT_TIMEOUT;
                    } else {
                        return result;
                    }
                } else {
                    cel_thread_yield();
                }
            } else if (adjust_turn < 33) {
                cel_thread_yield();
            } else {
                adjust_turn = 1;
                cel_sleep_ms(5u);
            }
            goto redo;
        }

redo2:
        counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, UINT64_C(1)) + UINT64_C(1);
        if ((uint32_t)counter > (uint32_t)INT32_MAX) {
            cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, UINT64_C(1));
            *out_concurrent_id = 0;
            return CEL_RESULT_CAPACITY_EXCEEDED;
        }
        if (counter <= (uint64_t)(uint32_t)max_concurrent) {
            *out_concurrent_id = (int32_t)(uint32_t)counter;
            return CEL_RESULT_SUCCESS;
        }
        counter = cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, UINT64_C(1)) - UINT64_C(1);
        if (counter < CEL_EXCLUSIVE_ADD) {
            if (cel_now_ms() < deadline) {
                goto redo2;
            }
        } else {
            goto redo;
        }
    }

    *out_concurrent_id = 0;
    return CEL_RESULT_TIMEOUT;
}

cel_result cel_lock_release_concurrent(cel_lock* lock) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }
    cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, UINT64_C(1));
    return CEL_RESULT_SUCCESS;
}

cel_result cel_lock_acquire_exclusive(cel_lock* lock) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }

    int adjust_turn = 0;
    uint64_t counter;

redo:
    {
        cel_result result = cel_monitor_enter(&impl->cel_internal_monitor);
        if (result != CEL_RESULT_SUCCESS) {
            return result;
        }
    }
    counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD) + CEL_EXCLUSIVE_ADD;
    if (counter != CEL_EXCLUSIVE_ADD) {
        if (counter < CEL_EXCLUSIVE_ADD * UINT64_C(2)) {
            while ((counter = cel_atomic_u64_load(&impl->cel_internal_counter)) != CEL_EXCLUSIVE_ADD) {
                if (counter < CEL_EXCLUSIVE_ADD * UINT64_C(2)) {
                    cel_adjust_wait(&adjust_turn);
                } else {
                    cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
                    cel_monitor_exit(&impl->cel_internal_monitor);
                    cel_thread_yield();
                    goto redo;
                }
            }
        } else {
            cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
            cel_monitor_exit(&impl->cel_internal_monitor);
            cel_thread_yield();
            goto redo;
        }
    }
    return CEL_RESULT_SUCCESS;
}

cel_result cel_lock_try_acquire_exclusive(
    cel_lock* lock,
    bool preempt_concurrent) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }

    int adjust_turn = 0;
    uint64_t counter;

    if (preempt_concurrent) {
        if (cel_atomic_u64_load(&impl->cel_internal_counter)
                < CEL_EXCLUSIVE_ADD) {
            cel_result result = cel_monitor_enter(&impl->cel_internal_monitor);
            if (result != CEL_RESULT_SUCCESS) {
                return result;
            }
            counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD) + CEL_EXCLUSIVE_ADD;
            if (counter != CEL_EXCLUSIVE_ADD) {
                if (counter < CEL_EXCLUSIVE_ADD * UINT64_C(2)) {
                    while ((counter = cel_atomic_u64_load(&impl->cel_internal_counter)) != CEL_EXCLUSIVE_ADD) {
                        if (counter < CEL_EXCLUSIVE_ADD * UINT64_C(2)) {
                            cel_adjust_wait(&adjust_turn);
                        } else {
                            cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
                            cel_monitor_exit(&impl->cel_internal_monitor);
                            return CEL_RESULT_NOT_ACQUIRED;
                        }
                    }
                    return CEL_RESULT_SUCCESS;
                }
                cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
                cel_monitor_exit(&impl->cel_internal_monitor);
                return CEL_RESULT_NOT_ACQUIRED;
            }
            return CEL_RESULT_SUCCESS;
        }
        return CEL_RESULT_NOT_ACQUIRED;
    }

    cel_result result = cel_monitor_try_enter(&impl->cel_internal_monitor);
    if (result != CEL_RESULT_SUCCESS) {
        return result;
    }
    uint64_t expected = 0;
    if (cel_atomic_u64_compare_exchange(
            &impl->cel_internal_counter, &expected, CEL_EXCLUSIVE_ADD)) {
        return CEL_RESULT_SUCCESS;
    }
    cel_monitor_exit(&impl->cel_internal_monitor);
    return CEL_RESULT_NOT_ACQUIRED;
}

cel_result cel_lock_try_acquire_exclusive_for(
    cel_lock* lock,
    int64_t timeout_milliseconds) {
    if (timeout_milliseconds < 0) {
        return cel_lock_acquire_exclusive(lock);
    }

    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }

    int adjust_turn = 0;
    const uint64_t deadline = cel_now_ms() + (uint64_t)timeout_milliseconds;
    uint64_t counter;

redo:
    {
        uint64_t now = cel_now_ms();
        if (now > deadline) {
            return CEL_RESULT_TIMEOUT;
        }
        cel_result result = cel_monitor_try_enter_for(
            &impl->cel_internal_monitor,
            (int64_t)(deadline - now));
        if (result != CEL_RESULT_SUCCESS) {
            return result == CEL_RESULT_NOT_ACQUIRED
                ? CEL_RESULT_TIMEOUT
                : result;
        }
    }

    counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD) + CEL_EXCLUSIVE_ADD;
    if (counter != CEL_EXCLUSIVE_ADD) {
        if (counter < CEL_EXCLUSIVE_ADD * UINT64_C(2)) {
            while ((counter = cel_atomic_u64_load(&impl->cel_internal_counter)) != CEL_EXCLUSIVE_ADD) {
                if (counter < CEL_EXCLUSIVE_ADD * UINT64_C(2)) {
                    if (cel_now_ms() < deadline) {
                        cel_adjust_wait(&adjust_turn);
                    } else {
                        cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
                        cel_monitor_exit(&impl->cel_internal_monitor);
                        return CEL_RESULT_TIMEOUT;
                    }
                } else {
                    cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
                    cel_monitor_exit(&impl->cel_internal_monitor);
                    cel_thread_yield();
                    if (cel_now_ms() < deadline) {
                        goto redo;
                    }
                    return CEL_RESULT_TIMEOUT;
                }
            }
            return CEL_RESULT_SUCCESS;
        }
        cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
        cel_monitor_exit(&impl->cel_internal_monitor);
        cel_thread_yield();
        if (cel_now_ms() < deadline) {
            goto redo;
        }
        return CEL_RESULT_TIMEOUT;
    }
    return CEL_RESULT_SUCCESS;
}

cel_result cel_lock_release_exclusive(cel_lock* lock) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }
    cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
    return cel_monitor_exit(&impl->cel_internal_monitor);
}

cel_result cel_lock_exclusive_to_concurrent(cel_lock* lock) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }

    uint64_t counter = cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_CONVERGE_ADD) - CEL_CONVERGE_ADD;
    cel_result result = cel_monitor_exit(&impl->cel_internal_monitor);
    if (result != CEL_RESULT_SUCCESS) {
        return result;
    }

    if (counter >= CEL_EXCLUSIVE_ADD) {
        cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, UINT64_C(1));
        int32_t ignored;
        return cel_lock_acquire_concurrent(lock, CEL_MAX_CONCURRENT, &ignored);
    }
    return CEL_RESULT_SUCCESS;
}

cel_result cel_lock_concurrent_to_exclusive(cel_lock* lock) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }

    int adjust_turn = 0;
    uint64_t counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, CEL_CONVERGE_ADD) + CEL_CONVERGE_ADD;
    if (cel_low_i32(counter) != 0) {
        while (cel_low_i32(cel_atomic_u64_load(&impl->cel_internal_counter)) != 0) {
            cel_adjust_wait2(&adjust_turn);
        }
    }
    return cel_monitor_enter(&impl->cel_internal_monitor);
}

cel_result cel_lock_try_concurrent_to_exclusive_with_switch_context_id(
    cel_lock* lock,
    int32_t new_context_id) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }

    int adjust_turn = 0;
    uint64_t counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, CEL_CONVERGE_ADD) + CEL_CONVERGE_ADD;
    if (cel_low_i32(counter) != 0) {
        while (cel_low_i32(cel_atomic_u64_load(&impl->cel_internal_counter)) != 0) {
            cel_adjust_wait2(&adjust_turn);
        }
    }

    if (cel_atomic_i32_exchange(&impl->cel_internal_context_id, new_context_id) != new_context_id) {
        cel_result result = cel_monitor_enter(&impl->cel_internal_monitor);
        return result;
    }

    cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
    return CEL_RESULT_NOT_ACQUIRED;
}

cel_result cel_lock_try_concurrent_to_exclusive_with_raise_epoch_id(
    cel_lock* lock,
    int32_t new_epoch_id) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }

    int adjust_turn = 0;
    uint64_t counter = cel_atomic_u64_fetch_add(&impl->cel_internal_counter, CEL_CONVERGE_ADD) + CEL_CONVERGE_ADD;
    if (cel_low_i32(counter) != 0) {
        while (cel_low_i32(cel_atomic_u64_load(&impl->cel_internal_counter)) != 0) {
            cel_adjust_wait2(&adjust_turn);
        }
    }

    int32_t old_epoch = cel_atomic_i32_load(&impl->cel_internal_epoch_id);
    bool raised = false;
    for (;;) {
        if (new_epoch_id <= old_epoch) {
            break;
        }
        if (cel_atomic_i32_compare_exchange(
                &impl->cel_internal_epoch_id, &old_epoch, new_epoch_id)) {
            raised = true;
            break;
        }
    }

    if (raised) {
        return cel_monitor_enter(&impl->cel_internal_monitor);
    }

    cel_atomic_u64_fetch_sub(&impl->cel_internal_counter, CEL_EXCLUSIVE_ADD);
    return CEL_RESULT_NOT_ACQUIRED;
}

cel_result cel_lock_free_release(cel_lock* lock, int64_t counter_delta) {
    cel_lock_impl* impl;
    cel_result validation = cel_validate(lock, &impl);
    if (validation != CEL_RESULT_SUCCESS) {
        return validation;
    }
    cel_atomic_u64_fetch_add(&impl->cel_internal_counter, (uint64_t)counter_delta);
    if (counter_delta <= -(int64_t)CEL_EXCLUSIVE_ADD) {
        return cel_monitor_exit(&impl->cel_internal_monitor);
    }
    return CEL_RESULT_SUCCESS;
}

const char* cel_result_string(cel_result result) {
    switch (result) {
        case CEL_RESULT_SUCCESS: return "success";
        case CEL_RESULT_NOT_ACQUIRED: return "not acquired";
        case CEL_RESULT_TIMEOUT: return "timeout";
        case CEL_RESULT_INVALID_ARGUMENT: return "invalid argument";
        case CEL_RESULT_NOT_INITIALIZED: return "not initialized";
        case CEL_RESULT_BUSY: return "busy";
        case CEL_RESULT_CAPACITY_EXCEEDED: return "capacity exceeded";
        case CEL_RESULT_PLATFORM_ERROR: return "platform error";
        default: return "unknown result";
    }
}
