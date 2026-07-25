<p align="center">
  <a href="README.md">English</a> ｜ <strong>简体中文</strong>
</p>

# ConcurrentExclusiveLock

[![C# Build and Test](https://github.com/WangHHB/ConcurrentExclusiveLock/actions/workflows/dotnet.yml/badge.svg)](https://github.com/WangHHB/ConcurrentExclusiveLock/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/ConcurrentExclusiveLock.svg)](https://www.nuget.org/packages/ConcurrentExclusiveLock/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ConcurrentExclusiveLock.svg)](https://www.nuget.org/packages/ConcurrentExclusiveLock/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**ConcurrentExclusiveLock (CEL)** 是一个面向细粒度状态对象的 Concurrent / Exclusive 同步协议。

## 安装

```shell
dotnet add package ConcurrentExclusiveLock
```

它适合为玩家、房间、实体、会话、Actor、聚合根或任务上下文分别配置独立锁实例，在大量锁对象并存的情况下，协调：

- 可并发访问；
- 排他访问；
- 抢占式 Exclusive；
- Concurrent → Exclusive 原地升级；
- Exclusive → Concurrent 原地降级；
- ContextID / EpochID 业务状态协同；
- 权限流程编排；
- 异常路径自动释放。

项目当前以 **C# / .NET** 实现为原始和权威版本。

---

## 目录

- [核心概念](#核心概念)
- [为什么不是 ReadWriteLock](#为什么不是-readwritelock)
- [抢占式 Exclusive](#抢占式-exclusive)
- [原地升级与降级](#原地升级与降级)
- [ContextID 与 EpochID](#contextid-与-epochid)
- [三层 API](#三层-api)
- [快速开始](#快速开始)
- [Pipeline](#pipeline)
- [同步与异步边界](#同步与异步边界)
- [低分配设计](#低分配设计)
- [状态观察](#状态观察)
- [适用场景](#适用场景)
- [设计边界](#设计边界)
- [测试项目](#测试项目)
- [性能测试](#性能测试)
- [项目状态](#项目状态)

---

## 核心概念

CEL 表达的是**访问权限**，而不是代码内部的读写意图。

### Concurrent

表示当前操作允许与其他 Concurrent 操作同时进入。

Concurrent 区域内不一定只读。只要业务能够保证不同操作之间互不冲突，也可以在 Concurrent 权限下执行修改。

### Exclusive

表示当前操作必须独占进入，不能与任何 Concurrent 或 Exclusive 操作同时执行。

Exclusive 区域内也不一定只写，其中完全可以包含大量读取、校验和计算逻辑。

因此，CEL 关心的问题是：

> 这段业务代码是否允许与其他业务代码并发执行？

而不是：

> 这段代码是在读数据，还是在写数据？

---

## 为什么不是 ReadWriteLock

传统 Reader / Writer Lock 主要围绕“读共享、写独占”建立语义。

CEL 面向的是更广泛的实体业务权限模型：

- 多个互不冲突的状态修改可以并发；
- 某些纯读取逻辑也可能要求独占；
- 业务可能先并发检查，再升级为唯一提交者；
- 排他修改完成后，可能需要继续保持连续的 Concurrent 上下文；
- 权限获取可能与业务 ContextID 或 EpochID 的变更绑定；
- 一条业务流程中可能连续发生多次权限切换。

因此，CEL 使用 Concurrent / Exclusive，而不是 Read / Write。

---

## 抢占式 Exclusive

CEL 的主要特征是**抢占式 Exclusive**。

普通 Concurrent 获取和释放主要依赖轻量原子计数，不进入 `Monitor` 排序队列。

当 Exclusive 请求进入竞争窗口后：

1. 阻止新的 Concurrent 继续进入；
2. 等待已经持有 Concurrent 的调用者自然退出；
3. Concurrent 排空后获得 Exclusive；
4. Exclusive 完成后恢复后续竞争。

这意味着在持续存在 Concurrent 流量时，Exclusive 不需要无限等待一个偶然出现的完全空闲窗口。

普通 Exclusive 获取以及 Concurrent → Exclusive 转换，会借用 `Monitor` 的互斥、等待、唤醒和排他排序能力。

CEL 不额外承诺严格 FIFO，也不承诺比 `Monitor` 更强的调度公平性。线程实际执行顺序仍会受到操作系统调度、CPU 拓扑、缓存状态、系统负载和业务执行时长影响。

---

## 原地升级与降级

### Concurrent → Exclusive

典型业务流程通常不是简单的“加锁后修改”，而是：

1. 先以 Concurrent 权限读取或检查状态；
2. 判断是否需要修改；
3. 尝试成为该业务条件下的唯一提交者；
4. 成功后进入 Exclusive；
5. 完成修改。

CEL 支持从当前 Concurrent 上下文直接收敛到 Exclusive，而不需要先释放 Concurrent，再从外部重新竞争。

当前提供的业务条件升级方法包括：

```csharp
TryConcurrentToExclusiveWithSwitchContextID(int newContextID);
TryConcurrentToExclusiveWithRaiseEpochID(int newEpochID);
```

升级成功后，当前调用上下文持有 Exclusive。

升级失败时，原 Concurrent 权限已经由协议自动释放，不应再次调用 `ReleaseConcurrent()`。

### Exclusive → Concurrent

完成独占修改后，可以直接降级：

```csharp
scope.ExclusiveToConcurrent();
```

降级后：

- 不再持有 Exclusive；
- 继续持有 Concurrent；
- 可以继续执行依赖连续访问上下文的后续逻辑；
- 避免先释放 Exclusive、再重新申请 Concurrent 产生新的竞争窗口。

---

## ContextID 与 EpochID

CEL 可以在锁状态之外关联两个业务标识。

### ContextID

`ContextID` 用于表达当前业务上下文身份，例如：

- 当前房间实例；
- 当前战斗上下文；
- 当前玩家会话；
- 当前数据加载批次；
- 当前任务所有者；
- 当前逻辑事务上下文。

```csharp
bool changed = locker.SwitchContextID(newContextID);
```

当新值与当前值相同时，`SwitchContextID` 返回 `false`。

它可以用于识别同一业务上下文，避免同一上下文重复执行初始化、切换、提交或 Exclusive 逻辑。

### EpochID

`EpochID` 用于表达只能向前推进的生命周期、版本或阶段，例如：

- 实体版本；
- 房间 Tick；
- 战斗阶段；
- 快照版本；
- 生命周期代次；
- 数据处理批次。

```csharp
bool raised = locker.RaiseEpochID(newEpochID);
```

只有当 `newEpochID` 大于当前值时，推进才会成功。

ContextID 和 EpochID 都是锁协议之外的业务状态。它们的含义、分配方式、清理规则和生命周期由调用方负责。

---

## 三层 API

项目提供三个层次的 API。

### 1. ConcurrentExclusiveLock

`ConcurrentExclusiveLock` 是底层同步协议。

```csharp
private readonly ConcurrentExclusiveLock _locker = ConcurrentExclusiveLock.Create();
```

它是一个 `readonly struct`，真实共享状态保存在内部 Token 中。

复制 `ConcurrentExclusiveLock` 值不会复制锁状态，复制后的值仍然引用同一份内部同步状态。

默认初始化实例不可用，必须通过静态方法Create()创建：

```csharp
ConcurrentExclusiveLock.Create();
```

常用 API：

```csharp
AcquireConcurrent();
TryAcquireConcurrent();

AcquireExclusive();
TryAcquireExclusive();

ReleaseConcurrent();
ReleaseExclusive();

ExclusiveToConcurrent();

SwitchContextID(...);
RaiseEpochID(...);

TryConcurrentToExclusiveWithSwitchContextID(...);
TryConcurrentToExclusiveWithRaiseEpochID(...);
```

这一层适合需要精确控制每次权限获取、释放和转换的底层代码。

---

### 2. ConcurrentExclusiveLockScope

`ConcurrentExclusiveLockScope` 是基于 `using` 的权限生命周期封装。

```csharp
using (var scope = new ConcurrentExclusiveLockScope(_locker))
{
    scope.AcquireConcurrent();

    ReadEntityState();
}
```

调用方可以手动释放当前权限。

如果没有手动释放，`Dispose()` 会根据 Scope 最终记录的权限状态自动释放 Concurrent 或 Exclusive。

Scope 主要用于减少以下路径中的释放错误：

- 异常；
- 提前返回；
- 多分支退出；
- Concurrent → Exclusive 升级；
- Exclusive → Concurrent 降级；
- Try 操作失败后的状态变化。

`Dispose()` 只释放当前 Scope 仍然持有的访问权限，不会还原或清理 ContextID / EpochID。

Scope 是具有释放责任的可变值类型，只应由单个调用上下文持有和操作。

不要复制 Scope、按值传递 Scope、跨线程操作 Scope，或分别操作同一个 Scope 的多个副本。

---

### 3. ConcurrentExclusiveLockPipeline

`ConcurrentExclusiveLockPipeline` 用一组顺序 Segment 描述完整的权限工作流。

每个 Segment 声明：

- 当前业务代码；
- 当前段需要的访问权限；
- 可选的 ContextID 或 EpochID 条件。

Pipeline 根据上一段成功持有的权限，自动决定：

- 延续当前权限；
- 释放并重新申请；
- 原地升级；
- 原地降级；
- 条件失败时跳过当前段；
- 以 None 状态继续后续流程。

Pipeline 的定位可以概括为：

> Entity Permission Workflow Orchestration  
> 实体访问权限工作流编排

---

## 快速开始

### Concurrent

```csharp
private readonly ConcurrentExclusiveLock _locker = ConcurrentExclusiveLock.Create();

public void ReadState()
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireConcurrent();

        ReadEntityState();

        //最后可以手动释放，也可以让 scope 在 Dispose 时自动释放。
        //scope.ReleaseConcurrent();
    }
}
```

### Exclusive

```csharp
public void ModifyState()
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireExclusive();

        ModifyEntityState();

        //最后可以手动释放，也可以让 scope 在 Dispose 时自动释放。
        //scope.ReleaseExclusive();
    }
}
```

### Concurrent 检查后升级

```csharp
public void ApplyEpoch(int targetEpoch)
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireConcurrent();

        InspectCurrentState();

        if (!scope.TryConcurrentToExclusiveWithRaiseEpochID(targetEpoch))
        {
            // 升级失败时，原 Concurrent 已经自动释放。
            return;
        }

        ApplyEpochUpdate();

        //当前最终持有的是 Exclusive，可以手动释放，也可以让 scope 在 Dispose 时自动释放。
        //scope.ReleaseExclusive();
    }
}
```

### Exclusive 完成后降级

```csharp
public void RebuildAndPublish()
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireExclusive();

        RebuildEntityState();

        scope.ExclusiveToConcurrent();

        PublishSnapshot();

        //当前最终持有的是 Concurrent，可以手动释放，也可以让 scope 在 Dispose 时自动释放。
        //scope.ReleaseConcurrent();
    }
}
```

---

## Pipeline

### 示例

```csharp
var pipeline = new ConcurrentExclusiveLockPipeline(_locker);

pipeline.DoPipeline(
    ConcurrentExclusiveLockSegment.Concurrent(() =>
    {
        ReadCurrentState();
    }),

    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(() =>
    {
        ApplyNewEpoch();
    }, targetEpoch, ConcurrentExclusiveLockSegment.IDType.EpochID),

    ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
    {
        PublishNewSnapshot();
    }),

    ConcurrentExclusiveLockSegment.None(() =>
    {
        NotifyOtherSystems();
    })
);
```

### Segment 类型

| Segment | 语义 |
|---|---|
| `None` | 在不持有访问权限的状态下执行 |
| `Concurrent` | 获取一段独立 Concurrent；连续同类段也会切开并重新申请 |
| `TryConcurrent` | 尝试获取一段独立 Concurrent；失败则跳过当前段 |
| `Exclusive` | 获取一段独立 Exclusive；连续同类段也会释放后重新申请 |
| `TestExclusive` | 仅在锁处于 Idle 时尝试 Exclusive，不抢占已有 Concurrent |
| `TryExclusive` | 抢占式尝试 Exclusive，可以阻止新的 Concurrent 进入 |
| `ConvergeConcurrent` | 延续已有 Concurrent，或将 Exclusive 原地降级为 Concurrent |
| `TryApplyIDConvergeExclusive` | 尝试应用 ContextID / EpochID，并在成功后收敛到 Exclusive |

### Try Segment 的行为

Try 类型 Segment 没有获得执行条件时：

- 当前 Segment 不执行；
- Pipeline 不抛出异常；
- Pipeline 不提前结束；
- 当前权限状态视为 None；
- 后续 Segment 继续执行。

### 独立权限与收敛权限

`Concurrent` 和 `Exclusive` 表示独立权限段。

即使上一段已经持有相同权限，Pipeline 仍会先释放，再重新申请，从而为其他竞争者提供进入机会。

`ConvergeConcurrent` 表示延续或形成连续的 Concurrent 上下文。

`TryApplyIDConvergeExclusive` 表示在业务 ID 成功应用后，进入或延续 Exclusive 上下文。

---

## 同步与异步边界

Pipeline Segment 使用同步委托：

```csharp
Action Segment
```

因此 Pipeline 是一个**同步权限流程编排器**。

下面这种写法不受支持：

```csharp
ConcurrentExclusiveLockSegment.Concurrent(async () =>
{
    DoPartA();

    await SomethingAsync();

    DoPartB();
});
```

项目通过禁用的 `Func<Task>` 重载，在编译阶段拒绝直接传入异步 lambda，避免异步 lambda 被转换为 `async void` 后造成以下问题：

- Pipeline 无法知道 Segment 的异步部分何时真正结束；
- Pipeline 可能在异步后续完成前释放或转换权限；
- 异步异常无法通过 Pipeline 正常传播；
- Exclusive 基于具有线程所有权的同步机制，不能安全跨越 `await`。

**注意：**由于 C# 的重载解析规则，始终抛出异常的同步 lambda 也可能匹配到禁用的 Func<Task> 重载。此时需要显式转换为 Action：

```csharp
ConcurrentExclusiveLockSegment.Exclusive((Action)(() =>
{
    throw new Exception();
}));
```

### DoPipelineAsync

```csharp
await pipeline.DoPipelineAsync(segments);
```

`DoPipelineAsync` 的语义是：

> 使用 `Task.Run` 在线程池中完整执行一次同步 Pipeline。

它不会让 Segment 支持 `await`，也不是原生异步锁协议。

已经位于工作线程、线程池线程或服务端请求线程时，通常应直接调用同步 `DoPipeline()`。

---

## 低分配设计

CEL 面向大量细粒度锁对象和高频调用路径设计。

### 锁实例

每次调用 `ConcurrentExclusiveLock.Create()` 会创建一个内部 Token。

Token 的核心状态字段包括：

- 64-bit Counter；
- 32-bit ContextID；
- 32-bit EpochID。

核心状态字段合计为 128-bit。

这里的 128-bit 不包含 CLR 对象头、引用和内存对齐开销。

`Monitor` 直接作用于内部 Token，不额外创建独立同步对象。

### 热路径

锁实例初始化完成后：

- `ConcurrentExclusiveLock` 是值类型句柄；
- `ConcurrentExclusiveLockScope` 是 struct；
- `ConcurrentExclusiveLockPipeline` 是 readonly struct；
- Scope 的创建和释放不需要为每次进入创建对象；
- 普通 Concurrent 路径主要使用原子操作。

这使 CEL 适合：

- Unity3D `Update`；
- 游戏服务器实体循环；
- 高频状态更新；
- 严格控制 GC 的运行环境。

### 调用方仍需注意的分配

库本身的低分配设计不能消除业务代码产生的分配，例如：

- 捕获局部变量的 lambda；
- 每次动态创建委托；
- 每次通过 `params` 创建新的 Segment 数组；
- 调用 `Task.Run`；
- 业务逻辑自身创建对象。

极端热路径中，可以缓存委托和 Segment 数组，并直接调用同步 API。

---

## 状态观察

CEL 提供两个观察属性：

```csharp
ConcurrentExclusiveLockState ObservedState;
int ObservedContention;
```

### ObservedState

`ObservedState` 表示读取瞬间观察到的访问倾向或转换状态。

它不表示此刻一定已经有线程正在执行 Exclusive 业务代码。

例如，抢占式 Exclusive 已经进入竞争窗口，但仍在等待现有 Concurrent 退出时，状态也可能观察为 Exclusive。

### ObservedContention

`ObservedContention` 是读取瞬间的竞争压力观察值，用于：

- 诊断；
- 监控；
- 调度参考；
- 性能分析。

它不能作为建立同步正确性的判断条件。

纯 Concurrent 场景下，该值为 0；存在 Exclusive 压力时，才反映当前观察到的竞争规模。

---

## 适用场景

CEL 尤其适合以下模型：

- 游戏服务器中的玩家、房间、战斗和地图实体；
- Unity3D 中严格控制堆分配的状态访问；
- Actor 或类 Actor 实体；
- 会话状态和连接状态；
- 缓存条目与聚合根；
- 实体生命周期推进；
- 版本化数据更新；
- 后台任务状态机；
- 同一实体上的检查、升级、提交和回落流程；
- 大量细粒度锁对象长期并存的服务端系统。

典型流程：

```text
Concurrent 检查
    ↓
ContextID / EpochID 条件判断
    ↓
原地收敛到 Exclusive
    ↓
执行唯一提交
    ↓
降级回 Concurrent
    ↓
发布或读取新状态
```

---

## 设计边界

CEL 是一套同步、非递归的访问权限协议。

使用时应遵守以下规则：

1. 不要使用默认初始化的 `ConcurrentExclusiveLock`，必须调用 `Create()`。
2. 不要在已经持有 Concurrent 时直接调用普通 `AcquireExclusive()`，应使用升级协议。
3. 不要在已经持有 Exclusive 时直接调用普通 `AcquireConcurrent()`，应使用降级协议。
4. 不要把 Exclusive 当作递归锁。
5. 不要复制或并发操作 `ConcurrentExclusiveLockScope`。
6. 不要在 Pipeline Segment 中使用异步 lambda。
7. 不要让依赖当前权限的业务代码跨越 `await`。
8. `ObservedState` 和 `ObservedContention` 只用于观察，不用于建立同步正确性。
9. ContextID / EpochID 的业务含义和生命周期由调用方负责。
10. CEL 不承诺严格 FIFO 公平性。

这些限制是为了维持清晰的同步语义、较低的常态开销和高频路径适用性。

---

## 项目定位

ConcurrentExclusiveLock 不试图成为适用于所有问题的通用锁，也不是对传统 Reader / Writer Lock 的简单复制。

它重点解决的是：

> 在大量细粒度状态对象上，以较低常态成本表达 Concurrent / Exclusive 权限，并把抢占、升级、降级、业务 ID 收敛和连续流程编排组合为一套完整协议。

项目当前包含：

- `ConcurrentExclusiveLock`
- `ConcurrentExclusiveLockScope`
- `ConcurrentExclusiveLockPipeline`
- 完整 XML API 注释
- 同步 Segment 误用保护
- BenchmarkDotNet 性能测试
- 长时间随机调用压力测试

---

## 测试项目

仓库中的测试项目用于验证核心同步协议在不同竞争条件和权限转换路径下的行为，主要包括：

- Concurrent / Exclusive 基础获取与释放测试；
- 抢占式 Exclusive 竞争测试；
- Concurrent → Exclusive 升级测试；
- Exclusive → Concurrent 降级测试；
- ContextID / EpochID 相关协议测试；
- Pipeline 各 Segment 组合与状态转换测试；
- 随机调用压力测试；
- BenchmarkDotNet 性能测试。

**测试项目由 AI 编写。**

测试代码的作用是辅助验证当前实现、扩大路径覆盖范围并提供性能观察数据；核心同步协议、API 设计与语义定义以 C# / .NET 主项目实现为准。

## 性能测试

### 测试环境

- **操作系统**：Windows 11
- **CPU**：AMD Ryzen 7 5700X，8 核 16 线程
- **SMT**：开启
- **CPU 频率**：全核固定 4.5 GHz
- **运行时**：.NET 8.0.22
- **GC**：测试期间未发生 GC
- **工作线程**：使用独立 `Thread`，通过同一个启动门同时开始
- **工作负载**：共享内存随机访问
- **对比实现**：
  - `lock`
  - `ReaderWriterLockSlim`
  - `ConcurrentExclusiveLock`
  - `ConcurrentExclusiveLock` 的纯 Exclusive 用法

测试结果仅代表上述硬件、运行时、工作负载和测试参数下的观察结果，不构成对其他运行环境的绝对性能保证。

`avg write ns` 表示测试程序统计的平均写操作延迟。当前结果展示的是平均值，尚未包含 P95、P99、P99.9 或最大延迟，因此不应将其解释为尾延迟保证。

### 测试结论

#### 1. 单个热点锁最能展示实例内 Concurrent 并行能力

在单锁、64 个竞争线程的测试中，普通 `lock` 只能串行执行临界区，而 CEL 可以让 Concurrent 操作同时执行。

在当前内存工作负载下，CEL 相对 `lock` 的吞吐表现为：

| Concurrent / Exclusive | `lock` works/s | CEL works/s | CEL / `lock` |
|---:|---:|---:|---:|
| 100 / 0 | 657,517 | 5,928,072 | **9.02×** |
| 99.5 / 0.5 | 728,260 | 4,842,249 | **6.65×** |
| 90 / 10 | 712,098 | 2,109,201 | **2.96×** |
| 50 / 50 | 678,149 | 831,019 | **1.23×** |
| 30 / 70 | 665,893 | 723,968 | **1.09×** |
| 0 / 100 | 658,964 | 655,340 | **0.99×** |

结果呈现出自然的退化曲线：

- Concurrent 比例较高时，CEL 可以显著利用实例内并行；
- Exclusive 比例升高后，并发窗口逐渐缩小；
- 100% Exclusive 时，CEL 基本退化到普通互斥锁的吞吐水平；
- 没有 Concurrent 工作可以并行时，CEL 不会凭空产生吞吐优势。

CEL 并不适合临界区内几乎没有实际工作的极端短路径。普通 Concurrent 获取和释放仍然需要共享原子状态更新；当业务工作量小于并发协调成本时，直接串行执行可能更高效。

#### 2. 多锁场景会自然摊薄相对吞吐倍率

在 8 个锁实例、每个实例 8 个线程的测试中，普通 `lock` 本身也可以跨锁实例并行执行：

```text
Lock 1 -> 1 个临界区
Lock 2 -> 1 个临界区
...
Lock 8 -> 1 个临界区
```

因此，多锁场景下 CEL 相对普通 `lock` 的吞吐倍率会自然缩小。

| Concurrent / Exclusive | `lock` works/s | CEL works/s | CEL / `lock` |
|---:|---:|---:|---:|
| 100 / 0 | 4,290,496 | 9,932,028 | **2.31×** |
| 99.5 / 0.5 | 5,374,123 | 9,426,514 | **1.75×** |
| 90 / 10 | 5,075,562 | 6,457,895 | **1.27×** |
| 50 / 50 | 4,763,081 | 4,405,050 | **0.92×** |
| 30 / 70 | 4,589,396 | 4,379,425 | **0.95×** |
| 0 / 100 | 4,357,654 | 4,244,409 | **0.97×** |

这并不表示 CEL 的单锁并发能力下降，而是普通互斥锁也获得了实例间并行。

多锁测试中没有观察到随着锁实例增加而产生的结构性吞吐崩塌。这说明单锁测试中的高吞吐并不是通过全局自旋、持续抢占整机资源或锁实例之间相互干扰获得的。

需要注意的是，每个锁实例拥有独立的工作对象。因此，本组测试中每个实例使用 64 MiB 工作集，总工作集为 512 MiB。

#### 3. 吞吐倍率被摊薄，不代表写延迟优势被摊薄

吞吐反映整台机器在一段时间内完成的工作总量，会受到锁实例数量、CPU 核心数、内存带宽和业务工作量影响。

写延迟反映的是一个具体写请求在目标锁上等待权限收敛所需的时间，主要由该锁自身的状态转换和竞争模型决定。

在单个热点锁、99.5% Concurrent 的稀疏写场景中：

| 实现 | 平均写延迟 |
|---|---:|
| `lock` | 1,856,481 ns |
| `ReaderWriterLockSlim` | 1,356,004 ns |
| CEL | **16,300 ns** |

CEL 的平均写延迟约为：

- 普通 `lock` 的 **1/114**；
- `ReaderWriterLockSlim` 的 **1/83**。

单锁下的完整平均写延迟对比如下：

| Concurrent / Exclusive | `lock` | `ReaderWriterLockSlim` | CEL |
|---:|---:|---:|---:|
| 99.5 / 0.5 | 1,856.5 μs | 1,356.0 μs | **16.3 μs** |
| 90 / 10 | 321.2 μs | 263.7 μs | **33.6 μs** |
| 50 / 50 | 117.8 μs | 155.7 μs | **73.1 μs** |
| 30 / 70 | 99.9 μs | 124.1 μs | **75.4 μs** |
| 0 / 100 | 94.1 μs | 105.0 μs | **94.6 μs** |

当 Concurrent 比例较高时，CEL 的抢占式 Exclusive 会阻止新的 Concurrent 继续进入，只等待已经存在的 Concurrent 自然退出。

因此，写者面对的是一个已经封闭并持续缩小的等待集合，而不需要等待持续到达的新 Concurrent 流量偶然完全停止。

随着 Exclusive 比例升高，CEL 的写延迟逐渐接近普通互斥锁；在 100% Exclusive 时，CEL、CEL(ExclusiveOnly) 与普通 `lock` 基本处于同一水平。

#### 4. 多锁场景中，CEL 仍然保持较低的平均写延迟

8 个锁实例、每锁 8 个线程时：

| Concurrent / Exclusive | `lock` | `ReaderWriterLockSlim` | CEL |
|---:|---:|---:|---:|
| 99.5 / 0.5 | 144.9 μs | 949.1 μs | **54.9 μs** |
| 90 / 10 | 35.1 μs | 81.2 μs | **10.6 μs** |
| 50 / 50 | 16.2 μs | 22.6 μs | **6.0 μs** |
| 30 / 70 | 14.5 μs | 17.3 μs | **5.1 μs** |
| 0 / 100 | 14.2 μs | 15.0 μs | **14.6 μs** |

在 90/10、50/50 和 30/70 场景中，即使几种锁的总吞吐已经逐渐接近，CEL 的平均写延迟仍明显较低。

这说明：

> 多锁摊薄的是 CEL 的相对吞吐倍率，而不是单次权限收敛和交接效率。

单锁测试主要展示 CEL 的实例内并发上限；多锁测试验证大量独立锁实例同时运行时不会出现明显的全局退化；平均写延迟则更直接地展示抢占式 Exclusive 和权限收敛模型的效果。

### 完整测试结果

<details>
<summary><strong>单锁：1 个锁实例，64 个线程，64 MiB 共享内存，64 个工作步骤</strong></summary>

```text
F:\Projects\ConcurrentExclusiveLock\csharp\TestAndBenchmark\bin\Release\net8.0>TestAndBenchmark.exe --lock-instances 1 --threads 64 --workload memory --operations 10000 --memory-mb 64 --read-work 64 --write-work 64
Lock benchmark
.NET=8.0.22, OS=Microsoft Windows NT 10.0.26200.0
GC=False, CPU=16

lock-instances=1, threads/lock=64, total-threads=64, works/thread=10,000, read-steps=64, write-steps=64
workload=memory (64 MiB shared, read-steps=64, write-steps=64)
Workers use dedicated Thread instances and start from a common gate.
Each lock instance owns a fresh IWork; all worker groups share one start gate.

Scenario: read/write 100/0
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.973s      10.0%        657517        657517        65536       640,000             0           0.0  0000000000000000
  ReaderWriterLockSlim          0.141s      94.5%       4553332       4553332        48188       640,000             0           0.0  0000000000000000
  CEL                           0.108s      97.7%       5928072       5928072        60681       640,000             0           0.0  0000000000000000
  CEL(ExclusiveOnly)            0.899s      11.3%        711688        711688        63015       640,000             0           0.0  0000000000000000

Scenario: read/write 99.5/0.5
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.879s      11.0%        728260        728260        66198       636,838         3,162     1856481.2  8398C18E7F9AA0EB
  ReaderWriterLockSlim          0.220s      96.6%       2904039       2904039        30062       636,838         3,162     1356004.3  8398C18E7F9AA0EB
  CEL                           0.132s      69.5%       4842249       4842249        69719       636,838         3,162       16299.5  8398C18E7F9AA0EB
  CEL(ExclusiveOnly)            0.901s      10.4%        710583        710583        68267       636,838         3,162     1567402.2  8398C18E7F9AA0EB

Scenario: read/write 90/10
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.899s      11.0%        712098        712098        64887       576,034        63,966      321241.7  4304798A1CB10952
  ReaderWriterLockSlim          0.540s      55.7%       1186134       1186134        21278       576,034        63,966      263729.1  4304798A1CB10952
  CEL                           0.303s      44.4%       2109201       2109201        47490       576,034        63,966       33600.9  4304798A1CB10952
  CEL(ExclusiveOnly)            0.915s      11.0%        699329        699329        63627       576,034        63,966      288658.3  4304798A1CB10952

Scenario: read/write 50/50
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.944s      11.2%        678149        678149        60681       320,007       319,993      117790.0  7F3AA8C4A6F5CFA7
  ReaderWriterLockSlim          1.413s      50.9%        452787        452787         8892       320,007       319,993      155729.2  7F3AA8C4A6F5CFA7
  CEL                           0.770s      18.9%        831019        831019        43984       320,007       319,993       73086.6  7F3AA8C4A6F5CFA7
  CEL(ExclusiveOnly)            0.947s      10.6%        675557        675557        63627       320,007       319,993      114950.5  7F3AA8C4A6F5CFA7

Scenario: read/write 30/70
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.961s      10.8%        665893        665893        61826       191,321       448,679       99941.6  984C4BC0324B2349
  ReaderWriterLockSlim          1.578s      49.8%        405680        405680         8141       191,321       448,679      124113.9  984C4BC0324B2349
  CEL                           0.884s      15.2%        723968        723968        47490       191,321       448,679       75431.8  984C4BC0324B2349
  CEL(ExclusiveOnly)            0.966s      10.8%        662768        662768        61249       191,321       448,679       99725.6  984C4BC0324B2349

Scenario: read/write 0/100
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.971s      11.4%        658964        658964        57996             0       640,000       94123.9  9C619B979129B421
  ReaderWriterLockSlim          1.101s      26.3%        581326        581326        22066             0       640,000      105048.3  9C619B979129B421
  CEL                           0.977s      11.9%        655340        655340        55072             0       640,000       94603.5  9C619B979129B421
  CEL(ExclusiveOnly)            0.971s      11.2%        659281        659281        59041             0       640,000       94419.0  9C619B979129B421

sink=3007092141684130081
```

</details>

<details>
<summary><strong>多锁：8 个锁实例，每锁 8 个线程，每实例 64 MiB，32 个工作步骤</strong></summary>

```text
F:\Projects\ConcurrentExclusiveLock\csharp\TestAndBenchmark\bin\Release\net8.0>TestAndBenchmark.exe --lock-instances 8 --threads 8 --workload memory --operations 10000 --memory-mb 64 --read-work 32 --write-work 32
Lock benchmark
.NET=8.0.22, OS=Microsoft Windows NT 10.0.26200.0
GC=False, CPU=16

lock-instances=8, threads/lock=8, total-threads=64, works/thread=10,000, read-steps=32, write-steps=32
workload=memory (64 MiB shared, read-steps=32, write-steps=32)
Workers use dedicated Thread instances and start from a common gate.
Each lock instance owns a fresh IWork; all worker groups share one start gate.

Scenario: read/write 100/0
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.149s      87.7%       4290496        536312        48907       640,000             0           0.0  A57B1FF0E740A896
  ReaderWriterLockSlim          0.073s      94.1%       8810160       1101270        93623       640,000             0           0.0  A57B1FF0E740A896
  CEL                           0.064s      92.4%       9932028       1241503       107436       640,000             0           0.0  A57B1FF0E740A896
  CEL(ExclusiveOnly)            0.122s      86.8%       5266545        658318        60681       640,000             0           0.0  A57B1FF0E740A896

Scenario: read/write 99.5/0.5
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.119s      94.3%       5374123        671765        56988       636,726         3,274      144936.1  5FFADA82B8F7C3C6
  ReaderWriterLockSlim          0.078s      97.3%       8173388       1021674        84021       636,726         3,274      949142.2  5FFADA82B8F7C3C6
  CEL                           0.068s      86.3%       9426514       1178314       109227       636,726         3,274       54855.1  5FFADA82B8F7C3C6
  CEL(ExclusiveOnly)            0.122s      89.7%       5250588        656324        58514       636,726         3,274       93040.6  5FFADA82B8F7C3C6

Scenario: read/write 90/10
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.126s      81.3%       5075562        634445        62415       575,901        64,099       35149.4  7F8372CDB19E8250
  ReaderWriterLockSlim          0.107s      94.3%       6001664        750208        63627       575,901        64,099       81179.8  7F8372CDB19E8250
  CEL                           0.099s      92.6%       6457895        807237        69719       575,901        64,099       10565.8  7F8372CDB19E8250
  CEL(ExclusiveOnly)            0.128s      78.3%       4985029        623129        63627       575,901        64,099       26110.0  7F8372CDB19E8250

Scenario: read/write 50/50
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.134s      89.4%       4763081        595385        53281       320,069       319,931       16227.2  77DA7C15C44409F7
  ReaderWriterLockSlim          0.135s      95.2%       4726561        590820        49648       320,069       319,931       22625.6  77DA7C15C44409F7
  CEL                           0.145s      94.8%       4405050        550631        46479       320,069       319,931        6043.6  77DA7C15C44409F7
  CEL(ExclusiveOnly)            0.136s      86.5%       4721501        590188        54613       320,069       319,931       14954.3  77DA7C15C44409F7

Scenario: read/write 30/70
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.139s      84.7%       4589396        573675        54162       191,782       448,218       14530.9  E4C312562E36CC29
  ReaderWriterLockSlim          0.144s      93.4%       4433969        554246        47490       191,782       448,218       17349.8  E4C312562E36CC29
  CEL                           0.146s      88.2%       4379425        547428        49648       191,782       448,218        5116.5  E4C312562E36CC29
  CEL(ExclusiveOnly)            0.141s      87.9%       4536514        567064        51603       191,782       448,218       14007.9  E4C312562E36CC29

Scenario: read/write 0/100
  lock type                    elapsed       cpu%       works/s  works/s/lock    work/cpu%         reads        writes  avg write ns             state
  lock                          0.147s     100.4%       4357654        544707        43401             0       640,000       14154.6  4CD28C3524A9BA6F
  ReaderWriterLockSlim          0.161s      92.2%       3975301        496913        43116             0       640,000       15026.7  4CD28C3524A9BA6F
  CEL                           0.151s      80.3%       4244409        530551        52852             0       640,000       14633.0  4CD28C3524A9BA6F
  CEL(ExclusiveOnly)            0.148s      77.5%       4338768        542346        56014             0       640,000       14312.1  4CD28C3524A9BA6F

sink=4320303262889978983
```

</details>

---

## 项目状态

Pipeline 已完成约 **240 小时随机调用压力测试**。

当前版本以 C# / .NET 实现为语义基准。其他语言版本应以该实现的协议语义为参考，而不应仅进行机械语法翻译。

---

## 项目信息

- **项目名称**：ConcurrentExclusiveLock
- **简称**：CEL
- **作者**：王弈博（YiBoWang）
- **原始实现**：C# / .NET
- **兼容目标**：.NET 8.0、.NET Standard 2.1
- **适用环境**：.NET、Unity3D、游戏服务器及其他细粒度状态系统
- **GitHub**：<https://github.com/WangHHB/ConcurrentExclusiveLock>

---

> A compact, high-performance Concurrent/Exclusive synchronization protocol for entity-level state objects, featuring preemptive Exclusive access, in-place upgrade/downgrade, and ContextID/EpochID support.
