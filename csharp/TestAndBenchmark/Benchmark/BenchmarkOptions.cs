using System;
using System.Globalization;

namespace LockBenchmark;

/// <summary>可由命令行单独加载的业务工作集类型。</summary>
internal enum WorkloadKind
{
    Cpu,
    Memory,
    Dictionary,
    Ledger,
    Payload
}

/// <summary>
/// 标准锁测试的配置模型。
/// 同文件中的解析器和帮助输出共同构成完整的命令行配置入口。
/// </summary>
internal sealed class BenchmarkOptions
{
    internal const int DefaultThreads = 32;
    internal const int DefaultLockInstances = 1000;
    internal const int DefaultOperationsPerThread = 100;
    internal const int DefaultReadSteps = 64;
    internal const int DefaultWriteSteps = 128;
    internal const int DefaultMemoryWorkingSetMb = 64;
    internal const int DefaultDictionaryEntries = 1280;
    internal const int DefaultAdvancedOperationsPerLock = 1;
    internal const int DefaultSemanticWorkersPerLock = 4;
    internal const int DefaultSemanticOperationsPerLock = 256;

    /// <summary>本案例同时运行的独立“锁 + Work”实例数量。</summary>
    public int LockInstances { get; internal set; } = DefaultLockInstances;

    /// <summary>每个锁实例参与竞争的专用工作线程数量。</summary>
    public int Threads { get; internal set; } = DefaultThreads;

    /// <summary>每个线程获取锁并执行一次 Tick 的次数。</summary>
    public int OperationsPerThread { get; internal set; } = DefaultOperationsPerThread;

    /// <summary>每次获取读权限后，TickRead 在锁内执行的业务步骤数。</summary>
    public int ReadSteps { get; internal set; } = DefaultReadSteps;

    /// <summary>每次获取写权限后，TickWrite 在锁内执行的业务步骤数。</summary>
    public int WriteSteps { get; internal set; } = DefaultWriteSteps;
    public int MemoryWorkingSetMb { get; internal set; } = DefaultMemoryWorkingSetMb;
    public int DictionaryEntries { get; internal set; } = DefaultDictionaryEntries;
    public WorkloadKind Workload { get; internal set; } = WorkloadKind.Dictionary;
    public bool ShowHelp { get; internal set; }
    public bool RunAdvancedCorrectness { get; internal set; }
    public bool RunAdvancedPerformance { get; internal set; }
    public bool RunPipelineSemantics { get; internal set; }
    public TimeSpan? PipelineStressDuration { get; internal set; }
    public int AdvancedOperationsPerLock { get; internal set; } = DefaultAdvancedOperationsPerLock;
    public int? AdvancedSeed { get; internal set; }
    public bool RunFullSemantics { get; internal set; }
    public int SemanticWorkersPerLock { get; internal set; } = DefaultSemanticWorkersPerLock;
    public int SemanticOperationsPerLock { get; internal set; } = DefaultSemanticOperationsPerLock;
    public int? SemanticSeed { get; internal set; }
    public TimeSpan? EnduranceDuration { get; internal set; }
    public TimeSpan? FullSemanticStressDuration { get; internal set; }
    public TimeSpan? ContentionStressDuration { get; internal set; }

    /// <summary>本案例实际创建的专用工作线程总数。</summary>
    public long TotalWorkerThreads => (long)LockInstances * Threads;

    /// <summary>
    /// 创建只用于 JIT 和运行时预热的缩小配置。
    /// Work 类型及数据规模保持一致，线程数、操作数和单次步骤数会缩小。
    /// </summary>
    public static BenchmarkOptions CreateWarmup(BenchmarkOptions source)
    {
        return new BenchmarkOptions
        {
            // 预热只覆盖代码路径，不复制 N 份并发案例。
            LockInstances = 1,
            Threads = Math.Min(4, Math.Max(1, Environment.ProcessorCount)),
            OperationsPerThread = 2_000,
            ReadSteps = Math.Min(8, source.ReadSteps),
            WriteSteps = Math.Min(8, source.WriteSteps),
            MemoryWorkingSetMb = source.MemoryWorkingSetMb,
            DictionaryEntries = source.DictionaryEntries,
            Workload = source.Workload
        };
    }

