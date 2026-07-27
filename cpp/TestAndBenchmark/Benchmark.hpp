#ifndef CEL_BENCHMARK_HPP
#define CEL_BENCHMARK_HPP

#include <string>

namespace celtest {

struct BenchmarkOptions {
    int lockInstances = 1;
    int threadsPerLock = 0;
    int operationsPerThread = 10000;
    int readSteps = 32;
    int writeSteps = 32;
    int memoryMb = 64;
    std::string workload = "memory";
};

void RunBenchmark(const BenchmarkOptions& options);

} // namespace celtest

#endif
