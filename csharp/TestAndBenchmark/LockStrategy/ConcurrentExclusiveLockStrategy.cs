using IntomicLib;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// ConcurrentExclusiveLock 的标准读写适配器。
/// 这里只使用普通 Concurrent/Exclusive 能力；高级原地转换由独立测试负责。
/// </summary>
internal sealed class ConcurrentExclusiveLockStrategy : ILockStrategy
{
    // ConcurrentExclusiveLock 是可变 struct；字段不能 readonly，否则每次实例调用都会操作防御性副本。
    private ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

    public string Name
    {
        get
        {
            if (UseExclusiveOnly)
            {
                return "CEL(ExclusiveOnly)";
            }
            else
            {
                return "CEL";
            }
        }
    }

    bool UseExclusiveOnly;
    public ConcurrentExclusiveLockStrategy(bool useExclusiveOnly)
    {
        UseExclusiveOnly = useExclusiveOnly;
    }

    public long ExecuteRead(IWork work)
    {
        if (UseExclusiveOnly)
        {
            locker.AcquireExclusive();
            try
            {
                return work.TickRead();
            }
            finally
            {
                locker.ReleaseExclusive();
            }
        }
        else
        {
            locker.AcquireConcurrent();
            try
            {
                return work.TickRead();
            }
            finally
            {
                locker.ReleaseConcurrent();
            }
        }
    }

    public long ExecuteWrite(IWork work)
    {
        locker.AcquireExclusive();
        try
        {
            return work.TickWrite();
        }
        finally
        {
            locker.ReleaseExclusive();
        }
    }

    public void Dispose()
    {
    }
}
