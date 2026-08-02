using System.Threading;

namespace LockBenchmark;

internal sealed class MonitorLockStrategy : ILockStrategy
{
    private readonly object locker = new();
    public string Name => "lock";
    public void AcquireConcurrent() => Monitor.Enter(locker);
    public void ReleaseConcurrent() => Monitor.Exit(locker);
    public void AcquireExclusive() => Monitor.Enter(locker);
    public void ReleaseExclusive() => Monitor.Exit(locker);
    public void Dispose() { }
}
