using System;
using System.Collections.Generic;

namespace LockBenchmark;

/// <summary>阶段5总控：完整访问契约和随机合法状态路径测试。</summary>
internal static class FullSemanticCorrectnessRunner
{
    public static List<IAdvancedLockCorrectnessCase> CreateCases(
        int lockInstances,
        int workersPerLock,
        int operationsPerLock,
        int randomSeed,
        bool printRandomSummaries = true)
    {
        return new List<IAdvancedLockCorrectnessCase>
        {
            new ConcurrentAcquireFullSemanticCase(),
            new ExclusiveAcquireFullSemanticCase(),
            new ConcurrentExclusiveLockScopeBindingCase(),
            new ConcurrentExclusiveLockScopeLifecycleCase(),
            new ContextIdExclusiveNestingProtocolCase(),
            new LockStateSnapshotCorrectnessCase(),
            new LockContentionSnapshotCorrectnessCase(),
            new ExclusiveToConcurrentCorrectnessCase(),
            new PermissionConversionCycleCorrectnessCase(),
            new ConcurrentToExclusiveCorrectnessCase(),
            new ConcurrentExclusiveLockPipelineCorrectnessCase(
                lockInstances,
                workersPerLock,
                operationsPerLock,
                randomSeed,
                printRandomSummaries),
            new RandomizedValidSemanticPathsCase(
                lockInstances,
                workersPerLock,
                operationsPerLock,
                randomSeed,
                printSummary: printRandomSummaries)
        };
    }

    public static int Run(
        int lockInstances,
        int workersPerLock,
        int operationsPerLock,
        int? requestedSeed)
    {
        int randomSeed = requestedSeed ?? Random.Shared.Next();
        List<IAdvancedLockCorrectnessCase> cases = CreateCases(
            lockInstances,
            workersPerLock,
            operationsPerLock,
            randomSeed);

        Console.WriteLine("Stage 5: full lock semantic correctness");
        Console.WriteLine("State/Contention are validated only as diagnostic snapshots, not as strong-consistency counters.");
        Console.WriteLine(
            $"Random valid paths: locks={lockInstances:n0}, workers/lock={workersPerLock:n0}, " +
            $"rounds/lock={operationsPerLock:n0}, total-threads={lockInstances * (long)workersPerLock:n0}, " +
            $"seed={randomSeed}.");
        Console.WriteLine();

        int passed = 0;
        foreach (IAdvancedLockCorrectnessCase testCase in cases)
        {
            try
            {
                testCase.Run();
                passed++;
                Console.WriteLine($"[PASS] {testCase.Name}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[FAIL] {testCase.Name}");
                Console.WriteLine($"       {exception.GetType().Name}: {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Summary: passed={passed}, failed={cases.Count - passed}, total={cases.Count}");
        return passed == cases.Count ? 0 : 3;
    }
}
