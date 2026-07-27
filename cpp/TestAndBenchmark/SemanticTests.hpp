#ifndef CEL_SEMANTIC_TESTS_HPP
#define CEL_SEMANTIC_TESTS_HPP

#include <chrono>
#include <cstdint>

namespace celtest {

struct SemanticOptions {
    int lockInstances = 8;
    int workers = 4;
    int operations = 256;
    std::uint64_t seed = 0;
};

void RunFullSemantics(const SemanticOptions& options);
void RunPipelineSemantics();
void RunPipelineStress(
    std::chrono::milliseconds duration,
    const SemanticOptions& options);
void RunContentionStress(
    std::chrono::milliseconds duration,
    int workers);

} // namespace celtest

#endif
