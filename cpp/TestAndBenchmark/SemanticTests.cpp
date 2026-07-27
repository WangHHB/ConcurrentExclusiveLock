#include "SemanticTests.hpp"

#include "ConcurrentExclusiveLock.hpp"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <exception>
#include <future>
#include <iostream>
#include <limits>
#include <mutex>
#include <random>
#include <sstream>
#include <stdexcept>
#include <thread>
#include <vector>

extern "C" int cel_run_c_api_smoke(void);

namespace celtest {
namespace {

using intomic::ConcurrentExclusiveAccessMode;
using intomic::ConcurrentExclusiveLock;
using intomic::ConcurrentExclusiveLockPipeline;
using intomic::ConcurrentExclusiveLockScope;
using intomic::ConcurrentExclusiveLockSegment;
using intomic::ConcurrentExclusiveLockState;

class StartGate {
public:
    explicit StartGate(int participants)
        : participants_(participants) {}

    void ArriveAndWait() {
        std::unique_lock<std::mutex> lock(mutex_);
        ++arrived_;
        if (arrived_ == participants_) {
            open_ = true;
            condition_.notify_all();
            return;
        }
        condition_.wait(lock, [this] { return open_; });
    }

private:
    int participants_;
    int arrived_ = 0;
    bool open_ = false;
    std::mutex mutex_;
    std::condition_variable condition_;
};

[[noreturn]] void Fail(const std::string& message) {
    throw std::runtime_error(message);
}

void Require(bool condition, const std::string& message) {
    if (!condition) {
        Fail(message);
    }
}

void RecordFailure(
    std::exception_ptr error,
    std::mutex& mutex,
    std::exception_ptr& firstError) {
    std::lock_guard<std::mutex> guard(mutex);
    if (!firstError) {
        firstError = error;
    }
}

struct PermissionProbe {
    std::atomic<int> concurrent{0};
    std::atomic<int> exclusive{0};

    void EnterConcurrent() {
        Require(exclusive.load(std::memory_order_acquire) == 0,
                "Concurrent overlapped Exclusive");
        concurrent.fetch_add(1, std::memory_order_acq_rel);
        Require(exclusive.load(std::memory_order_acquire) == 0,
                "Concurrent overlapped Exclusive after entry");
    }

    void ExitConcurrent() {
        concurrent.fetch_sub(1, std::memory_order_acq_rel);
    }

    void EnterExclusive() {
        int previous = exclusive.fetch_add(1, std::memory_order_acq_rel);
        Require(previous == 0, "Exclusive overlapped another Exclusive");
        Require(concurrent.load(std::memory_order_acquire) == 0,
                "Exclusive overlapped Concurrent");
    }

    void ExitExclusive() {
        exclusive.fetch_sub(1, std::memory_order_acq_rel);
    }
};

void TestCAPI() {
    Require(cel_run_c_api_smoke() == 0, "C API smoke test failed");
}

void TestBasicSnapshotsAndIDs() {
    ConcurrentExclusiveLock lock;
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "new lock was not Idle");
    Require(lock.ObservedContention() == 0,
            "new lock contention was not zero");

    int id1 = lock.AcquireConcurrent(2);
    int id2 = lock.AcquireConcurrent(2);
    Require(id1 == 1 && id2 == 2, "Concurrent IDs were not sequential");
    Require(lock.TryAcquireConcurrent(2) == 0,
            "maxConcurrent limit was not enforced");
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Concurrent,
            "Concurrent state was not observed");
    lock.ReleaseConcurrent();
    lock.ReleaseConcurrent();

    lock.AcquireExclusive();
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Exclusive,
            "Exclusive state was not observed");
    Require(lock.ObservedContention() >= 1,
            "Exclusive contention was not observed");
    lock.ReleaseExclusive();
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "lock did not return to Idle");

    Require(lock.ContextID() == 0, "initial ContextID was not zero");
    Require(lock.SwitchContextID(17), "ContextID switch did not succeed");
    Require(!lock.SwitchContextID(17), "same ContextID switched twice");
    Require(lock.ContextID() == 17, "ContextID readback failed");

    Require(lock.RaiseEpochID(3), "EpochID did not advance");
    Require(!lock.RaiseEpochID(3), "EpochID advanced to same value");
    Require(!lock.RaiseEpochID(2), "EpochID moved backward");
    Require(lock.EpochID() == 3, "EpochID readback failed");
}

