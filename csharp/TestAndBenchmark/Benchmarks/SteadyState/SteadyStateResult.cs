namespace TestAndBenchmark.Benchmarks.SteadyState;

internal sealed record SteadyStateResult(
    string LockName,
    string Scenario,
    int LockInstances,
    int ThreadsPerLock,
    int OperationsPerThread,
    TimeSpan Elapsed,
    double CpuPercent,
    long ConcurrentWorks,
    long ExclusiveWorks,
    long StateHash,
    long Sink,
    double AverageExclusiveLatencyNs)
{
    public long Works => ConcurrentWorks + ExclusiveWorks;
}
