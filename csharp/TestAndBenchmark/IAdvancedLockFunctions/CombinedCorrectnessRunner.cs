namespace LockBenchmark;

internal static class CombinedCorrectnessRunner
{
    public static int Run(BenchmarkOptions options, BenchmarkSession session)
    {
        PortableRandomContract.Validate();
        Console.WriteLine("Combined correctness suite");
        Console.WriteLine();

        int result = 0;
        result = Merge(result, AdvancedLockCorrectnessRunner.Run(
            options.LockInstances,
            options.AdvancedOperationsPerLock,
            options.AdvancedSeed));
        Console.WriteLine();
        result = Merge(result, FullSemanticCorrectnessRunner.Run(
            options.LockInstances,
            options.SemanticWorkersPerLock,
            options.SemanticOperationsPerLock,
            options.SemanticSeed,
            options.PipelineExceptionPermille));

        session.Write("correctness-summary", new
        {
            exitCode = result,
            options.LockInstances,
            options.SemanticWorkersPerLock,
            options.SemanticOperationsPerLock,
            options.SemanticSeed,
            options.PipelineExceptionPermille,
            options.AdvancedOperationsPerLock,
            options.AdvancedSeed
        });
        return result;
    }

    private static int Merge(int current, int next) => current == 0 ? next : current;
}
