using System.Globalization;

namespace LockBenchmark;

internal enum WorkloadKind
{
    Cpu,
    Memory,
    Dictionary,
    Ledger,
    Payload
}

internal enum ExecutionMode
{
    Throughput,
    AcquisitionLatency,
    ExclusiveProgress,
    PipelinePerformance,
    UpgradeContention,
    Correctness,
    PipelineStress,
    Endurance,
    ContentionDiagnostic
}

/// <summary>
/// Complete command-line configuration. Every workload/topology value is literal:
/// runtime hardware discovery is used only for reporting and CPU normalization.
/// </summary>
/// <remarks>
/// Porting contract: keep one canonical parameter name for each concept and require one explicit mode.
/// Do not add aliases, implicit default modes, compatibility translation, or mode-specific silent ignores.
/// Explicitly supplied parameters that a mode cannot use must be rejected. Do not clamp, scale, cap,
/// or replace user values based on CPU count, memory, NUMA, runtime, or elapsed time.
/// </remarks>
internal sealed class BenchmarkOptions
{
    internal const int DefaultThreads = 32;
    internal const int DefaultLockInstances = 1;
    internal const int DefaultOperationsPerThread = 10_000;
    internal const int DefaultConcurrentWorkSteps = 32;
    internal const int DefaultExclusiveWorkSteps = 32;
    internal const int DefaultMemoryWorkingSetMb = 64;
    internal const int DefaultDictionaryEntries = 1_280;
    internal const int DefaultPayloadFrames = 1_024;
    internal const int DefaultLatencySampleEvery = 1;
    internal const int DefaultPrepareSteps = 64;
    internal const int DefaultCommitSteps = 8;
    internal const int DefaultPostSteps = 64;
    internal const int DefaultAdvancedOperationsPerLock = 1;
    internal const int DefaultSemanticWorkersPerLock = 4;
    internal const int DefaultSemanticOperationsPerLock = 256;
    internal const int DefaultPipelineExceptionPermille = 10;

    public ExecutionMode Mode { get; internal set; }
    public bool ShowHelp { get; internal set; }

    public int LockInstances { get; internal set; } = DefaultLockInstances;
    public int Threads { get; internal set; } = DefaultThreads;
    public int OperationsPerThread { get; internal set; } = DefaultOperationsPerThread;
    public int ConcurrentWorkSteps { get; internal set; } = DefaultConcurrentWorkSteps;
    public int ExclusiveWorkSteps { get; internal set; } = DefaultExclusiveWorkSteps;
    public int MemoryWorkingSetMb { get; internal set; } = DefaultMemoryWorkingSetMb;
    public int DictionaryEntries { get; internal set; } = DefaultDictionaryEntries;
    public int PayloadFrames { get; internal set; } = DefaultPayloadFrames;
    public WorkloadKind Workload { get; internal set; } = WorkloadKind.Memory;
    public int? ConcurrentPermille { get; internal set; }
    public int LatencySampleEvery { get; internal set; } = DefaultLatencySampleEvery;

    public int PrepareSteps { get; internal set; } = DefaultPrepareSteps;
    public int CommitSteps { get; internal set; } = DefaultCommitSteps;
    public int PostSteps { get; internal set; } = DefaultPostSteps;

    public int SemanticWorkersPerLock { get; internal set; } = DefaultSemanticWorkersPerLock;
    public int SemanticOperationsPerLock { get; internal set; } = DefaultSemanticOperationsPerLock;
    public int? SemanticSeed { get; internal set; }
    public int PipelineExceptionPermille { get; internal set; } = DefaultPipelineExceptionPermille;
    public int AdvancedOperationsPerLock { get; internal set; } = DefaultAdvancedOperationsPerLock;
    public int? AdvancedSeed { get; internal set; }

    public TimeSpan? PipelineStressDuration { get; internal set; }
    public TimeSpan? EnduranceDuration { get; internal set; }
    public TimeSpan? ContentionDiagnosticDuration { get; internal set; }
    public int? UpgradeContentionConcurrentThreads { get; internal set; }
    public int UpgradeContentionExclusiveThreads { get; internal set; }

    public string? OutputPath { get; internal set; }
    public string MachineId { get; internal set; } = "unlabeled";
    public string ExperimentId { get; internal set; } = "manual";

    public long TotalWorkerThreads => checked((long)LockInstances * Threads);

