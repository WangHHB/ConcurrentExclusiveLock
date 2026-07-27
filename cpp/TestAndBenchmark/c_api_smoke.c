#include "ConcurrentExclusiveLock.h"

int cel_run_c_api_smoke(void) {
    cel_lock lock;
    int32_t id = 0;

    if (cel_lock_init(&lock) != CEL_RESULT_SUCCESS) return 1;
    if (cel_lock_observed_state(&lock) != CEL_LOCK_STATE_IDLE) return 2;

    if (cel_lock_acquire_concurrent(&lock, CEL_MAX_CONCURRENT, &id)
            != CEL_RESULT_SUCCESS || id != 1) return 3;
    if (cel_lock_observed_state(&lock) != CEL_LOCK_STATE_CONCURRENT) return 4;
    if (cel_lock_release_concurrent(&lock) != CEL_RESULT_SUCCESS) return 5;

    if (cel_lock_acquire_exclusive(&lock) != CEL_RESULT_SUCCESS) return 6;
    if (cel_lock_observed_state(&lock) != CEL_LOCK_STATE_EXCLUSIVE) return 7;
    if (cel_lock_exclusive_to_concurrent(&lock) != CEL_RESULT_SUCCESS) return 8;
    if (cel_lock_release_concurrent(&lock) != CEL_RESULT_SUCCESS) return 9;

    if (cel_lock_get_context_id(&lock) != 0) return 10;
    if (!cel_lock_switch_context_id(&lock, 42)) return 11;
    if (cel_lock_switch_context_id(&lock, 42)) return 12;
    if (!cel_lock_raise_epoch_id(&lock, 7)) return 13;
    if (cel_lock_raise_epoch_id(&lock, 7)) return 14;

    if (cel_lock_destroy(&lock) != CEL_RESULT_SUCCESS) return 15;
    return 0;
}
