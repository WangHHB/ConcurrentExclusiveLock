cmake_minimum_required(VERSION 3.20)

project(ConcurrentExclusiveLock
    VERSION 1.0.0
    DESCRIPTION "Concurrent/Exclusive synchronization lock with a C core and C++ wrappers"
    LANGUAGES C CXX)

include(GNUInstallDirs)
include(CMakePackageConfigHelpers)
include(CheckCSourceCompiles)
find_package(Threads REQUIRED)

set(CEL_ATOMIC_LINK_LIBRARY "")
if(NOT WIN32)
    check_c_source_compiles("
        #include <stdint.h>
        static uint64_t value;
        int main(void) {
            (void)__atomic_fetch_add(&value, UINT64_C(1), __ATOMIC_SEQ_CST);
            return 0;
        }" CEL_ATOMIC_BUILTINS_LINK_DIRECTLY)
    if(NOT CEL_ATOMIC_BUILTINS_LINK_DIRECTLY)
        set(CMAKE_REQUIRED_LIBRARIES atomic)
        check_c_source_compiles("
            #include <stdint.h>
            static uint64_t value;
            int main(void) {
                (void)__atomic_fetch_add(&value, UINT64_C(1), __ATOMIC_SEQ_CST);
                return 0;
            }" CEL_ATOMIC_BUILTINS_LINK_WITH_LIBATOMIC)
        unset(CMAKE_REQUIRED_LIBRARIES)
        if(NOT CEL_ATOMIC_BUILTINS_LINK_WITH_LIBATOMIC)
            message(FATAL_ERROR
                "64-bit __atomic operations are unavailable on this target")
        endif()
        set(CEL_ATOMIC_LINK_LIBRARY atomic)
    endif()
endif()

option(CEL_BUILD_TESTS "Build TestAndBenchmark" ON)
option(CEL_BUILD_SHARED "Build shared libraries in addition to static libraries" OFF)

add_library(ConcurrentExclusiveLockC STATIC
    src/ConcurrentExclusiveLock.c)
add_library(ConcurrentExclusiveLock::C ALIAS ConcurrentExclusiveLockC)

target_compile_features(ConcurrentExclusiveLockC PUBLIC c_std_11)
target_include_directories(ConcurrentExclusiveLockC
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(ConcurrentExclusiveLockC
    PUBLIC Threads::Threads ${CEL_ATOMIC_LINK_LIBRARY})
set_target_properties(ConcurrentExclusiveLockC PROPERTIES
    OUTPUT_NAME concurrent-exclusive-lock-c
    EXPORT_NAME C
    POSITION_INDEPENDENT_CODE ON)

add_library(ConcurrentExclusiveLockCpp STATIC
    src/ConcurrentExclusiveLock.cpp)
add_library(ConcurrentExclusiveLock::Cpp ALIAS ConcurrentExclusiveLockCpp)

target_compile_features(ConcurrentExclusiveLockCpp PUBLIC cxx_std_17)
target_include_directories(ConcurrentExclusiveLockCpp
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src)
target_link_libraries(ConcurrentExclusiveLockCpp PUBLIC ConcurrentExclusiveLockC)
set_target_properties(ConcurrentExclusiveLockCpp PROPERTIES
    OUTPUT_NAME concurrent-exclusive-lock-cpp
    EXPORT_NAME Cpp
    POSITION_INDEPENDENT_CODE ON)

if(MSVC)
    target_compile_options(ConcurrentExclusiveLockC PRIVATE /W4)
    target_compile_options(ConcurrentExclusiveLockCpp PRIVATE /W4 /permissive-)
else()
    target_compile_options(ConcurrentExclusiveLockC PRIVATE
        -Wall -Wextra -Wpedantic -Wconversion -Wsign-conversion)
    target_compile_options(ConcurrentExclusiveLockCpp PRIVATE
        -Wall -Wextra -Wpedantic -Wconversion -Wsign-conversion)
endif()

if(CEL_BUILD_SHARED)
    add_library(ConcurrentExclusiveLockCShared SHARED
        src/ConcurrentExclusiveLock.c)
    add_library(ConcurrentExclusiveLock::CShared ALIAS
        ConcurrentExclusiveLockCShared)
    target_compile_features(ConcurrentExclusiveLockCShared PUBLIC c_std_11)
    target_include_directories(ConcurrentExclusiveLockCShared
        PUBLIC
            $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
            $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>
        PRIVATE
            ${CMAKE_CURRENT_SOURCE_DIR}/src)
    target_link_libraries(ConcurrentExclusiveLockCShared
        PUBLIC Threads::Threads ${CEL_ATOMIC_LINK_LIBRARY})
    set_target_properties(ConcurrentExclusiveLockCShared PROPERTIES
        OUTPUT_NAME concurrent-exclusive-lock-c-shared
        EXPORT_NAME CShared
        VERSION ${PROJECT_VERSION}
        SOVERSION ${PROJECT_VERSION_MAJOR}
        WINDOWS_EXPORT_ALL_SYMBOLS ON)

    add_library(ConcurrentExclusiveLockCppShared SHARED
        src/ConcurrentExclusiveLock.cpp)
    add_library(ConcurrentExclusiveLock::CppShared ALIAS
        ConcurrentExclusiveLockCppShared)
    target_compile_features(ConcurrentExclusiveLockCppShared PUBLIC cxx_std_17)
    target_include_directories(ConcurrentExclusiveLockCppShared
        PUBLIC
            $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
            $<INSTALL_INTERFACE:${CMAKE_INSTALL_INCLUDEDIR}>
        PRIVATE
            ${CMAKE_CURRENT_SOURCE_DIR}/src)
    target_link_libraries(ConcurrentExclusiveLockCppShared
        PUBLIC ConcurrentExclusiveLockCShared)
    set_target_properties(ConcurrentExclusiveLockCppShared PROPERTIES
        OUTPUT_NAME concurrent-exclusive-lock-cpp-shared
        EXPORT_NAME CppShared
        VERSION ${PROJECT_VERSION}
        SOVERSION ${PROJECT_VERSION_MAJOR}
        WINDOWS_EXPORT_ALL_SYMBOLS ON)

    if(MSVC)
        target_compile_options(ConcurrentExclusiveLockCShared PRIVATE /W4)
        target_compile_options(ConcurrentExclusiveLockCppShared PRIVATE
            /W4 /permissive-)
    else()
        target_compile_options(ConcurrentExclusiveLockCShared PRIVATE
            -Wall -Wextra -Wpedantic -Wconversion -Wsign-conversion)
        target_compile_options(ConcurrentExclusiveLockCppShared PRIVATE
            -Wall -Wextra -Wpedantic -Wconversion -Wsign-conversion)
    endif()
endif()

if(CEL_BUILD_TESTS)
    enable_testing()
    add_subdirectory(TestAndBenchmark)
endif()

install(TARGETS ConcurrentExclusiveLockC ConcurrentExclusiveLockCpp
    EXPORT ConcurrentExclusiveLockTargets
    ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
    LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
    RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
    INCLUDES DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})

if(CEL_BUILD_SHARED)
    install(TARGETS
        ConcurrentExclusiveLockCShared
        ConcurrentExclusiveLockCppShared
        EXPORT ConcurrentExclusiveLockTargets
        ARCHIVE DESTINATION ${CMAKE_INSTALL_LIBDIR}
        LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}
        RUNTIME DESTINATION ${CMAKE_INSTALL_BINDIR}
        INCLUDES DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
endif()

install(FILES
    include/ConcurrentExclusiveLock.h
    include/ConcurrentExclusiveLock.hpp
    DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})

install(FILES
    README.md
    README_CN.md
    TESTING.md
    TESTING_CN.md
    PERFORMANCE.md
    PERFORMANCE_CN.md
    VERIFICATION.md
    LICENSE-MIT
    LICENSE-APACHE-2.0
    DESTINATION ${CMAKE_INSTALL_DOCDIR})

install(EXPORT ConcurrentExclusiveLockTargets
    FILE ConcurrentExclusiveLockTargets.cmake
    NAMESPACE ConcurrentExclusiveLock::
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/ConcurrentExclusiveLock)

configure_package_config_file(
    cmake/ConcurrentExclusiveLockConfig.cmake.in
    ${CMAKE_CURRENT_BINARY_DIR}/ConcurrentExclusiveLockConfig.cmake
    INSTALL_DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/ConcurrentExclusiveLock)

write_basic_package_version_file(
    ${CMAKE_CURRENT_BINARY_DIR}/ConcurrentExclusiveLockConfigVersion.cmake
    VERSION ${PROJECT_VERSION}
    COMPATIBILITY SameMajorVersion)

install(FILES
    ${CMAKE_CURRENT_BINARY_DIR}/ConcurrentExclusiveLockConfig.cmake
    ${CMAKE_CURRENT_BINARY_DIR}/ConcurrentExclusiveLockConfigVersion.cmake
    DESTINATION ${CMAKE_INSTALL_LIBDIR}/cmake/ConcurrentExclusiveLock)
