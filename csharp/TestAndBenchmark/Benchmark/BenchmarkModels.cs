using System.Diagnostics;

namespace LockBenchmark;

internal readonly record struct BenchmarkScenario(string Name, int ConcurrentPermille)
{
    public static BenchmarkScenario FromPermille(int permille) =>
        new($"Concurrent/Exclusive {permille / 10.0:0.0}/{(1000 - permille) / 10.0:0.0}", permille);
}

internal static class BenchmarkScenarioCatalog
{
    private static readonly BenchmarkScenario[] Scenarios =
    {
        BenchmarkScenario.FromPermille(1000),
        BenchmarkScenario.FromPermille(995),
        BenchmarkScenario.FromPermille(900),
        BenchmarkScenario.FromPermille(500),
        BenchmarkScenario.FromPermille(300),
        BenchmarkScenario.FromPermille(0)
    };

    public static IReadOnlyList<BenchmarkScenario> Resolve(int? selectedPermille) =>
        selectedPermille.HasValue
            ? new[] { BenchmarkScenario.FromPermille(selectedPermille.Value) }
            : Scenarios;
}

internal readonly record struct ThroughputResult(
    string LockName,
    TimeSpan Elapsed,
    double CpuPercent,
    long ConcurrentOperations,
    long ExclusiveOperations,
    long StateHash,
    long ExclusiveOperationTicks,
    long Checksum)
{
    public long Operations => ConcurrentOperations + ExclusiveOperations;
    public double OperationsPerSecond => Operations / MeasurementMath.ElapsedSeconds(Elapsed);
    public double AverageExclusiveOperationNs => ExclusiveOperations == 0
        ? 0
        : MeasurementMath.TicksToNanoseconds(ExclusiveOperationTicks) / ExclusiveOperations;
}

internal readonly record struct LatencySummary(
    long Count,
    double MeanNs,
    double P50Ns,
    double P95Ns,
    double P99Ns,
    double P999Ns,
    double MaxNs)
{
    public static LatencySummary Empty => new(0, 0, 0, 0, 0, 0, 0);
}

internal readonly record struct AcquisitionLatencyResult(
    string LockName,
    TimeSpan Elapsed,
    double CpuPercent,
    long ConcurrentOperations,
    long ExclusiveOperations,
    long StateHash,
    LatencySummary ConcurrentLatency,
    LatencySummary ExclusiveLatency)
{
    public long Operations => ConcurrentOperations + ExclusiveOperations;
    public double OperationsPerSecond => Operations / MeasurementMath.ElapsedSeconds(Elapsed);
}

internal readonly record struct ExclusiveProgressResult(
    string LockName,
    TimeSpan Elapsed,
    double CpuPercent,
    long ConcurrentOperations,
    long ExclusiveOperations,
    IReadOnlyList<long> PerLockExclusiveOperations,
    long StateHash)
{
    public double ConcurrentOperationsPerSecond => ConcurrentOperations / MeasurementMath.ElapsedSeconds(Elapsed);
    public double ExclusiveOperationsPerSecond => ExclusiveOperations / MeasurementMath.ElapsedSeconds(Elapsed);
    public double ExclusivePerMillionConcurrent => ConcurrentOperations == 0
        ? 0
        : ExclusiveOperations * 1_000_000.0 / ConcurrentOperations;
    public long MinLockExclusiveOperations => PerLockExclusiveOperations.Count == 0 ? 0 : PerLockExclusiveOperations.Min();
    public long MaxLockExclusiveOperations => PerLockExclusiveOperations.Count == 0 ? 0 : PerLockExclusiveOperations.Max();
}

internal readonly record struct PipelinePerformanceResult(
    string Strategy,
    string Semantics,
    TimeSpan Elapsed,
    double CpuPercent,
    long Operations,
    long CommitCount,
    long StateHash,
    long Sink)
{
    public double OperationsPerSecond => Operations / MeasurementMath.ElapsedSeconds(Elapsed);
    public double NanosecondsPerOperation => Elapsed.TotalSeconds * 1_000_000_000.0 / Operations;
}

