namespace TestAndBenchmark.Benchmarks.AdvancedPerf;

using TestAndBenchmark.Common.Workloads;

internal sealed record AdvancedPerfOptions(
    int Threads,
    int OperationsPerThread,
    int Work,
    AdvancedPerfTarget[] Targets,
    WorkloadMode Workload,
    int ConcurrentWork,
    int ExclusiveWork,
    int MemoryMb,
    int DictionarySize)
{
    public static AdvancedPerfOptions Parse(string[] args)
    {
        int? commonWork = TryGetInt(args, "--work");
        int concurrentWork = Math.Max(0, commonWork ?? 0);
        int exclusiveWork = Math.Max(0, commonWork ?? 0);

        return new AdvancedPerfOptions(
            Math.Max(1, GetInt(args, "--threads", Environment.ProcessorCount)),
            Math.Max(1, GetInt(args, "--operations", 100_000)),
            Math.Max(0, commonWork ?? 0),
            ParseTargets(GetString(args, "--target", "scope")),
            WorkloadModeParser.Parse(GetString(args, "--workload", "cpu")),
            Math.Max(0, GetInt(args, "--read-work", concurrentWork)),
            Math.Max(0, GetInt(args, "--write-work", exclusiveWork)),
            Math.Max(1, GetInt(args, "--memory-mb", 64)),
            Math.Max(1, GetInt(args, "--dictionary-size", 1280)));
    }

    private static AdvancedPerfTarget[] ParseTargets(string text)
    {
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            return [AdvancedPerfTarget.Scope, AdvancedPerfTarget.Rwls, AdvancedPerfTarget.Monitor];
        }

        AdvancedPerfTarget[] values = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.ToLowerInvariant() switch
            {
                "scope" or "cel" => AdvancedPerfTarget.Scope,
                "rwls" or "readerwriterlockslim" => AdvancedPerfTarget.Rwls,
                "monitor" or "lock" => AdvancedPerfTarget.Monitor,
                _ => throw new ArgumentException($"Unknown advanced-perf target: {value}"),
            })
            .Distinct()
            .ToArray();

        return values.Length == 0 ? [AdvancedPerfTarget.Scope] : values;
    }

    private static string GetString(string[] args, string name, string defaultValue)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
        {
            return defaultValue;
        }

        return args[index + 1];
    }

    private static int GetInt(string[] args, string name, int defaultValue)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
        {
            return defaultValue;
        }

        return int.Parse(args[index + 1]);
    }

    private static int? TryGetInt(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        return int.Parse(args[index + 1]);
    }
}
