using System;
using System.Threading;
using IntomicLib;

namespace LockBenchmark;

internal sealed class RandomizedValidSemanticPathsCase : IAdvancedLockCorrectnessCase
{
    private readonly int lockInstances;
    private readonly int workersPerLock;
    private readonly int roundsPerLock;
    private readonly int seed;
    private readonly SharedConcurrentExclusiveLock[] sharedLocks;
    private readonly bool printSummary;
    private long concurrentPaths;
    private long exclusivePaths;
    private long downgradePaths;
    private long contextUpgradePaths;
    private long conversionCyclePaths;
    private int nextEpochId = 0x10000000;

    public RandomizedValidSemanticPathsCase(
        int lockInstances,
        int workersPerLock,
        int roundsPerLock,
        int seed,
        SharedConcurrentExclusiveLock[] sharedLocks = null,
        bool printSummary = true)
    {
        this.lockInstances = Math.Max(1, lockInstances);
        this.workersPerLock = Math.Max(1, workersPerLock);
        this.roundsPerLock = Math.Max(1, roundsPerLock);
        this.seed = seed;
        this.sharedLocks = sharedLocks;
        this.printSummary = printSummary;
    }

    public string Name => "Randomized valid call paths preserve access and transition invariants";

    public long[] LastOperationCounts =>
        new[]
        {
            Volatile.Read(ref concurrentPaths),
            Volatile.Read(ref exclusivePaths),
            Volatile.Read(ref downgradePaths),
            Volatile.Read(ref contextUpgradePaths),
            Volatile.Read(ref conversionCyclePaths)
        };

    public void Run()
    {
        SharedConcurrentExclusiveLock[] locks = sharedLocks ?? CreateLocks(lockInstances);
        AccessTracker[] trackers = new AccessTracker[locks.Length];
        for (int i = 0; i < trackers.Length; i++)
        {
            trackers[i] = new AccessTracker(i);
        }

        AdvancedTestThreadGroup threads = new AdvancedTestThreadGroup();
        ManualResetEventSlim start = new ManualResetEventSlim(false);
        int totalThreads = locks.Length * workersPerLock;
        CountdownEvent ready = new CountdownEvent(totalThreads);

        for (int lockIndex = 0; lockIndex < locks.Length; lockIndex++)
        {
            int capturedLockIndex = lockIndex;
            for (int workerIndex = 0; workerIndex < workersPerLock; workerIndex++)
            {
                int capturedWorkerIndex = workerIndex;
                threads.Start($"random-semantic-{capturedLockIndex}-{capturedWorkerIndex}", () =>
                {
                    Random random = new Random(seed + capturedLockIndex * 1009 + capturedWorkerIndex * 9176);
                    ready.Signal();
                    start.Wait();
                    for (int round = 0; round < roundsPerLock; round++)
                    {
                        ExecuteRandomPath(
                            ref locks[capturedLockIndex].Value,
                            trackers[capturedLockIndex],
                            random,
                            random.Next(5),
                            capturedLockIndex,
                            capturedWorkerIndex,
                            round);
                    }
                });
            }
        }

        AdvancedAssert.Wait(ready, "Random semantic workers did not become ready.");
        start.Set();
        threads.JoinAll(Name, TimeSpan.FromSeconds(Math.Max(10, locks.Length * workersPerLock / 50)));

        for (int i = 0; i < locks.Length; i++)
        {
            trackers[i].AssertIdle("randomized semantic completion");
            ref ConcurrentExclusiveLock locker = ref locks[i].Value;
            if (!locker.TryAcquireExclusive(preemptConcurrent: false))
            {
                throw new InvalidOperationException($"Lock {i} was not reusable after randomized semantic paths.");
            }

            locker.ReleaseExclusive();
        }

        if (printSummary)
        {
            Console.WriteLine(
                $"       random paths concurrent={concurrentPaths:n0}, exclusive={exclusivePaths:n0}, " +
                $"downgrade={downgradePaths:n0}, upgrade={contextUpgradePaths:n0}, " +
                $"conversion-cycle={conversionCyclePaths:n0}");
        }
    }

    private static SharedConcurrentExclusiveLock[] CreateLocks(int count)
    {
        SharedConcurrentExclusiveLock[] result = new SharedConcurrentExclusiveLock[count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new SharedConcurrentExclusiveLock();
        }

        return result;
    }

