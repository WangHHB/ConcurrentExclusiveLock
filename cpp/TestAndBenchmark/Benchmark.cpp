#include "Benchmark.hpp"

#include "ConcurrentExclusiveLock.hpp"
#include "Workloads.hpp"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <iomanip>
#include <iostream>
#include <memory>
#include <mutex>
#include <shared_mutex>
#include <sstream>
#include <thread>
#include <vector>

#if defined(_WIN32)
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#else
#  include <ctime>
#endif

namespace celtest {
namespace {

using Clock = std::chrono::steady_clock;
using intomic::ConcurrentExclusiveLock;

double CurrentProcessCpuSeconds() noexcept {
#if defined(_WIN32)
    FILETIME creationTime{};
    FILETIME exitTime{};
    FILETIME kernelTime{};
    FILETIME userTime{};
    if (!GetProcessTimes(
            GetCurrentProcess(),
            &creationTime,
            &exitTime,
            &kernelTime,
            &userTime)) {
        return 0.0;
    }

    ULARGE_INTEGER kernel{};
    kernel.LowPart = kernelTime.dwLowDateTime;
    kernel.HighPart = kernelTime.dwHighDateTime;

    ULARGE_INTEGER user{};
    user.LowPart = userTime.dwLowDateTime;
    user.HighPart = userTime.dwHighDateTime;

    constexpr double FileTimeTicksPerSecond = 10000000.0;
    return static_cast<double>(kernel.QuadPart + user.QuadPart)
        / FileTimeTicksPerSecond;
#else
    const std::clock_t value = std::clock();
    if (value == static_cast<std::clock_t>(-1)) {
        return 0.0;
    }
    return static_cast<double>(value)
        / static_cast<double>(CLOCKS_PER_SEC);
#endif
}

unsigned LogicalProcessorCount() noexcept {
#if defined(_WIN32)
    const DWORD activeProcessors =
        GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
    if (activeProcessors != 0) {
        return static_cast<unsigned>(activeProcessors);
    }
#endif
    return std::max(1u, std::thread::hardware_concurrency());
}

class StartGate {
public:
    explicit StartGate(int participants) : participants_(participants) {}
    void Wait() {
        std::unique_lock<std::mutex> lock(mutex_);
        if (++arrived_ == participants_) {
            open_ = true;
            condition_.notify_all();
        } else {
            condition_.wait(lock, [this] { return open_; });
        }
    }
private:
    int participants_;
    int arrived_ = 0;
    bool open_ = false;
    std::mutex mutex_;
    std::condition_variable condition_;
};

enum class StrategyKind {
    Mutex,
    SharedMutex,
    CEL,
    CELExclusiveOnly
};

const char* StrategyName(StrategyKind kind) {
    switch (kind) {
        case StrategyKind::Mutex: return "std::mutex";
        case StrategyKind::SharedMutex: return "std::shared_mutex";
        case StrategyKind::CEL: return "CEL";
        case StrategyKind::CELExclusiveOnly: return "CEL(ExclusiveOnly)";
    }
    return "unknown";
}

struct Scenario {
    const char* name;
    int writesPerThousand;
};

struct Instance {
    explicit Instance(const BenchmarkOptions& options)
        : work(CreateWork(
              options.workload,
              options.readSteps,
              options.writeSteps,
              options.memoryMb)) {}

