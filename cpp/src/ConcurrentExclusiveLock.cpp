#include "ConcurrentExclusiveLock.hpp"
#include "ConcurrentExclusiveLockInternal.h"

#include <sstream>

namespace intomic {

ConcurrentExclusiveLockCapacityExceededException::
ConcurrentExclusiveLockCapacityExceededException()
    : std::runtime_error(
          "ConcurrentExclusiveLock Concurrent capacity was exceeded") {}

void ConcurrentExclusiveLock::ThrowForResult(
    cel_result result,
    const char* operation) {
    if (result == CEL_RESULT_SUCCESS) {
        return;
    }
    if (result == CEL_RESULT_CAPACITY_EXCEEDED) {
        throw ConcurrentExclusiveLockCapacityExceededException();
    }
    if (result == CEL_RESULT_INVALID_ARGUMENT) {
        throw std::invalid_argument(
            std::string(operation) + ": " + cel_result_string(result));
    }
    std::ostringstream message;
    message << operation << ": " << cel_result_string(result);
    throw std::runtime_error(message.str());
}

ConcurrentExclusiveLock::ConcurrentExclusiveLock() {
    ThrowForResult(cel_lock_init(&core_), "cel_lock_init");
}

ConcurrentExclusiveLock::~ConcurrentExclusiveLock() noexcept {
    (void)cel_lock_destroy(&core_);
}

ConcurrentExclusiveLockState
ConcurrentExclusiveLock::ObservedState() const noexcept {
    return static_cast<ConcurrentExclusiveLockState>(
        cel_lock_observed_state(&core_));
}

std::int32_t ConcurrentExclusiveLock::ObservedContention() const noexcept {
    return cel_lock_observed_contention(&core_);
}

std::int32_t ConcurrentExclusiveLock::ContextID() const noexcept {
    return cel_lock_get_context_id(&core_);
}

void ConcurrentExclusiveLock::ContextID(std::int32_t value) noexcept {
    cel_lock_set_context_id(&core_, value);
}

bool ConcurrentExclusiveLock::SwitchContextID(
    std::int32_t newContextID) noexcept {
    return cel_lock_switch_context_id(&core_, newContextID);
}

std::int32_t ConcurrentExclusiveLock::EpochID() const noexcept {
    return cel_lock_get_epoch_id(&core_);
}

void ConcurrentExclusiveLock::EpochID(std::int32_t value) noexcept {
    cel_lock_set_epoch_id(&core_, value);
}

bool ConcurrentExclusiveLock::RaiseEpochID(std::int32_t newEpochID) noexcept {
    return cel_lock_raise_epoch_id(&core_, newEpochID);
}

std::int32_t ConcurrentExclusiveLock::AcquireConcurrent(
    std::int32_t maxConcurrent) {
    std::int32_t concurrentID = 0;
    ThrowForResult(
        cel_lock_acquire_concurrent(&core_, maxConcurrent, &concurrentID),
        "AcquireConcurrent");
    return concurrentID;
}

std::int32_t ConcurrentExclusiveLock::TryAcquireConcurrent(
    std::int32_t maxConcurrent) {
    std::int32_t concurrentID = 0;
    cel_result result = cel_lock_try_acquire_concurrent(
        &core_, maxConcurrent, &concurrentID);
    if (result == CEL_RESULT_NOT_ACQUIRED) {
        return 0;
    }
    ThrowForResult(result, "TryAcquireConcurrent");
    return concurrentID;
}

std::int32_t ConcurrentExclusiveLock::TryAcquireConcurrent(
    std::chrono::milliseconds timeout,
    std::int32_t maxConcurrent) {
    std::int32_t concurrentID = 0;
    cel_result result = cel_lock_try_acquire_concurrent_for(
        &core_, timeout.count(), maxConcurrent, &concurrentID);
    if (result == CEL_RESULT_NOT_ACQUIRED || result == CEL_RESULT_TIMEOUT) {
        return 0;
    }
    ThrowForResult(result, "TryAcquireConcurrent(timeout)");
    return concurrentID;
}

void ConcurrentExclusiveLock::ReleaseConcurrent() {
    ThrowForResult(cel_lock_release_concurrent(&core_), "ReleaseConcurrent");
}

void ConcurrentExclusiveLock::AcquireExclusive() {
    ThrowForResult(cel_lock_acquire_exclusive(&core_), "AcquireExclusive");
}

bool ConcurrentExclusiveLock::TryAcquireExclusive(bool preemptConcurrent) {
    cel_result result = cel_lock_try_acquire_exclusive(
        &core_, preemptConcurrent);
    if (result == CEL_RESULT_NOT_ACQUIRED || result == CEL_RESULT_TIMEOUT) {
        return false;
    }
    ThrowForResult(result, "TryAcquireExclusive");
    return true;
}

bool ConcurrentExclusiveLock::TryAcquireExclusive(
    std::chrono::milliseconds timeout) {
    cel_result result = cel_lock_try_acquire_exclusive_for(
        &core_, timeout.count());
    if (result == CEL_RESULT_NOT_ACQUIRED || result == CEL_RESULT_TIMEOUT) {
        return false;
    }
    ThrowForResult(result, "TryAcquireExclusive(timeout)");
    return true;
}

void ConcurrentExclusiveLock::ReleaseExclusive() {
    ThrowForResult(cel_lock_release_exclusive(&core_), "ReleaseExclusive");
}

void ConcurrentExclusiveLock::ExclusiveToConcurrent() {
    ThrowForResult(
        cel_lock_exclusive_to_concurrent(&core_),
        "ExclusiveToConcurrent");
}

void ConcurrentExclusiveLock::ConcurrentToExclusive() {
    ThrowForResult(
        cel_lock_concurrent_to_exclusive(&core_),
        "ConcurrentToExclusive");
}

bool ConcurrentExclusiveLock::
TryConcurrentToExclusiveWithSwitchContextID(std::int32_t newContextID) {
    cel_result result =
        cel_lock_try_concurrent_to_exclusive_with_switch_context_id(
            &core_, newContextID);
    if (result == CEL_RESULT_NOT_ACQUIRED) {
        return false;
    }
    ThrowForResult(
        result,
        "TryConcurrentToExclusiveWithSwitchContextID");
    return true;
}

bool ConcurrentExclusiveLock::
TryConcurrentToExclusiveWithRaiseEpochID(std::int32_t newEpochID) {
    cel_result result =
        cel_lock_try_concurrent_to_exclusive_with_raise_epoch_id(
            &core_, newEpochID);
    if (result == CEL_RESULT_NOT_ACQUIRED) {
        return false;
    }
    ThrowForResult(
        result,
        "TryConcurrentToExclusiveWithRaiseEpochID");
    return true;
}

void ConcurrentExclusiveLock::FreeRelease(
    std::int64_t counterDelta) noexcept {
    (void)cel_lock_free_release(&core_, counterDelta);
}

ConcurrentExclusiveLockScope::ConcurrentExclusiveLockScope(
    ConcurrentExclusiveLock& locker) noexcept
    : locker_(&locker) {}

ConcurrentExclusiveLockScope::~ConcurrentExclusiveLockScope() noexcept {
    ReleaseHeldPermission();
}

ConcurrentExclusiveLockState
ConcurrentExclusiveLockScope::ObservedState() const noexcept {
    return locker_->ObservedState();
}

std::int32_t ConcurrentExclusiveLockScope::ObservedContention() const noexcept {
    return locker_->ObservedContention();
}

std::int32_t ConcurrentExclusiveLockScope::ContextID() const noexcept {
    return locker_->ContextID();
}

void ConcurrentExclusiveLockScope::ContextID(std::int32_t value) noexcept {
    locker_->ContextID(value);
}

bool ConcurrentExclusiveLockScope::SwitchContextID(
    std::int32_t newContextID) noexcept {
    return locker_->SwitchContextID(newContextID);
}

std::int32_t ConcurrentExclusiveLockScope::EpochID() const noexcept {
    return locker_->EpochID();
}

void ConcurrentExclusiveLockScope::EpochID(std::int32_t value) noexcept {
    locker_->EpochID(value);
}

bool ConcurrentExclusiveLockScope::RaiseEpochID(
    std::int32_t newEpochID) noexcept {
    return locker_->RaiseEpochID(newEpochID);
}

std::int32_t ConcurrentExclusiveLockScope::AcquireConcurrent(
    std::int32_t maxConcurrent) {
    std::int32_t id = locker_->AcquireConcurrent(maxConcurrent);
    ++counterMate_;
    return id;
}

std::int32_t ConcurrentExclusiveLockScope::TryAcquireConcurrent(
    std::int32_t maxConcurrent) {
    std::int32_t id = locker_->TryAcquireConcurrent(maxConcurrent);
    if (id != 0) {
        ++counterMate_;
    }
    return id;
}

std::int32_t ConcurrentExclusiveLockScope::TryAcquireConcurrent(
    std::chrono::milliseconds timeout,
    std::int32_t maxConcurrent) {
    std::int32_t id = locker_->TryAcquireConcurrent(timeout, maxConcurrent);
    if (id != 0) {
        ++counterMate_;
    }
    return id;
}

void ConcurrentExclusiveLockScope::ReleaseConcurrent() {
    locker_->ReleaseConcurrent();
    --counterMate_;
}

void ConcurrentExclusiveLockScope::AcquireExclusive() {
    locker_->AcquireExclusive();
    counterMate_ += ConcurrentExclusiveLock::ExclusiveAdd;
}

bool ConcurrentExclusiveLockScope::TryAcquireExclusive(
    bool preemptConcurrent) {
    bool success = locker_->TryAcquireExclusive(preemptConcurrent);
    if (success) {
        counterMate_ += ConcurrentExclusiveLock::ExclusiveAdd;
    }
    return success;
}

bool ConcurrentExclusiveLockScope::TryAcquireExclusive(
    std::chrono::milliseconds timeout) {
    bool success = locker_->TryAcquireExclusive(timeout);
    if (success) {
        counterMate_ += ConcurrentExclusiveLock::ExclusiveAdd;
    }
    return success;
}

void ConcurrentExclusiveLockScope::ReleaseExclusive() {
    locker_->ReleaseExclusive();
    counterMate_ -= ConcurrentExclusiveLock::ExclusiveAdd;
}

void ConcurrentExclusiveLockScope::ExclusiveToConcurrent() {
    locker_->ExclusiveToConcurrent();
    counterMate_ -= ConcurrentExclusiveLock::ConvergeAdd;
}

void ConcurrentExclusiveLockScope::ConcurrentToExclusive() {
    locker_->ConcurrentToExclusive();
    counterMate_ += ConcurrentExclusiveLock::ConvergeAdd;
}

bool ConcurrentExclusiveLockScope::
TryConcurrentToExclusiveWithSwitchContextID(std::int32_t newContextID) {
    bool success = locker_->TryConcurrentToExclusiveWithSwitchContextID(
        newContextID);
    counterMate_ += success
        ? ConcurrentExclusiveLock::ConvergeAdd
        : -INT64_C(1);
    return success;
}

bool ConcurrentExclusiveLockScope::
TryConcurrentToExclusiveWithRaiseEpochID(std::int32_t newEpochID) {
    bool success = locker_->TryConcurrentToExclusiveWithRaiseEpochID(
        newEpochID);
    counterMate_ += success
        ? ConcurrentExclusiveLock::ConvergeAdd
        : -INT64_C(1);
    return success;
}

void ConcurrentExclusiveLockScope::ReleaseHeldPermission() noexcept {
    if (counterMate_ != 0) {
        locker_->FreeRelease(-counterMate_);
        counterMate_ = 0;
    }
}

ConcurrentExclusiveLockSegment::ConcurrentExclusiveLockSegment(
    ConcurrentExclusiveAccessMode access,
    std::function<void()> segment,
    std::int32_t contextOrEpochID,
    IDType idKind)
    : access_(access),
      segment_(std::move(segment)),
      contextOrEpochID_(contextOrEpochID),
      idKind_(idKind) {
    if (!segment_) {
        throw std::invalid_argument("segment must not be empty");
    }
}

#define CEL_DEFINE_SEGMENT_FACTORY(Name, Mode)                         \
ConcurrentExclusiveLockSegment ConcurrentExclusiveLockSegment::Name( \
    std::function<void()> segment) {                                  \
    return ConcurrentExclusiveLockSegment(                            \
        ConcurrentExclusiveAccessMode::Mode,                          \
        std::move(segment));                                          \
}

CEL_DEFINE_SEGMENT_FACTORY(None, None)
CEL_DEFINE_SEGMENT_FACTORY(Concurrent, Concurrent)
CEL_DEFINE_SEGMENT_FACTORY(TryConcurrent, TryConcurrent)
CEL_DEFINE_SEGMENT_FACTORY(Exclusive, Exclusive)
CEL_DEFINE_SEGMENT_FACTORY(TestExclusive, TestExclusive)
CEL_DEFINE_SEGMENT_FACTORY(TryExclusive, TryExclusive)
CEL_DEFINE_SEGMENT_FACTORY(ConvergeConcurrent, ConvergeConcurrent)
CEL_DEFINE_SEGMENT_FACTORY(ConvergeExclusive, ConvergeExclusive)

#undef CEL_DEFINE_SEGMENT_FACTORY

ConcurrentExclusiveLockSegment
ConcurrentExclusiveLockSegment::TryApplyIDConvergeExclusive(
    std::function<void()> segment,
    std::int32_t contextOrEpochID,
    IDType idType) {
    return ConcurrentExclusiveLockSegment(
        ConcurrentExclusiveAccessMode::TryApplyIDConvergeExclusive,
        std::move(segment),
        contextOrEpochID,
        idType);
}

void ConcurrentExclusiveLockPipeline::DoPipeline(
    const ConcurrentExclusiveLockSegment* segments,
    std::size_t count) const {
    if (segments == nullptr && count != 0) {
        throw std::invalid_argument("segments must not be null");
    }

    ConcurrentExclusiveLockScope scope(*locker_);
    bool isSuccess;
    ConcurrentExclusiveAccessMode lastSuccessAccess =
        ConcurrentExclusiveAccessMode::None;

    for (std::size_t index = 0; index < count; ++index) {
        const auto& segment = segments[index];
        switch (segment.Access()) {
            case ConcurrentExclusiveAccessMode::Concurrent:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        scope.ReleaseConcurrent();
                        (void)scope.AcquireConcurrent();
                        segment.Execute();
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        scope.ReleaseExclusive();
                        (void)scope.AcquireConcurrent();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::Concurrent;
                        segment.Execute();
                        break;
                    default:
                        (void)scope.AcquireConcurrent();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::Concurrent;
                        segment.Execute();
                        break;
                }
                break;

            case ConcurrentExclusiveAccessMode::TryConcurrent:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        scope.ReleaseConcurrent();
                        if (scope.TryAcquireConcurrent() != 0) {
                            segment.Execute();
                        } else {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::None;
                        }
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        scope.ReleaseExclusive();
                        if (scope.TryAcquireConcurrent() != 0) {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::Concurrent;
                            segment.Execute();
                        } else {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::None;
                        }
                        break;
                    default:
                        if (scope.TryAcquireConcurrent() != 0) {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::Concurrent;
                            segment.Execute();
                        }
                        break;
                }
                break;

            case ConcurrentExclusiveAccessMode::Exclusive:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        scope.ReleaseConcurrent();
                        scope.AcquireExclusive();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::Exclusive;
                        segment.Execute();
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        scope.ReleaseExclusive();
                        scope.AcquireExclusive();
                        segment.Execute();
                        break;
                    default:
                        scope.AcquireExclusive();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::Exclusive;
                        segment.Execute();
                        break;
                }
                break;

