namespace TestAndBenchmark.Benchmarks.ExclusivePreemption;

internal sealed record ExclusivePreemptionAttempt(
    ExclusivePreemptionTarget Target,
    int Attempt,
    bool Entered,
    double ExclusiveWaitNs,
    long NewConcurrentAfterExclusiveArrived,
    long ConcurrentOperationsAtArrival,
    long ConcurrentOperationsAtEntry);