    public void Validate()
    {
        RequirePositive(Threads, nameof(Threads));
        RequirePositive(LockInstances, nameof(LockInstances));
        RequirePositive(OperationsPerThread, nameof(OperationsPerThread));
        RequireNonNegative(ConcurrentWorkSteps, nameof(ConcurrentWorkSteps));
        RequireNonNegative(ExclusiveWorkSteps, nameof(ExclusiveWorkSteps));
        RequirePositive(MemoryWorkingSetMb, nameof(MemoryWorkingSetMb));
        RequirePositive(DictionaryEntries, nameof(DictionaryEntries));
        RequirePositive(PayloadFrames, nameof(PayloadFrames));
        RequirePositive(LatencySampleEvery, nameof(LatencySampleEvery));
        RequireNonNegative(PrepareSteps, nameof(PrepareSteps));
        RequireNonNegative(CommitSteps, nameof(CommitSteps));
        RequireNonNegative(PostSteps, nameof(PostSteps));
        RequirePositive(AdvancedOperationsPerLock, nameof(AdvancedOperationsPerLock));
        if (SemanticWorkersPerLock < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(SemanticWorkersPerLock), "semantic-workers must be at least 2.");
        }
        RequirePositive(SemanticOperationsPerLock, nameof(SemanticOperationsPerLock));
        if (PipelineExceptionPermille is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(PipelineExceptionPermille), "pipeline-exception-permille must be in [0, 1000].");
        }

        if (TotalWorkerThreads > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(LockInstances), "lock-instances × threads exceeds the runtime array limit.");
        }
        if (Mode == ExecutionMode.ExclusiveProgress && (long)LockInstances * (Threads + 1L) > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(LockInstances), "exclusive-progress thread count exceeds the runtime array limit.");
        }
        if ((long)LockInstances * SemanticWorkersPerLock > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(SemanticWorkersPerLock), "lock-instances × semantic-workers exceeds the runtime array limit.");
        }
        if (ConcurrentPermille is < 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(ConcurrentPermille), "concurrent-permille must be in [0, 1000].");
        }
        if (string.IsNullOrWhiteSpace(MachineId))
        {
            throw new ArgumentException("machine-id must not be empty.", nameof(MachineId));
        }
        if (string.IsNullOrWhiteSpace(ExperimentId))
        {
            throw new ArgumentException("experiment-id must not be empty.", nameof(ExperimentId));
        }

        if (Mode == ExecutionMode.UpgradeContention)
        {
            if (UpgradeContentionConcurrentThreads is null or < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(UpgradeContentionConcurrentThreads), "upgrade-contention requires n >= 1.");
            }
            RequireNonNegative(UpgradeContentionExclusiveThreads, nameof(UpgradeContentionExclusiveThreads));
        }

        ValidateDuration(PipelineStressDuration, nameof(PipelineStressDuration));
        ValidateDuration(EnduranceDuration, nameof(EnduranceDuration));
        ValidateDuration(ContentionDiagnosticDuration, nameof(ContentionDiagnosticDuration));

    }

    private static void RequirePositive(int value, string name)
    {
        if (value < 1) throw new ArgumentOutOfRangeException(name, $"{name} must be greater than 0.");
    }

    private static void RequireNonNegative(int value, string name)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(name, $"{name} must not be negative.");
    }

    private static void ValidateDuration(TimeSpan? value, string name)
    {
        if (value.HasValue && value.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be greater than zero.");
        }
    }
}

internal static class CommandLineParser
{
    public static BenchmarkOptions Parse(string[] args)
    {
        BenchmarkOptions options = new();
        int? commonWork = null;
        ExecutionMode? explicitlySelectedMode = null;
        HashSet<string> suppliedOptions = new(StringComparer.Ordinal);

        void Mark(string canonicalArgument)
        {
            if (!suppliedOptions.Add(canonicalArgument))
            {
                throw new ArgumentException($"Option specified more than once: {canonicalArgument}.");
            }
        }

        void SelectMode(ExecutionMode mode, string argument)
        {
            if (explicitlySelectedMode.HasValue)
            {
                throw new ArgumentException($"Multiple execution modes selected: {explicitlySelectedMode.Value} and {argument}.");
            }
            explicitlySelectedMode = mode;
            options.Mode = mode;
        }

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            string NextValue()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {argument}.");
                return args[++i];
            }