void TestConcurrentAndExclusiveExclusion(int workers, int operations) {
    ConcurrentExclusiveLock lock;
    PermissionProbe probe;
    StartGate gate(workers);
    std::mutex errorMutex;
    std::exception_ptr firstError;
    std::vector<std::thread> threads;

    for (int worker = 0; worker < workers; ++worker) {
        threads.emplace_back([&, worker] {
            try {
                gate.ArriveAndWait();
                std::uint32_t random =
                    static_cast<std::uint32_t>(worker + 1) * UINT32_C(747796405)
                    + UINT32_C(2891336453);
                for (int i = 0; i < operations; ++i) {
                    random ^= random << 13;
                    random ^= random >> 17;
                    random ^= random << 5;
                    if ((random % 10u) == 0u) {
                        lock.AcquireExclusive();
                        probe.EnterExclusive();
                        std::this_thread::yield();
                        probe.ExitExclusive();
                        lock.ReleaseExclusive();
                    } else {
                        (void)lock.AcquireConcurrent();
                        probe.EnterConcurrent();
                        if ((random & 7u) == 0u) {
                            std::this_thread::yield();
                        }
                        probe.ExitConcurrent();
                        lock.ReleaseConcurrent();
                    }
                }
            } catch (...) {
                RecordFailure(
                    std::current_exception(), errorMutex, firstError);
            }
        });
    }

    for (auto& thread : threads) {
        thread.join();
    }
    if (firstError) {
        std::rethrow_exception(firstError);
    }
    Require(probe.concurrent.load() == 0 && probe.exclusive.load() == 0,
            "permission probe did not return to zero");
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "lock was not reusable after contention");
}

void TestPreemptiveExclusive() {
    ConcurrentExclusiveLock lock;
    (void)lock.AcquireConcurrent();

    std::atomic<bool> writerStarted{false};
    std::atomic<bool> writerEntered{false};
    std::thread writer([&] {
        writerStarted.store(true, std::memory_order_release);
        lock.AcquireExclusive();
        writerEntered.store(true, std::memory_order_release);
        lock.ReleaseExclusive();
    });

    while (!writerStarted.load(std::memory_order_acquire)) {
        std::this_thread::yield();
    }
    for (int i = 0; i < 10000 &&
                    lock.ObservedState() != ConcurrentExclusiveLockState::Exclusive;
         ++i) {
        std::this_thread::yield();
    }
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Exclusive,
            "preemptive Exclusive did not enter contention window");
    Require(lock.TryAcquireConcurrent() == 0,
            "new Concurrent entered after Exclusive preemption");
    Require(!writerEntered.load(std::memory_order_acquire),
            "writer entered before current Concurrent released");

    lock.ReleaseConcurrent();
    writer.join();
    Require(writerEntered.load(std::memory_order_acquire),
            "writer never entered after Concurrent released");
}

void TestUpgradeAndDowngrade() {
    ConcurrentExclusiveLock lock;
    PermissionProbe probe;

    (void)lock.AcquireConcurrent();
    probe.EnterConcurrent();
    probe.ExitConcurrent();
    lock.ConcurrentToExclusive();
    probe.EnterExclusive();
    probe.ExitExclusive();
    lock.ExclusiveToConcurrent();
    probe.EnterConcurrent();
    probe.ExitConcurrent();
    lock.ReleaseConcurrent();

    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "upgrade/downgrade cycle did not end Idle");
}

