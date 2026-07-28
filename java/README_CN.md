# ConcurrentExclusiveLock Java 版

本 Java 实现参考 ConcurrentExclusiveLock 的 C# 原版移植而来。C# 版本仍是设计和同步语义的参考实现：[`../csharp`](../csharp)。

Java 版保留相同的 Concurrent/Exclusive 访问模型，并针对 Java 内存模型和标准同步机制调整具体实现。


## 安装

### Maven

```xml
<dependency>
    <groupId>io.github.wanghhb</groupId>
    <artifactId>concurrent-exclusive-lock</artifactId>
    <version>1.1.3</version>
</dependency>
```

### Gradle

```gradle
implementation 'io.github.wanghhb:concurrent-exclusive-lock:1.1.3'
```

[Maven Central 页面](https://central.sonatype.com/artifact/io.github.wanghhb/concurrent-exclusive-lock)


## 使用说明

Java API 与 C# 参考实现保持相同的三层结构：

1. `ConcurrentExclusiveLock`：底层权限协议；
2. `ConcurrentExclusiveLockScope`：基于 `AutoCloseable` 的生命周期封装；
3. `ConcurrentExclusiveLockPipeline`：顺序权限工作流编排。

通常应为每个需要独立同步的实体、房间、玩家、会话、聚合根或任务上下文分别创建一把锁：

```java
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLock;

private final ConcurrentExclusiveLock locker =
        ConcurrentExclusiveLock.create();
```

### 底层 API

`ConcurrentExclusiveLock` 提供对权限获取、释放、升级、降级和业务 ID 的直接控制：

```java
locker.acquireConcurrent();
locker.tryAcquireConcurrent();

locker.acquireExclusive();
locker.tryAcquireExclusive();

locker.releaseConcurrent();
locker.releaseExclusive();

locker.concurrentToExclusive();
locker.exclusiveToConcurrent();

locker.switchContextID(newContextID);
locker.raiseEpochID(newEpochID);

locker.tryConcurrentToExclusiveWithSwitchContextID(newContextID);
locker.tryConcurrentToExclusiveWithRaiseEpochID(newEpochID);
```

超时重载使用 `java.time.Duration`。

Exclusive 权限具有线程所有权，必须由获得它的同一线程释放或降级。

### Scope

大多数业务代码推荐使用 `ConcurrentExclusiveLockScope`。它实现了 `AutoCloseable`，因此可以配合 try-with-resources 使用；无论正常结束、提前返回还是抛出异常，`close()` 都会按照 Scope 最终持有的权限自动释放。

Scope 是可变对象，不是线程安全对象，不能跨线程共享。

#### Concurrent

```java
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockScope;

public void readState() {
    try (ConcurrentExclusiveLockScope scope =
                 new ConcurrentExclusiveLockScope(locker)) {

        scope.acquireConcurrent();

        readEntityState();

        // 可以手动释放；省略时由 close() 自动释放。
        // scope.releaseConcurrent();
    }
}
```

#### Exclusive

```java
public void modifyState() {
    try (ConcurrentExclusiveLockScope scope =
                 new ConcurrentExclusiveLockScope(locker)) {

        scope.acquireExclusive();

        modifyEntityState();

        // 可以手动释放；省略时由 close() 自动释放。
        // scope.releaseExclusive();
    }
}
```

#### Concurrent 检查后升级

```java
public void applyEpoch(int targetEpoch) {
    try (ConcurrentExclusiveLockScope scope =
                 new ConcurrentExclusiveLockScope(locker)) {

        scope.acquireConcurrent();

        inspectCurrentState();

        if (!scope.tryConcurrentToExclusiveWithRaiseEpochID(targetEpoch)) {
            // 升级失败时，原 Concurrent 已由协议自动释放，
            // 不应再次调用 releaseConcurrent()。
            return;
        }

        applyEpochUpdate();

        // 当前 Scope 最终持有 Exclusive。
    }
}
```

#### Exclusive 完成后降级

```java
public void rebuildAndPublish() {
    try (ConcurrentExclusiveLockScope scope =
                 new ConcurrentExclusiveLockScope(locker)) {

        scope.acquireExclusive();

        rebuildEntityState();

        scope.exclusiveToConcurrent();

        publishSnapshot();

        // 当前 Scope 最终持有 Concurrent。
    }
}
```

### Pipeline

`ConcurrentExclusiveLockPipeline` 使用一组同步 `Runnable` Segment 描述完整的权限工作流。

```java
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockPipeline;
import io.github.wanghhb.concurrentexclusivelock.ConcurrentExclusiveLockSegment;

ConcurrentExclusiveLockPipeline pipeline =
        new ConcurrentExclusiveLockPipeline(locker);

pipeline.doPipeline(
        ConcurrentExclusiveLockSegment.concurrent(
                this::readCurrentState),

        ConcurrentExclusiveLockSegment.tryApplyIDConvergeExclusive(
                this::applyNewEpoch,
                targetEpoch,
                ConcurrentExclusiveLockSegment.IDType.EPOCH_ID),

        ConcurrentExclusiveLockSegment.convergeConcurrent(
                this::publishNewSnapshot),

        ConcurrentExclusiveLockSegment.none(
                this::notifyOtherSystems)
);
```

可用的 Segment 工厂方法：

| Segment 工厂方法 | 语义 |
|---|---|
| `none(...)` | 在不持有访问权限的状态下执行。 |
| `concurrent(...)` | 获取一段独立 Concurrent；连续独立段也会释放后重新申请。 |
| `tryConcurrent(...)` | 尝试获取一段独立 Concurrent；失败时跳过当前段。 |
| `exclusive(...)` | 获取一段独立 Exclusive；连续独立段也会释放后重新申请。 |
| `testExclusive(...)` | 仅在锁处于 Idle 时尝试 Exclusive，不抢占已有 Concurrent。 |
| `tryExclusive(...)` | 尝试抢占式 Exclusive。 |
| `convergeConcurrent(...)` | 延续 Concurrent、将 Exclusive 原地降级为 Concurrent，或重新获取 Concurrent。 |
| `convergeExclusive(...)` | 延续 Exclusive、将 Concurrent 原地升级为 Exclusive，或重新获取 Exclusive。 |
| `tryApplyIDConvergeExclusive(...)` | 尝试应用 ContextID 或 EpochID，仅在成功时收敛到 Exclusive。 |

Try 类型 Segment 未满足执行条件时：

- 当前 Segment 不执行；
- Pipeline 不抛出异常；
- 当前权限状态变为 `NONE`；
- 后续 Segment 继续执行。

### 同步与异步边界

Pipeline Segment 是同步 `Runnable`。受保护的业务必须在 `Runnable.run()` 返回之前全部完成。

不能在 Segment 中启动异步任务后立即返回：

```java
ConcurrentExclusiveLockSegment.exclusive(() -> {
    // 不受支持：Runnable 返回后，Pipeline 无法继续为异步任务保持权限。
    java.util.concurrent.CompletableFuture.runAsync(this::modifyEntityState);
});
```

`doPipelineAsync(...)` 只是把整条同步 Pipeline 调度到公共线程池或指定的 `Executor` 中执行，并不会让单个 Segment 支持异步跨越。

目录结构对应 C# 版本：

```text
java/
├─ ConcurrentExclusiveLock/   # 核心库
├─ TestAndBenchmark/          # 语义测试、压力测试和性能对比
└─ pom.xml                    # Maven 多模块入口
```

## 环境

- JDK 17 或更高版本；
- Maven 3.9 或更高版本；
- 推荐使用 JDK 21 开发和测试，生成的字节码兼容 Java 17。

## 构建

在 `java` 目录执行：

```powershell
mvn clean package
```

生成：

```text
ConcurrentExclusiveLock\target\concurrent-exclusive-lock-1.1.1.jar
TestAndBenchmark\target\TestAndBenchmark.jar
```

也提供只依赖 JDK 的 Windows 构建脚本：

```powershell
.\build-jdk.ps1
```

该脚本直接调用 `javac` 和 `jar`，不要求安装 Maven。

## 测试

```powershell
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar --help

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --full-semantics `
  --lock-instances 8 `
  --semantic-workers 4 `
  --semantic-operations 256

java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --pipeline-stress 10m `
  --lock-instances 8 `
  --semantic-workers 64 `
  --semantic-operations 1000
```

## 标准性能对比

```powershell
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 1 `
  --threads 64 `
  --workload memory `
  --operations 10000 `
  --memory-mb 64 `
  --read-work 64 `
  --write-work 64
```

标准对比包含：

- `synchronized`；
- 非公平 `ReentrantLock`；
- 非公平 `ReentrantReadWriteLock`；
- `StampedLock`；
- `CEL`；
- `CEL(ExclusiveOnly)`。

详细说明参阅 [`TESTING_CN.md`](TESTING_CN.md) 和 [`PERFORMANCE_CN.md`](PERFORMANCE_CN.md)。

## 本地性能评测结果

下面的数据来自本项目自带评测程序的一次本地长跑，仅作为参考，不代表所有机器、JVM 或业务负载下的普遍结论。

评测环境：

```text
Java：            OpenJDK 21.0.12，64 位 Server VM
操作系统：        Windows 11
逻辑处理器：      16
锁实例数：        8
每把锁线程数：    8
总线程数：        64
业务负载：        memory，每把锁共享 64 MiB
每线程操作数：    500,000
每策略每场景总量：32,000,000 次锁操作
读写工作量：      32 / 32 steps
```

评测命令：

```powershell
java -jar .\TestAndBenchmark\target\TestAndBenchmark.jar `
  --lock-instances 8 `
  --threads 8 `
  --workload memory `
  --operations 500000 `
  --memory-mb 64 `
  --read-work 32 `
  --write-work 32
```

CEL 结果：

| 读/写比例 | 吞吐量 | Work/CPU% | 平均写入时间 | 简要观察 |
|---:|---:|---:|---:|---|
| 100/0 | 15,857,525 works/s | 166,928 | — | 略高于 `StampedLock`，但差距很小。 |
| 99.5/0.5 | 13,888,372 works/s | 155,005 | 14.52 μs | 本轮吞吐量和 Work/CPU% 最高，写入时间明显低于参与对比的读写锁。 |
| 90/10 | 10,048,027 works/s | 113,071 | 7.61 μs | 本轮三项指标均为最高。 |
| 50/50 | 5,347,318 works/s | 72,256 | 12.63 μs | 在参与对比的显式锁中吞吐量和写入时间最好，但总吞吐低于 `synchronized`。 |
| 30/70 | 4,594,175 works/s | 65,628 | 15.02 μs | 与其他显式锁接近，没有形成明确的全面优势。 |
| 0/100 | 3,792,801 works/s | 57,347 | 16.60 μs | 与其他显式锁处于相近区间，但略低于其中最快的实现。 |

在这次评测中，CEL 从读主导负载到 90/10 场景表现最突出；在 50/50 场景仍保持较强竞争力，并在参与对比的显式锁中取得最高吞吐量。随着写入比例继续提高，CEL 的表现逐渐收敛到普通 Exclusive 锁的水平，而 `synchronized` 在高写入场景更有优势。

这组数据说明，CEL 在混合负载下展现出了较好的吞吐量、CPU 效率与写入延迟综合平衡，但并不意味着 CEL 在所有负载、JVM、硬件和实际应用中都是最快选择。正式使用前仍建议结合实际业务进行评测。

## 许可证

ConcurrentExclusiveLock 采用 MIT License 或 Apache License 2.0 双重许可，使用者可以任选其一。

许可证文件位于仓库根目录：`LICENSE-MIT` 和 `LICENSE-APACHE-2.0`。
