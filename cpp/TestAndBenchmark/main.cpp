#include "Benchmark.hpp"
#include "SemanticTests.hpp"

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <exception>
#include <iostream>
#include <stdexcept>
#include <string>
#include <thread>

namespace {

struct Options {
    bool help = false;
    bool fullSemantics = false;
    bool pipelineSemantics = false;
    bool pipelineStress = false;
    bool contentionStress = false;
    std::chrono::milliseconds duration{0};
    celtest::SemanticOptions semantic;
    celtest::BenchmarkOptions benchmark;
};

int ParseInt(const std::string& value, const char* option) {
    try {
        std::size_t position = 0;
        int result = std::stoi(value, &position);
        if (position != value.size()) throw std::invalid_argument("tail");
        return result;
    } catch (...) {
        throw std::invalid_argument(std::string("invalid value for ") + option);
    }
}

std::uint64_t ParseU64(const std::string& value, const char* option) {
    try {
        std::size_t position = 0;
        std::uint64_t result = std::stoull(value, &position, 0);
        if (position != value.size()) throw std::invalid_argument("tail");
        return result;
    } catch (...) {
        throw std::invalid_argument(std::string("invalid value for ") + option);
    }
}

std::chrono::milliseconds ParseDuration(const std::string& value) {
    if (value.empty()) throw std::invalid_argument("duration is empty");
    auto parse = [&](std::size_t trim, std::int64_t multiplier) {
        std::string number = value.substr(0, value.size() - trim);
        return std::chrono::milliseconds(
            static_cast<std::int64_t>(std::stoll(number)) * multiplier);
    };
    if (value.size() > 2 && value.substr(value.size() - 2) == "ms") {
        return parse(2, 1);
    }
    if (value.back() == 's') return parse(1, 1000);
    if (value.back() == 'm') return parse(1, 60 * 1000);
    if (value.back() == 'h') return parse(1, 60 * 60 * 1000);
    if (value.back() == 'd') return parse(1, 24 * 60 * 60 * 1000);
    return std::chrono::milliseconds(std::stoll(value));
}

Options ParseOptions(int argc, char** argv) {
    Options options;
    options.benchmark.threadsPerLock = 0;

    auto next = [&](int& index, const char* name) -> std::string {
        if (++index >= argc) {
            throw std::invalid_argument(std::string("missing value for ") + name);
        }
        return argv[index];
    };

    for (int i = 1; i < argc; ++i) {
        std::string argument = argv[i];
        if (argument == "--help" || argument == "-h") {
            options.help = true;
        } else if (argument == "--full-semantics" ||
                   argument == "--advanced-correctness") {
            options.fullSemantics = true;
        } else if (argument == "--pipeline-semantics") {
            options.pipelineSemantics = true;
        } else if (argument == "--pipeline-stress") {
            options.pipelineStress = true;
            options.duration = ParseDuration(next(i, "--pipeline-stress"));
        } else if (argument == "--contention-stress") {
            options.contentionStress = true;
            options.duration = ParseDuration(next(i, "--contention-stress"));
        } else if (argument == "--lock-instances") {
            int value = ParseInt(next(i, "--lock-instances"), argument.c_str());
            options.semantic.lockInstances = value;
            options.benchmark.lockInstances = value;
        } else if (argument == "--semantic-workers") {
            options.semantic.workers = ParseInt(
                next(i, "--semantic-workers"), argument.c_str());
        } else if (argument == "--semantic-operations") {
            options.semantic.operations = ParseInt(
                next(i, "--semantic-operations"), argument.c_str());
        } else if (argument == "--semantic-seed") {
            options.semantic.seed = ParseU64(
                next(i, "--semantic-seed"), argument.c_str());
        } else if (argument == "--threads") {
            options.benchmark.threadsPerLock = ParseInt(
                next(i, "--threads"), argument.c_str());
        } else if (argument == "--operations") {
            options.benchmark.operationsPerThread = ParseInt(
                next(i, "--operations"), argument.c_str());
        } else if (argument == "--work" ) {
            int work = ParseInt(next(i, "--work"), argument.c_str());
            options.benchmark.readSteps = work;
            options.benchmark.writeSteps = work;
        } else if (argument == "--read-work") {
            options.benchmark.readSteps = ParseInt(
                next(i, "--read-work"), argument.c_str());
        } else if (argument == "--write-work") {
            options.benchmark.writeSteps = ParseInt(
                next(i, "--write-work"), argument.c_str());
        } else if (argument == "--memory-mb") {
            options.benchmark.memoryMb = ParseInt(
                next(i, "--memory-mb"), argument.c_str());
        } else if (argument == "--workload") {
            options.benchmark.workload = next(i, "--workload");
        } else {
            throw std::invalid_argument("unknown option: " + argument);
        }
    }
    return options;
}

void PrintHelp() {
    std::cout <<
R"(ConcurrentExclusiveLock C/C++ TestAndBenchmark

Semantic and stress modes:
  --full-semantics
  --advanced-correctness          Alias of --full-semantics
  --pipeline-semantics
  --pipeline-stress <duration>    Examples: 30s, 10m, 24h, 1d
  --contention-stress <duration>

Semantic parameters:
  --lock-instances <N>            Default: 8
  --semantic-workers <N>          Dedicated workers per lock. Default: 4
  --semantic-operations <N>       Legal-path rounds or max batch rounds. Default: 256
  --semantic-seed <N>             Reproducible random seed

Benchmark mode is used when no semantic/stress mode is selected:
  --lock-instances <N>            Default: 1
  --threads <N>                   Threads per lock. Default: CPU / lock count
  --operations <N>                Completed Works per thread. Default: 10000
  --workload <memory|cpu>         Default: memory
  --work <N>                      Sets both read and write Work steps
  --read-work <N>                 Default: 32
  --write-work <N>                Default: 32
  --memory-mb <N>                 Shared memory per lock. Default: 64
)";
}

} // namespace

int main(int argc, char** argv) {
    try {
        Options options = ParseOptions(argc, argv);
        if (options.help) {
            PrintHelp();
            return 0;
        }
        if (options.fullSemantics) {
            celtest::RunFullSemantics(options.semantic);
        } else if (options.pipelineSemantics) {
            celtest::RunPipelineSemantics();
        } else if (options.pipelineStress) {
            celtest::RunPipelineStress(options.duration, options.semantic);
        } else if (options.contentionStress) {
            celtest::RunContentionStress(
                options.duration,
                options.semantic.workers);
        } else {
            celtest::RunBenchmark(options.benchmark);
        }
        return 0;
    } catch (const std::exception& exception) {
        std::cerr << "ERROR: " << exception.what() << "\n";
        return 1;
    }
}
