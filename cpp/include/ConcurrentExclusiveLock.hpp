#ifndef CONCURRENT_EXCLUSIVE_LOCK_HPP
#define CONCURRENT_EXCLUSIVE_LOCK_HPP

#include "ConcurrentExclusiveLock.h"

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <future>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <utility>
#include <vector>

#if defined(_MSC_VER)
#  define INTOMIC_CEL_FORCE_INLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
#  define INTOMIC_CEL_FORCE_INLINE inline __attribute__((always_inline))
#else
#  define INTOMIC_CEL_FORCE_INLINE inline
#endif

namespace intomic {

    class ConcurrentExclusiveLockCapacityExceededException final
        : public std::runtime_error {
    public:
        ConcurrentExclusiveLockCapacityExceededException();
    };

    enum class ConcurrentExclusiveLockState : std::uint8_t {
        Idle = CEL_LOCK_STATE_IDLE,
        Concurrent = CEL_LOCK_STATE_CONCURRENT,
        Exclusive = CEL_LOCK_STATE_EXCLUSIVE
    };

    class ConcurrentExclusiveLockScope;
    class ConcurrentExclusiveLockPipeline;

    /*
     * This C++ layer intentionally mirrors the C# public method names and order.
     * It does not implement another lock algorithm: Lock/Scope/Segment forwarding
     * methods stay in this header and are force-inlined over the C core.
     */

    class ConcurrentExclusiveLock final {
    public:
        static constexpr std::int32_t MaxConcurrent = CEL_MAX_CONCURRENT;
        static constexpr std::int64_t ExclusiveAdd = INT64_C(4294967296);
        static constexpr std::int64_t ConvergeAdd = INT64_C(4294967295);

        ConcurrentExclusiveLock();
        ~ConcurrentExclusiveLock() noexcept;

        ConcurrentExclusiveLock(const ConcurrentExclusiveLock&) = delete;
        ConcurrentExclusiveLock& operator=(const ConcurrentExclusiveLock&) = delete;
        ConcurrentExclusiveLock(ConcurrentExclusiveLock&&) = delete;
        ConcurrentExclusiveLock& operator=(ConcurrentExclusiveLock&&) = delete;

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE ConcurrentExclusiveLockState
            ObservedState() const noexcept {
            return static_cast<ConcurrentExclusiveLockState>(
                cel_lock_observed_state(&core_));
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t
            ObservedContention() const noexcept {
            return cel_lock_observed_contention(&core_);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t
            ContextID() const noexcept {
            return cel_lock_get_context_id(&core_);
        }

        INTOMIC_CEL_FORCE_INLINE void ContextID(std::int32_t value) noexcept {
            cel_lock_set_context_id(&core_, value);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t
            EpochID() const noexcept {
            return cel_lock_get_epoch_id(&core_);
        }

        INTOMIC_CEL_FORCE_INLINE void EpochID(std::int32_t value) noexcept {
            cel_lock_set_epoch_id(&core_, value);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool
            SwitchContextID(std::int32_t newContextID) noexcept {
            return cel_lock_switch_context_id(&core_, newContextID);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool
            RaiseEpochID(std::int32_t newEpochID) noexcept {
            return cel_lock_raise_epoch_id(&core_, newEpochID);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t AcquireConcurrent(
            std::int32_t maxConcurrent = MaxConcurrent) {
            std::int32_t concurrentID = 0;
            cel_result result = cel_lock_acquire_concurrent(
                &core_, maxConcurrent, &concurrentID);
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "AcquireConcurrent");
            }
            return concurrentID;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t TryAcquireConcurrent(
            std::int32_t maxConcurrent = MaxConcurrent) {
            std::int32_t concurrentID = 0;
            cel_result result = cel_lock_try_acquire_concurrent(
                &core_, maxConcurrent, &concurrentID);
            if (result == CEL_RESULT_NOT_ACQUIRED) {
                return 0;
            }
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "TryAcquireConcurrent");
            }
            return concurrentID;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t TryAcquireConcurrent(
            std::chrono::milliseconds timeout,
            std::int32_t maxConcurrent = MaxConcurrent) {
            std::int32_t concurrentID = 0;
            cel_result result = cel_lock_try_acquire_concurrent_for(
                &core_, timeout.count(), maxConcurrent, &concurrentID);
            if (result == CEL_RESULT_NOT_ACQUIRED ||
                result == CEL_RESULT_TIMEOUT) {
                return 0;
            }
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "TryAcquireConcurrent(timeout)");
            }
            return concurrentID;
        }

        INTOMIC_CEL_FORCE_INLINE void ReleaseConcurrent() {
            cel_result result = cel_lock_release_concurrent(&core_);
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "ReleaseConcurrent");
            }
        }

