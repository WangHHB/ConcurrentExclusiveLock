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
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine("Use --help for usage.");
            return 1;
        }

        if (options.ShowHelp)
        {
            UsagePrinter.Print();
            return 0;
        }

        try
        {
            using BenchmarkSession session = new(options);
            session.WriteHeader();
            return options.Mode switch
            {
                ExecutionMode.Throughput => BenchmarkRunner.Run(options, session),
                ExecutionMode.AcquisitionLatency => AcquisitionLatencyRunner.Run(options, session),
                ExecutionMode.ExclusiveProgress => ExclusiveProgressRunner.Run(options, session),
                ExecutionMode.PipelinePerformance => PipelinePerformanceRunner.Run(options, session),
                ExecutionMode.UpgradeContention => UpgradeContentionRunner.Run(options, session),
                ExecutionMode.Correctness => CombinedCorrectnessRunner.Run(options, session),
                ExecutionMode.PipelineStress => PipelineSemanticStressRunner.Run(
                    options.PipelineStressDuration!.Value,
                    options.LockInstances,
                    options.SemanticWorkersPerLock,
                    options.SemanticOperationsPerLock,
                    options.SemanticSeed,
                    options.PipelineExceptionPermille),
                ExecutionMode.Endurance => EnduranceSemanticRunner.Run(
                    options.EnduranceDuration!.Value,
                    options.LockInstances,
                    options.SemanticWorkersPerLock,
                    options.SemanticOperationsPerLock,
                    options.SemanticSeed),
                ExecutionMode.ContentionDiagnostic => ContentionStressRunner.Run(
                    options.ContentionDiagnosticDuration!.Value,
                    options.Threads),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[FATAL] {exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine(exception.StackTrace);
            return 4;
        }
    }
}