            switch (argument)
            {
                case "--throughput":
                    SelectMode(ExecutionMode.Throughput, argument);
                    break;
                case "--latency":
                    SelectMode(ExecutionMode.AcquisitionLatency, argument);
                    break;
                case "--exclusive-progress":
                    SelectMode(ExecutionMode.ExclusiveProgress, argument);
                    break;
                case "--pipeline-perf":
                    SelectMode(ExecutionMode.PipelinePerformance, argument);
                    break;
                case "--upgrade-contention":
                    SelectMode(ExecutionMode.UpgradeContention, argument);
                    options.UpgradeContentionConcurrentThreads = ParseInt(NextValue(), "--upgrade-contention n");
                    options.UpgradeContentionExclusiveThreads = ParseInt(NextValue(), "--upgrade-contention m");
                    break;
                case "--correctness":
                    SelectMode(ExecutionMode.Correctness, argument);
                    break;
                case "--pipeline-stress":
                    SelectMode(ExecutionMode.PipelineStress, argument);
                    options.PipelineStressDuration = ParseDuration(NextValue(), argument);
                    break;
                case "--endurance":
                    SelectMode(ExecutionMode.Endurance, argument);
                    options.EnduranceDuration = ParseDuration(NextValue(), argument);
                    break;
                case "--contention-diagnostic":
                    SelectMode(ExecutionMode.ContentionDiagnostic, argument);
                    options.ContentionDiagnosticDuration = ParseDuration(NextValue(), argument);
                    break;

                case "--threads":
                    Mark("--threads");
                    options.Threads = ParseInt(NextValue(), argument);
                    break;
                case "--lock-instances":
                    Mark("--lock-instances");
                    options.LockInstances = ParseInt(NextValue(), argument);
                    break;
                case "--operations":
                    Mark("--operations");
                    options.OperationsPerThread = ParseInt(NextValue(), argument);
                    break;
                case "--concurrent-permille":
                    Mark("--concurrent-permille");
                    options.ConcurrentPermille = ParseInt(NextValue(), argument);
                    break;
                case "--latency-sample-every":
                    Mark("--latency-sample-every");
                    options.LatencySampleEvery = ParseInt(NextValue(), argument);
                    break;
                case "--work":
                    Mark("--work");
                    commonWork = ParseInt(NextValue(), argument);
                    break;
                case "--concurrent-work":
                    Mark("--concurrent-work");
                    options.ConcurrentWorkSteps = ParseInt(NextValue(), argument);
                    break;
                case "--exclusive-work":
                    Mark("--exclusive-work");
                    options.ExclusiveWorkSteps = ParseInt(NextValue(), argument);
                    break;
                case "--workload":
                    Mark("--workload");
                    options.Workload = ParseWorkload(NextValue());
                    break;
                case "--memory-mb":
                    Mark("--memory-mb");
                    options.MemoryWorkingSetMb = ParseInt(NextValue(), argument);
                    break;
                case "--dictionary-size":
                    Mark("--dictionary-size");
                    options.DictionaryEntries = ParseInt(NextValue(), argument);
                    break;
                case "--payload-frames":
                    Mark("--payload-frames");
                    options.PayloadFrames = ParseInt(NextValue(), argument);
                    break;

                case "--prepare-work":
                    Mark("--prepare-work");
                    options.PrepareSteps = ParseInt(NextValue(), argument);
                    break;
                case "--commit-work":
                    Mark("--commit-work");
                    options.CommitSteps = ParseInt(NextValue(), argument);
                    break;
                case "--post-work":
                    Mark("--post-work");
                    options.PostSteps = ParseInt(NextValue(), argument);
                    break;

                case "--semantic-workers":
                    Mark("--semantic-workers");
                    options.SemanticWorkersPerLock = ParseInt(NextValue(), argument);
                    break;
                case "--semantic-operations":
                    Mark("--semantic-operations");
                    options.SemanticOperationsPerLock = ParseInt(NextValue(), argument);
                    break;
                case "--semantic-seed":
                    Mark("--semantic-seed");
                    options.SemanticSeed = ParseInt(NextValue(), argument);
                    break;
                case "--pipeline-exception-permille":
                    Mark("--pipeline-exception-permille");
                    options.PipelineExceptionPermille = ParseInt(NextValue(), argument);
                    break;
                case "--advanced-operations":
                    Mark("--advanced-operations");
                    options.AdvancedOperationsPerLock = ParseInt(NextValue(), argument);
                    break;
                case "--advanced-seed":
                    Mark("--advanced-seed");
                    options.AdvancedSeed = ParseInt(NextValue(), argument);
                    break;

                case "--output":
                    Mark("--output");
                    options.OutputPath = NextValue();
                    break;
                case "--machine-id":
                    Mark("--machine-id");
                    options.MachineId = NextValue();
                    break;
                case "--experiment-id":
                    Mark("--experiment-id");
                    options.ExperimentId = NextValue();
                    break;
                case "--help":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        if (commonWork.HasValue)
        {
            if (suppliedOptions.Contains("--concurrent-work") || suppliedOptions.Contains("--exclusive-work"))
            {
                throw new ArgumentException("--work cannot be combined with --concurrent-work or --exclusive-work.");
            }
            options.ConcurrentWorkSteps = commonWork.Value;
            options.ExclusiveWorkSteps = commonWork.Value;
        }

        if (!options.ShowHelp && !explicitlySelectedMode.HasValue)
        {
            throw new ArgumentException("Exactly one execution mode is required. Use --help to list the canonical modes.");
        }

        ValidateSuppliedOptions(options, suppliedOptions);
        options.Validate();
        return options;
    }