        INTOMIC_CEL_FORCE_INLINE void AcquireExclusive() {
            cel_result result = cel_lock_acquire_exclusive(&core_);
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "AcquireExclusive");
            }
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool TryAcquireExclusive(
            bool preemptConcurrent = true) {
            cel_result result = cel_lock_try_acquire_exclusive(
                &core_, preemptConcurrent);
            if (result == CEL_RESULT_NOT_ACQUIRED ||
                result == CEL_RESULT_TIMEOUT) {
                return false;
            }
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "TryAcquireExclusive");
            }
            return true;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool TryAcquireExclusive(
            std::chrono::milliseconds timeout) {
            cel_result result = cel_lock_try_acquire_exclusive_for(
                &core_, timeout.count());
            if (result == CEL_RESULT_NOT_ACQUIRED ||
                result == CEL_RESULT_TIMEOUT) {
                return false;
            }
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "TryAcquireExclusive(timeout)");
            }
            return true;
        }

        INTOMIC_CEL_FORCE_INLINE void ReleaseExclusive() {
            cel_result result = cel_lock_release_exclusive(&core_);
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "ReleaseExclusive");
            }
        }

        INTOMIC_CEL_FORCE_INLINE void ExclusiveToConcurrent() {
            cel_result result = cel_lock_exclusive_to_concurrent(&core_);
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "ExclusiveToConcurrent");
            }
        }

        INTOMIC_CEL_FORCE_INLINE void ConcurrentToExclusive() {
            cel_result result = cel_lock_concurrent_to_exclusive(&core_);
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(result, "ConcurrentToExclusive");
            }
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool
            TryConcurrentToExclusiveWithSwitchContextID(
                std::int32_t newContextID) {
            cel_result result =
                cel_lock_try_concurrent_to_exclusive_with_switch_context_id(
                    &core_, newContextID);
            if (result == CEL_RESULT_NOT_ACQUIRED) {
                return false;
            }
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(
                    result,
                    "TryConcurrentToExclusiveWithSwitchContextID");
            }
            return true;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool
            TryConcurrentToExclusiveWithRaiseEpochID(std::int32_t newEpochID) {
            cel_result result =
                cel_lock_try_concurrent_to_exclusive_with_raise_epoch_id(
                    &core_, newEpochID);
            if (result == CEL_RESULT_NOT_ACQUIRED) {
                return false;
            }
            if (result != CEL_RESULT_SUCCESS) {
                ThrowForResult(
                    result,
                    "TryConcurrentToExclusiveWithRaiseEpochID");
            }
            return true;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE cel_lock* NativeHandle() noexcept {
            return &core_;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE const cel_lock*
            NativeHandle() const noexcept {
            return &core_;
        }

    private:
        cel_lock core_{};

        void FreeRelease(std::int64_t counterDelta) noexcept;
        static void ThrowForResult(cel_result result, const char* operation);

        friend class ConcurrentExclusiveLockScope;
    };

    class ConcurrentExclusiveLockScope final {
    public:
        explicit INTOMIC_CEL_FORCE_INLINE ConcurrentExclusiveLockScope(
            ConcurrentExclusiveLock& locker) noexcept
            : locker_(&locker) {
        }

        ~ConcurrentExclusiveLockScope() noexcept;

        ConcurrentExclusiveLockScope(const ConcurrentExclusiveLockScope&) = delete;
        ConcurrentExclusiveLockScope& operator=(const ConcurrentExclusiveLockScope&) = delete;
        ConcurrentExclusiveLockScope(ConcurrentExclusiveLockScope&&) = delete;
        ConcurrentExclusiveLockScope& operator=(ConcurrentExclusiveLockScope&&) = delete;

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE ConcurrentExclusiveLockState
            ObservedState() const noexcept {
            return locker_->ObservedState();
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t
            ObservedContention() const noexcept {
            return locker_->ObservedContention();
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t
            ContextID() const noexcept {
            return locker_->ContextID();
        }

        INTOMIC_CEL_FORCE_INLINE void ContextID(std::int32_t value) noexcept {
            locker_->ContextID(value);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t
            EpochID() const noexcept {
            return locker_->EpochID();
        }

        INTOMIC_CEL_FORCE_INLINE void EpochID(std::int32_t value) noexcept {
            locker_->EpochID(value);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool
            SwitchContextID(std::int32_t newContextID) noexcept {
            return locker_->SwitchContextID(newContextID);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool
            RaiseEpochID(std::int32_t newEpochID) noexcept {
            return locker_->RaiseEpochID(newEpochID);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t AcquireConcurrent(
            std::int32_t maxConcurrent = ConcurrentExclusiveLock::MaxConcurrent) {
            std::int32_t concurrentID = locker_->AcquireConcurrent(maxConcurrent);
            ++counterMate_;
            return concurrentID;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t TryAcquireConcurrent(
            std::int32_t maxConcurrent = ConcurrentExclusiveLock::MaxConcurrent) {
            std::int32_t concurrentID = locker_->TryAcquireConcurrent(maxConcurrent);
            if (concurrentID != 0) {
                ++counterMate_;
            }
            return concurrentID;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t TryAcquireConcurrent(
            std::chrono::milliseconds timeout,
            std::int32_t maxConcurrent = ConcurrentExclusiveLock::MaxConcurrent) {
            std::int32_t concurrentID = locker_->TryAcquireConcurrent(
                timeout, maxConcurrent);
            if (concurrentID != 0) {
                ++counterMate_;
            }
            return concurrentID;
        }

        INTOMIC_CEL_FORCE_INLINE void ReleaseConcurrent() {
            locker_->ReleaseConcurrent();
            --counterMate_;
        }

        INTOMIC_CEL_FORCE_INLINE void AcquireExclusive() {
            locker_->AcquireExclusive();
            counterMate_ += ConcurrentExclusiveLock::ExclusiveAdd;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool TryAcquireExclusive(
            bool preemptConcurrent = true) {
            bool success = locker_->TryAcquireExclusive(preemptConcurrent);
            if (success) {
                counterMate_ += ConcurrentExclusiveLock::ExclusiveAdd;
            }
            return success;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool TryAcquireExclusive(
            std::chrono::milliseconds timeout) {
            bool success = locker_->TryAcquireExclusive(timeout);
            if (success) {
                counterMate_ += ConcurrentExclusiveLock::ExclusiveAdd;
            }
            return success;
        }

        INTOMIC_CEL_FORCE_INLINE void ReleaseExclusive() {
            locker_->ReleaseExclusive();
            counterMate_ -= ConcurrentExclusiveLock::ExclusiveAdd;
        }

        INTOMIC_CEL_FORCE_INLINE void ExclusiveToConcurrent() {
            locker_->ExclusiveToConcurrent();
            counterMate_ -= ConcurrentExclusiveLock::ConvergeAdd;
        }

        INTOMIC_CEL_FORCE_INLINE void ConcurrentToExclusive() {
            locker_->ConcurrentToExclusive();
            counterMate_ += ConcurrentExclusiveLock::ConvergeAdd;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool
            TryConcurrentToExclusiveWithSwitchContextID(
                std::int32_t newContextID) {
            bool success = locker_->TryConcurrentToExclusiveWithSwitchContextID(
                newContextID);
            if (success) {
                counterMate_ += ConcurrentExclusiveLock::ConvergeAdd;
            }
            else {
                --counterMate_;
            }
            return success;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE bool
            TryConcurrentToExclusiveWithRaiseEpochID(std::int32_t newEpochID) {
            bool success = locker_->TryConcurrentToExclusiveWithRaiseEpochID(
                newEpochID);
            if (success) {
                counterMate_ += ConcurrentExclusiveLock::ConvergeAdd;
            }
            else {
                --counterMate_;
            }
            return success;
        }

        INTOMIC_CEL_FORCE_INLINE void Dispose() noexcept {
            if (counterMate_ != 0) {
                locker_->FreeRelease(-counterMate_);
                counterMate_ = 0;
            }
        }

    private:
        ConcurrentExclusiveLock* locker_;
        std::int64_t counterMate_ = 0;
    };

    enum class ConcurrentExclusiveAccessMode : std::uint8_t {
        None = 0,
        Concurrent = 1,
        TryConcurrent = 2,
        Exclusive = 3,
        TestExclusive = 4,
        TryExclusive = 5,
        ConvergeConcurrent = 6,
        ConvergeExclusive = 7,
        TryApplyIDConvergeExclusive = 8
    };

    class ConcurrentExclusiveLockSegment final {
    public:
        enum class IDType : std::uint8_t {
            ContextID = 0,
            EpochID = 1
        };

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment None(std::function<void()> segment) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::None,
                std::move(segment));
        }

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment Concurrent(std::function<void()> segment) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::Concurrent,
                std::move(segment));
        }

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment TryConcurrent(std::function<void()> segment) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::TryConcurrent,
                std::move(segment));
        }

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment Exclusive(std::function<void()> segment) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::Exclusive,
                std::move(segment));
        }

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment TestExclusive(std::function<void()> segment) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::TestExclusive,
                std::move(segment));
        }

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment TryExclusive(std::function<void()> segment) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::TryExclusive,
                std::move(segment));
        }

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment ConvergeConcurrent(
                std::function<void()> segment) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::ConvergeConcurrent,
                std::move(segment));
        }

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment ConvergeExclusive(
                std::function<void()> segment) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::ConvergeExclusive,
                std::move(segment));
        }

        [[nodiscard]] static INTOMIC_CEL_FORCE_INLINE
            ConcurrentExclusiveLockSegment TryApplyIDConvergeExclusive(
                std::function<void()> segment,
                std::int32_t contextOrEpochID,
                IDType idType) {
            return ConcurrentExclusiveLockSegment(
                ConcurrentExclusiveAccessMode::TryApplyIDConvergeExclusive,
                std::move(segment),
                contextOrEpochID,
                idType);
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE ConcurrentExclusiveAccessMode
            Access() const noexcept {
            return access_;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE std::int32_t
            ContextOrEpochID() const noexcept {
            return contextOrEpochID_;
        }

        [[nodiscard]] INTOMIC_CEL_FORCE_INLINE IDType IDKind() const noexcept {
            return idKind_;
        }

        INTOMIC_CEL_FORCE_INLINE void Execute() const {
            segment_();
        }

    private:
        INTOMIC_CEL_FORCE_INLINE ConcurrentExclusiveLockSegment(
            ConcurrentExclusiveAccessMode access,
            std::function<void()> segment,
            std::int32_t contextOrEpochID = 0,
            IDType idKind = IDType::ContextID)
            : access_(access),
            segment_(std::move(segment)),
            contextOrEpochID_(contextOrEpochID),
            idKind_(idKind) {
            if (!segment_) {
                throw std::invalid_argument("segment must not be empty");
            }
        }

        ConcurrentExclusiveAccessMode access_;
        std::function<void()> segment_;
        std::int32_t contextOrEpochID_;
        IDType idKind_;
    };

    class ConcurrentExclusiveLockPipeline final {
    public:
        explicit INTOMIC_CEL_FORCE_INLINE ConcurrentExclusiveLockPipeline(
            ConcurrentExclusiveLock& locker) noexcept
            : locker_(&locker) {
        }

        ConcurrentExclusiveLockPipeline(const ConcurrentExclusiveLockPipeline&) = default;
        ConcurrentExclusiveLockPipeline& operator=(const ConcurrentExclusiveLockPipeline&) = default;

        void DoPipeline(
            const ConcurrentExclusiveLockSegment* segments,
            std::size_t count) const;

        INTOMIC_CEL_FORCE_INLINE void DoPipeline(
            const std::vector<ConcurrentExclusiveLockSegment>& segments) const {
            DoPipeline(segments.data(), segments.size());
        }

        template <typename... TSegments,
            typename = std::enable_if_t<
            (std::is_same_v<std::decay_t<TSegments>,
                ConcurrentExclusiveLockSegment> && ...)>>
            INTOMIC_CEL_FORCE_INLINE void DoPipeline(TSegments&&... segments) const {
            std::array<ConcurrentExclusiveLockSegment, sizeof...(TSegments)> values{
                std::forward<TSegments>(segments)... };
            DoPipeline(values.data(), values.size());
        }

        [[nodiscard]] std::future<void> DoPipelineAsync(
            std::vector<ConcurrentExclusiveLockSegment> segments) const;

    private:
        ConcurrentExclusiveLock* locker_;
    };

} // namespace intomic

#undef INTOMIC_CEL_FORCE_INLINE

#endif // CONCURRENT_EXCLUSIVE_LOCK_HPP
