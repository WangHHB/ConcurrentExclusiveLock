using IntomicLib;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 为值类型 ConcurrentExclusiveLock 提供稳定的共享存储位置。
/// 测试线程共享本引用对象，并始终直接操作 Value 字段，禁止按值传递锁副本。
/// </summary>
internal sealed class SharedConcurrentExclusiveLock
{
    public ConcurrentExclusiveLock Value;

    public SharedConcurrentExclusiveLock()
    {
        Value = ConcurrentExclusiveLock.Create();
    }
}
