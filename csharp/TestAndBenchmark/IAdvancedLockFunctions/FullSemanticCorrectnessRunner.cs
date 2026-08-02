using System;
using System.Collections.Generic;

namespace LockBenchmark;

/// <summary>Runs the complete access contract plus randomized contract-valid state paths.</summary>
internal static class FullSemanticCorrectnessRunner
{
    public static List<IAdvancedLockCorrectnessCase> CreateCases(
        int lockInstances,
        int workersPerLock,
        int operationsPerLock,
        int randomSeed,
        int pipelineExceptionPermille,
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
            new UnconditionalConcurrentToExclusiveCase(),
            new ConcurrentToExclusiveCorrectnessCase(),
            new ConcurrentExclusiveLockPipelineCorrectnessCase(
                lockInstances,
                workersPerLock,
                operationsPerLock,
                randomSeed,
                printRandomSummaries,
                randomExceptionPermille: pipelineExceptionPermille),
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
        int? requestedSeed,
        int pipelineExceptionPermille)
    {
        int randomSeed = requestedSeed ?? SeedSource.Create();
        List<IAdvancedLockCorrectnessCase> cases = CreateCases(
            lockInstances,
            workersPerLock,
            operationsPerLock,
            randomSeed,
            pipelineExceptionPermille);

        Console.WriteLine("Full semantic correctness");
        Console.WriteLine(
            $"Random valid paths: locks={lockInstances:n0}, workers/lock={workersPerLock:n0}, " +
            $"rounds/lock={operationsPerLock:n0}, total-threads={lockInstances * (long)workersPerLock:n0}, " +
            $"seed={randomSeed}, pipeline-exception-permille={pipelineExceptionPermille}.");
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
