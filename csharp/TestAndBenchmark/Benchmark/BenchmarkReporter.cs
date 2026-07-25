using System;
using System.Runtime;

namespace LockBenchmark;

/// <summary>标准压测的控制台输出与指标格式化。</summary>
internal static class BenchmarkReporter
{
    private const int LockNameWidth = 26;
    private const int ElapsedWidth = 8;
    private const int CpuPercentWidth = 9;
    private const int WorksWidth = 12;
    private const int WorkPerCpuWidth = 11;
    private const int CountWidth = 12;
    private const int StateWidth = 16;
    private const string ColumnGap = "  ";

    public static void PrintEnvironment()
    {
        Console.WriteLine("Lock benchmark");
        Console.WriteLine($".NET={Environment.Version}, OS={Environment.OSVersion}");
        Console.WriteLine($"GC={GCSettings.IsServerGC}, CPU={Environment.ProcessorCount}");
        Console.WriteLine();
    }

    public static void PrintConfiguration(BenchmarkOptions options, WorkDefinition workDefinition)
    {
        Console.WriteLine(
            $"lock-instances={options.LockInstances:n0}, threads/lock={options.Threads}, " +
            $"total-threads={options.TotalWorkerThreads:n0}, " +
            $"works/thread={options.OperationsPerThread:n0}, " +
            $"read-steps={options.ReadSteps}, write-steps={options.WriteSteps}");
        Console.WriteLine($"workload={workDefinition.Name}");
        Console.WriteLine("Workers use dedicated Thread instances and start from a common gate.");
        Console.WriteLine("Each lock instance owns a fresh IWork; all worker groups share one start gate.");
        if (options.TotalWorkerThreads > (long)Environment.ProcessorCount * 32)
        {
            Console.WriteLine(
                $"WARNING: preparing {options.TotalWorkerThreads:n0} dedicated OS threads may take a long time or exceed system resources.");
        }
        Console.WriteLine();
    }

    public static void PrintScenarioHeader(BenchmarkScenario scenario)
    {
        Console.WriteLine($"Scenario: {scenario.Name}");
        Console.WriteLine(
            $"  {"lock type",-LockNameWidth}{ColumnGap}" +
            $"{"elapsed",ElapsedWidth}{ColumnGap}" +
            $"{"cpu%",CpuPercentWidth}{ColumnGap}" +
            $"{"works/s",WorksWidth}{ColumnGap}" +
            $"{"works/s/lock",WorksWidth}{ColumnGap}" +
            $"{"work/cpu%",WorkPerCpuWidth}{ColumnGap}" +
            $"{"reads",CountWidth}{ColumnGap}" +
            $"{"writes",CountWidth}{ColumnGap}" +
            $"{"state",StateWidth}");
    }

    public static void PrintResult(BenchmarkResult result, int lockInstances)
    {
        double elapsedSeconds = Math.Max(0.001, result.Elapsed.TotalSeconds);
        double worksPerSecond = result.Works / elapsedSeconds;
        double worksPerSecondPerLock = worksPerSecond / lockInstances;
        double workPerCpuPercent = result.CpuPercent > 0.000001
            ? worksPerSecond / result.CpuPercent
            : 0.0;
        string elapsed = $"{result.Elapsed.TotalSeconds:0.000}s";
        string cpuPercent = $"{result.CpuPercent:0.0}%";
        string state = $"{unchecked((ulong)result.StateHash):X16}";

        Console.WriteLine(
            $"  {result.LockName,-LockNameWidth}{ColumnGap}" +
            $"{elapsed,ElapsedWidth}{ColumnGap}" +
            $"{cpuPercent,CpuPercentWidth}{ColumnGap}" +
            $"{worksPerSecond,WorksWidth:0}{ColumnGap}" +
            $"{worksPerSecondPerLock,WorksWidth:0}{ColumnGap}" +
            $"{workPerCpuPercent,WorkPerCpuWidth:0}{ColumnGap}" +
            $"{result.ReadWorks,CountWidth:n0}{ColumnGap}" +
            $"{result.WriteWorks,CountWidth:n0}{ColumnGap}" +
            $"{state,StateWidth}");
    }

    public static void PrintStateMismatch() =>
        Console.WriteLine("  WARNING: final work state differs between lock implementations.");

    public static void PrintScenarioEnd() => Console.WriteLine();

    public static void PrintSink(long sink) => Console.WriteLine($"sink={sink}");
}
