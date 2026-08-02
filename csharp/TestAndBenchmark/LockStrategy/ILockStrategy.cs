namespace LockBenchmark;

/// <summary>
/// Common lock protocol used by throughput and latency experiments.
/// Implementations expose acquisition separately so acquisition wait can be measured without held-lock work.
/// </summary>
internal interface ILockStrategy : IDisposable
{
    string Name { get; }
    void AcquireConcurrent();
    void ReleaseConcurrent();
    void AcquireExclusive();
    void ReleaseExclusive();

    long ExecuteConcurrent(IWork work)
    {
        AcquireConcurrent();
        try { return work.TickRead(); }
        finally { ReleaseConcurrent(); }
    }

    long ExecuteExclusive(IWork work)
    {
        AcquireExclusive();
        try { return work.TickWrite(); }
        finally { ReleaseExclusive(); }
    }
}