    /// <summary>在线程或业务对象创建前验证所有参数。</summary>
    public void Validate()
    {
        if (Threads < 1) throw new ArgumentOutOfRangeException(nameof(Threads));
        if (LockInstances < 1) throw new ArgumentOutOfRangeException(nameof(LockInstances));
        if (TotalWorkerThreads > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LockInstances),
                "lock-instances × threads exceeds the runtime thread-array limit.");
        }
        if (OperationsPerThread < 1) throw new ArgumentOutOfRangeException(nameof(OperationsPerThread));
        if (ReadSteps < 0) throw new ArgumentOutOfRangeException(nameof(ReadSteps));
        if (WriteSteps < 0) throw new ArgumentOutOfRangeException(nameof(WriteSteps));
        if (MemoryWorkingSetMb < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemoryWorkingSetMb),
                "Memory working set must be greater than 0 MiB.");
        }

        if (DictionaryEntries < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DictionaryEntries),
                "Dictionary size must be greater than 0.");
        }

        if (AdvancedOperationsPerLock < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AdvancedOperationsPerLock),
                "Advanced operations per lock must be greater than 0.");
        }

        if (SemanticWorkersPerLock < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SemanticWorkersPerLock),
                "Full semantic testing requires at least 2 workers per lock.");
        }

        if (SemanticOperationsPerLock < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SemanticOperationsPerLock),
                "Semantic operations per lock must be greater than 0.");
        }

        if ((long)LockInstances * SemanticWorkersPerLock > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SemanticWorkersPerLock),
                "lock-instances × semantic-workers exceeds the runtime thread-array limit.");
        }

        int selectedModes = (RunAdvancedCorrectness ? 1 : 0) +
                            (RunAdvancedPerformance ? 1 : 0) +
                            (RunPipelineSemantics ? 1 : 0) +
                            (PipelineStressDuration.HasValue ? 1 : 0) +
                            (RunFullSemantics ? 1 : 0) +
                            (FullSemanticStressDuration.HasValue ? 1 : 0) +
                            (EnduranceDuration.HasValue ? 1 : 0) +
                            (ContentionStressDuration.HasValue ? 1 : 0);
        if (selectedModes > 1)
        {
            throw new ArgumentException(
                "Select only one of --advanced-correctness, --advanced-perf, --pipeline-semantics, --pipeline-stress, --full-semantics, --full-semantics-stress, --endurance, or --contention-stress.");
        }

        if (EnduranceDuration.HasValue && EnduranceDuration.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EnduranceDuration),
                "Endurance duration must be greater than zero.");
        }

        if (FullSemanticStressDuration.HasValue && FullSemanticStressDuration.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FullSemanticStressDuration),
                "Full semantic stress duration must be greater than zero.");
        }

        if (PipelineStressDuration.HasValue && PipelineStressDuration.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PipelineStressDuration),
                "Pipeline stress duration must be greater than zero.");
        }

        if (ContentionStressDuration.HasValue && ContentionStressDuration.Value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ContentionStressDuration),
                "Contention stress duration must be greater than zero.");
        }
    }
}

/// <summary>将命令行文本转换为 <see cref="BenchmarkOptions"/>。</summary>
internal static class CommandLineParser
{
    public static BenchmarkOptions Parse(string[] args)
    {
        BenchmarkOptions options = new BenchmarkOptions();
        int? commonSteps = null;
        int? readSteps = null;
        int? writeSteps = null;

        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];

