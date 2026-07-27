add_executable(TestAndBenchmark
    main.cpp
    SemanticTests.cpp
    Benchmark.cpp
    c_api_smoke.c)

target_compile_features(TestAndBenchmark PRIVATE cxx_std_17)
target_include_directories(TestAndBenchmark PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}
    ${PROJECT_SOURCE_DIR}/include)
target_link_libraries(TestAndBenchmark PRIVATE
    ConcurrentExclusiveLockCpp
    Threads::Threads)

if(MSVC)
    target_compile_options(TestAndBenchmark PRIVATE /W4 /permissive-)
else()
    target_compile_options(TestAndBenchmark PRIVATE
        -Wall -Wextra -Wpedantic -Wconversion -Wsign-conversion)
endif()

add_test(NAME cel_full_semantics
    COMMAND TestAndBenchmark --full-semantics
        --lock-instances 4
        --semantic-workers 4
        --semantic-operations 64)
set_tests_properties(cel_full_semantics PROPERTIES TIMEOUT 120)