    private void ExecuteRandomPath(
        ref ConcurrentExclusiveLock locker,
        AccessTracker tracker,
        Random random,
        int path,
        int lockIndex,
        int workerIndex,
        int round)
    {
        switch (path)
        {
            case 0:
                Interlocked.Increment(ref concurrentPaths);
                locker.AcquireConcurrent();
                tracker.EnterConcurrent("random concurrent");
                DoRandomWork(random, workerIndex, round);
                tracker.ExitConcurrent("random concurrent");
                locker.ReleaseConcurrent();
                break;

            case 1:
                Interlocked.Increment(ref exclusivePaths);
                locker.AcquireExclusive();
                tracker.EnterExclusive("random exclusive");
                DoRandomWork(random, workerIndex, round);
                tracker.ExitExclusive("random exclusive");
                locker.ReleaseExclusive();
                break;

            case 2:
                Interlocked.Increment(ref downgradePaths);
                locker.AcquireExclusive();
                tracker.EnterExclusive("random downgrade exclusive");
                DoRandomWork(random, workerIndex, round);
                tracker.ExitExclusive("random downgrade exclusive");
                locker.ExclusiveToConcurrent();
                tracker.EnterConcurrent("random downgrade concurrent");
                DoRandomWork(random, workerIndex, round + 11);
                tracker.ExitConcurrent("random downgrade concurrent");
                locker.ReleaseConcurrent();
                break;

            case 3:
                Interlocked.Increment(ref contextUpgradePaths);
                locker.AcquireConcurrent();
                tracker.EnterConcurrent("random context-upgrade concurrent");
                DoRandomWork(random, workerIndex, round);
                tracker.ExitConcurrent("random context-upgrade handoff");
                bool contextUpgradeWon = (round & 1) == 0
                    ? locker.TryConcurrentToExclusiveWithSwitchContextID(CreateContextId(0x61, lockIndex, workerIndex, round))
                    : locker.TryConcurrentToExclusiveWithRaiseEpochID(CreateEpochId());
                if (contextUpgradeWon)
                {
                    tracker.EnterExclusive("random context-upgrade isolated runner");
                    DoRandomWork(random, workerIndex, round + 23);
                    tracker.ExitExclusive("random context-upgrade isolated runner");
                    locker.ReleaseExclusive();
                }
                break;

            default:
                Interlocked.Increment(ref conversionCyclePaths);
                locker.AcquireExclusive();
                tracker.EnterExclusive("random conversion-cycle exclusive");
                DoRandomWork(random, workerIndex, round);
                tracker.ExitExclusive("random conversion-cycle exclusive");
                locker.ExclusiveToConcurrent();
                tracker.EnterConcurrent("random conversion-cycle concurrent");
                DoRandomWork(random, workerIndex, round + 37);
                tracker.ExitConcurrent("random conversion-cycle handoff");
                bool conversionUpgradeWon = (round & 1) == 0
                    ? locker.TryConcurrentToExclusiveWithSwitchContextID(CreateContextId(0x62, lockIndex, workerIndex, round))
                    : locker.TryConcurrentToExclusiveWithRaiseEpochID(CreateEpochId());
                if (conversionUpgradeWon)
                {
                    tracker.EnterExclusive("random conversion-cycle isolated runner");
                    DoRandomWork(random, workerIndex, round + 41);
                    tracker.ExitExclusive("random conversion-cycle isolated runner");
                    locker.ReleaseExclusive();
                }
                break;
        }
    }

    private static int CreateContextId(int family, int lockIndex, int workerIndex, int round)
    {
        return (family << 24) |
               ((lockIndex & 0x3FF) << 14) |
               ((workerIndex & 0x3F) << 8) |
               (round & 0xFF);
    }

    private int CreateEpochId()
    {
        return Interlocked.Increment(ref nextEpochId);
    }

    private static void DoRandomWork(Random random, int workerIndex, int round)
    {
        if (random.Next(4) == 0)
        {
            return;
        }

        DoTinyWork(workerIndex, round);
    }

    private static void DoTinyWork(int workerIndex, int round)
    {
        ulong value = ((ulong)workerIndex << 32) ^ (uint)round ^ 0xD6E8FEB86659FD93UL;
        value ^= value << 13;
        value ^= value >> 7;
        AdvancedSemanticSink.Add(unchecked((long)value));
    }

    private sealed class AccessTracker
    {
        private readonly int lockIndex;
        private int activeConcurrent;
        private int activeExclusive;

        public AccessTracker(int lockIndex)
        {
            this.lockIndex = lockIndex;
        }

        public void EnterConcurrent(string operation)
        {
            int concurrent = Interlocked.Increment(ref activeConcurrent);
            int exclusive = Volatile.Read(ref activeExclusive);
            if (exclusive != 0)
            {
                throw new InvalidOperationException(
                    $"Lock {lockIndex}: Concurrent overlapped Exclusive during {operation}. " +
                    $"concurrent={concurrent}, exclusive={exclusive}.");
            }
        }

        public void ExitConcurrent(string operation)
        {
            int concurrent = Interlocked.Decrement(ref activeConcurrent);
            if (concurrent < 0)
            {
                throw new InvalidOperationException($"Lock {lockIndex}: Concurrent underflow during {operation}.");
            }
        }

        public void EnterExclusive(string operation)
        {
            int exclusive = Interlocked.Increment(ref activeExclusive);
            int concurrent = Volatile.Read(ref activeConcurrent);
            if (exclusive != 1 || concurrent != 0)
            {
                throw new InvalidOperationException(
                    $"Lock {lockIndex}: Exclusive overlapped during {operation}. " +
                    $"concurrent={concurrent}, exclusive={exclusive}.");
            }
        }

        public void ExitExclusive(string operation)
        {
            int exclusive = Interlocked.Decrement(ref activeExclusive);
            if (exclusive != 0)
            {
                throw new InvalidOperationException(
                    $"Lock {lockIndex}: Exclusive underflow/overlap during {operation}. exclusive={exclusive}.");
            }
        }

        public void AssertIdle(string operation)
        {
            int concurrent = Volatile.Read(ref activeConcurrent);
            int exclusive = Volatile.Read(ref activeExclusive);
            if (concurrent != 0 || exclusive != 0)
            {
                throw new InvalidOperationException(
                    $"Lock {lockIndex}: tracker not idle after {operation}. " +
                    $"concurrent={concurrent}, exclusive={exclusive}.");
            }
        }
    }
}