            string NextValue()
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Missing value for {argument}");
                }

                return args[++i];
            }

            switch (argument)
            {
                case "--threads":
                    options.Threads = ParseInt(NextValue(), argument);
                    break;
                case "--lock-instances":
                    options.LockInstances = ParseInt(NextValue(), argument);
                    break;
                case "--operations":
                    options.OperationsPerThread = ParseInt(NextValue(), argument);
                    break;
                case "--work":
                    commonSteps = ParseInt(NextValue(), argument);
                    break;
                case "--read-work":
                    readSteps = ParseInt(NextValue(), argument);
                    break;
                case "--write-work":
                    writeSteps = ParseInt(NextValue(), argument);
                    break;
                case "--workload":
                    options.Workload = ParseWorkload(NextValue());
                    break;
                case "--memory-mb":
                    options.MemoryWorkingSetMb = ParseInt(NextValue(), argument);
                    break;
                case "--dictionary-size":
                    options.DictionaryEntries = ParseInt(NextValue(), argument);
                    break;
                case "--advanced-correctness":
                    options.RunAdvancedCorrectness = true;
                    break;
                case "--advanced-perf":
                    options.RunAdvancedPerformance = true;
                    break;
                case "--pipeline-semantics":
                    options.RunPipelineSemantics = true;
                    break;
                case "--pipeline-stress":
                    options.PipelineStressDuration = ParseDuration(NextValue(), argument);
                    break;
                case "--advanced-operations":
                    options.AdvancedOperationsPerLock = ParseInt(NextValue(), argument);
                    break;
                case "--advanced-seed":
                    options.AdvancedSeed = ParseInt(NextValue(), argument);
                    break;
                case "--full-semantics":
                    options.RunFullSemantics = true;
                    break;
                case "--full-semantics-stress":
                    options.FullSemanticStressDuration = ParseDuration(NextValue(), argument);
                    break;
                case "--semantic-workers":
                    options.SemanticWorkersPerLock = ParseInt(NextValue(), argument);
                    break;
                case "--semantic-operations":
                    options.SemanticOperationsPerLock = ParseInt(NextValue(), argument);
                    break;
                case "--semantic-seed":
                    options.SemanticSeed = ParseInt(NextValue(), argument);
                    break;
                case "--endurance":
                    options.EnduranceDuration = ParseDuration(NextValue(), argument);
                    break;
                case "--contention-stress":
                    options.ContentionStressDuration = ParseDuration(NextValue(), argument);
                    break;
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {argument}");
            }
        }

        options.ReadSteps = readSteps ?? commonSteps ?? options.ReadSteps;
        options.WriteSteps = writeSteps ?? commonSteps ?? options.WriteSteps;
        options.Validate();
        return options;
    }

    private static int ParseInt(string value, string argument)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            throw new ArgumentException($"Invalid integer for {argument}: {value}");
        }

        return result;
    }

    private static WorkloadKind ParseWorkload(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "cpu" => WorkloadKind.Cpu,
            "memory" => WorkloadKind.Memory,
            "dictionary" => WorkloadKind.Dictionary,
            "ledger" => WorkloadKind.Ledger,
            "payload" => WorkloadKind.Payload,
            _ => throw new ArgumentException($"Unknown workload: {value}. Select exactly one workload.")
        };
    }

    private static TimeSpan ParseDuration(string value, string argument)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan timeSpan) &&
            timeSpan > TimeSpan.Zero)
        {
            return timeSpan;
        }

        if (value.Length >= 2 &&
            double.TryParse(
                value.Substring(0, value.Length - 1),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double amount) &&
            amount > 0)
        {
            return char.ToLowerInvariant(value[value.Length - 1]) switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => throw new ArgumentException(
                    $"Invalid duration for {argument}: {value}. Use 30s, 15m, 24h, 1d, or hh:mm:ss.")
            };
        }

        throw new ArgumentException(
            $"Invalid duration for {argument}: {value}. Use 30s, 15m, 24h, 1d, or hh:mm:ss.");
    }
}

