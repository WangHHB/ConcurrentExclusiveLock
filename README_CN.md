<p align="center">
  <a href="README.md">English</a> ｜ <strong>简体中文</strong>
</p>

# ConcurrentExclusiveLock

[![C# Build and Test](https://github.com/WangHHB/ConcurrentExclusiveLock/actions/workflows/dotnet.yml/badge.svg)](https://github.com/WangHHB/ConcurrentExclusiveLock/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/ConcurrentExclusiveLock.svg)](https://www.nuget.org/packages/ConcurrentExclusiveLock/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ConcurrentExclusiveLock.svg)](https://www.nuget.org/packages/ConcurrentExclusiveLock/)
[![License](https://img.shields.io/badge/License-MIT%20OR%20Apache--2.0-blue.svg)](#许可证)

**ConcurrentExclusiveLock（CEL） 是一套面向细粒度状态对象的 Concurrent / Exclusive 同步协议。所有已实现的语言版本均以 C# 版本为语义基准，完整提供抢占式 Exclusive、Concurrent 原地升级、Exclusive 原地降级，以及核心锁、Scope 和 Pipeline 三层封装；其中 Pipeline 可在不中断同步上下文的情况下，统一编排多阶段读写、升降级、条件收敛与异常释放，避免复杂并发控制逻辑分散在业务代码中。

据目前公开可查的实现，CEL 是唯一能够在不区分特殊读写模式、无需提前声明升级意图、也无需预先申请升级权限的情况下，直接完成原地升级与原地降级的读写同步实现；其升降级路径设计简洁而巧妙，以极少的状态转换完成了连续、对称且高效的权限切换。

所有语言版本均经过性能基准测试和长期压力测试。C# 参考实现还完成了覆盖单核、SMT 开关、4 vCPU 虚拟机以及双路 52 核 / 104 线程 Windows 与 Linux 环境的统一正式矩阵。结果显示：在存在真实 Concurrent 并行、Exclusive 及时性或频繁权限收敛的场景中，CEL 能稳定领先传统实现；在写密集和纯 Exclusive 等非优势场景中，通常仍保持互斥锁级别的性能，没有出现结构性退化。**


## 语言实现

- [C#](./csharp) — 参考实现
- [Java](./java/README_CN.md) — 支持 Java 17+，已发布到 [Maven Central](https://central.sonatype.com/artifact/io.github.wanghhb/concurrent-exclusive-lock)
- [C++](./cpp/README_CN.md) — 核心锁使用 C 实现，C++ 提供 Scope 和 Pipeline 封装。
- [Rust](./rust/README_CN.md) — 已发布至 [crates.io](https://crates.io/crates/concurrent-exclusive-lock)。


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
ConcurrentToExclusive();
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

ConcurrentToExclusive();
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

### Concurrent 直接升级

```csharp
public void ExecuteCommand(PlayerCommand command)
{
    using (var scope = new ConcurrentExclusiveLockScope(_locker))
    {
        scope.AcquireConcurrent();

        if (!CanPrepareCommand(command))
        {
            scope.ReleaseConcurrent();
            return;
        }

        PreparedCommand prepared = PrepareCommand(command);

        scope.ConcurrentToExclusive();

        if (CanCommitCommand(prepared))
        {
            CommitCommand(prepared);
        }

        //当前最终持有的是 Exclusive，可以手动释放，也可以让 scope 在 Dispose 时自动释放。
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
| ConvergeConcurrent | 延续现有 Concurrent 权限，在可能的情况下将 Exclusive 原地降级为 Concurrent，或获取 Concurrent 权限 |
| ConvergeExclusive | 延续现有 Exclusive 权限，将 Concurrent 原地升级为 Exclusive，或获取 Exclusive 权限 |
| TryApplyIDConvergeExclusive | 尝试应用 ContextID / EpochID，并在成功后收敛为 Exclusive 权限 |

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

ConvergeConcurrent 表示延续现有的 Concurrent 上下文、尝试通过将 Exclusive 上下文原地降级来建立 Concurrent 上下文，或新获取一个 Concurrent 上下文。

ConvergeExclusive 表示延续现有的 Exclusive 上下文、通过将 Concurrent 上下文原地升级来建立 Exclusive 上下文，或新获取一个 Exclusive 上下文。

TryApplyIDConvergeExclusive 表示在业务 ID 成功应用后，延续现有的 Exclusive 上下文、建立 Exclusive 上下文，或新获取一个 Exclusive 上下文。

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

仓库中的测试项目用于验证核心同步协议在不同竞争条件、权限转换路径和硬件拓扑下的行为，主要包括：

- Concurrent / Exclusive 基础获取、释放与状态一致性测试；
- 抢占式 Exclusive 与固定 Concurrent 洪流下的推进测试；
- Concurrent → Exclusive 原地升级与 Exclusive → Concurrent 原地降级测试；
- 普通 Exclusive 与批量升级链的竞争顺序测试；
- ContextID / EpochID 相关协议测试；
- Pipeline 各 Segment 组合、异常释放与随机语义测试；
- 单锁 / 多锁吞吐与纯权限获取延迟测试；
- Pipeline converge、Core handoff、RWLS handoff 与串行基线的阶段化性能对比；
- 单核、SMT 开关、虚拟机、Windows / Linux 和双路 NUMA 的统一跨机器矩阵；
- 长时间随机调用压力测试与 BenchmarkDotNet 性能测试。

所有命令行拓扑和工作量参数均按字面执行，测试程序只记录硬件与运行时信息，不会根据检测到的核心数自动改变线程、锁实例或操作量。

**测试项目由 AI 编写。**

测试代码的作用是辅助验证当前实现、扩大路径覆盖范围并提供性能观察数据；核心同步协议、API 设计与语义定义以 C# / .NET 主项目实现为准。


## 性能测试

当前性能章节使用统一的跨机器正式矩阵，替代此前只基于单台 Windows 11 工作机的历史快照。

正式矩阵同时验证：

- 语义正确性；
- 混合 Concurrent / Exclusive 吞吐；
- 纯权限获取延迟及尾延迟；
- 固定 Concurrent 工作量下的 Exclusive 推进能力；
- `Concurrent → Exclusive → Concurrent` 多阶段流程；
- 大规模原地升级与普通 Exclusive 的竞争顺序。

完整命令、指标定义、输出字段和历史快照见 [C# 测试与基准指南](./csharp/TestAndBenchmark/README.md)。

### 测试矩阵

| 环境 | 操作系统 / 运行时 | 可见处理器 | 主要用途 |
|---|---|---:|---|
| AMD Ryzen 7 5700X，BIOS 仅启用 1 个核心 | Windows 11 / .NET 8.0.22 | 1 | 单核退化与固定开销基线 |
| AMD Ryzen 7 5700X，SMT 关闭，固定 4.5 GHz | Windows 11 / .NET 8.0.22 | 8 | 物理核心竞争 |
| AMD Ryzen 7 5700X，SMT 开启，固定 4.5 GHz | Windows 11 / .NET 8.0.22 | 16 | 主工作机参考结果 |
| AMD EPYC 9V74 云虚拟机 | Debian 13 / .NET 8.0.29 | 4 vCPU | 超额订阅和 Linux 虚拟化 |
| Intel Platinum 8269CY，2 路 52 核 104 线程 | Ubuntu 26.04 / .NET 8.0.29 | 104 | 双路 NUMA / Linux |
| Intel Platinum 8269CY，2 路 52 核 104 线程 | Windows Server 2025 / .NET 8.0.29 | 104 | 同硬件跨操作系统对照 |

每个环境均输出一份包含 **94 条记录、14 个实验**的 JSONL。所有正式矩阵的 correctness 模式均以 `exitCode = 0` 完成。

### 统一参数与测量边界

吞吐测试使用两种固定拓扑：

- `1×64`：1 个热点锁，64 个工作线程；
- `8×8`：8 个独立锁，每锁 8 个工作线程。

两种拓扑均执行总计 6,400,000 次操作，使用每锁 8 MiB 共享内存、64 个 Concurrent 工作步骤和 64 个 Exclusive 工作步骤。命令行中的线程数、锁实例数、工作量和操作次数均按字面执行，不会根据机器核心数自动缩放。

`throughput` 中的 `averageExclusiveOperationNs` 是一次完整 Exclusive 操作的计时，包括获取、受保护工作、释放及计时开销；它不是纯获取延迟。

`latency` 模式专门测量权限获取时间。每次获取都会被计时，`--latency-sample-every 10` 仅控制确定性样本保留；样本选择与 Concurrent / Exclusive 权限选择使用独立随机状态，并在权限释放后完成，不改变被测竞争过程。

以下结果均为对应环境的一次正式矩阵运行，用于展示可重复的性能形态，不构成对所有硬件和负载的绝对保证。跨硬件时应比较趋势和相对倍率，而不是直接比较绝对吞吐。

### 1. 5700X SMT 开启：单个热点锁

`1×64` 最直接地展示单个锁实例内部的 Concurrent 并行能力。

| Concurrent / Exclusive | `lock` works/s | `ReaderWriterLockSlim` works/s | CEL works/s | CEL / `lock` | CEL / RWLS |
|---:|---:|---:|---:|---:|---:|
| 100 / 0 | 2,589,515 | 6,660,960 | **11,083,470** | **4.28×** | **1.66×** |
| 99.5 / 0.5 | 2,682,341 | 5,094,422 | **12,337,668** | **4.60×** | **2.42×** |
| 90 / 10 | 2,392,583 | 2,107,590 | **5,392,722** | **2.25×** | **2.56×** |
| 50 / 50 | 1,982,155 | 1,050,757 | **2,178,160** | **1.10×** | **2.07×** |
| 30 / 70 | 1,855,687 | 892,264 | **1,923,724** | **1.04×** | **2.16×** |
| 0 / 100 | 1,688,922 | 1,082,263 | **1,704,085** | **1.01×** | **1.57×** |

这组结果呈现出清晰的退化曲线：

- Concurrent 比例较高时，CEL 同时领先普通 `lock` 和 `ReaderWriterLockSlim`；
- 在 90/10 时，CEL 吞吐是 `lock` 的 **2.25×**、RWLS 的 **2.56×**；
- 即使进入 50/50 和 30/70，CEL 仍保持 `lock` 级别吞吐，并约为 RWLS 的两倍；
- 100% Exclusive 时，CEL 为 `lock` 的 **1.01×**，说明丰富的 Concurrent、抢占、升级和降级语义没有带来明显的纯串行路径税。

### 2. 5700X SMT 开启：8 个独立锁

`8×8` 中，普通互斥锁也能跨锁实例并行，因此 CEL 相对 `lock` 的倍率会自然缩小。

| Concurrent / Exclusive | `lock` works/s | `ReaderWriterLockSlim` works/s | CEL works/s | CEL / `lock` | CEL / RWLS |
|---:|---:|---:|---:|---:|---:|
| 100 / 0 | 3,563,840 | 10,388,661 | **12,432,672** | **3.49×** | **1.20×** |
| 99.5 / 0.5 | 3,642,434 | 7,004,578 | **14,775,721** | **4.06×** | **2.11×** |
| 90 / 10 | 3,528,619 | 4,423,317 | **5,494,419** | **1.56×** | **1.24×** |
| 50 / 50 | 3,257,390 | 3,250,963 | **3,294,069** | **1.01×** | **1.01×** |
| 30 / 70 | 3,118,793 | 3,025,585 | **3,270,777** | **1.05×** | **1.08×** |
| 0 / 100 | 3,076,909 | 2,752,932 | **3,048,696** | **0.99×** | **1.11×** |

多锁场景说明：

- CEL 的单锁优势不是依赖全局自旋或全局互斥获得的；
- 99.5/0.5 时，CEL 仍达到 RWLS 的 **2.11×**；
- 50/50 至 100% Exclusive 时，CEL 与 `lock` 的差异保持在约 1% 至 5% 内；
- 多锁摊薄的是相对吞吐倍率，而不是 CEL 的单实例权限收敛能力。

### 3. 跨拓扑结果：优势只在存在真实并行和竞争时展开

下表用 90/10 混合负载观察 CEL 相对 RWLS 的扩展性，并用 100% Exclusive 观察 CEL 相对普通 `lock` 的串行路径成本。

| 环境 | 1×64，90/10 CEL / RWLS | 8×8，90/10 CEL / RWLS | 1×64，0/100 CEL / `lock` |
|---|---:|---:|---:|
| 5700X 单核 | **0.99×** | **1.05×** | 0.97× |
| 5700X SMT 关闭 | **2.34×** | **1.49×** | 1.01× |
| 5700X SMT 开启 | **2.56×** | **1.24×** | 1.01× |
| EPYC 4 vCPU / Debian | **12.20×** | **2.69×** | 1.04× |
| 8269CY / Ubuntu | **9.89×** | **11.82×** | 0.99× |
| 8269CY / Windows Server | **2.88×** | **1.76×** | 0.82× |

单核配置中，CEL 与 RWLS 基本持平；没有可利用的并行资源时，CEL 不会凭空得到吞吐优势。

进入真实多核、虚拟化或双路 NUMA 环境后，混合竞争中的差距开始展开：

- 4 vCPU Debian 虚拟机的 `1×64` 90/10 中，CEL 为 RWLS 的 **12.20×**；
- 双路 Ubuntu 的 `1×64` 和 `8×8` 90/10 中，CEL 分别为 RWLS 的 **9.89×** 和 **11.82×**；
- Windows Server 2025 明显改善了 RWLS 的表现，但 CEL 在同一双路硬件上仍分别领先 **2.88×** 和 **1.76×**。

在 `1×64` 100% Exclusive 中，CEL 相对 `lock` 落在 **0.82× 至 1.04×**。这说明 CEL 的优势不是以写密集场景发生结构性崩塌为代价；最弱结果出现在双路 Windows Server，而其他多核环境均与普通互斥锁接近或持平。

### 4. 同一台双路机器上的 Ubuntu / Windows Server 对照

双路 8269CY 的两份结果使用相同硬件、相同 .NET 运行时版本和相同测试参数，因此可以观察操作系统同步与调度路径的影响。

对 12 个吞吐场景的 `Windows / Ubuntu` 比值取几何平均：

| 实现 | Windows / Ubuntu 几何平均 | 最低 | 最高 |
|---|---:|---:|---:|
| `lock` | **1.458×** | 0.771× | 2.632× |
| RWLS | **2.227×** | 0.699× | 6.985× |
| **CEL** | **1.019×** | 0.637× | 1.939× |

代表性绝对吞吐如下：

| 场景 | Ubuntu RWLS | Ubuntu CEL | Windows RWLS | Windows CEL |
|---|---:|---:|---:|---:|
| 1×64，100/0 | 1.500 M/s | **7.959 M/s** | 1.233 M/s | **7.957 M/s** |
| 1×64，90/10 | 0.203 M/s | **2.002 M/s** | 0.559 M/s | **1.613 M/s** |
| 8×8，90/10 | 0.667 M/s | **7.882 M/s** | 4.659 M/s | **8.184 M/s** |
| 8×8，50/50 | 0.420 M/s | **3.152 M/s** | 2.095 M/s | **3.235 M/s** |

最值得注意的是：

- `1×64` 纯 Concurrent 下，两套系统的 CEL 吞吐仅相差约 **0.02%**；
- `8×8` 90/10 下，两套系统的 CEL 仅相差约 **3.8%**；
- 同一场景中，RWLS 在 Windows 与 Ubuntu 之间相差约 **6.99×**；
- Ubuntu 上 RWLS 从 `8×8` 100/0 的 10.560 M/s 降至 90/10 的 0.667 M/s，writer 介入后吞吐下降约 **93.7%**。

这组对照不表示 CEL 在所有操作系统上都必然具有相同绝对速度，但说明其关键性能形态主要由协议自身决定；RWLS 则更容易受到操作系统等待、唤醒、调度和跨 NUMA 协调路径影响。

### 5. Exclusive 获取延迟

`latency` 测试使用 90/10 混合负载，表中数值是 Exclusive **纯获取时间的平均值**，不包含 Exclusive 工作和释放。

| 环境 | 1×64 RWLS | 1×64 CEL | 改善 | 8×8 RWLS | 8×8 CEL | 改善 |
|---|---:|---:|---:|---:|---:|---:|
| 5700X SMT 关闭 | 228.0 μs | **12.4 μs** | **18.41×** | 70.4 μs | **8.1 μs** | **8.73×** |
| 5700X SMT 开启 | 177.4 μs | **11.9 μs** | **14.97×** | 121.3 μs | **14.9 μs** | **8.12×** |
| EPYC 4 vCPU / Debian | 342.5 μs | **18.8 μs** | **18.25×** | 173.0 μs | **39.3 μs** | **4.40×** |
| 8269CY / Ubuntu | 160.0 μs | **33.0 μs** | **4.84×** | 151.7 μs | **7.6 μs** | **20.03×** |
| 8269CY / Windows Server | 60.6 μs | **51.9 μs** | **1.17×** | 25.9 μs | **7.6 μs** | **3.40×** |

除单核退化基线外，CEL 在所有多核环境和两种拓扑中均降低了平均 Exclusive 获取时间。

部分代表性 p99：

- 5700X SMT 关闭，`1×64`：RWLS 1,240.9 μs，CEL **10.0 μs**；
- 4 vCPU Debian，`1×64`：RWLS 2,583.4 μs，CEL **3.81 μs**；
- 双路 Ubuntu，`8×8`：RWLS 1,568.8 μs，CEL **144.0 μs**；
- 双路 Windows Server，`8×8`：RWLS 102.1 μs，CEL **80.2 μs**。

延迟分布中可能存在少量极端调度长尾，因此 p99 不能脱离 mean、p99.9 和 max 单独解释。完整分位数保存在原始 JSONL 中。

### 6. 固定 Concurrent 工作量下的 Exclusive Progress

`exclusive-progress` 不测“谁在更长运行时间里循环得更多”，而是在每个实现都完成固定数量 Concurrent 操作的同时，统计 Exclusive 实际完成次数。

每个锁拥有一个持续申请 Exclusive 的 writer。每次 Exclusive 完成后，该 writer 必须等待同一锁至少出现一次新的 Concurrent 完成，才能再次申请 Exclusive，避免 writer 进入次数通过延长测试时间自我放大。

| 环境 | 1×64：RWLS → CEL | CEL / RWLS | 8×8：RWLS → CEL | CEL / RWLS |
|---|---:|---:|---:|---:|
| 5700X SMT 关闭 | 39 → **23,640** | **606.15×** | 2,513 → **68,519** | **27.27×** |
| 5700X SMT 开启 | 709 → **80,506** | **113.55×** | 1,803 → **95,370** | **52.90×** |
| EPYC 4 vCPU / Debian | 1,524 → **51,890** | **34.05×** | 9,054 → **274,774** | **30.35×** |
| 8269CY / Ubuntu | 14,774 → **519,254** | **35.15×** | 245,361 → **874,909** | **3.57×** |
| 8269CY / Windows Server | 12,424 → **527,689** | **42.47×** | 181,427 → **654,287** | **3.61×** |

除没有真实并行性的单核配置外，CEL 在所有多核环境中都显著提高了 Exclusive 推进次数。

该模式用于观察固定 Concurrent 洪流下的推进能力，不等同于严格 FIFO 公平性证明。短时间运行中的每锁 min / max 仍会受到线程调度影响，因此首页使用总 Exclusive entries 作为主要指标。

### 7. Pipeline：原地收敛与释放后重新申请

Pipeline 性能测试使用固定的三阶段流程：

```text
Concurrent prepare(128)
    → Exclusive commit(16)
    → Concurrent post(128)
```

`CEL Pipeline converge` 保持同一同步上下文并原地升级 / 降级；`CEL Core handoff` 和 `RWLS handoff` 则在阶段之间释放并重新申请权限。

| 环境 | 1×64 Pipeline / Core handoff | 1×64 Pipeline / RWLS | 8×8 Pipeline / Core handoff | 8×8 Pipeline / RWLS |
|---|---:|---:|---:|---:|
| 5700X 单核 | 0.90× | **1.00×** | 0.86× | **0.94×** |
| 5700X SMT 关闭 | 1.08× | **2.15×** | 1.41× | **1.51×** |
| 5700X SMT 开启 | 1.07× | **2.54×** | 1.22× | **1.72×** |
| EPYC 4 vCPU / Debian | 0.91× | **4.00×** | 0.76× | **1.47×** |
| 8269CY / Ubuntu | 1.75× | **7.09×** | 1.89× | **3.48×** |
| 8269CY / Windows Server | 1.72× | **4.41×** | 1.64× | **2.15×** |

结果表明：

- 在 5700X 和双路 8269CY 的真实多核环境中，Pipeline converge 同时领先 CEL Core handoff 和 RWLS handoff；
- 双路 Ubuntu 的 `1×64` 中，Pipeline 为 Core handoff 的 **1.75×**、RWLS handoff 的 **7.09×**；
- 双路 Windows Server 的 `1×64` 中，对应倍率为 **1.72×** 和 **4.41×**；
- 单核和仅有 4 vCPU、64 工作线程的超额订阅环境中，原地保持权限上下文不一定比释放后重新调度更快，这构成了清晰的适用边界；
- `Monitor serialized` 可以作为串行化上界对照，但它不提供阶段间 Concurrent 并行和连续权限语义，因此不是 Pipeline 的等价替代。

### 8. 正确性与升级竞争

六套正式矩阵的 correctness 模式全部成功完成。

升级竞争测试覆盖：

- 1 个锁，64 个升级线程，0 个普通 Exclusive；
- 1 个锁，64 个升级线程，16 个普通 Exclusive；
- 8 个锁，每锁 8 个升级线程，0 个普通 Exclusive；
- 8 个锁，每锁 8 个升级线程，4 个普通 Exclusive。

六个环境、共 24 组升级竞争结果全部满足：

```text
ordinaryEnteredBeforeUpgradeDrain = 0
```

即在这些测试中，普通 Exclusive 没有插入尚未排空的升级链。该结果验证了当前实现的升级优先顺序，但不应被扩大解释为对所有操作系统调度顺序的严格 FIFO 保证。

### 完整测试结果

首页只保留能够解释主要性能形态的表格，避免将几十组原始输出直接铺开。

完整内容包括：

- 所有正式矩阵命令；
- 各指标的精确定义；
- JSONL 字段说明；
- latency 的 mean / p50 / p95 / p99 / p99.9 / max；
- Exclusive Progress 的每锁明细；
- Pipeline 各策略的绝对吞吐；
- 历史单机测试快照。

请参阅 [C# 测试与基准指南](./csharp/TestAndBenchmark/README.md)。

<a href="CEL_Intel Platinum 8269CY @ 2.50GHz, 2 Sockets 52 Cores 104 Threads, Ubuntu 26.04.md">双路 Intel Xeon Platinum 8269CY（52 核 104 线程，Ubuntu 26.04）完整基准测试报告</a>

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

## 许可证

ConcurrentExclusiveLock 采用 MIT License 或 Apache License 2.0 双重许可，您可以自行选择其中任意一种许可证。
详细信息请参阅 [`LICENSE-MIT`](LICENSE-MIT) 和 [`LICENSE-APACHE-2.0`](LICENSE-APACHE-2.0)。

---

> A compact, high-performance Concurrent/Exclusive synchronization protocol for entity-level state objects, featuring preemptive Exclusive access, in-place upgrade/downgrade, and ContextID/EpochID support.
