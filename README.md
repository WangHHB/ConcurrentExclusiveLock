ConcurrentExclusiveLock

ConcurrentExclusiveLock（CEL） 是一个面向细粒度状态对象的高性能 Concurrent / Exclusive 同步协议。

它适合为每个玩家、房间、实体、会话、Actor、聚合根或任务上下文分别配置一把锁，在大量锁对象并存的情况下，以较低的常态开销协调并发访问、排他访问、权限转换以及业务阶段收敛。

项目的核心能力包括：

抢占式 Exclusive；
Concurrent 原地升级为 Exclusive；
Exclusive 原地降级为 Concurrent；
ContextID / EpochID 业务状态协同；
Scope 自动释放；
基于权限段的 Pipeline 流程编排；
状态与竞争压力观察；
面向 Unity3D 和高频服务端路径的低分配设计。

当前以 C# / .NET 实现为原始和权威版本，目标兼容 .NET Standard 2.1。

一、它表达的是访问权限，而不是读写意图

CEL 没有使用传统的 Read / Write 命名，而是使用：

Concurrent：当前操作允许与其他 Concurrent 操作同时执行；
Exclusive：当前操作必须独占执行，不能与任何其他操作并发。

这两个概念描述的是：

当前业务代码是否允许并发进入。

它们并不描述代码内部究竟是在读取还是修改数据。

因此：

Exclusive 区域中完全可以包含大量读取逻辑；
Concurrent 区域中也可以执行由业务规则保证互不冲突的修改；
访问权限的选择由业务并发模型决定，而不是由“读”和“写”两个字决定。

这使 CEL 更适合实体状态、游戏逻辑、业务流程和阶段转换，而不仅仅是保护普通集合或数据字段。

二、抢占式 Exclusive

CEL 最核心的特征是 抢占式 Exclusive。

普通 Concurrent 获取和释放主要通过轻量原子计数完成，不进入 Monitor 排序队列。

当 Exclusive 请求进入竞争窗口后：

阻止新的 Concurrent 继续进入；
等待已经进入的 Concurrent 自然退出；
在 Concurrent 排空后获得 Exclusive；
Exclusive 完成后重新开放后续竞争。

因此，在持续存在 Concurrent 流量的情况下，Exclusive 不需要一直等待某个偶然出现的完全空闲窗口。

普通 Exclusive 获取和 Concurrent → Exclusive 转换会借用 Monitor 的互斥、等待、唤醒与排他排序能力；项目不额外承诺严格 FIFO，也不承诺比 Monitor 更强的公平性。

三、原地升级与原地降级

很多实体业务并不是简单地“先读后写”，而是：

先以 Concurrent 权限检查状态；
根据检查结果决定是否修改；
必要时进入 Exclusive；
修改完成后继续以 Concurrent 权限执行后续逻辑。

CEL 为此提供权限转换协议。

Concurrent → Exclusive

调用方可以在已经持有 Concurrent 的情况下发起升级。

升级进入竞争窗口后，会阻止新的 Concurrent 进入，并等待其他 Concurrent 持有者退出。成功后，当前调用方直接进入 Exclusive 区域，而不需要先释放 Concurrent，再重新从外部竞争 Exclusive。

业务 ID 版本的升级方法包括：

TryConcurrentToExclusiveWithSwitchContextID(int newContextID)
TryConcurrentToExclusiveWithRaiseEpochID(int newEpochID)

升级成功后，当前 Scope 持有 Exclusive；升级失败时，原 Concurrent 权限已经自动释放，调用方不应再次释放 Concurrent。

Exclusive → Concurrent

调用方完成独占修改后，可以直接降级：

scope.ExclusiveToConcurrent();

降级后不再持有 Exclusive，但仍持有 Concurrent，可以继续执行依赖连续访问上下文的业务逻辑，避免先释放 Exclusive、再重新申请 Concurrent 所产生的访问窗口。

四、ContextID 与 EpochID

CEL 的内部状态除了权限计数，还可以关联两个业务标识。

ContextID

ContextID 表达当前业务上下文身份，例如：

当前房间实例；
当前战斗上下文；
当前玩家会话；
当前数据装载批次；
当前任务所有者。

SwitchContextID(newContextID) 仅在新 ID 与原值不同时切换成功。

它可以用于识别同一业务上下文，避免同一上下文重复执行某些 Exclusive 初始化、切换或提交逻辑。

EpochID

EpochID 表达单调推进的生命周期、版本或阶段，例如：

实体版本；
房间 Tick；
战斗阶段；
数据快照版本；
生命周期代次；
处理批次。

RaiseEpochID(newEpochID) 只有在新值大于当前值时才会成功，因此可以把“阶段推进”和“获得 Exclusive 执行资格”组合成一个协议。

ContextID 和 EpochID 都属于锁协议之外的业务状态。它们的具体含义、分配规则、清理策略和生命周期由调用方定义。

五、三层 API 结构

项目由三个相互独立但可以组合使用的层次构成。

1. ConcurrentExclusiveLock

ConcurrentExclusiveLock 是核心同步协议。

private readonly ConcurrentExclusiveLock _locker =
    ConcurrentExclusiveLock.Create();

