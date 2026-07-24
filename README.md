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

项目当前以 **C# / .NET** 实现为原始和权威版本，目标兼容 **.NET Standard 2.1**，并兼顾 Unity3D 热路径对堆分配和 GC 的严格要求。

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
