using TestAndBenchmark.Common.Workloads;

namespace TestAndBenchmark.Benchmarks.ExclusivePreemption;

internal sealed record ExclusivePreemptionOptions(
    string Profile,
    int ConcurrentThreads,
    int Attempts,
    int ConcurrentSpin,
    int ExclusiveHoldMilliseconds,
    int ExclusivePauseMilliseconds,
    int ExclusiveTimeoutMilliseconds,
    ExclusivePreemptionTarget[] Targets,
    WorkloadMode Workload,
    int ConcurrentWork,
    int ExclusiveWork,
    int MemoryMb,
    int DictionarySize)
{
    public static ExclusivePreemptionOptions Parse(string[] args)
    {
        string profile = GetString(args, "--profile", "quick").ToLowerInvariant();
        int logical = Environment.ProcessorCount;

        int concurrentThreads;
        int attempts;
        int concurrentSpin;
        int exclusiveHoldMilliseconds;
        int exclusivePauseMilliseconds;
        int exclusiveTimeoutMilliseconds;

        if (profile == "standard")
        {
            concurrentThreads = logical;
            attempts = 100;
            concurrentSpin = 64;
            exclusiveHoldMilliseconds = 1;
            exclusivePauseMilliseconds = 10;
            exclusiveTimeoutMilliseconds = 5000;
        }
        else if (profile == "quick")
        {
            concurrentThreads = Math.Min(4, logical);
            attempts = 20;
            concurrentSpin = 64;
            exclusiveHoldMilliseconds = 1;
            exclusivePauseMilliseconds = 5;
            exclusiveTimeoutMilliseconds = 2000;
        }
        else
        {
            throw new ArgumentException($"Unknown Exclusive preemption profile: {profile}");
        }

        int? commonWork = TryGetInt(args, "--work");
        int concurrentWork = Math.Max(0, commonWork ?? 64);
        int exclusiveWork = Math.Max(0, commonWork ?? 64);

        return new ExclusivePreemptionOptions(
            profile,
            Math.Max(1, GetInt(args, "--threads", GetInt(args, "--concurrents", concurrentThreads))),
            Math.Max(1, GetInt(args, "--attempts", attempts)),
            Math.Max(0, GetInt(args, "--concurrent-spin", GetInt(args, "--concurrentSpin", concurrentSpin))),
            Math.Max(0, GetInt(args, "--exclusive-hold-ms", GetInt(args, "--exclusiveHoldMs", exclusiveHoldMilliseconds))),
            Math.Max(0, GetInt(args, "--exclusive-pause-ms", GetInt(args, "--exclusivePauseMs", exclusivePauseMilliseconds))),
            Math.Max(1, GetInt(args, "--exclusive-timeout-ms", GetInt(args, "--exclusiveTimeoutMs", exclusiveTimeoutMilliseconds))),
            ParseTargets(GetString(args, "--target", "scope")),
            WorkloadModeParser.Parse(GetString(args, "--workload", "cpu")),
            Math.Max(0, GetInt(args, "--read-work", concurrentWork)),
            Math.Max(0, GetInt(args, "--write-work", exclusiveWork)),
            Math.Max(1, GetInt(args, "--memory-mb", 64)),
            Math.Max(1, GetInt(args, "--dictionary-size", 1280)));
    }

    private static ExclusivePreemptionTarget[] ParseTargets(string text)
    {
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            return [ExclusivePreemptionTarget.Scope, ExclusivePreemptionTarget.Rwls, ExclusivePreemptionTarget.Monitor];
        }

        ExclusivePreemptionTarget[] values = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.ToLowerInvariant() switch
            {
                "scope" or "cel" => ExclusivePreemptionTarget.Scope,
                "rwls" or "readerwriterlockslim" => ExclusivePreemptionTarget.Rwls,
                "monitor" or "lock" => ExclusivePreemptionTarget.Monitor,
                _ => throw new ArgumentException($"Unknown Exclusive preemption target: {value}"),
            })
            .Distinct()
            .ToArray();

        return values.Length == 0 ? [ExclusivePreemptionTarget.Scope] : values;
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
        return int.Parse(GetString(args, name, defaultValue.ToString()));
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