void TestMultipleUpgrades(int workers) {
    ConcurrentExclusiveLock lock;
    PermissionProbe probe;
    StartGate acquired(workers);
    std::atomic<int> completed{0};
    std::mutex errorMutex;
    std::exception_ptr firstError;
    std::vector<std::thread> threads;

    for (int worker = 0; worker < workers; ++worker) {
        threads.emplace_back([&] {
            try {
                (void)lock.AcquireConcurrent();
                acquired.ArriveAndWait();
                lock.ConcurrentToExclusive();
                probe.EnterExclusive();
                completed.fetch_add(1, std::memory_order_relaxed);
                std::this_thread::yield();
                probe.ExitExclusive();
                lock.ReleaseExclusive();
            } catch (...) {
                RecordFailure(
                    std::current_exception(), errorMutex, firstError);
            }
        });
    }

    for (auto& thread : threads) {
        thread.join();
    }
    if (firstError) {
        std::rethrow_exception(firstError);
    }
    Require(completed.load() == workers,
            "not every unconditional upgrade completed");
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "multiple upgrades did not end Idle");
}

void TestConditionalContextSingleWinner(int workers) {
    ConcurrentExclusiveLock lock;
    StartGate acquired(workers);
    PermissionProbe probe;
    std::atomic<int> winners{0};
    std::mutex errorMutex;
    std::exception_ptr firstError;
    std::vector<std::thread> threads;

    for (int worker = 0; worker < workers; ++worker) {
        threads.emplace_back([&] {
            try {
                (void)lock.AcquireConcurrent();
                acquired.ArriveAndWait();
                if (lock.TryConcurrentToExclusiveWithSwitchContextID(1001)) {
                    winners.fetch_add(1, std::memory_order_relaxed);
                    probe.EnterExclusive();
                    probe.ExitExclusive();
                    lock.ReleaseExclusive();
                }
            } catch (...) {
                RecordFailure(
                    std::current_exception(), errorMutex, firstError);
            }
        });
    }

    for (auto& thread : threads) {
        thread.join();
    }
    if (firstError) {
        std::rethrow_exception(firstError);
    }
    Require(winners.load() == 1,
            "same ContextID did not produce exactly one winner");
    Require(lock.ContextID() == 1001, "ContextID winner was not recorded");
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "failed conditional upgrades were not auto-released");
}

void TestConditionalEpoch(int workers) {
    ConcurrentExclusiveLock lock;
    StartGate acquired(workers);
    PermissionProbe probe;
    std::atomic<int> successes{0};
    std::mutex errorMutex;
    std::exception_ptr firstError;
    std::vector<std::thread> threads;

    for (int worker = 0; worker < workers; ++worker) {
        threads.emplace_back([&, worker] {
            try {
                (void)lock.AcquireConcurrent();
                acquired.ArriveAndWait();
                if (lock.TryConcurrentToExclusiveWithRaiseEpochID(worker + 1)) {
                    successes.fetch_add(1, std::memory_order_relaxed);
                    probe.EnterExclusive();
                    probe.ExitExclusive();
                    lock.ReleaseExclusive();
                }
            } catch (...) {
                RecordFailure(
                    std::current_exception(), errorMutex, firstError);
            }
        });
    }

    for (auto& thread : threads) {
        thread.join();
    }
    if (firstError) {
        std::rethrow_exception(firstError);
    }
    Require(successes.load() >= 1, "no EpochID upgrade succeeded");
    Require(lock.EpochID() >= 1 && lock.EpochID() <= workers,
            "EpochID ended outside expected range");
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "EpochID conditional upgrades did not end Idle");
}

void TestScopeLifecycle() {
    ConcurrentExclusiveLock lock;

    {
        ConcurrentExclusiveLockScope scope(lock);
        (void)scope.AcquireConcurrent();
    }
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "Scope did not release Concurrent");

    try {
        ConcurrentExclusiveLockScope scope(lock);
        scope.AcquireExclusive();
        throw std::runtime_error("expected");
    } catch (const std::runtime_error&) {
    }
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "Scope did not release Exclusive on exception");

    {
        ConcurrentExclusiveLockScope scope(lock);
        (void)scope.AcquireConcurrent();
        scope.ConcurrentToExclusive();
        scope.ExclusiveToConcurrent();
    }
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "Scope did not release converted permission");
}

