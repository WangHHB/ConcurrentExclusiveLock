using IntomicLib;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Provides a stable shared storage location for the value-type ConcurrentExclusiveLock handle.
/// Test threads share this reference object and operate directly on Value; ports must not accidentally copy independent lock state.
/// </summary>
internal sealed class SharedConcurrentExclusiveLock
{
    public ConcurrentExclusiveLock Value;

    public SharedConcurrentExclusiveLock()
    {
        Value = ConcurrentExclusiveLock.Create();
    }
}
