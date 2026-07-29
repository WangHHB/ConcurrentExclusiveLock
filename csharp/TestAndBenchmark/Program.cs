using IntomicLib;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LockBenchmark;

internal static class Program
{
    private static int Main(string[] args)
    {
        BenchmarkOptions options;
        try
        {
            options = CommandLineParser.Parse(args);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            UsagePrinter.Print();
            return 1;
        }

        if (options.ShowHelp)
        {
            UsagePrinter.Print();
            return 0;
        }

        BenchmarkReporter.PrintEnvironment();
        if (options.UpgradeContentionConcurrentThreads.HasValue)
        {
            return UpgradeContentionRunner.Run(
                options.UpgradeContentionConcurrentThreads.Value,
                options.UpgradeContentionExclusiveThreads);
        }

        if (options.ContentionStressDuration.HasValue)
        {
            return ContentionStressRunner.Run(
                options.ContentionStressDuration.Value,
                options.Threads);
        }

        if (options.EnduranceDuration.HasValue)
        {
            return EnduranceSemanticRunner.Run(options.EnduranceDuration.Value);
        }

        if (options.FullSemanticStressDuration.HasValue)
        {
            return FullSemanticStressRunner.Run(
                options.FullSemanticStressDuration.Value,
                options.LockInstances,
                options.SemanticWorkersPerLock,
                options.SemanticOperationsPerLock,
                options.SemanticSeed);
        }

        if (options.PipelineStressDuration.HasValue)
        {
            return PipelineSemanticStressRunner.Run(
                options.PipelineStressDuration.Value,
                options.LockInstances,
                options.SemanticWorkersPerLock,
                options.SemanticOperationsPerLock,
                options.SemanticSeed);
        }

        if (options.RunFullSemantics)
        {
            return FullSemanticCorrectnessRunner.Run(
                options.LockInstances,
                options.SemanticWorkersPerLock,
                options.SemanticOperationsPerLock,
                options.SemanticSeed);
        }

        if (options.RunPipelineSemantics)
        {
            return PipelineSemanticCorrectnessRunner.Run(
                options.LockInstances,
                options.SemanticWorkersPerLock,
                options.SemanticOperationsPerLock,
                options.SemanticSeed);
        }

        if (options.RunAdvancedPerformance)
        {
            return AdvancedLockPerformanceRunner.Run(
                options.Threads,
                options.OperationsPerThread,
                options.ReadSteps);
        }

        if (options.RunAdvancedCorrectness)
        {
            return AdvancedLockCorrectnessRunner.Run(
                options.LockInstances,
                options.AdvancedOperationsPerLock,
                options.AdvancedSeed);
        }

        BenchmarkRunner.Run(options);
        return 0;
    }
}