void TestTimeouts() {
    ConcurrentExclusiveLock lock;
    (void)lock.AcquireConcurrent();

    std::promise<bool> resultPromise;
    std::thread waiter([&] {
        bool acquired = lock.TryAcquireExclusive(std::chrono::milliseconds(20));
        resultPromise.set_value(acquired);
        if (acquired) {
            lock.ReleaseExclusive();
        }
    });
    bool acquired = resultPromise.get_future().get();
    Require(!acquired, "Exclusive timeout unexpectedly acquired");
    lock.ReleaseConcurrent();
    waiter.join();

    lock.AcquireExclusive();
    std::promise<int> concurrentPromise;
    std::thread reader([&] {
        int id = lock.TryAcquireConcurrent(std::chrono::milliseconds(20));
        concurrentPromise.set_value(id);
        if (id != 0) {
            lock.ReleaseConcurrent();
        }
    });
    Require(concurrentPromise.get_future().get() == 0,
            "Concurrent timeout unexpectedly acquired");
    lock.ReleaseExclusive();
    reader.join();
}

void TestPipelineFixed() {
    ConcurrentExclusiveLock lock;
    ConcurrentExclusiveLockPipeline pipeline(lock);
    std::vector<int> trace;

    pipeline.DoPipeline(
        ConcurrentExclusiveLockSegment::Concurrent([&] {
            Require(lock.ObservedState() ==
                        ConcurrentExclusiveLockState::Concurrent,
                    "Pipeline Concurrent segment lacked permission");
            trace.push_back(1);
        }),
        ConcurrentExclusiveLockSegment::ConvergeExclusive([&] {
            Require(lock.ObservedState() ==
                        ConcurrentExclusiveLockState::Exclusive,
                    "Pipeline upgrade segment lacked Exclusive");
            trace.push_back(2);
        }),
        ConcurrentExclusiveLockSegment::ConvergeConcurrent([&] {
            Require(lock.ObservedState() ==
                        ConcurrentExclusiveLockState::Concurrent,
                    "Pipeline downgrade segment lacked Concurrent");
            trace.push_back(3);
        }),
        ConcurrentExclusiveLockSegment::None([&] {
            trace.push_back(4);
        }));

    Require(trace == std::vector<int>({1, 2, 3, 4}),
            "Pipeline fixed trace was incorrect");
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "Pipeline did not release final permission");

    (void)lock.AcquireConcurrent();
    bool testExclusiveExecuted = false;
    bool noneExecuted = false;
    pipeline.DoPipeline(
        ConcurrentExclusiveLockSegment::TestExclusive([&] {
            testExclusiveExecuted = true;
        }),
        ConcurrentExclusiveLockSegment::None([&] {
            noneExecuted = true;
        }));
    lock.ReleaseConcurrent();
    Require(!testExclusiveExecuted && noneExecuted,
            "Try failure did not skip segment and continue from None");

    bool threw = false;
    try {
        pipeline.DoPipeline(
            ConcurrentExclusiveLockSegment::Exclusive([] {
                throw std::runtime_error("pipeline exception");
            }),
            ConcurrentExclusiveLockSegment::None([] {}));
    } catch (const std::runtime_error&) {
        threw = true;
    }
    Require(threw, "Pipeline exception did not propagate");
    Require(lock.ObservedState() == ConcurrentExclusiveLockState::Idle,
            "Pipeline exception did not release permission");
}