它是一个 readonly struct，但真实共享状态位于内部 CELToken 中，因此复制 ConcurrentExclusiveLock 值不会复制锁状态，副本仍然指向同一份同步状态。

默认初始化得到的实例不可用，必须通过：

ConcurrentExclusiveLock.Create()

创建。

核心支持：

AcquireConcurrent()
TryAcquireConcurrent()

AcquireExclusive()
TryAcquireExclusive()

ReleaseConcurrent()
ReleaseExclusive()

ExclusiveToConcurrent()

TryConcurrentToExclusiveWithSwitchContextID(...)
TryConcurrentToExclusiveWithRaiseEpochID(...)

适合需要直接控制每一次权限获取、释放和转换的底层代码。

2. ConcurrentExclusiveLockScope

ConcurrentExclusiveLockScope 是基于 using 的权限生命周期封装。

using (var scope = new ConcurrentExclusiveLockScope(_locker))
{
    scope.AcquireConcurrent();

    ReadEntityState();
}

如果调用方没有手动释放权限，Dispose() 会根据 Scope 最终记录的权限状态自动释放 Concurrent 或 Exclusive。

它能够减少以下路径中的释放错误：

异常；
提前返回；
多分支退出；
Concurrent → Exclusive 升级；
Exclusive → Concurrent 降级；
尝试获取失败后的状态变化。

Dispose() 只释放 Scope 当前仍然持有的访问权限，不会回退或清理 ContextID / EpochID。

Exclusive 使用
using (var scope = new ConcurrentExclusiveLockScope(_locker))
{
    scope.AcquireExclusive();

    ModifyEntityState();
}
升级使用
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
}
降级使用
using (var scope = new ConcurrentExclusiveLockScope(_locker))
{
    scope.AcquireExclusive();

    RebuildEntityState();

    scope.ExclusiveToConcurrent();

    PublishSnapshot();
}

Scope 是具有释放责任的可变值类型，只应由单个调用上下文持有和操作。

不要：

复制 Scope；
按值传递 Scope；
将 Scope 捕获到其他线程；
同时操作同一个 Scope 的多个副本。

这是为了维持热路径零堆分配而保留的明确使用协议。Scope 的设计文档同样明确规定其不支持多线程并发操作，也不应被复制。

3. ConcurrentExclusiveLockPipeline

Pipeline 用一组顺序业务段描述完整的权限工作流。

每个 Segment 只声明：

当前业务代码；
当前段需要的访问权限；
可选的 ContextID 或 EpochID 条件。

Pipeline 根据上一段成功持有的权限，自动决定：

直接延续；
手动释放；
重新申请；
原地升级；
原地降级；
条件失败后跳过当前段；
以 None 状态继续后续流程。

这使复杂的权限切换从命令式锁操作转化为声明式流程编排。

示例：

var pipeline = new ConcurrentExclusiveLockPipeline(_locker);

pipeline.DoPipeline(
    ConcurrentExclusiveLockSegment.Concurrent(() =>
    {
        ReadCurrentState();
    }),

    ConcurrentExclusiveLockSegment.TryApplyIDConvergeExclusive(
        () =>
        {
            ApplyNewEpoch();
        },
        targetEpoch,
        ConcurrentExclusiveLockSegment.IDType.EpochID),

    ConcurrentExclusiveLockSegment.ConvergeConcurrent(() =>
    {
        PublishNewSnapshot();
    }),

    ConcurrentExclusiveLockSegment.None(() =>
    {
        NotifyOtherSystems();
    })
);
六、Pipeline Segment 类型
Segment	语义
None	在不持有访问权限的状态下执行
Concurrent	获取一段独立的 Concurrent；连续同类段也会切开并重新申请
TryConcurrent	尝试获取独立 Concurrent；失败则跳过当前段
Exclusive	获取一段独立 Exclusive；连续同类段也会释放后重新申请
TestExclusive	仅在锁为 Idle 时尝试 Exclusive，不抢占已有 Concurrent
TryExclusive	抢占式尝试 Exclusive，可以阻止新的 Concurrent 进入
ConvergeConcurrent	延续已有 Concurrent，或把 Exclusive 原地降级为 Concurrent
TryApplyIDConvergeExclusive	尝试应用 ContextID / EpochID，并在成功后收敛到 Exclusive

TestExclusive 不会抢占已有 Concurrent；TryExclusive 则允许进入抢占窗口。TryApplyIDConvergeExclusive 的 Try 语义针对业务 ID 是否成功应用，而不是简单地表示“试一下锁”。

Try 类型段没有获得执行条件时：

当前段不会执行；
Pipeline 不会因此抛出异常；
Pipeline 不会提前结束；
后续段会以 None 状态继续解释。
七、同步边界与异步封装

Pipeline Segment 采用同步委托：

Action Segment

因此 Pipeline 是一个同步权限流程编排器。

直接传入异步 lambda 属于误用：

ConcurrentExclusiveLockSegment.Concurrent(async () =>
{
    DoPartA();

    await SomethingAsync();

    DoPartB();
});

