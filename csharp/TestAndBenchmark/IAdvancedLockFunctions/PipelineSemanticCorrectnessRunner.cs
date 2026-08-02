using System;

namespace LockBenchmark;

/// <summary>Runs only the ConcurrentExclusiveLockPipeline semantic suite, isolated from other full-contract cases.</summary>
internal static class PipelineSemanticCorrectnessRunner
{
    public static int Run(
        int lockInstances,
        int workersPerLock,
        int operationsPerLock,
        int? requestedSeed,
        int pipelineExceptionPermille)
    {
        int randomSeed = requestedSeed ?? SeedSource.Create();
        IAdvancedLockCorrectnessCase testCase = new ConcurrentExclusiveLockPipelineCorrectnessCase(
            lockInstances,
            workersPerLock,
            operationsPerLock,
            randomSeed,
            randomExceptionPermille: pipelineExceptionPermille);

        Console.WriteLine("Pipeline semantic correctness");
        Console.WriteLine(
            $"locks={lockInstances:n0}, workers/lock={workersPerLock:n0}, " +
            $"rounds/lock={operationsPerLock:n0}, total-threads={lockInstances * (long)workersPerLock:n0}, " +
            $"seed={randomSeed}, pipeline-exception-permille={pipelineExceptionPermille}.");
        Console.WriteLine();

        try
        {
            testCase.Run();
            Console.WriteLine($"[PASS] {testCase.Name}");
            Console.WriteLine();
            Console.WriteLine("Summary: passed=1, failed=0, total=1");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[FAIL] {testCase.Name}");
            Console.WriteLine($"       {exception.GetType().Name}: {exception.Message}");
            Console.WriteLine();
            Console.WriteLine("Summary: passed=0, failed=1, total=1");
            return 3;
        }
    }
}