void RunRandomValidPaths(const SemanticOptions& options) {
    const int lockCount = std::max(1, options.lockInstances);
    const int workersPerLock = std::max(2, options.workers);
    const int operations = std::max(1, options.operations);

    struct Shared {
        ConcurrentExclusiveLock lock;
        PermissionProbe probe;
        std::atomic<int> epochSource{0};
    };

    std::vector<std::unique_ptr<Shared>> locks;
    locks.reserve(static_cast<std::size_t>(lockCount));
    for (int i = 0; i < lockCount; ++i) {
        locks.push_back(std::make_unique<Shared>());
    }

    std::mutex errorMutex;
    std::exception_ptr firstError;
    std::vector<std::thread> threads;
    const int totalWorkers = lockCount * workersPerLock;
    StartGate gate(totalWorkers);

    for (int lockIndex = 0; lockIndex < lockCount; ++lockIndex) {
        for (int worker = 0; worker < workersPerLock; ++worker) {
            threads.emplace_back([&, lockIndex, worker] {
                try {
                    Shared& shared = *locks[static_cast<std::size_t>(lockIndex)];
                    std::uint64_t seed = options.seed != 0
                        ? options.seed
                        : UINT64_C(0x9E3779B97F4A7C15);
                    std::mt19937_64 random(
                        seed ^ (static_cast<std::uint64_t>(lockIndex + 1) << 32)
                             ^ static_cast<std::uint64_t>(worker + 1));
                    gate.ArriveAndWait();

                    for (int operation = 0; operation < operations; ++operation) {
                        switch (random() % 6u) {
                            case 0: {
                                ConcurrentExclusiveLockScope scope(shared.lock);
                                (void)scope.AcquireConcurrent();
                                shared.probe.EnterConcurrent();
                                shared.probe.ExitConcurrent();
                                break;
                            }
                            case 1: {
                                ConcurrentExclusiveLockScope scope(shared.lock);
                                scope.AcquireExclusive();
                                shared.probe.EnterExclusive();
                                shared.probe.ExitExclusive();
                                break;
                            }
                            case 2: {
                                ConcurrentExclusiveLockScope scope(shared.lock);
                                (void)scope.AcquireConcurrent();
                                scope.ConcurrentToExclusive();
                                shared.probe.EnterExclusive();
                                shared.probe.ExitExclusive();
                                break;
                            }
                            case 3: {
                                ConcurrentExclusiveLockScope scope(shared.lock);
                                scope.AcquireExclusive();
                                shared.probe.EnterExclusive();
                                shared.probe.ExitExclusive();
                                scope.ExclusiveToConcurrent();
                                shared.probe.EnterConcurrent();
                                shared.probe.ExitConcurrent();
                                break;
                            }
                            case 4: {
                                ConcurrentExclusiveLockScope scope(shared.lock);
                                (void)scope.AcquireConcurrent();
                                int target = shared.epochSource.fetch_add(
                                    1, std::memory_order_relaxed) + 1;
                                if (scope.TryConcurrentToExclusiveWithRaiseEpochID(
                                        target)) {
                                    shared.probe.EnterExclusive();
                                    shared.probe.ExitExclusive();
                                }
                                break;
                            }
                            default: {
                                ConcurrentExclusiveLockPipeline pipeline(shared.lock);
                                pipeline.DoPipeline(
                                    ConcurrentExclusiveLockSegment::Concurrent([&] {
                                        shared.probe.EnterConcurrent();
                                        shared.probe.ExitConcurrent();
                                    }),
                                    ConcurrentExclusiveLockSegment::ConvergeExclusive([&] {
                                        shared.probe.EnterExclusive();
                                        shared.probe.ExitExclusive();
                                    }),
                                    ConcurrentExclusiveLockSegment::ConvergeConcurrent([&] {
                                        shared.probe.EnterConcurrent();
                                        shared.probe.ExitConcurrent();
                                    }));
                                break;
                            }
                        }
                    }
                } catch (...) {
                    RecordFailure(
                        std::current_exception(), errorMutex, firstError);
                }
            });
        }
    }

    for (auto& thread : threads) {
        thread.join();
    }
    if (firstError) {
        std::rethrow_exception(firstError);
    }
    for (const auto& shared : locks) {
        Require(shared->lock.ObservedState() ==
                    ConcurrentExclusiveLockState::Idle,
                "random valid paths left a lock non-Idle");
    }
}

} // namespace

void RunPipelineSemantics() {
    std::cout << "[semantic] pipeline fixed contracts\n";
    TestPipelineFixed();
    std::cout << "[semantic] pipeline fixed contracts: PASS\n";
}

