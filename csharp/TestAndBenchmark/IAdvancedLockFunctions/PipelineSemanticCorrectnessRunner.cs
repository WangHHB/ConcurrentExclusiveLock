using System;

namespace LockBenchmark;

/// <summary>
/// 只运行 ConcurrentExclusiveLockPipeline 的语义压力测试，避免被其他 full-semantics 用例干扰。
/// </summary>
internal static class PipelineSemanticCorrectnessRunner
{
    public static int Run(
        int lockInstances,
        int workersPerLock,
        int operationsPerLock,
        int? requestedSeed)
    {
        int randomSeed = requestedSeed ?? Random.Shared.Next();
        IAdvancedLockCorrectnessCase testCase = new ConcurrentExclusiveLockPipelineCorrectnessCase(
            lockInstances,
            workersPerLock,
            operationsPerLock,
            randomSeed);

        Console.WriteLine("Pipeline semantic stress");
        Console.WriteLine(
            $"locks={lockInstances:n0}, workers/lock={workersPerLock:n0}, " +
            $"rounds/lock={operationsPerLock:n0}, total-threads={lockInstances * (long)workersPerLock:n0}, " +
            $"seed={randomSeed}.");
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
