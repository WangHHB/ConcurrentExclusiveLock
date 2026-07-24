# ConcurrentExclusivePack 使用范例

此目录只放示例代码，不作为测试入口执行。

This directory contains usage samples only. It is not a test entry point.

示例代码会随 `TestAndBenchmark` 一起编译，用于保证示例中的公开 API 调用不会过期。

The sample code is compiled together with `TestAndBenchmark`, so public API usage in the samples stays checked by the compiler.

简单运行：

Run the samples:

```powershell
TestAndBenchmark.exe sample
```

- `ScopeSample.cs`：展示 `ConcurrentExclusiveLockScope` 的典型使用方式，包括只获取 / 释放 Concurrent、只获取 / 释放 Exclusive、Try 入口、Exclusive 原地降级为 Concurrent、Concurrent 通过 EpochID / ContextID 原地升级为 Exclusive。
- `PipelineSample.cs`：展示 `ConcurrentExclusiveLockPipeline` 的典型使用方式，包括独立 Concurrent / Exclusive 段、None 段、ConvergeConcurrent 降级、TryApplyIDConvergeExclusive 通过 EpochID / ContextID 升级、Try 段跳过业务的行为，以及 `DoPipelineAsync` 在线程池中执行同步 Pipeline。

- `ScopeSample.cs`: shows typical `ConcurrentExclusiveLockScope` usage, including Concurrent-only acquisition/release, Exclusive-only acquisition/release, Try as an outer entry point, in-place downgrade from Exclusive to Concurrent, and in-place upgrade from Concurrent to Exclusive through EpochID / ContextID.
- `PipelineSample.cs`: shows typical `ConcurrentExclusiveLockPipeline` usage, including independent Concurrent / Exclusive segments, None segments, ConvergeConcurrent downgrade, TryApplyIDConvergeExclusive upgrade through EpochID / ContextID, Try segments that may skip work, and `DoPipelineAsync` scheduling a synchronous Pipeline on the thread pool.

示例重点：

Key points:

- `Concurrent` / `Exclusive` 表示访问权限，不表示读写意图。
- 普通 Try 获取适合作为最外层权限入口，不要在已经持有权限的锁范围内当作嵌套锁使用。
- 同一业务流程内需要切换权限时，优先使用原地升级 / 降级协议。
- `ContextID` / `EpochID` 是业务身份和生命周期标识，由业务代码负责分配和解释。

- `Concurrent` / `Exclusive` describe access permission, not read/write intent.
- Normal Try acquisition is best used as the outer access entry point. Do not use it as a nested lock inside an already-held access region.
- When one business flow needs to switch access mode, prefer the in-place upgrade / downgrade protocol.
- `ContextID` / `EpochID` are business identity and lifecycle markers. Allocation and interpretation are owned by the caller.
