using IntomicLib;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LockBenchmark;

/// <summary>CEL-specific simultaneous in-place upgrade contention experiment.</summary>
/// <remarks>
/// Porting contract for each lock group:
/// 1. N workers acquire Concurrent and wait at the upgrade gate.
/// 2. M ordinary Exclusive contenders become ready.
/// 3. All lock groups are released from one global gate.
/// 4. Ordinary Exclusive must not enter before its own N-upgrader chain has drained.
/// Acquisition/release samples and drain times are recorded both globally and per lock. N and M
/// are literal per-lock populations; M may be zero. Platform RW-locks are not included because they
/// do not expose CEL's undeclared direct in-place upgrade semantics.
/// </remarks>
internal static class UpgradeContentionRunner
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromMinutes(2);

    private sealed record UpgradeGroupRawResult(
        int LockIndex,
        TimeSpan FirstUpgrade,
        TimeSpan Drain,
        long[] UpgradeAcquireTicks,
        long[] UpgradeReleaseTicks,
        long[] OrdinaryAcquireTicks,
        int OrdinaryEnteredBeforeUpgradeDrain);

    public static int Run(BenchmarkOptions options, BenchmarkSession session)
    {
        int concurrentThreadsPerLock = options.UpgradeContentionConcurrentThreads!.Value;
        int ordinaryThreadsPerLock = options.UpgradeContentionExclusiveThreads;
        long totalUpgradeThreads = checked((long)options.LockInstances * concurrentThreadsPerLock);
        long totalOrdinaryThreads = checked((long)options.LockInstances * ordinaryThreadsPerLock);

        Console.WriteLine("Concurrent-to-Exclusive upgrade contention");
        Console.WriteLine(
            $"lock-instances={options.LockInstances:n0}, upgrade-threads/lock={concurrentThreadsPerLock:n0}, " +
            $"ordinary-exclusive/lock={ordinaryThreadsPerLock:n0}, total-upgrade-threads={totalUpgradeThreads:n0}, " +
            $"total-ordinary-exclusive={totalOrdinaryThreads:n0}");
        Console.WriteLine();

        _ = RunOne(lockInstances: 1, concurrentThreadsPerLock: 1, ordinaryThreadsPerLock: 1);

        Console.WriteLine(
            $"  {"first",11}  {"drain",11}  {"upgrades/s",12}  {"acq p50",11} {"acq p95",11} " +
            $"{"acq p99",11} {"acq max",11}  {"worst-lock p99",15} {"worst drain",12} {"ordinary-before",15}");

        UpgradeContentionResult result = RunOne(options.LockInstances, concurrentThreadsPerLock, ordinaryThreadsPerLock);
        Console.WriteLine(
            $"  {BenchmarkReporter.FormatDuration(result.FirstUpgrade),11}  {BenchmarkReporter.FormatDuration(result.Drain),11}  " +
            $"{result.UpgradeThroughput,12:0}  {BenchmarkReporter.FormatLatency(result.UpgradeAcquireLatency.P50Ns),11} " +
            $"{BenchmarkReporter.FormatLatency(result.UpgradeAcquireLatency.P95Ns),11} {BenchmarkReporter.FormatLatency(result.UpgradeAcquireLatency.P99Ns),11} " +
            $"{BenchmarkReporter.FormatLatency(result.UpgradeAcquireLatency.MaxNs),11}  " +
            $"{BenchmarkReporter.FormatLatency(result.WorstLockAcquireP99Ns),15} " +
            $"{BenchmarkReporter.FormatLatency(result.WorstLockDrainNs),12} {result.OrdinaryEnteredBeforeUpgradeDrain,15:n0}");

        session.Write("upgrade-contention", new
        {
            options.LockInstances,
            upgradeThreadsPerLock = concurrentThreadsPerLock,
            ordinaryExclusiveThreadsPerLock = ordinaryThreadsPerLock,
            totalUpgradeThreads,
            totalOrdinaryExclusiveThreads = totalOrdinaryThreads,
            firstUpgradeMicroseconds = result.FirstUpgrade.TotalMicroseconds,
            drainMicroseconds = result.Drain.TotalMicroseconds,
            result.UpgradeThroughput,
            result.UpgradeAcquireLatency,
            result.UpgradeReleaseLatency,
            result.OrdinaryAcquireLatency,
            result.WorstLockAcquireP99Ns,
            result.WorstLockDrainNs,
            result.OrdinaryEnteredBeforeUpgradeDrain,
            perLock = result.PerLock
        });

        Console.WriteLine();
        Console.WriteLine(
            $"[PASS] {options.LockInstances:n0} lock instance(s); " +
            "no ordinary Exclusive request entered before its own upgrade chain drained.");
        return 0;
    }

    private static UpgradeContentionResult RunOne(
        int lockInstances,
        int concurrentThreadsPerLock,
        int ordinaryThreadsPerLock)
    {
        UpgradeGroupRawResult?[] rawResults = new UpgradeGroupRawResult?[lockInstances];
        Thread[] controllers = new Thread[lockInstances];
        ExceptionDispatchInfo? firstFailure = null;
        using CountdownEvent groupsReady = new(lockInstances);
        using ManualResetEventSlim releaseAllGroups = new(false);

        void Capture(Exception exception) =>
            Interlocked.CompareExchange(ref firstFailure, ExceptionDispatchInfo.Capture(exception), null);

        for (int lockIndex = 0; lockIndex < lockInstances; lockIndex++)
        {
            int capturedLockIndex = lockIndex;
            controllers[lockIndex] = new Thread(() =>
            {
                bool readySignaled = false;
                try
                {
                    rawResults[capturedLockIndex] = RunGroup(
                        capturedLockIndex,
                        concurrentThreadsPerLock,
                        ordinaryThreadsPerLock,
                        () =>
                        {
                            groupsReady.Signal();
                            readySignaled = true;
                        },
                        releaseAllGroups,
                        Capture,
                        ref firstFailure);
                }
                catch (Exception exception)
                {
                    Capture(exception);
                }
                finally
                {
                    if (!readySignaled) groupsReady.Signal();
                }
            })
            {
                IsBackground = true,
                Name = $"UpgradeContention-Controller-L{lockIndex}"
            };
            controllers[lockIndex].Start();
        }

        if (!groupsReady.Wait(PhaseTimeout))
        {
            releaseAllGroups.Set();
            throw new TimeoutException("Upgrade contention groups did not become ready.");
        }

        releaseAllGroups.Set();
        foreach (Thread controller in controllers)
        {
            if (!controller.Join(PhaseTimeout))
            {
                throw new TimeoutException($"Upgrade contention controller did not terminate: {controller.Name}.");
            }
        }
        firstFailure?.Throw();

        UpgradeGroupRawResult[] groups = rawResults
            .Select((result, index) => result ?? throw new InvalidOperationException($"Missing result for lock instance {index}."))
            .ToArray();

        UpgradeContentionPerLockResult[] perLock = groups.Select(group => new UpgradeContentionPerLockResult(
            group.LockIndex,
            group.FirstUpgrade,
            group.Drain,
            Statistics.SummarizeTicks(group.UpgradeAcquireTicks),
            Statistics.SummarizeTicks(group.UpgradeReleaseTicks),
            Statistics.SummarizeTicks(group.OrdinaryAcquireTicks),
            group.OrdinaryEnteredBeforeUpgradeDrain)).ToArray();

        return new UpgradeContentionResult(
            lockInstances,
            concurrentThreadsPerLock,
            ordinaryThreadsPerLock,
            perLock.Min(result => result.FirstUpgrade),
            perLock.Max(result => result.Drain),
            Statistics.SummarizeTicks(groups.SelectMany(group => group.UpgradeAcquireTicks)),
            Statistics.SummarizeTicks(groups.SelectMany(group => group.UpgradeReleaseTicks)),
            Statistics.SummarizeTicks(groups.SelectMany(group => group.OrdinaryAcquireTicks)),
            perLock.Sum(result => result.OrdinaryEnteredBeforeUpgradeDrain),
            perLock);
    }

    private static UpgradeGroupRawResult RunGroup(
        int lockIndex,
        int concurrentThreads,
        int ordinaryThreads,
        Action signalGroupReady,
        ManualResetEventSlim releaseAllGroups,
        Action<Exception> capture,
        ref ExceptionDispatchInfo? sharedFailure)
    {
        ConcurrentExclusiveLock cel = ConcurrentExclusiveLock.Create();
        int abort = 0;
        int activeExclusive = 0;
        int completedUpgrades = 0;
        int remainingUpgrades = concurrentThreads;
        int upgradeDrainPublished = 0;
        int completedOrdinary = 0;
        int ordinaryEnteredBeforeDrain = 0;
        long startTimestamp = 0;
        long[] upgradeAcquire = new long[concurrentThreads];
        long[] upgradeRelease = new long[concurrentThreads];
        long[] ordinaryAcquire = new long[ordinaryThreads];

        using CountdownEvent concurrentEntered = new(concurrentThreads);
        using CountdownEvent ordinaryReady = new(ordinaryThreads);
        using ManualResetEventSlim ordinaryStart = new(false);
        using ManualResetEventSlim upgradeStart = new(false);
        using ManualResetEventSlim upgradesCompleted = new(false);
        Thread[] upgrades = new Thread[concurrentThreads];
        Thread[] ordinary = new Thread[ordinaryThreads];

        for (int i = 0; i < upgrades.Length; i++)
        {
            int index = i;
            upgrades[i] = new Thread(() =>
            {
                bool concurrent = false;
                bool exclusive = false;
                try
                {
                    cel.AcquireConcurrent();
                    concurrent = true;
                    concurrentEntered.Signal();
                    upgradeStart.Wait();
                    if (Volatile.Read(ref abort) != 0) return;
                    cel.ConcurrentToExclusive();
                    concurrent = false;
                    exclusive = true;
                    long acquired = Stopwatch.GetTimestamp();
                    upgradeAcquire[index] = acquired - Volatile.Read(ref startTimestamp);
                    if (Interlocked.Increment(ref activeExclusive) != 1) throw new InvalidOperationException("Exclusive isolation failed during upgrade.");
                    Thread.SpinWait(1);
                    if (Interlocked.Decrement(ref activeExclusive) != 0) throw new InvalidOperationException("Exclusive counter failed during upgrade.");
                    if (Interlocked.Decrement(ref remainingUpgrades) == 0) Volatile.Write(ref upgradeDrainPublished, 1);
                    cel.ReleaseExclusive();
                    exclusive = false;
                    upgradeRelease[index] = Stopwatch.GetTimestamp() - Volatile.Read(ref startTimestamp);
                    if (Interlocked.Increment(ref completedUpgrades) == concurrentThreads) upgradesCompleted.Set();
                }
                catch (Exception exception)
                {
                    capture(exception);
                    upgradesCompleted.Set();
                }
                finally
                {
                    try
                    {
                        if (exclusive) cel.ReleaseExclusive();
                        else if (concurrent) cel.ReleaseConcurrent();
                    }
                    catch (Exception exception) { capture(exception); }
                }
            })
            {
                IsBackground = true,
                Name = $"Upgrade-L{lockIndex}-W{index}"
            };
            upgrades[i].Start();
        }

        if (!concurrentEntered.Wait(PhaseTimeout)) throw new TimeoutException($"Concurrent holders did not enter for lock {lockIndex}.");

        for (int i = 0; i < ordinary.Length; i++)
        {
            int index = i;
            ordinary[i] = new Thread(() =>
            {
                bool exclusive = false;
                try
                {
                    ordinaryReady.Signal();
                    ordinaryStart.Wait();
                    if (Volatile.Read(ref abort) != 0) return;
                    long before = Stopwatch.GetTimestamp();
                    cel.AcquireExclusive();
                    ordinaryAcquire[index] = Stopwatch.GetTimestamp() - before;
                    exclusive = true;
                    if (Volatile.Read(ref upgradeDrainPublished) == 0) Interlocked.Increment(ref ordinaryEnteredBeforeDrain);
                    if (Interlocked.Increment(ref activeExclusive) != 1) throw new InvalidOperationException("Exclusive isolation failed for ordinary contender.");
                    Thread.SpinWait(1);
                    if (Interlocked.Decrement(ref activeExclusive) != 0) throw new InvalidOperationException("Exclusive counter failed for ordinary contender.");
                    cel.ReleaseExclusive();
                    exclusive = false;
                    Interlocked.Increment(ref completedOrdinary);
                }
                catch (Exception exception) { capture(exception); }
                finally
                {
                    try { if (exclusive) cel.ReleaseExclusive(); }
                    catch (Exception exception) { capture(exception); }
                }
            })
            {
                IsBackground = true,
                Name = $"OrdinaryExclusive-L{lockIndex}-W{index}"
            };
            ordinary[i].Start();
        }

        if (!ordinaryReady.Wait(PhaseTimeout)) throw new TimeoutException($"Ordinary Exclusive contenders did not become ready for lock {lockIndex}.");
        ordinaryStart.Set();
        if (ordinaryThreads > 0)
        {
            Stopwatch pressure = Stopwatch.StartNew();
            while (cel.ObservedContention < concurrentThreads + 1)
            {
                Volatile.Read(ref sharedFailure)?.Throw();
                if (pressure.Elapsed >= PhaseTimeout)
                {
                    throw new TimeoutException($"Ordinary Exclusive pressure did not become observable for lock {lockIndex}.");
                }
                Thread.Yield();
            }
        }

        signalGroupReady();
        releaseAllGroups.Wait();
        Volatile.Write(ref startTimestamp, Stopwatch.GetTimestamp());
        upgradeStart.Set();

        if (!upgradesCompleted.Wait(PhaseTimeout))
        {
            Volatile.Write(ref abort, 1);
            throw new TimeoutException($"Upgrade drain timed out for lock {lockIndex}: completed={completedUpgrades:n0}/{concurrentThreads:n0}.");
        }
        Volatile.Read(ref sharedFailure)?.Throw();

        foreach (Thread thread in upgrades)
        {
            if (!thread.Join(PhaseTimeout)) throw new TimeoutException($"Upgrade thread did not terminate: {thread.Name}.");
        }
        foreach (Thread thread in ordinary)
        {
            if (!thread.Join(PhaseTimeout)) throw new TimeoutException($"Ordinary thread did not terminate: {thread.Name}.");
        }
        Volatile.Read(ref sharedFailure)?.Throw();

        if (completedUpgrades != concurrentThreads || completedOrdinary != ordinaryThreads)
            throw new InvalidOperationException($"Contention completion count mismatch for lock {lockIndex}.");
        if (ordinaryEnteredBeforeDrain != 0)
            throw new InvalidOperationException($"Lock {lockIndex}: {ordinaryEnteredBeforeDrain} ordinary Exclusive request(s) entered before upgrade drain.");
        if (cel.ObservedState != ConcurrentExclusiveLockState.Idle || cel.ObservedContention != 0)
            throw new InvalidOperationException($"Lock {lockIndex} did not return to Idle: {cel.ObservedState}, contention={cel.ObservedContention}.");

        long first = upgradeAcquire.Min();
        long drain = upgradeRelease.Max();
        return new UpgradeGroupRawResult(
            lockIndex,
            Stopwatch.GetElapsedTime(0, first),
            Stopwatch.GetElapsedTime(0, drain),
            upgradeAcquire,
            upgradeRelease,
            ordinaryAcquire,
            ordinaryEnteredBeforeDrain);
    }
}