internal readonly record struct UpgradeContentionPerLockResult(
    int LockIndex,
    TimeSpan FirstUpgrade,
    TimeSpan Drain,
    LatencySummary UpgradeAcquireLatency,
    LatencySummary UpgradeReleaseLatency,
    LatencySummary OrdinaryAcquireLatency,
    int OrdinaryEnteredBeforeUpgradeDrain);

internal readonly record struct UpgradeContentionResult(
    int LockInstances,
    int ConcurrentThreadsPerLock,
    int OrdinaryExclusiveThreadsPerLock,
    TimeSpan FirstUpgrade,
    TimeSpan Drain,
    LatencySummary UpgradeAcquireLatency,
    LatencySummary UpgradeReleaseLatency,
    LatencySummary OrdinaryAcquireLatency,
    int OrdinaryEnteredBeforeUpgradeDrain,
    IReadOnlyList<UpgradeContentionPerLockResult> PerLock)
{
    public long TotalUpgradeThreads => checked((long)LockInstances * ConcurrentThreadsPerLock);
    public long TotalOrdinaryExclusiveThreads => checked((long)LockInstances * OrdinaryExclusiveThreadsPerLock);
    public double UpgradeThroughput => TotalUpgradeThreads / MeasurementMath.ElapsedSeconds(Drain);
    public double WorstLockAcquireP99Ns => PerLock.Count == 0 ? 0 : PerLock.Max(x => x.UpgradeAcquireLatency.P99Ns);
    public double WorstLockDrainNs => PerLock.Count == 0 ? 0 : PerLock.Max(x => x.Drain.TotalNanoseconds);
}


/// <summary>Shared unit conversion and normalized process-CPU calculations.</summary>
/// <remarks>
/// CPU percentage is process CPU time divided by wall time and runtime-effective logical CPU count.
/// It is observational metadata only and never changes the workload or topology. No minimum-duration
/// heuristic is applied; very short runs may reflect the operating system's CPU-time resolution.
/// </remarks>
internal static class MeasurementMath
{
    public static double ElapsedSeconds(TimeSpan elapsed) =>
        Math.Max(elapsed.TotalSeconds, 1.0 / Stopwatch.Frequency);

    public static double TicksToNanoseconds(long ticks) =>
        ticks * 1_000_000_000.0 / Stopwatch.Frequency;

    public static double CpuPercent(TimeSpan cpu, TimeSpan elapsed) =>
        cpu.TotalSeconds / ElapsedSeconds(elapsed) / Math.Max(1, Environment.ProcessorCount) * 100.0;
}

/// <summary>Latency summarization shared by every mode.</summary>
/// <remarks>
/// Percentiles use linear interpolation at p * (count - 1) over the sorted complete sample set.
/// Ports must use the same convention or reported p95/p99/p99.9 values will differ even when the
/// raw tick samples are identical. Samples are converted from the platform monotonic-clock frequency.
/// </remarks>
internal static class Statistics
{
    public static double Percentile(IReadOnlyList<long> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0) return 0;
        if (sortedValues.Count == 1) return sortedValues[0];

        double position = percentile * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper) return sortedValues[lower];
        double fraction = position - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * fraction;
    }

    public static LatencySummary SummarizeTicks(IEnumerable<long> ticks)
    {
        long[] values = ticks.ToArray();
        if (values.Length == 0) return LatencySummary.Empty;
        Array.Sort(values);
        double scale = 1_000_000_000.0 / Stopwatch.Frequency;
        return new LatencySummary(
            values.LongLength,
            values.Average(v => v * scale),
            Percentile(values, 0.50) * scale,
            Percentile(values, 0.95) * scale,
            Percentile(values, 0.99) * scale,
            Percentile(values, 0.999) * scale,
            values[^1] * scale);
    }


}
