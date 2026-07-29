using IntomicLib;
using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Reproduces the strongest single-lock upgrade contention window:
/// N threads first hold Concurrent together, M ordinary Exclusive requests then enter contention,
/// and all N Concurrent holders are released to upgrade to Exclusive at the same instant.
/// </summary>
internal static class UpgradeContentionRunner
{
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromMinutes(2);

    public static int Run(int concurrentThreads, int ordinaryExclusiveThreads)
    {
        if (concurrentThreads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(concurrentThreads));
        }

        if (ordinaryExclusiveThreads < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinaryExclusiveThreads));
        }

        ConcurrentExclusiveLock cel = ConcurrentExclusiveLock.Create();
        ExceptionDispatchInfo? firstFailure = null;
        int abort = 0;
        int activeExclusive = 0;
        int completedUpgrades = 0;
        int remainingUpgrades = concurrentThreads;
        int upgradeDrainReleased = 0;
        int completedOrdinaryExclusive = 0;
        int ordinaryEnteredBeforeUpgradeDrain = 0;
        long firstUpgradeAcquiredTimestamp = long.MaxValue;
        long lastUpgradeReleasedTimestamp = 0;

        using CountdownEvent concurrentEntered = new CountdownEvent(concurrentThreads);
        using CountdownEvent ordinaryReady = new CountdownEvent(ordinaryExclusiveThreads);
        using ManualResetEventSlim ordinaryStartGate = new ManualResetEventSlim(false);
        using ManualResetEventSlim upgradeStartGate = new ManualResetEventSlim(false);
        using ManualResetEventSlim upgradeDrainCompleted = new ManualResetEventSlim(false);

        Thread[] upgradeWorkers = new Thread[concurrentThreads];
        Thread[] ordinaryWorkers = new Thread[ordinaryExclusiveThreads];

        void CaptureFailure(Exception exception)
        {
            Interlocked.CompareExchange(
                ref firstFailure,
                ExceptionDispatchInfo.Capture(exception),
                null);
        }

        for (int workerIndex = 0; workerIndex < upgradeWorkers.Length; workerIndex++)
        {
            int capturedWorkerIndex = workerIndex;
            upgradeWorkers[workerIndex] = new Thread(() =>
            {
                bool holdsConcurrent = false;
                bool holdsExclusive = false;
                try
                {
                    cel.AcquireConcurrent();
                    holdsConcurrent = true;
                    concurrentEntered.Signal();

                    upgradeStartGate.Wait();
                    if (Volatile.Read(ref abort) != 0)
                    {
                        return;
                    }

                    cel.ConcurrentToExclusive();
                    holdsConcurrent = false;
                    holdsExclusive = true;

                    long acquiredTimestamp = Stopwatch.GetTimestamp();
                    SetMinimum(ref firstUpgradeAcquiredTimestamp, acquiredTimestamp);

                    int exclusiveCount = Interlocked.Increment(ref activeExclusive);
                    if (exclusiveCount != 1)
                    {
                        throw new InvalidOperationException(
                            $"Exclusive isolation failed in upgrade worker {capturedWorkerIndex}: active={exclusiveCount}.");
                    }

                    // Keep the Exclusive region deliberately minimal. The measured interval is primarily
                    // the protocol's ability to serialize and drain all simultaneous upgrade requests.
                    Thread.SpinWait(1);

                    if (Interlocked.Decrement(ref activeExclusive) != 0)
                    {
                        throw new InvalidOperationException(
                            $"Exclusive activity counter failed to return to zero in upgrade worker {capturedWorkerIndex}.");
                    }

                    if (Interlocked.Decrement(ref remainingUpgrades) == 0)
                    {
                        // Publish before the last ReleaseExclusive. Ordinary Exclusive cannot enter
                        // until that release completes, so it cannot observe a stale "not drained" state.
                        Volatile.Write(ref upgradeDrainReleased, 1);
                    }

                    cel.ReleaseExclusive();
                    holdsExclusive = false;

                    long releasedTimestamp = Stopwatch.GetTimestamp();
                    SetMaximum(ref lastUpgradeReleasedTimestamp, releasedTimestamp);
                    if (Interlocked.Increment(ref completedUpgrades) == concurrentThreads)
                    {
                        upgradeDrainCompleted.Set();
                    }
                }
                catch (Exception exception)
                {
                    CaptureFailure(exception);
                    upgradeDrainCompleted.Set();
                }
                finally
                {
                    try
                    {
                        if (holdsExclusive)
                        {
                            if (Volatile.Read(ref activeExclusive) > 0)
                            {
                                Interlocked.Decrement(ref activeExclusive);
                            }

                            cel.ReleaseExclusive();
                        }
                        else if (holdsConcurrent)
                        {
                            cel.ReleaseConcurrent();
                        }
                    }
                    catch (Exception exception)
                    {
                        CaptureFailure(exception);
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"CEL-Upgrade-{capturedWorkerIndex}"
            };
            upgradeWorkers[workerIndex].Start();
        }

        if (!concurrentEntered.Wait(PhaseTimeout))
        {
            Volatile.Write(ref abort, 1);
            upgradeStartGate.Set();
            Console.WriteLine(
                $"[FAIL] Concurrent entry timed out: entered={concurrentThreads - concurrentEntered.CurrentCount:n0}/{concurrentThreads:n0}.");
            return 1;
        }

        for (int workerIndex = 0; workerIndex < ordinaryWorkers.Length; workerIndex++)
        {
            int capturedWorkerIndex = workerIndex;
            ordinaryWorkers[workerIndex] = new Thread(() =>
            {
                bool holdsExclusive = false;
                try
                {
                    ordinaryReady.Signal();
                    ordinaryStartGate.Wait();
                    if (Volatile.Read(ref abort) != 0)
                    {
                        return;
                    }

                    cel.AcquireExclusive();
                    holdsExclusive = true;

                    if (Volatile.Read(ref upgradeDrainReleased) == 0)
                    {
                        Interlocked.Increment(ref ordinaryEnteredBeforeUpgradeDrain);
                    }

                    int exclusiveCount = Interlocked.Increment(ref activeExclusive);
                    if (exclusiveCount != 1)
                    {
                        throw new InvalidOperationException(
                            $"Exclusive isolation failed in ordinary worker {capturedWorkerIndex}: active={exclusiveCount}.");
                    }

                    Thread.SpinWait(1);

                    if (Interlocked.Decrement(ref activeExclusive) != 0)
                    {
                        throw new InvalidOperationException(
                            $"Exclusive activity counter failed to return to zero in ordinary worker {capturedWorkerIndex}.");
                    }

                    cel.ReleaseExclusive();
                    holdsExclusive = false;
                    Interlocked.Increment(ref completedOrdinaryExclusive);
                }
                catch (Exception exception)
                {
                    CaptureFailure(exception);
                }
                finally
                {
                    try
                    {
                        if (holdsExclusive)
                        {
                            if (Volatile.Read(ref activeExclusive) > 0)
                            {
                                Interlocked.Decrement(ref activeExclusive);
                            }

                            cel.ReleaseExclusive();
                        }
                    }
                    catch (Exception exception)
                    {
                        CaptureFailure(exception);
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"CEL-OrdinaryExclusive-{capturedWorkerIndex}"
            };
            ordinaryWorkers[workerIndex].Start();
        }

        if (!ordinaryReady.Wait(PhaseTimeout))
        {
            Volatile.Write(ref abort, 1);
            ordinaryStartGate.Set();
            upgradeStartGate.Set();
            Console.WriteLine(
                $"[FAIL] Ordinary Exclusive readiness timed out: ready={ordinaryExclusiveThreads - ordinaryReady.CurrentCount:n0}/{ordinaryExclusiveThreads:n0}.");
            return 1;
        }

        if (ordinaryExclusiveThreads > 0)
        {
            ordinaryStartGate.Set();

            Stopwatch pressureWait = Stopwatch.StartNew();
            while (cel.ObservedContention < concurrentThreads + 1)
            {
                firstFailure?.Throw();
                if (pressureWait.Elapsed >= PhaseTimeout)
                {
                    Volatile.Write(ref abort, 1);
                    upgradeStartGate.Set();
                    Console.WriteLine(
                        $"[FAIL] Ordinary Exclusive did not establish the preemptive contention window within {PhaseTimeout}.");
                    return 1;
                }

                Thread.Yield();
            }
        }
        else
        {
            ordinaryStartGate.Set();
        }

        long upgradeStartTimestamp = Stopwatch.GetTimestamp();
        upgradeStartGate.Set();

        if (!upgradeDrainCompleted.Wait(PhaseTimeout))
        {
            Volatile.Write(ref abort, 1);
            Console.WriteLine(
                $"[FAIL] Upgrade drain timed out after {PhaseTimeout}: completed={Volatile.Read(ref completedUpgrades):n0}/{concurrentThreads:n0}, " +
                $"ordinary-completed={Volatile.Read(ref completedOrdinaryExclusive):n0}/{ordinaryExclusiveThreads:n0}, " +
                $"state={cel.ObservedState}, contention={cel.ObservedContention:n0}.");
            return 1;
        }

        firstFailure?.Throw();

        long lastRelease = Volatile.Read(ref lastUpgradeReleasedTimestamp);
        long firstAcquire = Volatile.Read(ref firstUpgradeAcquiredTimestamp);
        TimeSpan firstAcquireElapsed = Stopwatch.GetElapsedTime(upgradeStartTimestamp, firstAcquire);
        TimeSpan allUpgradesElapsed = Stopwatch.GetElapsedTime(upgradeStartTimestamp, lastRelease);

        foreach (Thread worker in upgradeWorkers)
        {
            if (!worker.Join(PhaseTimeout))
            {
                Console.WriteLine($"[FAIL] Upgrade worker did not terminate: {worker.Name}.");
                return 1;
            }
        }

        foreach (Thread worker in ordinaryWorkers)
        {
            if (!worker.Join(PhaseTimeout))
            {
                Console.WriteLine($"[FAIL] Ordinary Exclusive worker did not terminate: {worker.Name}.");
                return 1;
            }
        }

        firstFailure?.Throw();

        if (completedUpgrades != concurrentThreads)
        {
            throw new InvalidOperationException(
                $"Upgrade completion mismatch: expected={concurrentThreads:n0}, actual={completedUpgrades:n0}.");
        }

        if (completedOrdinaryExclusive != ordinaryExclusiveThreads)
        {
            throw new InvalidOperationException(
                $"Ordinary Exclusive completion mismatch: expected={ordinaryExclusiveThreads:n0}, actual={completedOrdinaryExclusive:n0}.");
        }

        if (ordinaryEnteredBeforeUpgradeDrain != 0)
        {
            throw new InvalidOperationException(
                $"Upgrade priority failed: {ordinaryEnteredBeforeUpgradeDrain:n0} ordinary Exclusive request(s) entered before all upgrades drained.");
        }

        if (cel.ObservedState != ConcurrentExclusiveLockState.Idle || cel.ObservedContention != 0)
        {
            throw new InvalidOperationException(
                $"Lock did not return to Idle: state={cel.ObservedState}, contention={cel.ObservedContention:n0}.");
        }

        Console.WriteLine();
        Console.WriteLine("Concurrent-to-Exclusive upgrade contention:");
        Console.WriteLine($"  Concurrent upgrade threads : {concurrentThreads:n0}");
        Console.WriteLine($"  Ordinary Exclusive threads : {ordinaryExclusiveThreads:n0}");
        Console.WriteLine($"  First upgrade acquired      : {FormatDuration(firstAcquireElapsed)}");
        Console.WriteLine($"  All upgrades completed      : {FormatDuration(allUpgradesElapsed)}");
        Console.WriteLine($"  Upgrade throughput          : {concurrentThreads / allUpgradesElapsed.TotalSeconds:n0} upgrades/s");
        Console.WriteLine($"  Ordinary-before-drain       : {ordinaryEnteredBeforeUpgradeDrain:n0}");
        Console.WriteLine($"  Final state                 : {cel.ObservedState}");
        Console.WriteLine(
            $"[PASS] all {concurrentThreads:n0} simultaneous upgrades completed in {FormatDuration(allUpgradesElapsed)} " +
            $"with {ordinaryExclusiveThreads:n0} ordinary Exclusive contender(s).");
        return 0;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds >= 1)
        {
            return $"{duration.TotalMilliseconds:n3} ms";
        }

        return $"{duration.TotalMicroseconds:n3} us";
    }

    private static void SetMinimum(ref long location, long value)
    {
        long observed = Volatile.Read(ref location);
        while (value < observed)
        {
            long previous = Interlocked.CompareExchange(ref location, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private static void SetMaximum(ref long location, long value)
    {
        long observed = Volatile.Read(ref location);
        while (value > observed)
        {
            long previous = Interlocked.CompareExchange(ref location, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }
}
