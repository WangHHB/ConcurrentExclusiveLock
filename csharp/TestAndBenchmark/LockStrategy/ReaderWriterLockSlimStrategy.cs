using System.Threading;

namespace LockBenchmark;

internal sealed class ReaderWriterLockSlimStrategy : ILockStrategy
{
    private readonly ReaderWriterLockSlim locker = new(LockRecursionPolicy.NoRecursion);
    public string Name => "ReaderWriterLockSlim";
    public void AcquireConcurrent() => locker.EnterReadLock();
    public void ReleaseConcurrent() => locker.ExitReadLock();
    public void AcquireExclusive() => locker.EnterWriteLock();
    public void ReleaseExclusive() => locker.ExitWriteLock();
    public void Dispose() => locker.Dispose();
}
