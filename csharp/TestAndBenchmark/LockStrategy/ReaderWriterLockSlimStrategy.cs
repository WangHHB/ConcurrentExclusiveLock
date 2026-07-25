using System.Threading;

namespace LockBenchmark;

/// <summary>
/// ReaderWriterLockSlim 适配器：读业务进入共享读区，写业务进入独占写区。
/// </summary>
internal sealed class ReaderWriterLockSlimStrategy : ILockStrategy
{
    private readonly ReaderWriterLockSlim locker = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

    public string Name => "ReaderWriterLockSlim";

    public long ExecuteRead(IWork work)
    {
        locker.EnterReadLock();
        try
        {
            return work.TickRead();
        }
        finally
        {
            locker.ExitReadLock();
        }
    }

    public long ExecuteWrite(IWork work)
    {
        locker.EnterWriteLock();
        try
        {
            return work.TickWrite();
        }
        finally
        {
            locker.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        locker.Dispose();
    }
}
