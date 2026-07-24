using System.Text;
using BenchmarkDotNet.Running;
using TestAndBenchmark.Benchmarks.AdvancedPerf;
using TestAndBenchmark.Benchmarks.ExclusivePreemption;
using TestAndBenchmark.Benchmarks.Micro;
using TestAndBenchmark.Benchmarks.SteadyState;
using TestAndBenchmark.Common.Diagnostics;
using TestAndBenchmark.Common.Testing;
using TestAndBenchmark.Correctness.Core;
using TestAndBenchmark.Correctness.Pipeline;
using TestAndBenchmark.Correctness.Scope;
using TestAndBenchmark.Sample;
using TestAndBenchmark.Stress.RandomStateMachine;
using TestAndBenchmark.Suite;

namespace TestAndBenchmark;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        string command = args.Length == 0 ? "correctness" : args[0].Trim().ToLowerInvariant();
        return command switch
        {
            "correctness" or "test" or "tests" => await RunCorrectnessAsync(),
            "micro" or "benchmark" or "benchmarks" => RunMicroBenchmarks(args.Skip(1).ToArray()),
            "advanced-perf" or "advanced" => RunAdvancedPerf(args.Skip(1).ToArray()),
            "steady" or "steadystate" => RunSteadyState(args.Skip(1).ToArray()),
            "exclusive-preemption" or "preemption" => RunExclusivePreemption(args.Skip(1).ToArray()),
            "stress" or "random-stress" => RunRandomStress(args.Skip(1).ToArray()),
            "sample" or "samples" => await RunSamplesAsync(),
            "suite" => await RunSuiteAsync(args.Skip(1).ToArray()),
            "help" or "-h" or "--help" => PrintHelp(),
            _ => PrintUnknownCommand(command),
        };
    }

    private static async Task<int> RunCorrectnessAsync()
    {
        EnvironmentReport.Print();

        var tests = new List<TestCase>();
        tests.AddRange(ConcurrentExclusiveLockCoreTests.GetTests());
        tests.AddRange(ConcurrentExclusiveLockScopeTests.GetTests());
        tests.AddRange(ConcurrentExclusiveLockPipelineTests.GetTests());

        return await TestRunner.RunAsync(tests, TimeSpan.FromSeconds(5));
    }

    private static int PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  TestAndBenchmark.exe correctness");
        Console.WriteLine("  TestAndBenchmark.exe micro");
        Console.WriteLine("  TestAndBenchmark.exe suite --profile quick --group smoke|empty|short|long|workloads|instances100|all");
        Console.WriteLine("  TestAndBenchmark.exe steady --lock-instances 1 --threads 64 --workload dictionary --operations 10000 --dictionary-size 65536 --read-work 64 --write-work 128 --target all");
        Console.WriteLine("  TestAndBenchmark.exe exclusive-preemption --profile standard --target all --workload dictionary --threads 16 --read-work 64 --write-work 128");
        Console.WriteLine("  TestAndBenchmark.exe advanced-perf --target all --threads 64 --operations 100000 --workload dictionary --read-work 64 --write-work 128");
        Console.WriteLine("  TestAndBenchmark.exe stress --profile forever");
        Console.WriteLine("  TestAndBenchmark.exe stress --profile max");
        Console.WriteLine("  TestAndBenchmark.exe sample");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  correctness  Run deterministic black-box correctness tests.");
        Console.WriteLine("  micro        Run BenchmarkDotNet microbenchmarks.");
        Console.WriteLine("  suite        Run an integrated correctness/performance/stress suite.");
        Console.WriteLine("  advanced-perf  Run CEL advanced semantic performance tests.");
        Console.WriteLine("  steady       Run fixed-worker steady-state benchmarks.");
        Console.WriteLine("  exclusive-preemption  Run Exclusive preemption latency tests.");
        Console.WriteLine("  stress       Run random Scope/Pipeline stress tests.");
        Console.WriteLine("  sample       Run simple Scope/Pipeline usage samples.");
        return 0;
    }

    private static async Task<int> RunSamplesAsync()
    {
        Console.WriteLine("ConcurrentExclusivePack usage samples");
        Console.WriteLine();

        RunSampleCase("Scope/ConcurrentOnly", ScopeSample.ConcurrentOnly);
        RunSampleCase("Scope/ExclusiveOnly", ScopeSample.ExclusiveOnly);
        RunSampleCase("Scope/TryConcurrentAsOuterEntry", () => ScopeSample.TryConcurrentAsOuterEntry());
        RunSampleCase("Scope/ExclusiveDowngradeToConcurrent", ScopeSample.ExclusiveDowngradeToConcurrent);
        RunSampleCase("Scope/ConcurrentUpgradeToExclusiveWithEpoch", () => ScopeSample.ConcurrentUpgradeToExclusiveWithEpoch());
        RunSampleCase("Scope/ConcurrentUpgradeToExclusiveWithContext", () => ScopeSample.ConcurrentUpgradeToExclusiveWithContext());

        RunSampleCase("Pipeline/ConcurrentThenExclusiveThenNone", PipelineSample.ConcurrentThenExclusiveThenNone);
        RunSampleCase("Pipeline/ExclusiveDowngradeWithConvergeConcurrent", PipelineSample.ExclusiveDowngradeWithConvergeConcurrent);
        RunSampleCase("Pipeline/ConcurrentUpgradeWithEpoch", PipelineSample.ConcurrentUpgradeWithEpoch);
        RunSampleCase("Pipeline/ConcurrentUpgradeWithContext", PipelineSample.ConcurrentUpgradeWithContext);
        RunSampleCase("Pipeline/TrySegmentsCanSkipWork", PipelineSample.TrySegmentsCanSkipWork);
        await RunSampleCaseAsync("Pipeline/RunPipelineOnThreadPoolAsync", PipelineSample.RunPipelineOnThreadPoolAsync);

        Console.WriteLine();
        Console.WriteLine("Sample result: PASS");
        return 0;
    }

    private static void RunSampleCase(string name, Action action)
    {
        action();
        Console.WriteLine($"PASS {name}");
    }

    private static async Task RunSampleCaseAsync(string name, Func<Task> action)
    {
        await action();
        Console.WriteLine($"PASS {name}");
    }

    private static async Task<int> RunSuiteAsync(string[] args)
    {
        SuiteOptions options = SuiteOptions.Parse(args);
        string[][] commands = GetSuiteCommands(options);

        Console.WriteLine("Integrated test suite");
        Console.WriteLine($"Profile             : {options.Profile}");
        Console.WriteLine($"Group               : {options.Group}");
        Console.WriteLine($"IncludeMicro        : {options.IncludeMicro}");
        Console.WriteLine($"Seed                : {options.Seed}");
        Console.WriteLine();

        foreach (string[] command in commands)
        {
            int result = await RunSuiteCommandAsync(command);
            if (result != 0)
            {
                Console.WriteLine($"Suite stopped after failure: {FormatCommand(command)}");
                return result;
            }
        }

        if (options.IncludeMicro)
        {
            int result = await RunSuiteCommandAsync(GetMicroSuiteCommand());
            if (result != 0)
            {
                Console.WriteLine($"Suite stopped after failure: {FormatCommand(GetMicroSuiteCommand())}");
                return result;
            }
        }

        Console.WriteLine("Suite result: PASS");
        return 0;
    }

    private static async Task<int> RunSuiteCommandAsync(string[] command)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {FormatCommand(command)} ===");
        Console.WriteLine();

        string name = command[0];
        string[] args = command.Skip(1).ToArray();
        return name switch
        {
            "correctness" => await RunCorrectnessAsync(),
            "steady" => RunSteadyState(args),
            "advanced-perf" => RunAdvancedPerf(args),
            "exclusive-preemption" => RunExclusivePreemption(args),
            "stress" => RunRandomStress(args),
            "micro" => RunMicroBenchmarks(args),
            _ => throw new ArgumentException($"Unknown suite command: {name}"),
        };
    }

    private static string[][] GetSuiteCommands(SuiteOptions options)
    {
        if (options.Profile is not ("quick" or "standard"))
        {
            throw new ArgumentException($"Unknown suite profile: {options.Profile}");
        }

        return options.Group switch
        {
            "smoke" => GetSmokeSuiteCommands(options),
            "empty" => GetEmptyLockSuiteCommands(options),
            "short" => GetShortBoundarySuiteCommands(options),
            "long" => GetLongWorkSuiteCommands(options),
            "workloads" => GetWorkloadSuiteCommands(options),
            "instances100" => GetInstances100SuiteCommands(options),
            "all" => GetAllSuiteCommands(options),
            _ => throw new ArgumentException($"Unknown suite group: {options.Group}"),
        };
    }

    private static string[][] GetEmptyLockSuiteCommands(SuiteOptions options)
    {
        return options.Profile == "quick"
            ?
            [
                ["steady", "--lock-instances", "1", "--threads", "64", "--workload", "cpu", "--operations", "100000", "--work", "0", "--target", "all"],
                ["advanced-perf", "--target", "all", "--threads", "64", "--operations", "100000", "--workload", "cpu", "--work", "0"],
                ["exclusive-preemption", "--profile", "quick", "--target", "all", "--workload", "cpu", "--work", "0", "--threads", "64", "--attempts", "64", "--concurrent-spin", "0", "--exclusive-hold-ms", "0", "--exclusive-pause-ms", "1"],
            ]
            :
            [
                ["steady", "--lock-instances", "1", "--threads", "64", "--workload", "cpu", "--operations", "300000", "--work", "0", "--target", "all"],
                ["advanced-perf", "--target", "all", "--threads", "64", "--operations", "300000", "--workload", "cpu", "--work", "0"],
                ["exclusive-preemption", "--profile", "standard", "--target", "all", "--workload", "cpu", "--work", "0", "--threads", "64", "--concurrent-spin", "0", "--exclusive-hold-ms", "0", "--exclusive-pause-ms", "1"],
            ];
    }

    private static string[][] GetSmokeSuiteCommands(SuiteOptions options)
    {
        return options.Profile == "quick"
            ?
            [
                ["correctness"],
                ["steady", "--lock-instances", "1", "--threads", "4", "--workload", "cpu", "--operations", "100", "--work", "4", "--target", "all"],
                ["advanced-perf", "--target", "all", "--threads", "4", "--operations", "1000", "--workload", "cpu", "--work", "4"],
                ["exclusive-preemption", "--profile", "quick", "--target", "all", "--workload", "cpu", "--work", "4", "--threads", "4", "--attempts", "3", "--exclusive-hold-ms", "0", "--exclusive-pause-ms", "1"],
                ["stress", "--profile", "quick", "--seed", options.Seed.ToString()],
            ]
            :
            [
                ["correctness"],
                ["steady", "--lock-instances", "1", "--threads", "64", "--workload", "dictionary", "--operations", "10000", "--dictionary-size", "65536", "--read-work", "64", "--write-work", "128", "--target", "all"],
                ["advanced-perf", "--target", "all", "--threads", "64", "--operations", "100000", "--workload", "dictionary", "--dictionary-size", "65536", "--read-work", "64", "--write-work", "128"],
                ["exclusive-preemption", "--profile", "standard", "--target", "all", "--workload", "dictionary", "--dictionary-size", "65536", "--read-work", "64", "--write-work", "128"],
                ["stress", "--profile", "standard", "--seed", options.Seed.ToString()],
            ];
    }

    private static string[][] GetShortBoundarySuiteCommands(SuiteOptions options)
    {
        return options.Profile == "quick"
            ?
            [
                ["steady", "--lock-instances", "1", "--threads", "64", "--workload", "cpu", "--operations", "100000", "--work", "16", "--target", "all"],
                ["advanced-perf", "--target", "all", "--threads", "64", "--operations", "100000", "--workload", "cpu", "--work", "16"],
                ["exclusive-preemption", "--profile", "quick", "--target", "all", "--workload", "cpu", "--work", "16", "--threads", "64", "--attempts", "64", "--exclusive-hold-ms", "0", "--exclusive-pause-ms", "1"],
            ]
            :
            [
                ["steady", "--lock-instances", "1", "--threads", "64", "--workload", "cpu", "--operations", "300000", "--work", "16", "--target", "all"],
                ["advanced-perf", "--target", "all", "--threads", "64", "--operations", "300000", "--workload", "cpu", "--work", "16"],
                ["exclusive-preemption", "--profile", "standard", "--target", "all", "--workload", "cpu", "--work", "16", "--threads", "64", "--exclusive-hold-ms", "0"],
            ];
    }

    private static string[][] GetLongWorkSuiteCommands(SuiteOptions options)
    {
        return options.Profile == "quick"
            ?
            [
                ["steady", "--lock-instances", "1", "--threads", "64", "--workload", "cpu", "--operations", "100000", "--work", "640", "--target", "all"],
                ["advanced-perf", "--target", "all", "--threads", "64", "--operations", "100000", "--workload", "cpu", "--work", "640"],
                ["exclusive-preemption", "--profile", "quick", "--target", "all", "--workload", "cpu", "--work", "640", "--threads", "64", "--attempts", "64", "--exclusive-hold-ms", "0", "--exclusive-pause-ms", "1"],
            ]
            :
            [
                ["steady", "--lock-instances", "1", "--threads", "64", "--workload", "cpu", "--operations", "300000", "--work", "640", "--target", "all"],
                ["advanced-perf", "--target", "all", "--threads", "64", "--operations", "300000", "--workload", "cpu", "--work", "640"],
                ["exclusive-preemption", "--profile", "standard", "--target", "all", "--workload", "cpu", "--work", "640", "--threads", "64", "--exclusive-hold-ms", "0"],
            ];
    }

    private static string[][] GetWorkloadSuiteCommands(SuiteOptions options)
    {
        string threads = "64";
        string cpuOperations = options.Profile == "quick" ? "100000" : "300000";
        string dataOperations = options.Profile == "quick" ? "10000" : "30000";
        string dictionarySize = "65536";
        string memoryMb = "64";

        return
        [
            ["steady", "--lock-instances", "1", "--threads", threads, "--workload", "cpu", "--operations", cpuOperations, "--read-work", "64", "--write-work", "128", "--target", "all"],
            ["steady", "--lock-instances", "1", "--threads", threads, "--workload", "memory", "--operations", dataOperations, "--memory-mb", memoryMb, "--read-work", "64", "--write-work", "128", "--target", "all"],
            ["steady", "--lock-instances", "1", "--threads", threads, "--workload", "dictionary", "--operations", dataOperations, "--dictionary-size", dictionarySize, "--read-work", "64", "--write-work", "128", "--target", "all"],
            ["steady", "--lock-instances", "1", "--threads", threads, "--workload", "ledger", "--operations", dataOperations, "--dictionary-size", dictionarySize, "--read-work", "64", "--write-work", "128", "--target", "all"],
            ["steady", "--lock-instances", "1", "--threads", threads, "--workload", "payload", "--operations", dataOperations, "--dictionary-size", dictionarySize, "--read-work", "64", "--write-work", "128", "--target", "all"],
        ];
    }

    private static string[][] GetInstances100SuiteCommands(SuiteOptions options)
    {
        string threads = "64";
        string cpuOperations = options.Profile == "quick" ? "1000" : "3000";
        string dataOperations = options.Profile == "quick" ? "100" : "300";
        string dictionarySize = "65536";
        string memoryMb = "64";

        return
        [
            ["steady", "--lock-instances", "100", "--threads", threads, "--workload", "cpu", "--operations", cpuOperations, "--read-work", "64", "--write-work", "128", "--target", "all"],
            ["steady", "--lock-instances", "100", "--threads", threads, "--workload", "memory", "--operations", dataOperations, "--memory-mb", memoryMb, "--read-work", "64", "--write-work", "128", "--target", "all"],
            ["steady", "--lock-instances", "100", "--threads", threads, "--workload", "dictionary", "--operations", dataOperations, "--dictionary-size", dictionarySize, "--read-work", "64", "--write-work", "128", "--target", "all"],
            ["steady", "--lock-instances", "100", "--threads", threads, "--workload", "ledger", "--operations", dataOperations, "--dictionary-size", dictionarySize, "--read-work", "64", "--write-work", "128", "--target", "all"],
            ["steady", "--lock-instances", "100", "--threads", threads, "--workload", "payload", "--operations", dataOperations, "--dictionary-size", dictionarySize, "--read-work", "64", "--write-work", "128", "--target", "all"],
        ];
    }

    private static string[][] GetAllSuiteCommands(SuiteOptions options)
    {
        return
        [
            ["correctness"],
            .. GetEmptyLockSuiteCommands(options),
            .. GetShortBoundarySuiteCommands(options),
            .. GetLongWorkSuiteCommands(options),
            .. GetWorkloadSuiteCommands(options),
            .. GetInstances100SuiteCommands(options),
            ["stress", "--profile", options.Profile == "quick" ? "quick" : "standard", "--seed", options.Seed.ToString()],
        ];
    }

    private static string[] GetMicroSuiteCommand()
    {
        return ["micro", "--filter", "*LowContention*", "--job", "short", "--warmupCount", "1", "--iterationCount", "1"];
    }

    private static string FormatCommand(string[] command)
    {
        return "TestAndBenchmark.exe " + string.Join(' ', command);
    }

    private static int RunMicroBenchmarks(string[] args)
    {
        BenchmarkSwitcher
            .FromTypes(
            [
                typeof(ScopeUncontendedMicroBenchmarks),
                typeof(ScopeTryFailureMicroBenchmarks),
                typeof(LowContentionMicroBenchmarks),
            ])
            .Run(args);

        return 0;
    }

    private static int RunAdvancedPerf(string[] args)
    {
        EnvironmentReport.Print();

        AdvancedPerfOptions options = AdvancedPerfOptions.Parse(args);
        AdvancedPerfRunner.Run(options);
        return 0;
    }

    private static int RunSteadyState(string[] args)
    {
        EnvironmentReport.Print();

        SteadyStateOptions options = SteadyStateOptions.Parse(args);
        SteadyStateBenchmarkRunner.Run(options);
        return 0;
    }

    private static int RunExclusivePreemption(string[] args)
    {
        EnvironmentReport.Print();

        ExclusivePreemptionOptions options = ExclusivePreemptionOptions.Parse(args);
        ExclusivePreemptionRunner.Run(options);
        return 0;
    }

    private static int RunRandomStress(string[] args)
    {
        EnvironmentReport.Print();

        RandomStressOptions options = RandomStressOptions.Parse(args);
        return RandomStressRunner.Run(options);
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Run `TestAndBenchmark.exe help` for available commands.");
        return 2;
    }
}