    private static void ValidateSuppliedOptions(BenchmarkOptions options, HashSet<string> supplied)
    {
        HashSet<string> allowed = new(StringComparer.Ordinal)
        {
            "--output", "--machine-id", "--experiment-id"
        };

        static void Add(HashSet<string> target, params string[] values)
        {
            foreach (string value in values) target.Add(value);
        }

        switch (options.Mode)
        {
            case ExecutionMode.Throughput:
                Add(allowed, "--lock-instances", "--threads", "--operations", "--concurrent-permille",
                    "--work", "--concurrent-work", "--exclusive-work", "--workload",
                    "--memory-mb", "--dictionary-size", "--payload-frames");
                break;
            case ExecutionMode.AcquisitionLatency:
                Add(allowed, "--lock-instances", "--threads", "--operations", "--concurrent-permille",
                    "--latency-sample-every", "--work", "--concurrent-work", "--exclusive-work",
                    "--workload", "--memory-mb", "--dictionary-size", "--payload-frames");
                break;
            case ExecutionMode.ExclusiveProgress:
                Add(allowed, "--lock-instances", "--threads", "--operations", "--work",
                    "--concurrent-work", "--exclusive-work", "--workload",
                    "--memory-mb", "--dictionary-size", "--payload-frames");
                break;
            case ExecutionMode.PipelinePerformance:
                Add(allowed, "--lock-instances", "--threads", "--operations",
                    "--prepare-work", "--commit-work", "--post-work");
                break;
            case ExecutionMode.UpgradeContention:
                Add(allowed, "--lock-instances");
                break;
            case ExecutionMode.Correctness:
                Add(allowed, "--lock-instances", "--semantic-workers", "--semantic-operations",
                    "--semantic-seed", "--pipeline-exception-permille",
                    "--advanced-operations", "--advanced-seed");
                break;
            case ExecutionMode.PipelineStress:
                Add(allowed, "--lock-instances", "--semantic-workers", "--semantic-operations",
                    "--semantic-seed", "--pipeline-exception-permille");
                break;
            case ExecutionMode.Endurance:
                Add(allowed, "--lock-instances", "--semantic-workers", "--semantic-operations", "--semantic-seed");
                break;
            case ExecutionMode.ContentionDiagnostic:
                Add(allowed, "--threads");
                break;
        }

        string[] unused = supplied.Where(argument => !allowed.Contains(argument)).OrderBy(argument => argument).ToArray();
        if (unused.Length != 0)
        {
            throw new ArgumentException($"Option(s) not used by {options.Mode}: {string.Join(", ", unused)}.");
        }

        if (options.Mode is ExecutionMode.Throughput or ExecutionMode.AcquisitionLatency or ExecutionMode.ExclusiveProgress)
        {
            if (supplied.Contains("--memory-mb") && options.Workload != WorkloadKind.Memory)
            {
                throw new ArgumentException("--memory-mb is used only with --workload memory.");
            }
            if (supplied.Contains("--dictionary-size") && options.Workload is not (WorkloadKind.Dictionary or WorkloadKind.Ledger))
            {
                throw new ArgumentException("--dictionary-size is used only with --workload dictionary or ledger.");
            }
            if (supplied.Contains("--payload-frames") && options.Workload != WorkloadKind.Payload)
            {
                throw new ArgumentException("--payload-frames is used only with --workload payload.");
            }
        }
    }

