using System.Globalization;
using TestAndBenchmark.Common.Workloads;

namespace TestAndBenchmark.Benchmarks.SteadyState;

internal sealed record SteadyStateOptions(
    string Profile,
    TimeSpan Warmup,
    int LockInstances,
    int[] ThreadCounts,
    int OperationsPerThread,
    SteadyStateScenario[] Scenarios,
    SteadyStateTarget[] Targets,
    WorkloadMode Workload,
    int ConcurrentWork,
    int ExclusiveWork,
    int MemoryMb,
    int DictionarySize)
{
    public static SteadyStateOptions Parse(string[] args)
    {
        string profile = GetString(args, "--profile", "quick").ToLowerInvariant();
        int logical = Environment.ProcessorCount;

        TimeSpan warmup;
        int lockInstances;
        int operationsPerThread;
        int[] threadCounts;

        if (profile == "standard")
        {
            warmup = TimeSpan.FromSeconds(2);
            lockInstances = 1;
            operationsPerThread = 100_000;
            threadCounts = [Math.Max(1, logical * 4)];
        }
        else if (profile == "quick")
        {
            warmup = TimeSpan.FromMilliseconds(500);
            lockInstances = 1;
            operationsPerThread = 100;
            threadCounts = [Math.Min(4, logical)];
        }
        else
        {
            throw new ArgumentException($"Unknown steady-state profile: {profile}");
        }

        int? commonWork = TryGetInt(args, "--work");
        int concurrentWork = Math.Max(0, commonWork ?? 64);
        int exclusiveWork = Math.Max(0, commonWork ?? 128);

        warmup = TimeSpan.FromSeconds(GetDouble(args, "--warmupSeconds", warmup.TotalSeconds));
        lockInstances = Math.Max(1, GetInt(args, "--lock-instances", GetInt(args, "--entities", lockInstances)));
        operationsPerThread = Math.Max(1, GetInt(args, "--operations", operationsPerThread));
        concurrentWork = Math.Max(0, GetInt(args, "--read-work", concurrentWork));
        exclusiveWork = Math.Max(0, GetInt(args, "--write-work", exclusiveWork));

        string threadText = GetString(args, "--threads", string.Join(",", threadCounts));
        threadCounts = ParseThreadCounts(threadText);

        return new SteadyStateOptions(
            profile,
            warmup,
            lockInstances,
            threadCounts,
            operationsPerThread,
            ParseScenarios(args),
            ParseTargets(GetString(args, "--target", "scope")),
            WorkloadModeParser.Parse(GetString(args, "--workload", "cpu")),
            concurrentWork,
            exclusiveWork,
            Math.Max(1, GetInt(args, "--memory-mb", 64)),
            Math.Max(1, GetInt(args, "--dictionary-size", 1280)));
    }

    private static SteadyStateScenario[] ParseScenarios(string[] args)
    {
        if (HasArgument(args, "--concurrent-percent") || HasArgument(args, "--concurrentPercent"))
        {
            double percent = Math.Clamp(GetDouble(args, "--concurrent-percent", GetDouble(args, "--concurrentPercent", 95)), 0, 100);
            int permille = Math.Clamp((int)Math.Round(percent * 10.0, MidpointRounding.AwayFromZero), 0, 1000);
            return [new SteadyStateScenario($"Concurrent/Exclusive {percent:N2}/{100 - percent:N2}", permille)];
        }

        return
        [
            new SteadyStateScenario("Concurrent/Exclusive 100/0", 1000),
            new SteadyStateScenario("Concurrent/Exclusive 99.5/0.5", 995),
            new SteadyStateScenario("Concurrent/Exclusive 90/10", 900),
            new SteadyStateScenario("Concurrent/Exclusive 50/50", 500),
            new SteadyStateScenario("Concurrent/Exclusive 30/70", 300),
            new SteadyStateScenario("Concurrent/Exclusive 0/100", 0),
        ];
    }

    private static SteadyStateTarget[] ParseTargets(string text)
    {
        if (string.Equals(text, "all", StringComparison.OrdinalIgnoreCase))
        {
            return [SteadyStateTarget.Scope, SteadyStateTarget.Rwls, SteadyStateTarget.Monitor];
        }

        SteadyStateTarget[] values = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value.ToLowerInvariant() switch
            {
                "scope" or "cel" => SteadyStateTarget.Scope,
                "rwls" or "readerwriterlockslim" => SteadyStateTarget.Rwls,
                "monitor" or "lock" => SteadyStateTarget.Monitor,
                _ => throw new ArgumentException($"Unknown steady-state target: {value}"),
            })
            .Distinct()
            .ToArray();

        return values.Length == 0 ? [SteadyStateTarget.Scope] : values;
    }

    private static int[] ParseThreadCounts(string text)
    {
        int[] values = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => int.Parse(value, CultureInfo.InvariantCulture))
            .Where(static value => value > 0)
            .Distinct()
            .Order()
            .ToArray();

        return values.Length == 0 ? [1] : values;
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

    private static bool HasArgument(string[] args, string name)
    {
        return Array.IndexOf(args, name) >= 0;
    }

    private static int GetInt(string[] args, string name, int defaultValue)
    {
        string value = GetString(args, name, defaultValue.ToString(CultureInfo.InvariantCulture));
        return int.Parse(value, CultureInfo.InvariantCulture);
    }

    private static int? TryGetInt(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        return int.Parse(args[index + 1], CultureInfo.InvariantCulture);
    }

    private static double GetDouble(string[] args, string name, double defaultValue)
    {
        string value = GetString(args, name, defaultValue.ToString("R", CultureInfo.InvariantCulture));
        return double.Parse(value, CultureInfo.InvariantCulture);
    }
}

internal readonly record struct SteadyStateScenario(string Name, int ConcurrentPermille)
{
    public double ConcurrentPercent => ConcurrentPermille / 10.0;
}
