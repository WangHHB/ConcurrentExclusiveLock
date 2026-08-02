using IntomicLib;

namespace LockBenchmark;

internal sealed class ConcurrentExclusiveLockStrategy : ILockStrategy
{
    private ConcurrentExclusiveLock locker = ConcurrentExclusiveLock.Create();

    public string Name => "CEL";

    public void AcquireConcurrent() => locker.AcquireConcurrent();
    public void ReleaseConcurrent() => locker.ReleaseConcurrent();
    public void AcquireExclusive() => locker.AcquireExclusive();
    public void ReleaseExclusive() => locker.ReleaseExclusive();
    public void Dispose() { }
}
