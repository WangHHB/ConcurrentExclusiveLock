using System;

namespace LockBenchmark;

/// <summary>
/// 将不同锁的获取和释放方式统一为标准业务读写操作。
/// 实现只能负责锁协议，不应包含工作集创建、线程调度或指标统计。
/// </summary>
internal interface ILockStrategy : IDisposable
{
    string Name { get; }
    long ExecuteRead(IWork work);
    long ExecuteWrite(IWork work);
}