void RunFullSemantics(const SemanticOptions& options) {
    std::cout << "ConcurrentExclusiveLock full semantic regression\n";
    std::cout << "locks=" << options.lockInstances
              << ", workers/lock=" << options.workers
              << ", operations/worker=" << options.operations << "\n";

    TestCAPI();
    std::cout << "  C API smoke: PASS\n";
    TestBasicSnapshotsAndIDs();
    std::cout << "  snapshots, IDs and business IDs: PASS\n";
    TestConcurrentAndExclusiveExclusion(
        std::max(2, options.workers),
        std::max(32, options.operations));
    std::cout << "  Concurrent/Exclusive exclusion: PASS\n";
    TestPreemptiveExclusive();
    std::cout << "  preemptive Exclusive: PASS\n";
    TestUpgradeAndDowngrade();
    std::cout << "  upgrade/downgrade: PASS\n";
    TestMultipleUpgrades(std::max(2, options.workers));
    std::cout << "  multiple upgrade serialization: PASS\n";
    TestConditionalContextSingleWinner(std::max(2, options.workers));
    std::cout << "  ContextID single-winner upgrade: PASS\n";
    TestConditionalEpoch(std::max(2, options.workers));
    std::cout << "  EpochID conditional upgrade: PASS\n";
    TestScopeLifecycle();
    std::cout << "  Scope lifecycle and exception release: PASS\n";
    TestTimeouts();
    std::cout << "  timeout paths: PASS\n";
    TestPipelineFixed();
    std::cout << "  Pipeline transitions and exception release: PASS\n";
    RunRandomValidPaths(options);
    std::cout << "  randomized valid semantic paths: PASS\n";
    std::cout << "FULL SEMANTICS: PASS\n";
}

void RunPipelineStress(
    std::chrono::milliseconds duration,
    const SemanticOptions& options) {
    const int lockCount = std::max(1, options.lockInstances);
    const int workersPerLock = std::max(2, options.workers);
    const int maxRounds = std::max(1, options.operations);

    struct Shared {
        ConcurrentExclusiveLock lock;
        PermissionProbe probe;
        std::atomic<int> epoch{0};
    };
    std::vector<std::unique_ptr<Shared>> locks;
    for (int i = 0; i < lockCount; ++i) {
        locks.push_back(std::make_unique<Shared>());
    }

    std::atomic<bool> stop{false};
    std::atomic<std::uint64_t> batches{0};
    std::atomic<std::uint64_t> pipelines{0};
    std::mutex errorMutex;
    std::exception_ptr firstError;
    std::vector<std::thread> threads;

    std::cout << "Pipeline randomized stress\n"
              << "duration=" << duration.count() << "ms"
              << ", locks=" << lockCount
              << ", workers/lock=" << workersPerLock
              << ", max-rounds/batch=" << maxRounds << "\n";

    for (int lockIndex = 0; lockIndex < lockCount; ++lockIndex) {
        for (int worker = 0; worker < workersPerLock; ++worker) {
            threads.emplace_back([&, lockIndex, worker] {
                try {
                    Shared& shared = *locks[static_cast<std::size_t>(lockIndex)];
                    std::uint64_t baseSeed = options.seed != 0
                        ? options.seed
                        : UINT64_C(0xD1B54A32D192ED03);
                    std::mt19937_64 random(
                        baseSeed ^
                        (static_cast<std::uint64_t>(lockIndex + 1) << 32) ^
                        static_cast<std::uint64_t>(worker + 1));
                    ConcurrentExclusiveLockPipeline pipeline(shared.lock);

                    while (!stop.load(std::memory_order_acquire)) {
                        int rounds = 1 + static_cast<int>(
                            random() % static_cast<std::uint64_t>(maxRounds));
                        for (int round = 0; round < rounds; ++round) {
                            switch (random() % 5u) {
                                case 0:
                                    pipeline.DoPipeline(
                                        ConcurrentExclusiveLockSegment::Concurrent([&] {
                                            shared.probe.EnterConcurrent();
                                            shared.probe.ExitConcurrent();
                                        }),
                                        ConcurrentExclusiveLockSegment::ConvergeExclusive([&] {
                                            shared.probe.EnterExclusive();
                                            shared.probe.ExitExclusive();
                                        }),
                                        ConcurrentExclusiveLockSegment::ConvergeConcurrent([&] {
                                            shared.probe.EnterConcurrent();
                                            shared.probe.ExitConcurrent();
                                        }));
                                    break;
                                case 1:
                                    pipeline.DoPipeline(
                                        ConcurrentExclusiveLockSegment::Exclusive([&] {
                                            shared.probe.EnterExclusive();
                                            shared.probe.ExitExclusive();
                                        }),
                                        ConcurrentExclusiveLockSegment::None([] {}),
                                        ConcurrentExclusiveLockSegment::Concurrent([&] {
                                            shared.probe.EnterConcurrent();
                                            shared.probe.ExitConcurrent();
                                        }));
                                    break;
                                case 2: {
                                    int epoch = shared.epoch.fetch_add(
                                        1, std::memory_order_relaxed) + 1;
                                    pipeline.DoPipeline(
                                        ConcurrentExclusiveLockSegment::Concurrent([&] {
                                            shared.probe.EnterConcurrent();
                                            shared.probe.ExitConcurrent();
                                        }),
                                        ConcurrentExclusiveLockSegment::TryApplyIDConvergeExclusive(
                                            [&] {
                                                shared.probe.EnterExclusive();
                                                shared.probe.ExitExclusive();
                                            },
                                            epoch,
                                            ConcurrentExclusiveLockSegment::IDType::EpochID),
                                        ConcurrentExclusiveLockSegment::ConvergeConcurrent([&] {
                                            shared.probe.EnterConcurrent();
                                            shared.probe.ExitConcurrent();
                                        }));
                                    break;
                                }
                                case 3:
                                    pipeline.DoPipeline(
                                        ConcurrentExclusiveLockSegment::TryConcurrent([&] {
                                            shared.probe.EnterConcurrent();
                                            shared.probe.ExitConcurrent();
                                        }),
                                        ConcurrentExclusiveLockSegment::TryExclusive([&] {
                                            shared.probe.EnterExclusive();
                                            shared.probe.ExitExclusive();
                                        }),
                                        ConcurrentExclusiveLockSegment::None([] {}));
                                    break;
                                default:
                                    try {
                                        pipeline.DoPipeline(
                                            ConcurrentExclusiveLockSegment::Exclusive([&] {
                                                shared.probe.EnterExclusive();
                                                shared.probe.ExitExclusive();
                                                throw std::runtime_error("injected");
                                            }));
                                    } catch (const std::runtime_error&) {
                                    }
                                    break;
                            }
                            pipelines.fetch_add(1, std::memory_order_relaxed);
                        }
                        batches.fetch_add(1, std::memory_order_relaxed);
                    }
                } catch (...) {
                    RecordFailure(
                        std::current_exception(), errorMutex, firstError);
                    stop.store(true, std::memory_order_release);
                }
            });
        }
    }

    std::this_thread::sleep_for(duration);
    stop.store(true, std::memory_order_release);
    for (auto& thread : threads) {
        thread.join();
    }
    if (firstError) {
        std::rethrow_exception(firstError);
    }
    for (const auto& shared : locks) {
        Require(shared->lock.ObservedState() ==
                    ConcurrentExclusiveLockState::Idle,
                "Pipeline stress left a lock non-Idle");
    }

    std::cout << "batches=" << batches.load()
              << ", pipelines=" << pipelines.load() << "\n"
              << "PIPELINE STRESS: PASS\n";
}