    std::unique_ptr<IWork> work;
    std::mutex mutex;
    std::shared_mutex sharedMutex;
    ConcurrentExclusiveLock cel;
};

struct Result {
    double elapsedSeconds = 0;
    double cpuPercent = 0;
    double worksPerSecond = 0;
    double worksPerLock = 0;
    double workPerCpuPercent = 0;
    std::uint64_t reads = 0;
    std::uint64_t writes = 0;
    double averageWriteNs = 0;
    std::uint64_t state = 0;
};

std::atomic<std::uint64_t> globalSink{0};

bool IsWrite(std::uint32_t random, int writesPerThousand) {
    return static_cast<int>(random % 1000u) < writesPerThousand;
}

Result RunOne(
    StrategyKind strategy,
    const Scenario& scenario,
    const BenchmarkOptions& options,
    int threadsPerLock) {
    const int lockInstances = options.lockInstances;
    const int totalThreads = lockInstances * threadsPerLock;
    std::vector<std::unique_ptr<Instance>> instances;
    instances.reserve(static_cast<std::size_t>(lockInstances));
    for (int i = 0; i < lockInstances; ++i) {
        instances.push_back(std::make_unique<Instance>(options));
    }

    StartGate gate(totalThreads + 1);
    std::atomic<std::uint64_t> reads{0};
    std::atomic<std::uint64_t> writes{0};
    std::atomic<std::uint64_t> writeNanoseconds{0};
    std::vector<std::thread> threads;
    threads.reserve(static_cast<std::size_t>(totalThreads));

    for (int lockIndex = 0; lockIndex < lockInstances; ++lockIndex) {
        for (int worker = 0; worker < threadsPerLock; ++worker) {
            threads.emplace_back([&, lockIndex, worker] {
                Instance& instance = *instances[static_cast<std::size_t>(lockIndex)];
                std::uint32_t random =
                    static_cast<std::uint32_t>(worker + 1) * UINT32_C(747796405)
                    + static_cast<std::uint32_t>(lockIndex + 1) * UINT32_C(2891336453);
                std::uint64_t localSink = 0;
                std::uint64_t localReads = 0;
                std::uint64_t localWrites = 0;
                std::uint64_t localWriteNs = 0;
                gate.Wait();

                for (int operation = 0;
                     operation < options.operationsPerThread;
                     ++operation) {
                    random = NextRandom(random);
                    const bool write = IsWrite(random, scenario.writesPerThousand);
                    if (write) {
                        auto beginWrite = Clock::now();
                        switch (strategy) {
                            case StrategyKind::Mutex: {
                                std::lock_guard<std::mutex> guard(instance.mutex);
                                localSink ^= instance.work->TickWrite();
                                break;
                            }
                            case StrategyKind::SharedMutex: {
                                std::unique_lock<std::shared_mutex> guard(
                                    instance.sharedMutex);
                                localSink ^= instance.work->TickWrite();
                                break;
                            }
                            case StrategyKind::CEL:
                            case StrategyKind::CELExclusiveOnly:
                                instance.cel.AcquireExclusive();
                                localSink ^= instance.work->TickWrite();
                                instance.cel.ReleaseExclusive();
                                break;
                        }
                        auto endWrite = Clock::now();
                        localWriteNs += static_cast<std::uint64_t>(
                            std::chrono::duration_cast<std::chrono::nanoseconds>(
                                endWrite - beginWrite).count());
                        ++localWrites;
                    } else {
                        switch (strategy) {
                            case StrategyKind::Mutex: {
                                std::lock_guard<std::mutex> guard(instance.mutex);
                                localSink ^= instance.work->TickRead(random);
                                break;
                            }
                            case StrategyKind::SharedMutex: {
                                std::shared_lock<std::shared_mutex> guard(
                                    instance.sharedMutex);
                                localSink ^= instance.work->TickRead(random);
                                break;
                            }
                            case StrategyKind::CEL:
                                (void)instance.cel.AcquireConcurrent();
                                localSink ^= instance.work->TickRead(random);
                                instance.cel.ReleaseConcurrent();
                                break;
                            case StrategyKind::CELExclusiveOnly:
                                instance.cel.AcquireExclusive();
                                localSink ^= instance.work->TickRead(random);
                                instance.cel.ReleaseExclusive();
                                break;
                        }
                        ++localReads;
                    }
                }

                reads.fetch_add(localReads, std::memory_order_relaxed);
                writes.fetch_add(localWrites, std::memory_order_relaxed);
                writeNanoseconds.fetch_add(localWriteNs, std::memory_order_relaxed);
                globalSink.fetch_xor(localSink, std::memory_order_relaxed);
            });
        }
    }

    gate.Wait();
    const double cpuStart = CurrentProcessCpuSeconds();
    auto start = Clock::now();
    for (auto& thread : threads) {
        thread.join();
    }
    auto finish = Clock::now();
    const double cpuFinish = CurrentProcessCpuSeconds();

    Result result;
    result.elapsedSeconds = std::chrono::duration<double>(finish - start).count();
    const double processCpuSeconds = std::max(0.0, cpuFinish - cpuStart);
    const unsigned hardware = LogicalProcessorCount();
    result.cpuPercent = result.elapsedSeconds > 0
        ? processCpuSeconds / result.elapsedSeconds
            / static_cast<double>(hardware) * 100.0
        : 0.0;
    result.reads = reads.load(std::memory_order_relaxed);
    result.writes = writes.load(std::memory_order_relaxed);
    const std::uint64_t total = result.reads + result.writes;
    result.worksPerSecond = result.elapsedSeconds > 0
        ? static_cast<double>(total) / result.elapsedSeconds
        : 0.0;
    result.worksPerLock = result.worksPerSecond
        / static_cast<double>(lockInstances);
    result.workPerCpuPercent = result.cpuPercent > 0
        ? result.worksPerSecond / result.cpuPercent
        : 0.0;
    result.averageWriteNs = result.writes > 0
        ? static_cast<double>(writeNanoseconds.load(std::memory_order_relaxed))
            / static_cast<double>(result.writes)
        : 0.0;

    std::uint64_t state = 0;
    for (const auto& instance : instances) {
        state ^= Mix(instance->work->StateHash() + state);
    }
    result.state = state;
    return result;
}

std::string Hex(std::uint64_t value) {
    std::ostringstream stream;
    stream << std::hex << std::uppercase << value;
    return stream.str();
}

} // namespace

void RunBenchmark(const BenchmarkOptions& options) {
    int threadsPerLock = options.threadsPerLock;
    if (threadsPerLock <= 0) {
        const unsigned hardware = LogicalProcessorCount();
        threadsPerLock = std::max(
            1,
            static_cast<int>(hardware) / std::max(1, options.lockInstances));
    }

    const int totalThreads = options.lockInstances * threadsPerLock;
    const std::uint64_t totalOperations =
        static_cast<std::uint64_t>(options.lockInstances)
        * static_cast<std::uint64_t>(threadsPerLock)
        * static_cast<std::uint64_t>(options.operationsPerThread);

    std::cout << "ConcurrentExclusiveLock C/C++ benchmark\n"
              << "CPU=" << LogicalProcessorCount() << "\n"
              << "lock-instances=" << options.lockInstances
              << ", threads/lock=" << threadsPerLock
              << ", total-threads=" << totalThreads
              << ", works/thread=" << options.operationsPerThread
              << ", read-steps=" << options.readSteps
              << ", write-steps=" << options.writeSteps << "\n"
              << "workload=" << options.workload;
    if (options.workload == "memory") {
        std::cout << " (" << options.memoryMb << " MiB shared per lock)";
    }
    std::cout << "\n"
              << "total lock operations per strategy/scenario="
              << totalOperations << "\n\n";

    const Scenario scenarios[] = {
        {"100/0", 0},
        {"99.5/0.5", 5},
        {"90/10", 100},
        {"50/50", 500},
        {"30/70", 700},
        {"0/100", 1000}
    };
    const StrategyKind strategies[] = {
        StrategyKind::Mutex,
        StrategyKind::SharedMutex,
        StrategyKind::CEL,
        StrategyKind::CELExclusiveOnly
    };

    for (const Scenario& scenario : scenarios) {
        std::cout << "Scenario: read/write " << scenario.name << "\n";
        std::cout << "  " << std::left << std::setw(26) << "lock type"
                  << std::right << std::setw(11) << "elapsed"
                  << std::setw(10) << "cpu%"
                  << std::setw(15) << "works/s"
                  << std::setw(15) << "works/s/lock"
                  << std::setw(14) << "work/cpu%"
                  << std::setw(14) << "reads"
                  << std::setw(14) << "writes"
                  << std::setw(16) << "avg write ns"
                  << "  state\n";

        std::uint64_t expectedReads = 0;
        std::uint64_t expectedWrites = 0;
        std::uint64_t expectedState = 0;
        bool first = true;

        for (StrategyKind strategy : strategies) {
            Result result = RunOne(
                strategy, scenario, options, threadsPerLock);
            if (first) {
                expectedReads = result.reads;
                expectedWrites = result.writes;
                expectedState = result.state;
                first = false;
            } else if (result.reads != expectedReads ||
                       result.writes != expectedWrites ||
                       result.state != expectedState) {
                throw std::runtime_error(
                    "benchmark correctness mismatch between strategies");
            }

            std::ostringstream elapsed;
            elapsed << std::fixed << std::setprecision(3)
                    << result.elapsedSeconds << "s";
            std::cout << "  " << std::left << std::setw(26)
                      << StrategyName(strategy)
                      << std::right << std::setw(11) << elapsed.str()
                      << std::setw(9) << std::fixed << std::setprecision(1)
                      << result.cpuPercent << "%"
                      << std::setw(15) << std::fixed << std::setprecision(0)
                      << result.worksPerSecond
                      << std::setw(15) << result.worksPerLock
                      << std::setw(14) << result.workPerCpuPercent
                      << std::setw(14) << result.reads
                      << std::setw(14) << result.writes
                      << std::setw(16) << std::fixed << std::setprecision(1)
                      << result.averageWriteNs
                      << "  " << Hex(result.state) << "\n";
        }
        std::cout << "\n";
    }

    std::cout << "sink=" << globalSink.load(std::memory_order_relaxed) << "\n";
}

} // namespace celtest
