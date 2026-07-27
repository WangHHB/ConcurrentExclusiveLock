# ConcurrentExclusiveLock C / C++ 版

[English](README.md)

ConcurrentExclusiveLock（CEL）是一个基于 **Concurrent / Exclusive 访问权限**的高性能、非递归同步协议。

本项目由 ConcurrentExclusiveLock 的 [C# 原版](https://github.com/WangHHB/ConcurrentExclusiveLock/tree/main/csharp)移植而来。**C# 版本仍是同步语义的参考实现**，包括权限状态、抢占、升级/降级、ContextID/EpochID、Scope 和 Pipeline 的行为。

本移植分为两层：

- 调用方直接持有的 **C 核心**，使用直接锁对象，不引入获取 Token；
- 基于 **C++17** 的 RAII Scope、同步 Pipeline 编排和异常错误处理封装。

项目采用 MIT License 或 Apache License 2.0 双重许可，使用者可任选其一。

---

## 目录

- [设计定位](#设计定位)
- [核心概念](#核心概念)
- [为什么它不是读写锁](#为什么它不是读写锁)
- [抢占式 Exclusive](#抢占式-exclusive)
- [实用顺序，而不是严格 FIFO](#实用顺序而不是严格-fifo)
- [原地升级与降级](#原地升级与降级)
- [ContextID 与 EpochID](#contextid-与-epochid)
- [架构](#架构)
- [支持平台](#支持平台)
- [构建](#构建)
- [通过 CMake 安装和引用](#通过-cmake-安装和引用)
- [C API](#c-api)
- [C++ API](#c-api-1)
- [C++ Scope](#c-scope)
- [C++ Pipeline](#c-pipeline)
- [同步与异步边界](#同步与异步边界)
- [状态观察](#状态观察)
- [低分配设计](#低分配设计)
- [适用场景](#适用场景)
- [设计边界](#设计边界)
- [语义测试与压力测试](#语义测试与压力测试)
- [性能评测](#性能评测)
- [本地性能评测结果](#本地性能评测结果)
- [项目结构](#项目结构)
- [项目状态](#项目状态)
- [许可证](#许可证)

---

## 设计定位

CEL 表达的是：

> 当前业务操作是否允许与其他业务操作并发执行。

它表达的不是代码内部的“读意图”和“写意图”。

通常应当为每个需要独立同步的业务对象分别配置一把锁，例如：

- 玩家；
- 房间；
- 实体；
- 会话；
- Actor；
- 聚合根；
- 缓存项；
- 订单；
- 任务上下文。

推荐模型：

```text
一个独立同步的业务实体
          ↓
一把 ConcurrentExclusiveLock
```

CEL 尤其适合大量细粒度锁实例共存、每把锁通常只有少量竞争者的服务器或实体级场景。

---

## 核心概念

### Concurrent

Concurrent 表示当前操作允许与其他 Concurrent 操作同时执行。

Concurrent 区域**不等于只读区域**。只要业务规则保证同时发生的修改互不冲突，就可以在 Concurrent 权限下修改状态。

例如：

- 修改同一实体中的独立槽位；
- 向各自独占的通道写入事件；
- 读取共享状态，同时更新线程本地或分区独占数据；
- 并行执行彼此不冲突的业务校验。

### Exclusive

Exclusive 表示当前操作必须单独执行，不能与任何 Concurrent 或其他 Exclusive 区域重叠。

Exclusive 区域**不等于只写区域**。它可以包含大量读取、校验、聚合、序列化和决策逻辑。

CEL 回答的是：

> 这个业务操作能否与其他业务操作并发？

它不回答：

> 这段代码是在读还是在写？

---

## 为什么它不是读写锁

传统 Reader/Writer Lock 主要围绕“共享读、独占写”构建。

CEL 面向更广义的实体级权限协议：

- 多个互不冲突的状态修改可以并发；
- 纯读取操作也可能因为业务一致性要求而必须独占；
- 操作可以先在 Concurrent 下检查，再成为唯一提交者；
- Exclusive 完成后可以原地降级并保持连续访问上下文；
- 权限变化可以与 ContextID 或 EpochID 的推进绑定；
- 一条工作流可以包含多个独立或连续收敛的权限段。

因此 API 使用 Concurrent / Exclusive，而不是 Read / Write。

---

## 抢占式 Exclusive

CEL 最核心的特征是 **抢占式 Exclusive**。

普通 Concurrent 获取和释放主要走原子计数快速路径，通常不会进入平台 Monitor 的互斥排队路径。

当 Exclusive 请求进入竞争窗口后：

1. 新的 Concurrent 获取被阻止；
2. 已经持有 Concurrent 的调用方自然退出；
3. 当前 Concurrent 排空后，Exclusive 获得权限；
4. Exclusive 释放或降级后恢复普通竞争。

因此，在持续存在 Concurrent 流量时，Exclusive 不需要等待一个偶然出现的“完全空闲瞬间”。

C/C++ 版保留参考实现的结构：

```text
Concurrent 快速路径
    原子状态检查 + 原子增减

Exclusive / 升级慢路径
    平台 Monitor 互斥
    + 原子竞争状态
    + 自旋 / Yield 等待 Concurrent 排空
```

C# 原版实际使用的是 `Monitor.Enter`、`Monitor.TryEnter` 和 `Monitor.Exit`，并没有使用 `Monitor.Wait` 或 `Monitor.Pulse`。因此本移植需要的是平台互斥后端，而不是条件变量等待队列。

---

## 实用顺序，而不是严格 FIFO

CEL 明确**不实现** ticket lock 或严格 FIFO 等待队列。

严格 FIFO 会带来：

- 额外排队状态；
- 超时和取消产生的队列空洞；
- 队头线程暂停导致的队头阻塞；
- 每次 Exclusive 的额外原子竞争；
- 普通 Exclusive 与 Concurrent→Exclusive 升级之间更复杂的绝对排序。

CEL 使用一个串行化的平台 Monitor 慢路径，在竞争中获得足够实用的顺序性，同时不承诺绝对 FIFO。

实际执行顺序仍然受到以下因素影响：

- 操作系统调度；
- CPU 拓扑；
- 缓存状态；
- 线程挂起；
- 系统负载；
- 业务临界区长度；
- 平台互斥原语本身的公平性。

准确保证是：

> Exclusive 请求通过串行化的阻塞慢路径协调，但不保证严格 FIFO。

这与 C# 参考实现一致。

---

## 原地升级与降级

### Concurrent → Exclusive

典型业务流程：

1. 在 Concurrent 下检查或校验；
2. 判断是否确实需要修改；
3. 不释放当前访问上下文，直接收敛到 Exclusive；
4. 成为唯一提交者并执行修改。

无条件升级：

```text
ConcurrentToExclusive
```

带业务条件的升级：

```text
TryConcurrentToExclusiveWithSwitchContextID
TryConcurrentToExclusiveWithRaiseEpochID
```

多个 Concurrent 持有者同时执行无条件升级时，它们的 Exclusive 区域会串行执行。在当前升级组排空前，升级请求优先于普通 Exclusive 请求。这一调度关系严格参考 C# 原版。

升级成功后，调用方持有 Exclusive。

条件升级失败后，原来的 Concurrent 已经由协议自动释放，不能再调用 `ReleaseConcurrent`。

### Exclusive → Concurrent

Exclusive 完成后可以直接降级：

```text
ExclusiveToConcurrent
```

降级后：

- 不再持有 Exclusive；
- 继续持有 Concurrent；
- 可以执行依赖连续访问上下文的后续逻辑；
- 最后按照 Concurrent 协议释放；
- 在没有其他升级竞争时，不会产生普通“释放后重新获取”的窗口。

存在其他升级请求时，参考协议可能切断当前上下文并重新获取 Concurrent，以便剩余升级请求继续执行。C/C++ 版保留了这一行为。

---

## ContextID 与 EpochID

锁中保存两个位于权限协议之外的原子业务 ID。

### ContextID

ContextID 表示当前业务上下文，例如：

- 房间实例；
- 战斗上下文；
- 玩家会话；
- 数据加载批次；
- 逻辑事务；
- 任务所有者。

`SwitchContextID` 会原子替换当前值，并返回值是否发生变化。

多个 Concurrent 持有者使用同一个新 ContextID 执行条件升级时，只有真正改变 ContextID 的调用方成功。失败者的原 Concurrent 权限自动释放。

ContextID 不是所有权 Token，也不会在释放权限或 Scope 析构时自动清零。

### EpochID

EpochID 表示只能单调向前推进的生命周期、版本或阶段，例如：

- 实体版本；
- 房间 Tick；
- 战斗阶段；
- 快照版本；
- 生命周期代数；
- 数据处理批次。

`RaiseEpochID` 仅在新值大于当前值时成功。

EpochID 也可以用来筛选哪些 Concurrent 调用方可以收敛到 Exclusive。条件失败时，原 Concurrent 自动释放。

ContextID 和 EpochID 都是业务状态。它们的分配、语义、清理、持久化和重置规则由调用方负责。

---

## 架构

```text
C API
└─ cel_lock
   ├─ 64 位原子权限计数器
   ├─ 原子 ContextID
   ├─ 原子 EpochID
   └─ 平台 Monitor 互斥

C++ API
├─ ConcurrentExclusiveLock
├─ ConcurrentExclusiveLockScope
├─ ConcurrentExclusiveLockSegment
└─ ConcurrentExclusiveLockPipeline
```

逻辑权限状态仍然与 C# 参考设计一样由 128 位字段构成：

```text
Counter     64 位
ContextID   32 位
EpochID     32 位
```

平台 Monitor 是额外的运行时同步存储。

### 不引入获取 Token

C API 不返回、也不要求保存所有权 Token。

Concurrent 获取返回 `[1, maxConcurrent]` 范围内的 **Concurrent ID**。它表示当前连续 Concurrent 轮次中分配的 ID，不是释放凭证。

释放仍然直接区分：

```c
cel_lock_release_concurrent(&lock);
cel_lock_release_exclusive(&lock);
```

也就是说，C API 的使用形式接近 Java 版的直接锁对象调用，但权限协议严格参考 C#。

---

## 支持平台

源码内置两套平台后端：

| 平台 | Monitor 后端 | 原子后端 |
|---|---|---|
| Windows | `SRWLOCK` | Windows `Interlocked` |
| POSIX | `pthread_mutex_t` | GCC/Clang 兼容的 `__atomic` 操作 |

POSIX 路径面向 Linux、macOS、Android、iOS 以及其他 pthread 系统。 CMake 会检测目标平台的 64 位原子操作是否需要单独链接 `libatomic`，并在必要时自动加入。

本压缩包已在生成它的 Linux 环境中完成编译和测试。Windows 后端已经包含在源码中，设计用于 MSVC、clang-cl 或兼容 Windows 工具链，但无法在当前 Linux 沙盒中实际执行 Windows 二进制测试。

其他设备可以增加内部 Monitor/原子后端，不需要改变公开 C/C++ API。

---

## 使用 Visual Studio 2026 直接构建（无需 CMake）

项目根目录提供了可直接打开的：

```text
ConcurrentExclusiveLock.sln
```

双击打开后，选择 `Release | x64`，将 `TestAndBenchmark` 设为启动项目，然后生成解决方案。生成程序位于：

```text
bin\x64\Release\TestAndBenchmark.exe
```

Release 配置已经预设 `memory` Work 640 的正式跑分参数。也可以执行：

```powershell
.\build-vs.ps1
.\run-benchmark-vs.ps1
```

详细步骤见 [`VisualStudio/README_CN.md`](VisualStudio/README_CN.md)。

## 构建

要求：

- C API：支持 C11 的编译器；
- POSIX 后端：提供 `__atomic` 内建函数的 GCC/Clang 兼容编译器；
- C++ 封装与 TestAndBenchmark：C++17 编译器；
- CMake 3.20 或更高版本；
- Windows 或 pthread 平台。

### 配置和构建

```shell
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

多配置生成器可以不设置 `CMAKE_BUILD_TYPE`，通过 `--config Release` 选择配置。

### 运行 CTest

```shell
ctest --test-dir build -C Release --output-on-failure
```

### 只构建库

```shell
cmake -S . -B build -DCEL_BUILD_TESTS=OFF -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

### 同时构建动态库

```shell
cmake -S . -B build -DCEL_BUILD_SHARED=ON -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

静态库始终构建；`CEL_BUILD_SHARED=ON` 会额外构建 `ConcurrentExclusiveLock::CShared` 和 `ConcurrentExclusiveLock::CppShared`。动态库输出名带 `-shared` 后缀，避免 Windows 导入库与静态 `.lib` 重名。

提供便捷脚本：

```powershell
.\build.ps1
```

```shell
./build.sh
```

---

## 通过 CMake 安装和引用

安装：

```shell
cmake --install build --config Release --prefix ./install
```

C++ 项目引用：

```cmake
find_package(ConcurrentExclusiveLock CONFIG REQUIRED)

add_executable(MyApplication main.cpp)

target_link_libraries(MyApplication PRIVATE
    ConcurrentExclusiveLock::Cpp)
```

纯 C 项目引用：

```cmake
target_link_libraries(MyCApplication PRIVATE
    ConcurrentExclusiveLock::C)
```

通过 `add_subdirectory` 引入时也使用相同别名：

```cmake
add_subdirectory(cpp)
target_link_libraries(MyApplication PRIVATE ConcurrentExclusiveLock::Cpp)
```

---

## C API

头文件：

```c
#include <ConcurrentExclusiveLock.h>
```

### 生命周期

`cel_lock` 由调用方直接持有，不会为每把锁额外分配堆对象。

```c
cel_lock lock;

if (cel_lock_init(&lock) != CEL_RESULT_SUCCESS) {
    /* 处理初始化失败 */
}

/* 使用 */

if (cel_lock_destroy(&lock) != CEL_RESULT_SUCCESS) {
    /* 销毁时必须已经没有持有者和等待者 */
}
```

规则：

- 使用前只调用一次 `cel_lock_init`；
- 初始化后不能复制或搬移 `cel_lock` 的字节；
- 销毁前必须停止所有调用者和等待者；
- 锁对象必须比所有使用它的操作存活得更久。

### Concurrent

```c
int32_t concurrent_id = 0;
cel_result result = cel_lock_acquire_concurrent(
    &lock,
    CEL_MAX_CONCURRENT,
    &concurrent_id);

if (result == CEL_RESULT_SUCCESS) {
    /* Concurrent 业务 */

    cel_lock_release_concurrent(&lock);
}
```

立即尝试：

```c
int32_t concurrent_id = 0;
cel_result result = cel_lock_try_acquire_concurrent(
    &lock,
    CEL_MAX_CONCURRENT,
    &concurrent_id);

if (result == CEL_RESULT_SUCCESS) {
    cel_lock_release_concurrent(&lock);
} else if (result == CEL_RESULT_NOT_ACQUIRED) {
    /* 未获取 */
}
```

带超时：

```c
int32_t concurrent_id = 0;
cel_result result = cel_lock_try_acquire_concurrent_for(
    &lock,
    250,
    CEL_MAX_CONCURRENT,
    &concurrent_id);
```

负超时表示无限等待；`0` 表示立即尝试一次。

### Exclusive

```c
if (cel_lock_acquire_exclusive(&lock) == CEL_RESULT_SUCCESS) {
    /* Exclusive 业务 */

    cel_lock_release_exclusive(&lock);
}
```

Try 形式：

```c
cel_result result = cel_lock_try_acquire_exclusive(&lock, true);
```

`preempt_concurrent=true` 对应 C# 的 `TryAcquireExclusive(true)`：它可能等待当前 Concurrent 排空，但已有普通 Exclusive 压力或升级请求取得优先权时会失败。

`preempt_concurrent=false` 仅在锁当前立即 Idle 时尝试 Exclusive。

带超时的抢占式尝试：

```c
cel_result result = cel_lock_try_acquire_exclusive_for(&lock, 250);
```

### 升级

```c
int32_t concurrent_id;
cel_lock_acquire_concurrent(
    &lock,
    CEL_MAX_CONCURRENT,
    &concurrent_id);

/* Concurrent 下检查 */

cel_lock_concurrent_to_exclusive(&lock);

/* 当前为 Exclusive */

cel_lock_release_exclusive(&lock);
```

### ContextID 条件升级

```c
int32_t concurrent_id;
cel_lock_acquire_concurrent(
    &lock,
    CEL_MAX_CONCURRENT,
    &concurrent_id);

cel_result result =
    cel_lock_try_concurrent_to_exclusive_with_switch_context_id(
        &lock,
        new_context_id);

if (result == CEL_RESULT_SUCCESS) {
    /* 当前为 Exclusive */
    cel_lock_release_exclusive(&lock);
} else if (result == CEL_RESULT_NOT_ACQUIRED) {
    /* 原 Concurrent 已自动释放 */
}
```

### EpochID 条件升级

```c
cel_result result =
    cel_lock_try_concurrent_to_exclusive_with_raise_epoch_id(
        &lock,
        new_epoch_id);
```

失败同样表示原 Concurrent 已经自动释放。

### 降级

```c
cel_lock_acquire_exclusive(&lock);

/* Exclusive 修改 */

cel_lock_exclusive_to_concurrent(&lock);

/* 当前为 Concurrent */

cel_lock_release_concurrent(&lock);
```

### 返回码

| 返回码 | 含义 |
|---|---|
| `CEL_RESULT_SUCCESS` | 操作成功。 |
| `CEL_RESULT_NOT_ACQUIRED` | Try 操作没有获得权限。 |
| `CEL_RESULT_TIMEOUT` | 超时。 |
| `CEL_RESULT_INVALID_ARGUMENT` | 指针、上限或输出参数无效。 |
| `CEL_RESULT_NOT_INITIALIZED` | 锁未初始化。 |
| `CEL_RESULT_BUSY` | 销毁时锁仍处于活动或忙碌状态。 |
| `CEL_RESULT_CAPACITY_EXCEEDED` | 超过 31 位 Concurrent 计数边界。 |
| `CEL_RESULT_PLATFORM_ERROR` | 平台同步操作失败。 |

`cel_result_string` 可以取得稳定的诊断字符串。

---

## C++ API

头文件：

```cpp
#include <ConcurrentExclusiveLock.hpp>
```

命名空间：

```cpp
using intomic::ConcurrentExclusiveLock;
```

C++ 锁内部直接包含一把 C 锁，并自动初始化和销毁。

```cpp
class Entity {
private:
    ConcurrentExclusiveLock locker_;
};
```

C++ 锁明确禁止复制和移动，因为复制同步状态或搬移活动中的平台互斥对象都是无效行为。

### Concurrent

```cpp
void ReadState() {
    int concurrentID = locker_.AcquireConcurrent();

    ReadEntityState();

    locker_.ReleaseConcurrent();
}
```

### Exclusive

```cpp
void ModifyState() {
    locker_.AcquireExclusive();

    ModifyEntityState();

    locker_.ReleaseExclusive();
}
```

### 带超时的 Try

```cpp
using namespace std::chrono_literals;

if (locker_.TryAcquireExclusive(250ms)) {
    ModifyEntityState();
    locker_.ReleaseExclusive();
}
```

C++ Try 方法在普通未获取时返回 `0` 或 `false`。参数错误、容量错误、初始化错误和平台错误通过异常报告。

---

## C++ Scope

大多数包含提前返回、异常或权限转换的业务代码，推荐使用 `ConcurrentExclusiveLockScope`。

Scope 记录最终持有的权限，并在析构时自动释放。

```cpp
using intomic::ConcurrentExclusiveLockScope;

void ReadState() {
    ConcurrentExclusiveLockScope scope(locker_);
    scope.AcquireConcurrent();

    ReadEntityState();

    // 可以不手动释放。
}
```

### Exclusive 异常安全

```cpp
void ModifyState() {
    ConcurrentExclusiveLockScope scope(locker_);
    scope.AcquireExclusive();

    ModifyEntityState(); // 可能抛异常

    // 栈展开时 Scope 析构并释放 Exclusive。
}
```

### 检查后条件升级

```cpp
void ApplyEpoch(std::int32_t targetEpoch) {
    ConcurrentExclusiveLockScope scope(locker_);
    scope.AcquireConcurrent();

    InspectCurrentState();

    if (!scope.TryConcurrentToExclusiveWithRaiseEpochID(targetEpoch)) {
        // 条件失败时，Concurrent 已经由协议自动释放。
        return;
    }

    ApplyEpochUpdate();
    // Scope 当前最终持有 Exclusive。
}
```

### 降级

```cpp
void RebuildAndPublish() {
    ConcurrentExclusiveLockScope scope(locker_);
    scope.AcquireExclusive();

    RebuildEntityState();

    scope.ExclusiveToConcurrent();

    PublishSnapshot();
    // Scope 当前最终持有 Concurrent。
}
```

Scope 规则：

- 只供一个调用上下文使用；
- Scope 对象本身不是线程安全对象；
- 禁止复制和移动；
- 不恢复、不清理 ContextID/EpochID；
- 手动释放会同步更新最终权限记录；
- 析构函数不抛异常。

---

## C++ Pipeline

`ConcurrentExclusiveLockPipeline` 使用一组同步业务 Segment 描述完整权限工作流。每个 Segment 声明执行时需要的权限。

```cpp
using intomic::ConcurrentExclusiveLockPipeline;
using intomic::ConcurrentExclusiveLockSegment;

ConcurrentExclusiveLockPipeline pipeline(locker_);

pipeline.DoPipeline(
    ConcurrentExclusiveLockSegment::Concurrent([&] {
        ReadCurrentState();
    }),

    ConcurrentExclusiveLockSegment::TryApplyIDConvergeExclusive(
        [&] {
            ApplyNewEpoch();
        },
        targetEpoch,
        ConcurrentExclusiveLockSegment::IDType::EpochID),

    ConcurrentExclusiveLockSegment::ConvergeConcurrent([&] {
        PublishNewSnapshot();
    }),

    ConcurrentExclusiveLockSegment::None([&] {
        NotifyOtherSystems();
    }));
```

Pipeline 会根据前一段成功持有的权限，自动执行释放、重新获取、延续、升级或降级。

### Segment 类型

| 工厂方法 | 语义 |
|---|---|
| `None` | 释放仍持有的权限，在无 CEL 权限状态下执行。 |
| `Concurrent` | 获取独立 Concurrent 段；前一段即使也是 Concurrent 也会释放并重新获取。 |
| `TryConcurrent` | 尝试独立 Concurrent；失败时跳过当前段。 |
| `Exclusive` | 获取独立 Exclusive 段；前一段即使也是 Exclusive 也会释放并重新获取。 |
| `TestExclusive` | 仅在 Idle 时尝试 Exclusive，不抢占 Concurrent。 |
| `TryExclusive` | 尝试抢占式 Exclusive；遇到升级请求时可能让出并失败。 |
| `ConvergeConcurrent` | 延续 Concurrent、把 Exclusive 降级，或重新获取 Concurrent。 |
| `ConvergeExclusive` | 延续 Exclusive、把 Concurrent 升级，或重新获取 Exclusive。 |
| `TryApplyIDConvergeExclusive` | 应用 ContextID/EpochID，仅在成功时收敛到 Exclusive。 |

### Try Segment 失败行为

Try 类型 Segment 未满足执行条件时：

- 当前 Segment 不执行；
- 普通失败不会抛异常；
- 与失败转换关联的权限按协议释放；
- Pipeline 从 None 状态继续；
- 后续 Segment 继续执行。

### 异常行为

Segment 抛异常时：

1. 后续 Segment 不再执行；
2. 栈展开过程中 Scope 释放最终持有的权限；
3. 原异常继续传播给调用方。

---

## 同步与异步边界

CEL 是同步权限协议。

Exclusive 具有线程所有权，必须由获得它的同一线程释放或降级。

Pipeline Segment 是同步 `std::function<void()>`。受保护业务必须在回调返回前完成。

不支持：

```cpp
ConcurrentExclusiveLockSegment::Exclusive([&] {
    std::thread([&] {
        ModifyEntityState();
    }).detach();
    // Segment 返回时，分离线程中的业务仍在运行。
});
```

`DoPipelineAsync` 只是通过 `std::async` 调度**整条同步 Pipeline**，不会允许单个 Segment 跨越同步回调边界。

锁对象和 Segment 捕获的所有对象必须比异步 Pipeline 存活得更久。

---

## 状态观察

### ObservedState

可能值：

```text
Idle
Concurrent
Exclusive
```

它只是瞬时观察快照。

抢占式 Exclusive 进入竞争窗口后，即使旧 Concurrent 尚未全部退出，`ObservedState` 也可能已经显示 Exclusive。因此它表达的是当前访问倾向或转换状态，不代表已经有线程进入 Exclusive 业务区。

### ObservedContention

这是竞争压力观察指标：

- 纯 Concurrent 场景通常为 `0`；
- 存在 Exclusive 压力后，返回当前观察到的 Concurrent + Exclusive 压力；
- 仅用于诊断、监控或调度参考；
- 不能作为同步正确性的判断条件。

---

## 低分配设计

### C 锁

`cel_lock` 直接嵌入调用方存储，初始化时不会为每把锁分配单独 Token。

### C++ 锁和 Scope

`ConcurrentExclusiveLock` 内嵌 `cel_lock`。Scope 只保存锁指针和最终权限计数状态。

### 热路径

普通 Concurrent 获取和释放走原子计数快速路径，不分配内存，通常不进入平台 Monitor。

Exclusive 和升级进入串行化 Monitor 慢路径。

### Pipeline 分配

可变参数 `DoPipeline` 会在调用点构造固定大小的 `std::array`。每个 Segment 内含 `std::function<void()>`；回调是否分配取决于标准库的小对象优化和捕获对象大小。

锁本身不分配业务对象、回调或任务状态。

---

## 适用场景

### 实体级状态

为每个玩家、房间、实体、订单、会话或聚合根分配独立锁。

### 缓存加载

1. Concurrent 下检查；
2. 已加载则延续 Concurrent；
3. 未加载时通过 ContextID/EpochID 选出一个加载者；
4. 选中的调用方升级为 Exclusive；
5. 发布加载结果；
6. 降级或释放。

### 订单 / 风控流程

1. Concurrent 下读取订单状态；
2. 离开 CEL 权限执行外部校验；
3. 通过 EpochID 选出当前提交阶段；
4. 收敛到 Exclusive 执行唯一修改；
5. 降级后读取新状态；
6. 必要时再获取独立 Exclusive 完成结算。

### 版本化状态发布

Concurrent 读取当前快照，由 EpochID 选中的调用方成为下一版本的唯一发布者。

---

## 设计边界

CEL 明确不提供：

- Concurrent 或 Exclusive 递归嵌套；
- 严格 FIFO；
- 所有权 Token；
- 自动死锁检测；
- ContextID/EpochID 生命周期管理；
- 跨进程共享锁；
- 协程感知的权限转移；
- 仍有调用者或等待者时的安全销毁；
- 业务对象生命周期保护。

使用规则：

- 持有 Concurrent 时不要普通获取 Exclusive，应当升级；
- 持有 Exclusive 时不要普通获取 Concurrent，应当降级；
- 按照最终转换后的权限释放；
- 初始化后的 C 锁不能复制；
- C++ Lock 和 Scope 不能复制或移动；
- 依赖 Exclusive 权限的执行流不能跨线程或异步边界；
- 不能用观察快照作为权威锁状态判断。

错误释放或非法转换不属于已定义协议，可能破坏计数器状态或违反平台互斥的线程所有权规则。

---

## 语义测试与压力测试

构建 TestAndBenchmark 后：

```shell
./build/TestAndBenchmark/TestAndBenchmark --help
```

完整语义回归：

```shell
./build/TestAndBenchmark/TestAndBenchmark --full-semantics --lock-instances 8 --semantic-workers 4 --semantic-operations 256
```

固定 Pipeline 语义：

```shell
./build/TestAndBenchmark/TestAndBenchmark --pipeline-semantics
```

随机 Pipeline 压测：

```shell
./build/TestAndBenchmark/TestAndBenchmark --pipeline-stress 10m --lock-instances 8 --semantic-workers 8 --semantic-operations 256
```

在该模式下，三个语义参数都表示上限。每个有限批次会在上限内选择可复现的随机形状；测试每 10 秒输出心跳，若某一批连续 10 分钟没有线程推进则报告失败。

单锁 Exclusive 高竞争诊断：

```shell
./build/TestAndBenchmark/TestAndBenchmark --contention-stress 10m --semantic-workers 64
```

语义测试覆盖：

- C API 编译和直接使用；
- Concurrent/Exclusive 不重叠；
- Concurrent ID 和 maxConcurrent；
- 抢占式 Exclusive；
- 升级与降级；
- 多升级请求串行；
- ContextID 单赢家条件升级；
- EpochID 条件升级；
- 超时路径；
- Scope 正常、转换和异常释放；
- Pipeline 转换、Try 失败和异常释放；
- 多把独立锁上的随机合法路径。

详细说明见 [TESTING_CN.md](TESTING_CN.md)。

---

## 性能评测

默认评测：

```shell
./build/TestAndBenchmark/TestAndBenchmark
```

较长的内存负载：

```shell
./build/TestAndBenchmark/TestAndBenchmark --lock-instances 8 --threads 8 --workload memory --operations 500000 --memory-mb 64 --read-work 32 --write-work 32
```

标准对比：

- `std::mutex`；
- `std::shared_mutex`；
- CEL；
- `CEL(ExclusiveOnly)`。

每种策略都使用全新的 Work。各线程的读写选择是确定性的，评测程序会校验所有策略的读次数、写次数和最终状态哈希完全一致。

Memory Work 参考 C# 评测模型：每把锁拥有一块共享内存；读操作执行随机索引读取和混合；写操作原位修改随机位置并推进串行状态哈希。

结果会受到编译器、标准库、操作系统、互斥实现、CPU 拓扑、NUMA、工作集大小、线程数量和业务任务量影响，不代表所有环境下的普遍结论。

详细说明见 [PERFORMANCE_CN.md](PERFORMANCE_CN.md)。

---

## 本地性能评测结果

下面的数据来自生成本源码包的 Linux 沙盒中的一次 Release 构建评测，仅用于提供可复现参考，不代表跨平台普遍性能。

```text
编译器：          GCC/G++ 14.2.0
容器报告 CPU：    5 个硬件线程
锁实例数：        4
每把锁线程数：    4
总线程数：        16
业务负载：        memory，每把锁共享 64 MiB
每线程操作数：    200,000
每策略每场景总量：3,200,000 次锁操作
读写工作量：      32 / 32 steps
```

CEL 结果：

| 读/写比例 | 吞吐量 | Work/CPU% | 平均写入时间 | 简要观察 |
|---:|---:|---:|---:|---|
| 100/0 | 4,771,451 works/s | 60,266 | — | 与 `std::shared_mutex` 接近，差距很小。 |
| 99.5/0.5 | 3,421,835 works/s | 42,857 | 89.85 μs | 本轮吞吐量最高；写入时间明显低于 `std::shared_mutex`，但完全串行的 `std::mutex` 写入时间更低。 |
| 90/10 | 3,108,759 works/s | 42,362 | 7.97 μs | 本轮吞吐量最高。 |
| 50/50 | 2,687,139 works/s | 35,262 | 5.11 μs | 本轮吞吐量略高，平均写入时间最低。 |
| 30/70 | 2,734,470 works/s | 36,925 | 4.82 μs | 保持竞争力，但 `std::shared_mutex` 吞吐量更高。 |
| 0/100 | 2,701,963 works/s | 36,209 | 5.06 μs | 已接近普通独占锁；本轮 CEL 吞吐量略高。 |

在该环境中，CEL 在读主导的混合负载里体现出最明确的吞吐优势；随着写入比例提高，表现逐渐接近普通独占锁。容器 CPU 配额、线程超配、标准库实现和操作系统调度都会显著影响结果。完整原始输出位于 [`TestResults/benchmark-memory-long-linux.txt`](TestResults/benchmark-memory-long-linux.txt)。

---

## 项目结构

```text
ConcurrentExclusiveLock-C-Cpp/
├─ include/
│  ├─ ConcurrentExclusiveLock.h       # 公开 C API
│  └─ ConcurrentExclusiveLock.hpp     # 公开 C++ API
├─ src/
│  ├─ ConcurrentExclusiveLock.c       # C 核心和平台后端
│  ├─ ConcurrentExclusiveLock.cpp     # C++ Lock/Scope/Pipeline
│  └─ ConcurrentExclusiveLockInternal.h
├─ TestAndBenchmark/
│  ├─ c_api_smoke.c
│  ├─ SemanticTests.cpp
│  ├─ Benchmark.cpp
│  └─ main.cpp
├─ cmake/
├─ CMakeLists.txt
├─ README.md
├─ README_CN.md
├─ TESTING.md
├─ TESTING_CN.md
├─ PERFORMANCE.md
├─ PERFORMANCE_CN.md
├─ VERIFICATION.md
├─ LICENSE-MIT
└─ LICENSE-APACHE-2.0
```

---

## 项目状态

版本：**1.0.0 初始移植版**

已经完成：

- 完整 C 核心 API；
- Windows / POSIX Monitor 和原子后端；
- C++ Lock 封装；
- C++ RAII Scope；
- 完整 C++ Segment / Pipeline 状态机；
- 带超时的获取方法；
- 语义、随机压力、高竞争和性能评测程序；
- CMake 构建、安装和包配置；
- 中英文详细文档。

本压缩包已在生成它的 Linux 沙盒中通过内置语义测试、随机 Pipeline 压测、AddressSanitizer/UndefinedBehaviorSanitizer 和 ThreadSanitizer。发布其他平台二进制前，仍应在对应 Windows 或 POSIX 目标系统上完成平台专项验证。

原始设计和参考实现作者：**王弈博（YiBoWang）**。

仓库：`https://github.com/WangHHB/ConcurrentExclusiveLock`

---

## 许可证

ConcurrentExclusiveLock 采用以下双重许可：

- MIT License；或
- Apache License 2.0。

使用者可以任选其一。

参阅 [LICENSE-MIT](LICENSE-MIT) 和 [LICENSE-APACHE-2.0](LICENSE-APACHE-2.0)。
