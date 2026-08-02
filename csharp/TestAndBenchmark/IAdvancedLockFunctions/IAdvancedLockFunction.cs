namespace LockBenchmark;

/// <summary>
/// Complete call protocol for one advanced lock capability.
/// </summary>
/// <remarks>
/// The implementation acquires permission, performs transitions, invokes business Work, and releases the final held state.
/// One implementation instance is shared by multiple test threads to create real convergence/transition contention.
/// </remarks>
internal interface IAdvancedLockFunction
{
    string Name { get; }

    /// <summary>
    /// Executes one complete advanced lock operation.
    /// </summary>
    /// <param name="work">Fresh workload owned by this correctness case.</param>
    /// <returns>Business checksum, transition outcome, and completed Work count.</returns>
    AdvancedLockFunctionResult Execute(IWork work);
}

/// <summary>
/// Result of one advanced lock operation.
/// </summary>
internal readonly struct AdvancedLockFunctionResult
{
    /// <summary>Combined business-code checksum.</summary>
    public long Checksum { get; }

    /// <summary>Number of TickRead/TickWrite operations completed.</summary>
    public int CompletedWorks { get; }

    /// <summary>
    /// Whether the advanced operation completed; for conditional upgrade, whether this caller became the unique winner.
    /// </summary>
    public bool Succeeded { get; }


    public AdvancedLockFunctionResult(
        long checksum,
        int completedWorks,
        bool succeeded)
    {
        Checksum = checksum;
        CompletedWorks = completedWorks;
        Succeeded = succeeded;;
    }
}
