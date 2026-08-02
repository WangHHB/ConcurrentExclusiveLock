using System.Diagnostics;
using System.Threading;

namespace LockBenchmark;

/// <summary>Executes one throughput strategy/scenario over the literal multi-lock topology.</summary>
/// <remarks>
/// Porting contract: global worker i maps to lock i / threadsPerLock and local worker
/// i % threadsPerLock. Each lock owns one fresh workload object. All workers start from the same
/// gate, and elapsed time ends only after every dedicated worker terminates. The operation-mix
/// stream is coordinate-deterministic and identical for every compared lock strategy.
/// </remarks>
internal static class BenchmarkCaseRunner
{
    public static ThroughputResult Run(
        LockStrategyDefinition strategyDefinition,
        WorkDefinition workDefinition,
        BenchmarkOptions options,
        int concurrentPermille)
    {
        ILockStrategy[] strategies = new ILockStrategy[options.LockInstances];
        IWork[] works = new IWork[options.LockInstances];

        try
        {
            for (int lockIndex = 0; lockIndex < options.LockInstances; lockIndex++)
            {
                strategies[lockIndex] = strategyDefinition.Create();
                works[lockIndex] = workDefinition.Create();
                works[lockIndex].Init();
            }

            long totalConcurrent = 0;
            long totalExclusive = 0;
            long totalExclusiveTicks = 0;
            long checksum = 0;
            int threadsPerLock = options.Threads;
            int totalThreads = checked((int)options.TotalWorkerThreads);

            ThreadRunMeasurement measurement = DedicatedThreadHarness.Run(
                totalThreads,
                "Throughput",
                globalWorkerIndex =>
                {
                    int lockIndex = globalWorkerIndex / threadsPerLock;
                    int localWorkerIndex = globalWorkerIndex % threadsPerLock;
                    ILockStrategy strategy = strategies[lockIndex];
                    IWork work = works[lockIndex];
                    uint random = DeterministicRandom.CreateWorkerSeed(lockIndex, localWorkerIndex);
                    long localConcurrent = 0;
                    long localExclusive = 0;
                    long localExclusiveTicks = 0;
                    long localChecksum = 0;

                    for (int operation = 0; operation < options.OperationsPerThread; operation++)
                    {
                        random = DeterministicRandom.Next(unchecked(random + (uint)operation));
                        if (DeterministicRandom.IsConcurrent(random, concurrentPermille))
                        {
                            localChecksum = unchecked(localChecksum + strategy.ExecuteConcurrent(work));
                            localConcurrent++;
                        }
                        else
                        {
                            long start = Stopwatch.GetTimestamp();
                            localChecksum = unchecked(localChecksum + strategy.ExecuteExclusive(work));
                            localExclusiveTicks += Stopwatch.GetTimestamp() - start;
                            localExclusive++;
                        }
                    }

                    Interlocked.Add(ref totalConcurrent, localConcurrent);
                    Interlocked.Add(ref totalExclusive, localExclusive);
                    Interlocked.Add(ref totalExclusiveTicks, localExclusiveTicks);
                    Interlocked.Add(ref checksum, localChecksum);
                });

            return new ThroughputResult(
                strategyDefinition.Name,
                measurement.Elapsed,
                MeasurementMath.CpuPercent(measurement.CpuTime, measurement.Elapsed),
                totalConcurrent,
                totalExclusive,
                CombineStateHashes(works),
                totalExclusiveTicks,
                checksum);
        }
        finally
        {
            for (int i = works.Length - 1; i >= 0; i--)
            {
                works[i]?.Dispose();
                strategies[i]?.Dispose();
            }
        }
    }

    internal static long CombineStateHashes(IWork[] works)
    {
        if (works.Length == 1) return works[0].StateHash;
        unchecked
        {
            ulong combined = 0x6A09E667F3BCC909UL;
            for (int i = 0; i < works.Length; i++)
            {
                ulong state = (ulong)works[i].StateHash;
                combined ^= state + 0x9E3779B97F4A7C15UL + (combined << 6) + (combined >> 2);
                combined ^= (uint)i;
            }
            return (long)combined;
        }
    }
}

/// <summary>Deterministic operation-mix generator used by throughput and acquisition-latency modes.</summary>
/// <remarks>
/// Porting contract:
/// - One independent 32-bit stream is derived from (lockIndex, localWorkerIndex).
/// - The stream uses xorshift32 with unsigned 32-bit wraparound.
/// - Before each xorshift step, the zero-based operation index is added modulo 2^32.
/// - Concurrent selection is value % 1000 &lt; concurrentPermille, with explicit 0/1000 fast paths.
/// Preserve this sequence so every compared strategy receives the same operation type at every
/// worker/operation coordinate. This generator is unrelated to PortableRandom semantic stress.
/// </remarks>
internal static class DeterministicRandom
{
    public static bool IsConcurrent(uint random, int concurrentPermille) =>
        concurrentPermille == 1_000 || (concurrentPermille != 0 && random % 1_000 < concurrentPermille);

    public static uint Next(uint value)
    {
        unchecked
        {
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            return value;
        }
    }

    public static uint CreateWorkerSeed(int lockIndex, int localWorkerIndex)
    {
        unchecked
        {
            uint value = 0x9E3779B9u;
            value ^= (uint)lockIndex * 0x85EBCA6Bu;
            value ^= (uint)localWorkerIndex * 0xC2B2AE35u;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }
    }
}
