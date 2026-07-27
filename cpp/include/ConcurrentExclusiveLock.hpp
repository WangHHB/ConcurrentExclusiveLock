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

    [[nodiscard]] ConcurrentExclusiveLockState ObservedState() const noexcept;
    [[nodiscard]] std::int32_t ObservedContention() const noexcept;

    [[nodiscard]] std::int32_t ContextID() const noexcept;
    void ContextID(std::int32_t value) noexcept;
    [[nodiscard]] bool SwitchContextID(std::int32_t newContextID) noexcept;

    [[nodiscard]] std::int32_t EpochID() const noexcept;
    void EpochID(std::int32_t value) noexcept;
    [[nodiscard]] bool RaiseEpochID(std::int32_t newEpochID) noexcept;

    [[nodiscard]] std::int32_t AcquireConcurrent(
        std::int32_t maxConcurrent = MaxConcurrent);
    [[nodiscard]] std::int32_t TryAcquireConcurrent(
        std::int32_t maxConcurrent = MaxConcurrent);
    [[nodiscard]] std::int32_t TryAcquireConcurrent(
        std::chrono::milliseconds timeout,
        std::int32_t maxConcurrent = MaxConcurrent);
    void ReleaseConcurrent();

    void AcquireExclusive();
    [[nodiscard]] bool TryAcquireExclusive(bool preemptConcurrent = true);
    [[nodiscard]] bool TryAcquireExclusive(std::chrono::milliseconds timeout);
    void ReleaseExclusive();

    void ExclusiveToConcurrent();
    void ConcurrentToExclusive();

    [[nodiscard]] bool TryConcurrentToExclusiveWithSwitchContextID(
        std::int32_t newContextID);
    [[nodiscard]] bool TryConcurrentToExclusiveWithRaiseEpochID(
        std::int32_t newEpochID);

    [[nodiscard]] cel_lock* NativeHandle() noexcept { return &core_; }
    [[nodiscard]] const cel_lock* NativeHandle() const noexcept { return &core_; }

private:
    cel_lock core_{};

    void FreeRelease(std::int64_t counterDelta) noexcept;
    static void ThrowForResult(cel_result result, const char* operation);

    friend class ConcurrentExclusiveLockScope;
};

class ConcurrentExclusiveLockScope final {
public:
    explicit ConcurrentExclusiveLockScope(ConcurrentExclusiveLock& locker) noexcept;
    ~ConcurrentExclusiveLockScope() noexcept;

    ConcurrentExclusiveLockScope(const ConcurrentExclusiveLockScope&) = delete;
    ConcurrentExclusiveLockScope& operator=(const ConcurrentExclusiveLockScope&) = delete;
    ConcurrentExclusiveLockScope(ConcurrentExclusiveLockScope&&) = delete;
    ConcurrentExclusiveLockScope& operator=(ConcurrentExclusiveLockScope&&) = delete;

    [[nodiscard]] ConcurrentExclusiveLockState ObservedState() const noexcept;
    [[nodiscard]] std::int32_t ObservedContention() const noexcept;

    [[nodiscard]] std::int32_t ContextID() const noexcept;
    void ContextID(std::int32_t value) noexcept;
    [[nodiscard]] bool SwitchContextID(std::int32_t newContextID) noexcept;

    [[nodiscard]] std::int32_t EpochID() const noexcept;
    void EpochID(std::int32_t value) noexcept;
    [[nodiscard]] bool RaiseEpochID(std::int32_t newEpochID) noexcept;

    [[nodiscard]] std::int32_t AcquireConcurrent(
        std::int32_t maxConcurrent = ConcurrentExclusiveLock::MaxConcurrent);
    [[nodiscard]] std::int32_t TryAcquireConcurrent(
        std::int32_t maxConcurrent = ConcurrentExclusiveLock::MaxConcurrent);
    [[nodiscard]] std::int32_t TryAcquireConcurrent(
        std::chrono::milliseconds timeout,
        std::int32_t maxConcurrent = ConcurrentExclusiveLock::MaxConcurrent);
    void ReleaseConcurrent();

