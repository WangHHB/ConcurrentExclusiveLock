using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

/// <summary>
/// Multi-lock isolation smoke test: each lock competes only with its own workers and ContextID upgrades must not interfere across locks.
/// </summary>
internal sealed class AdvancedMassiveIndependentLocksCase : IAdvancedLockCorrectnessCase
{
    private readonly int lockInstances;
    private readonly int operationsPerLock;
    private readonly int seed;

    public AdvancedMassiveIndependentLocksCase(int lockInstances, int operationsPerLock, int seed)
    {
        if (lockInstances < 1) throw new ArgumentOutOfRangeException(nameof(lockInstances));
        if (operationsPerLock < 1) throw new ArgumentOutOfRangeException(nameof(operationsPerLock));
        this.lockInstances = lockInstances;
        this.operationsPerLock = operationsPerLock;
        this.seed = seed;
    }

    public string Name => "Mass independent locks preserve advanced semantics";

    public void Run()
    {
        SharedConcurrentExclusiveLock[] locks = new SharedConcurrentExclusiveLock[lockInstances];
        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = new SharedConcurrentExclusiveLock();
        }

        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        int totalWinners = 0;
        int totalLosers = 0;

        for (int lockIndex = 0; lockIndex < locks.Length; lockIndex++)
        {
            int capturedLockIndex = lockIndex;
            for (int participant = 0; participant < 2; participant++)
            {
                int capturedParticipant = participant;
                threads.Start($"mass-lock-{capturedLockIndex}-{capturedParticipant}", () =>
                {
                    PortableRandom random = new PortableRandom(PortableSeed.Derive(seed, capturedLockIndex, 397U, capturedParticipant, 1U));
                    start.Wait();
                    for (int operation = 0; operation < operationsPerLock; operation++)
                    {
                        ref ConcurrentExclusiveLock locker = ref locks[capturedLockIndex].Value;
                        switch (random.Next(4))
                        {
                            case 0:
                                locker.AcquireConcurrent();
                                locker.ReleaseConcurrent();
                                break;

                            case 1:
                                locker.AcquireExclusive();
                                locker.ReleaseExclusive();
                                break;

                            case 2:
                                locker.AcquireExclusive();
                                locker.ExclusiveToConcurrent();
                                locker.ReleaseConcurrent();
                                break;

                            default:
                                locker.AcquireConcurrent();
                                if (locker.TryConcurrentToExclusiveWithSwitchContextID(
                                        10_000 + capturedLockIndex * 10 + capturedParticipant))
                                {
                                    Interlocked.Increment(ref totalWinners);
                                    locker.ReleaseExclusive();
                                }
                                else
                                {
                                    Interlocked.Increment(ref totalLosers);
                                    // A failed upgrade has already released the caller's Concurrent permission.
                                }
                                break;
                        }
                    }
                });
            }
        }

        start.Set();
        threads.JoinAll(Name, TimeSpan.FromSeconds(Math.Max(10, lockInstances / 20)));

        for (int i = 0; i < locks.Length; i++)
        {
            ref ConcurrentExclusiveLock locker = ref locks[i].Value;
            if (!locker.TryAcquireExclusive(preemptConcurrent: false))
            {
                throw new InvalidOperationException($"Lock {i} was not reusable after massive independent test.");
            }

            locker.ReleaseExclusive();
        }

        Console.WriteLine(
            $"       mass locks={lockInstances:n0}, operations/lock={operationsPerLock:n0}, " +
            $"context-upgrade winners={totalWinners:n0}, losers={totalLosers:n0}, seed={seed}");
    }
}