/// <summary>集中维护命令行帮助文本。</summary>
internal static class UsagePrinter
{
    public static void Print()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  LockBenchmark.exe --lock-instances 1 --threads 32 --workload ledger --operations 100000 --read-work 64 --write-work 128");
        Console.WriteLine("  LockBenchmark.exe --advanced-correctness");
        Console.WriteLine("  LockBenchmark.exe --advanced-perf --threads 64 --operations 100000 --work 64");
        Console.WriteLine("  LockBenchmark.exe --pipeline-semantics --lock-instances 1 --semantic-workers 64 --semantic-operations 1000");
        Console.WriteLine("  LockBenchmark.exe --pipeline-stress 10m --lock-instances 1 --semantic-workers 64 --semantic-operations 1000");
        Console.WriteLine("  LockBenchmark.exe --advanced-correctness --lock-instances 1000 --advanced-operations 4");
        Console.WriteLine("  LockBenchmark.exe --full-semantics --lock-instances 64 --semantic-workers 4 --semantic-operations 256");
        Console.WriteLine("  LockBenchmark.exe --full-semantics-stress 10m --lock-instances 1 --semantic-workers 64 --semantic-operations 256");
        Console.WriteLine("  LockBenchmark.exe --endurance 24h");
        Console.WriteLine("  LockBenchmark.exe --contention-stress 10s --threads 128");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  --lock-instances   Independent single-lock cases. Default: {BenchmarkOptions.DefaultLockInstances}");
        Console.WriteLine($"  --threads          Dedicated worker threads per lock instance. Default: {BenchmarkOptions.DefaultThreads}");
        Console.WriteLine($"  --operations       Lock acquisitions and completed Works per thread. Default: {BenchmarkOptions.DefaultOperationsPerThread}");
        Console.WriteLine("  --work             Business steps executed inside each held-lock TickRead/TickWrite.");
        Console.WriteLine($"  --read-work        Internal steps per TickRead. Default: {BenchmarkOptions.DefaultReadSteps}");
        Console.WriteLine($"  --write-work       Internal steps per TickWrite. Default: {BenchmarkOptions.DefaultWriteSteps}");
        Console.WriteLine("  --workload         Select exactly one: cpu, memory, dictionary, ledger, payload. Default: cpu");
        Console.WriteLine($"  --memory-mb        Shared memory workload size in MiB. Default: {BenchmarkOptions.DefaultMemoryWorkingSetMb}");
        Console.WriteLine($"  --dictionary-size  Entry count used by dictionary/ledger/payload. Default: {BenchmarkOptions.DefaultDictionaryEntries}");
        Console.WriteLine("  --advanced-correctness  Run stage-3 advanced lock semantic tests only.");
        Console.WriteLine("  --advanced-perf     Measure CEL advanced semantic costs only.");
        Console.WriteLine("  --pipeline-semantics    Run Pipeline semantic stress tests only.");
        Console.WriteLine("  --pipeline-stress       Run repeated Pipeline semantic tests until duration expires.");
        Console.WriteLine($"  --advanced-operations   Base operations per lock and advanced operation kind. Default: {BenchmarkOptions.DefaultAdvancedOperationsPerLock}");
        Console.WriteLine("  --advanced-seed         Optional random seed used by massive independent-lock operation counts.");
        Console.WriteLine("  --full-semantics        Run stage-5 complete access and transition semantic tests only.");
        Console.WriteLine("  --full-semantics-stress Run repeated stage-5 complete semantic tests until duration expires.");
        Console.WriteLine($"  --semantic-workers      Dedicated randomized state-machine workers per lock. Default: {BenchmarkOptions.DefaultSemanticWorkersPerLock}");
        Console.WriteLine($"  --semantic-operations   Random valid-path rounds per lock. Default: {BenchmarkOptions.DefaultSemanticOperationsPerLock}");
        Console.WriteLine("  --semantic-seed         Optional random seed used by full semantic valid-path testing.");
        Console.WriteLine("  --endurance         Run automatic long-duration semantic validation. Examples: 30s, 15m, 24h, 1d.");
        Console.WriteLine("  --contention-stress Measure peak/average Contention under sustained single-lock pressure.");
        Console.WriteLine("  -h, --help         Show this help.");
    }
}