项目通过禁用的 Func<Task> 重载在编译阶段拒绝这种直接用法，避免异步 lambda 被隐式转换为 async void，导致 Pipeline 无法跟踪 Segment 的真实完成时间。

异步操作应放在权限流程之外，或者把整条同步 Pipeline 调度到工作线程：

await pipeline.DoPipelineAsync(segments);

DoPipelineAsync 的语义是：

使用 Task.Run 在线程池中完整执行一次同步 Pipeline。

它不会使 Segment 支持 await，也不是原生异步锁协议。

由于 Exclusive 使用具有线程所有权的同步机制，持有 Exclusive 时不得跨越 await。

八、低分配与 Unity3D

CEL 面向大量细粒度状态对象和高频调用路径设计。

锁实例

每次调用 ConcurrentExclusiveLock.Create() 会创建一个内部 CELToken。它保存：

64-bit Counter；
32-bit ContextID；
32-bit EpochID。

核心状态字段合计为 128-bit，此数字不包含 CLR 对象头和对齐开销。Monitor 直接作用于该 Token，不需要再为锁单独创建另一个同步对象。

热路径

锁初始化完成后：

ConcurrentExclusiveLock 是值类型句柄；
ConcurrentExclusiveLockScope 是 struct；
ConcurrentExclusiveLockPipeline 是 readonly struct；
Scope 的创建与释放不要求为每次进入创建堆对象；
普通 Concurrent 路径主要使用原子操作。

因此它适合 Unity3D Update、游戏服务器实体循环和其他严格控制 GC 的高频路径。

需要注意，调用方自己的使用方式仍可能产生分配，例如：

捕获局部变量的 lambda；
每次动态创建委托；
每次通过 params 创建新的 Segment 数组；
使用 Task.Run；
业务代码自身创建对象。

在极端热路径中，可以缓存委托、复用 Segment 数组，并直接调用同步 API。

九、状态观察

CEL 提供两个观察属性：

ConcurrentExclusiveLockState ObservedState
int ObservedContention

ObservedState 表示读取瞬间观察到的访问倾向或转换状态，并不保证此刻一定已有线程正在执行 Exclusive 业务代码。

例如，抢占式 Exclusive 已经进入竞争窗口、但仍在等待现有 Concurrent 退出时，状态也可能观察为 Exclusive。

ObservedContention 是诊断、监控或调度参考值，不应被当作精确同步条件。纯 Concurrent 场景下其值为 0；存在 Exclusive 压力时，才反映当前竞争压力。

十、适用场景

CEL 尤其适合以下模型：

游戏服务器中的玩家、房间、战斗和地图实体；
Unity3D 中需要严格控制堆分配的状态访问；
Actor 或类 Actor 实体；
会话状态和连接状态；
缓存条目与聚合根；
实体生命周期推进；
版本化数据更新；
后台任务状态机；
同一实体上的检查、升级、提交和回落流程；
大量细粒度锁对象长期并存的服务端系统。

典型模式是：

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
十一、设计边界

CEL 是一套同步、非递归的访问权限协议。

使用时应遵守以下边界：

不要使用默认初始化的 ConcurrentExclusiveLock，必须调用 Create()。
不要在已持有 Concurrent 时直接调用普通 AcquireExclusive()，应使用升级协议。
不要在已持有 Exclusive 时直接调用普通 AcquireConcurrent()，应使用降级协议。
不要把 Exclusive 当作递归锁。
不要复制或并发操作 ConcurrentExclusiveLockScope。
不要在 Segment 中使用异步 lambda。
不要让依赖当前权限的代码跨越 await。
ObservedState 和 ObservedContention 仅用于观察，不用于建立同步正确性。
ContextID / EpochID 的业务含义和生命周期由调用方负责。
CEL 不承诺严格 FIFO 公平性。

这些限制不是附带缺陷，而是该同步模型为了保持明确语义、低开销和高频使用能力而建立的协议边界。

十二、项目定位

ConcurrentExclusiveLock 不试图成为所有场景的通用锁，也不是简单复制传统 Reader / Writer Lock。

它重点解决的是：

在大量细粒度状态对象上，以较低常态成本表达 Concurrent / Exclusive 权限，并把抢占、升级、降级、业务 ID 收敛和连续流程编排组合成一套完整协议。

项目当前包含：

ConcurrentExclusiveLock
ConcurrentExclusiveLockScope
ConcurrentExclusiveLockPipeline
完整 XML API 注释
同步 Segment 误用保护
BenchmarkDotNet 性能测试
长时间随机调用压力测试

Pipeline 已完成约 240 小时随机调用压力测试。

项目信息
项目名称：ConcurrentExclusiveLock
简称：CEL
作者：王弈博（YiBoWang）
当前版本：1.0.0
原始实现：C# / .NET
兼容目标：.NET Standard 2.1
适用环境：.NET、Unity3D、游戏服务器及其他细粒度状态系统
GitHub：WangHHB/ConcurrentExclusiveLock

A compact, high-performance Concurrent/Exclusive synchronization protocol for entity-level state objects, featuring preemptive Exclusive access, in-place upgrade/downgrade, and ContextID/EpochID support.