    void AcquireExclusive();
    [[nodiscard]] bool TryAcquireExclusive(bool preemptConcurrent = true);
    [[nodiscard]] bool TryAcquireExclusive(std::chrono::milliseconds timeout);
    void ReleaseExclusive();

    void ExclusiveToConcurrent();
    void ConcurrentToExclusive();

    [[nodiscard]] bool TryConcurrentToExclusiveWithSwitchContextID(
        std::int32_t newContextID);
    [[nodiscard]] bool TryConcurrentToExclusiveWithRaiseEpochID(
        std::int32_t newEpochID);

    void ReleaseHeldPermission() noexcept;

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

    [[nodiscard]] static ConcurrentExclusiveLockSegment None(
        std::function<void()> segment);
    [[nodiscard]] static ConcurrentExclusiveLockSegment Concurrent(
        std::function<void()> segment);
    [[nodiscard]] static ConcurrentExclusiveLockSegment TryConcurrent(
        std::function<void()> segment);
    [[nodiscard]] static ConcurrentExclusiveLockSegment Exclusive(
        std::function<void()> segment);
    [[nodiscard]] static ConcurrentExclusiveLockSegment TestExclusive(
        std::function<void()> segment);
    [[nodiscard]] static ConcurrentExclusiveLockSegment TryExclusive(
        std::function<void()> segment);
    [[nodiscard]] static ConcurrentExclusiveLockSegment ConvergeConcurrent(
        std::function<void()> segment);
    [[nodiscard]] static ConcurrentExclusiveLockSegment ConvergeExclusive(
        std::function<void()> segment);
    [[nodiscard]] static ConcurrentExclusiveLockSegment TryApplyIDConvergeExclusive(
        std::function<void()> segment,
        std::int32_t contextOrEpochID,
        IDType idType);

    [[nodiscard]] ConcurrentExclusiveAccessMode Access() const noexcept {
        return access_;
    }
    [[nodiscard]] std::int32_t ContextOrEpochID() const noexcept {
        return contextOrEpochID_;
    }
    [[nodiscard]] IDType IDKind() const noexcept { return idKind_; }
    void Execute() const { segment_(); }

private:
    ConcurrentExclusiveLockSegment(
        ConcurrentExclusiveAccessMode access,
        std::function<void()> segment,
        std::int32_t contextOrEpochID = 0,
        IDType idKind = IDType::ContextID);

    ConcurrentExclusiveAccessMode access_;
    std::function<void()> segment_;
    std::int32_t contextOrEpochID_;
    IDType idKind_;
};

class ConcurrentExclusiveLockPipeline final {
public:
    explicit ConcurrentExclusiveLockPipeline(
        ConcurrentExclusiveLock& locker) noexcept
        : locker_(&locker) {}

    ConcurrentExclusiveLockPipeline(const ConcurrentExclusiveLockPipeline&) = default;
    ConcurrentExclusiveLockPipeline& operator=(const ConcurrentExclusiveLockPipeline&) = default;

    void DoPipeline(
        const ConcurrentExclusiveLockSegment* segments,
        std::size_t count) const;

    void DoPipeline(
        const std::vector<ConcurrentExclusiveLockSegment>& segments) const {
        DoPipeline(segments.data(), segments.size());
    }

    template <typename... TSegments,
              typename = std::enable_if_t<
                  (std::is_same_v<std::decay_t<TSegments>,
                                  ConcurrentExclusiveLockSegment> && ...)>>
    void DoPipeline(TSegments&&... segments) const {
        std::array<ConcurrentExclusiveLockSegment, sizeof...(TSegments)> values{
            std::forward<TSegments>(segments)...};
        DoPipeline(values.data(), values.size());
    }

    [[nodiscard]] std::future<void> DoPipelineAsync(
        std::vector<ConcurrentExclusiveLockSegment> segments) const;

private:
    ConcurrentExclusiveLock* locker_;
};

} // namespace intomic

#endif // CONCURRENT_EXCLUSIVE_LOCK_HPP
