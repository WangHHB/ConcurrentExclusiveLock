#ifndef CEL_TEST_WORKLOADS_HPP
#define CEL_TEST_WORKLOADS_HPP

#include <atomic>
#include <algorithm>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace celtest {

inline std::uint32_t NextRandom(std::uint32_t value) noexcept {
    value ^= value << 13;
    value ^= value >> 17;
    value ^= value << 5;
    return value;
}

inline std::uint64_t Mix(std::uint64_t input) noexcept {
    input ^= input >> 33;
    input *= UINT64_C(0xFF51AFD7ED558CCD);
    input ^= input >> 33;
    input *= UINT64_C(0xC4CEB9FE1A85EC53);
    input ^= input >> 33;
    return input;
}

class IWork {
public:
    virtual ~IWork() = default;
    virtual std::uint64_t TickRead(std::uint32_t& threadRandom) = 0;
    virtual std::uint64_t TickWrite() = 0;
    virtual std::uint64_t StateHash() const noexcept = 0;
};

class MemoryWork final : public IWork {
public:
    MemoryWork(int readSteps, int writeSteps, int workingSetMb)
        : readSteps_(readSteps), writeSteps_(writeSteps) {
        std::size_t bytes = static_cast<std::size_t>(workingSetMb) * 1024u * 1024u;
        std::size_t count = std::max<std::size_t>(1024u, bytes / sizeof(std::uint64_t));
        buffer_.resize(count);
        std::uint64_t current = UINT64_C(0x6A09E667F3BCC909);
        for (std::size_t i = 0; i < count; ++i) {
            current = Mix(current + static_cast<std::uint64_t>(i));
            buffer_[i] = current;
        }
    }

    std::uint64_t TickRead(std::uint32_t& random) override {
        std::uint64_t result = state_.load(std::memory_order_acquire);
        for (int i = 0; i < readSteps_; ++i) {
            random = NextRandom(random);
            std::size_t index = static_cast<std::size_t>(random) % buffer_.size();
            result = Mix(result ^ (buffer_[index] + static_cast<std::uint64_t>(i)));
        }
        return result;
    }

    std::uint64_t TickWrite() override {
        std::uint64_t result = state_.load(std::memory_order_relaxed) + 1u;
        std::uint32_t random = writeRandom_;
        for (int i = 0; i < writeSteps_; ++i) {
            random = NextRandom(random);
            std::size_t index = static_cast<std::size_t>(random) % buffer_.size();
            std::uint64_t next = Mix(
                buffer_[index] ^ result ^ static_cast<std::uint64_t>(i));
            buffer_[index] = next;
            result = next;
        }
        writeRandom_ = random;
        state_.store(result, std::memory_order_release);
        return result;
    }

    std::uint64_t StateHash() const noexcept override {
        return state_.load(std::memory_order_acquire);
    }

private:
    int readSteps_;
    int writeSteps_;
    std::vector<std::uint64_t> buffer_;
    std::uint32_t writeRandom_ = UINT32_C(0xC8013EA4);
    std::atomic<std::uint64_t> state_{0};
};

class CpuWork final : public IWork {
public:
    CpuWork(int readSteps, int writeSteps)
        : readSteps_(readSteps), writeSteps_(writeSteps) {
        state_.store(UINT64_C(0x243F6A8885A308D3), std::memory_order_relaxed);
    }

    std::uint64_t TickRead(std::uint32_t&) override {
        return Run(state_.load(std::memory_order_acquire), readSteps_);
    }

    std::uint64_t TickWrite() override {
        std::uint64_t value = Run(
            state_.load(std::memory_order_relaxed) + 1u,
            writeSteps_);
        state_.store(value, std::memory_order_release);
        return value;
    }

    std::uint64_t StateHash() const noexcept override {
        return state_.load(std::memory_order_acquire);
    }

private:
    static std::uint64_t Run(std::uint64_t input, int steps) noexcept {
        std::uint64_t result = input;
        for (int i = 0; i < steps; ++i) {
            result ^= result << 7;
            result += UINT64_C(0x9E3779B97F4A7C1);
            result = (result << 11) | (result >> 53);
            result ^= result >> 17;
        }
        return result;
    }

    int readSteps_;
    int writeSteps_;
    std::atomic<std::uint64_t> state_{};
};

inline std::unique_ptr<IWork> CreateWork(
    const std::string& kind,
    int readSteps,
    int writeSteps,
    int memoryMb) {
    if (kind == "cpu") {
        return std::make_unique<CpuWork>(readSteps, writeSteps);
    }
    return std::make_unique<MemoryWork>(readSteps, writeSteps, memoryMb);
}

} // namespace celtest

#endif