            case ConcurrentExclusiveAccessMode::TestExclusive:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        scope.ReleaseConcurrent();
                        if (scope.TryAcquireExclusive(false)) {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::Exclusive;
                            segment.Execute();
                        } else {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::None;
                        }
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        scope.ReleaseExclusive();
                        if (scope.TryAcquireExclusive(false)) {
                            segment.Execute();
                        } else {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::None;
                        }
                        break;
                    default:
                        if (scope.TryAcquireExclusive(false)) {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::Exclusive;
                            segment.Execute();
                        }
                        break;
                }
                break;

            case ConcurrentExclusiveAccessMode::TryExclusive:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        scope.ReleaseConcurrent();
                        if (scope.TryAcquireExclusive(true)) {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::Exclusive;
                            segment.Execute();
                        } else {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::None;
                        }
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        scope.ReleaseExclusive();
                        if (scope.TryAcquireExclusive(true)) {
                            segment.Execute();
                        } else {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::None;
                        }
                        break;
                    default:
                        if (scope.TryAcquireExclusive(true)) {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::Exclusive;
                            segment.Execute();
                        }
                        break;
                }
                break;

            case ConcurrentExclusiveAccessMode::ConvergeConcurrent:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        segment.Execute();
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        scope.ExclusiveToConcurrent();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::Concurrent;
                        segment.Execute();
                        break;
                    default:
                        (void)scope.AcquireConcurrent();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::Concurrent;
                        segment.Execute();
                        break;
                }
                break;

            case ConcurrentExclusiveAccessMode::ConvergeExclusive:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        scope.ConcurrentToExclusive();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::Exclusive;
                        segment.Execute();
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        segment.Execute();
                        break;
                    default:
                        scope.AcquireExclusive();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::Exclusive;
                        segment.Execute();
                        break;
                }
                break;

            case ConcurrentExclusiveAccessMode::
                    TryApplyIDConvergeExclusive:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        isSuccess = segment.IDKind() ==
                                ConcurrentExclusiveLockSegment::IDType::ContextID
                            ? scope.TryConcurrentToExclusiveWithSwitchContextID(
                                  segment.ContextOrEpochID())
                            : scope.TryConcurrentToExclusiveWithRaiseEpochID(
                                  segment.ContextOrEpochID());
                        if (isSuccess) {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::Exclusive;
                            segment.Execute();
                        } else {
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::None;
                        }
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        isSuccess = segment.IDKind() ==
                                ConcurrentExclusiveLockSegment::IDType::ContextID
                            ? scope.SwitchContextID(
                                  segment.ContextOrEpochID())
                            : scope.RaiseEpochID(
                                  segment.ContextOrEpochID());
                        if (isSuccess) {
                            segment.Execute();
                        } else {
                            scope.ReleaseExclusive();
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::None;
                        }
                        break;
                    default:
                        isSuccess = segment.IDKind() ==
                                ConcurrentExclusiveLockSegment::IDType::ContextID
                            ? scope.SwitchContextID(
                                  segment.ContextOrEpochID())
                            : scope.RaiseEpochID(
                                  segment.ContextOrEpochID());
                        if (isSuccess) {
                            scope.AcquireExclusive();
                            lastSuccessAccess =
                                ConcurrentExclusiveAccessMode::Exclusive;
                            segment.Execute();
                        }
                        break;
                }
                break;

            default:
                switch (lastSuccessAccess) {
                    case ConcurrentExclusiveAccessMode::Concurrent:
                        scope.ReleaseConcurrent();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::None;
                        segment.Execute();
                        break;
                    case ConcurrentExclusiveAccessMode::Exclusive:
                        scope.ReleaseExclusive();
                        lastSuccessAccess =
                            ConcurrentExclusiveAccessMode::None;
                        segment.Execute();
                        break;
                    default:
                        segment.Execute();
                        break;
                }
                break;
        }
    }
}

std::future<void> ConcurrentExclusiveLockPipeline::DoPipelineAsync(
    std::vector<ConcurrentExclusiveLockSegment> segments) const {
    ConcurrentExclusiveLock* locker = locker_;
    return std::async(
        std::launch::async,
        [locker, segments = std::move(segments)]() mutable {
            ConcurrentExclusiveLockPipeline pipeline(*locker);
            pipeline.DoPipeline(segments);
        });
}

} // namespace intomic