void RunContentionStress(
    std::chrono::milliseconds duration,
    int workers) {
    workers = std::max(2, workers);
    ConcurrentExclusiveLock lock;
    std::atomic<bool> stop{false};
    std::vector<std::uint64_t> acquisitions(static_cast<std::size_t>(workers), 0);
    std::vector<std::thread> threads;

    for (int worker = 0; worker < workers; ++worker) {
        threads.emplace_back([&, worker] {
            while (!stop.load(std::memory_order_acquire)) {
                lock.AcquireExclusive();
                ++acquisitions[static_cast<std::size_t>(worker)];
                lock.ReleaseExclusive();
            }
        });
    }
    std::this_thread::sleep_for(duration);
    stop.store(true, std::memory_order_release);
    for (auto& thread : threads) {
        thread.join();
    }

    auto [minimum, maximum] = std::minmax_element(
        acquisitions.begin(), acquisitions.end());
    std::uint64_t total = 0;
    for (std::uint64_t value : acquisitions) {
        total += value;
    }
    Require(*minimum > 0, "at least one Exclusive waiter starved completely");
    std::cout << "Exclusive contention stress: workers=" << workers
              << ", total=" << total
              << ", min/thread=" << *minimum
              << ", max/thread=" << *maximum << "\n"
              << "CONTENTION STRESS: PASS\n";
}

} // namespace celtest
