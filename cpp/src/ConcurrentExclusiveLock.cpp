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
    cel_result result = cel_lock_init(&core_);
    if (result != CEL_RESULT_SUCCESS) {
        ThrowForResult(result, "cel_lock_init");
    }
}

ConcurrentExclusiveLock::~ConcurrentExclusiveLock() noexcept {
    (void)cel_lock_destroy(&core_);
}

void ConcurrentExclusiveLock::FreeRelease(
    std::int64_t counterDelta) noexcept {
    (void)cel_lock_free_release(&core_, counterDelta);
}

ConcurrentExclusiveLockScope::~ConcurrentExclusiveLockScope() noexcept {
    Dispose();
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
        ConcurrentExclusiveAccessMode::None; // Only None, Concurrent, or Exclusive.

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