    private static int ParseInt(string value, string argument)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new ArgumentException($"Invalid integer for {argument}: {value}");
        }
        return result;
    }

    private static WorkloadKind ParseWorkload(string value) => value.ToLowerInvariant() switch
    {
        "cpu" => WorkloadKind.Cpu,
        "memory" => WorkloadKind.Memory,
        "dictionary" => WorkloadKind.Dictionary,
        "ledger" => WorkloadKind.Ledger,
        "payload" => WorkloadKind.Payload,
        _ => throw new ArgumentException($"Unknown workload: {value}.")
    };

    private static TimeSpan ParseDuration(string value, string argument)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed) && parsed > TimeSpan.Zero)
        {
            return parsed;
        }
        if (value.Length >= 2 && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) && amount > 0)
        {
            return char.ToLowerInvariant(value[^1]) switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => throw new ArgumentException($"Invalid duration for {argument}: {value}.")
            };
        }
        throw new ArgumentException($"Invalid duration for {argument}: {value}. Use 30s, 15m, 24h, 1d, or hh:mm:ss.");
    }
}

internal static class UsagePrinter
{
    public static void Print()
    {
        Console.WriteLine("Usage: TestAndBenchmark <mode> [options]");
        Console.WriteLine();
        Console.WriteLine("Performance modes:");
        Console.WriteLine("  --throughput          Mixed Concurrent/Exclusive throughput");
        Console.WriteLine("  --latency             Pure acquisition-wait percentiles");
        Console.WriteLine("  --exclusive-progress  Exclusive completions during a fixed Concurrent flood");
        Console.WriteLine("  --pipeline-perf       Concurrent -> Exclusive -> Concurrent staged operation");
        Console.WriteLine("  --upgrade-contention n m   N simultaneous upgrades with M ordinary Exclusive contenders");
        Console.WriteLine();
        Console.WriteLine("Validation modes:");
        Console.WriteLine("  --correctness         Run deterministic, full-state, and Pipeline correctness suites");
        Console.WriteLine("  --pipeline-stress d   Run Pipeline semantic stress for duration d");
        Console.WriteLine("  --endurance d         Run persistent-lock semantic stress for duration d");
        Console.WriteLine("  --contention-diagnostic d  Sample CEL diagnostic Contention under pressure");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  --lock-instances n        Independent lock groups active in parallel. Default: {BenchmarkOptions.DefaultLockInstances}");
        Console.WriteLine($"  --threads n               Worker threads per lock group. Default: {BenchmarkOptions.DefaultThreads}");
        Console.WriteLine($"  --operations n            Operations per measured worker. Default: {BenchmarkOptions.DefaultOperationsPerThread}");
        Console.WriteLine("                              exclusive-progress: Concurrent operations per Concurrent worker");
        Console.WriteLine("  --concurrent-permille n   One mix in [0,1000]; 995 means 99.5% Concurrent");
        Console.WriteLine("  --work n | --concurrent-work n --exclusive-work n");
        Console.WriteLine("  --workload cpu|memory|dictionary|ledger|payload");
        Console.WriteLine("  --memory-mb n | --dictionary-size n | --payload-frames n");
        Console.WriteLine("  --latency-sample-every n  Keep one sample per worker block of n operations");
        Console.WriteLine("  --output path             Append raw JSON Lines records");
        Console.WriteLine("  --machine-id id           Stable platform configuration label");
        Console.WriteLine("  --experiment-id id        Stable experiment/matrix label");
        Console.WriteLine();
        Console.WriteLine("Pipeline options:");
        Console.WriteLine("  --prepare-work n --commit-work n --post-work n");
        Console.WriteLine();
        Console.WriteLine("Correctness/stress topology options:");
        Console.WriteLine("  --semantic-workers n --semantic-operations n --semantic-seed n");
        Console.WriteLine($"  --pipeline-exception-permille n  Random executed Pipeline segments that throw per 1000. Default: {BenchmarkOptions.DefaultPipelineExceptionPermille}");
        Console.WriteLine("  --advanced-operations n --advanced-seed n");
    }
}
