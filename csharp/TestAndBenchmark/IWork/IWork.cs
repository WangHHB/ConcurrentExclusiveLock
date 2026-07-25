using System;

namespace LockBenchmark;

/// <summary>
/// 一套由某个锁实现独占使用的共享业务工作集。
/// 每次锁测试都会重新创建并调用 <see cref="Init"/>，不会复用其他锁运行过的数据。
/// </summary>
/// <remarks>
/// <see cref="TickRead"/> 必须只读共享业务状态，允许多个读线程并发执行；
/// <see cref="TickWrite"/> 可以修改共享状态，只能在写锁或排他锁内执行。
/// 每次成功调用 TickRead/TickWrite 均计为一个 Work。
/// </remarks>
internal interface IWork : IDisposable
{
    /// <summary>构造本次锁测试专属的数据集，在计时开始前调用一次。</summary>
    void Init();

    /// <summary>执行一次只读业务操作，并返回用于防止代码消除的摘要。</summary>
    long TickRead();

    /// <summary>执行一次写业务操作，并返回更新后的摘要。</summary>
    long TickWrite();

    /// <summary>全部线程结束后读取的共享状态摘要，用于检查不同锁的执行结果。</summary>
    long StateHash { get; }
}
