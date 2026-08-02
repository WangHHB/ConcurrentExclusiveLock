using System;

namespace LockBenchmark;

/// <summary>
/// Shared business workload owned by exactly one lock instance in one strategy run.
/// Every strategy receives freshly created and initialized workload objects.
/// </summary>
/// <remarks>
/// <see cref="TickRead"/> must not mutate shared business state and must be safe for simultaneous
/// Concurrent callers. <see cref="TickWrite"/> may mutate shared state and is called only while
/// Exclusive permission is held. Each completed call counts as one benchmark operation.
///
/// Porting contract: create one independent workload object per lock instance, not one global
/// workload shared by all locks. Preserve the same state-transition rules and final-state checksum
/// so different lock strategies execute equivalent business operations.
/// </remarks>
internal interface IWork : IDisposable
{
    /// <summary>Initializes this run's private workload before the common start gate opens.</summary>
    void Init();

    /// <summary>Executes one Concurrent-safe operation and returns a value that prevents dead-code elimination.</summary>
    long TickRead();

    /// <summary>Executes one Exclusive state-changing operation and returns an updated checksum contribution.</summary>
    long TickWrite();

    /// <summary>Returns the final shared-state hash used to validate equivalence across strategies.</summary>
    long StateHash { get; }
}
