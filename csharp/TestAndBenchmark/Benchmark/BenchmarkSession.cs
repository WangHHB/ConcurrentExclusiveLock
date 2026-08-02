using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LockBenchmark;

/// <summary>Append-only JSONL session writer plus non-invasive environment capture.</summary>
/// <remarks>
/// Porting contract: one invocation emits environment and invocation records before result records.
/// Hardware discovery is metadata only. Missing topology information is recorded as unknown and must
/// never trigger workload scaling. JSONL is append-only so console logs and structured records can be
/// archived together without selecting a favorable result.
/// </remarks>
internal sealed class BenchmarkSession : IDisposable
{
    private const int SchemaVersion = 10;
    private readonly StreamWriter? writer;
    private readonly JsonSerializerOptions jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public BenchmarkOptions Options { get; }
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    public BenchmarkSession(BenchmarkOptions options)
    {
        Options = options;
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            string fullPath = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            writer = new StreamWriter(new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }
    }

    public void WriteHeader()
    {
        EnvironmentSnapshot snapshot = EnvironmentSnapshot.Capture();
        BenchmarkReporter.PrintEnvironment(snapshot);
        Write("environment", snapshot);
        Write("invocation", new
        {
            mode = Options.Mode.ToString(),
            Options.LockInstances,
            Options.Threads,
            Options.OperationsPerThread,
            Options.ConcurrentWorkSteps,
            Options.ExclusiveWorkSteps,
            workload = Options.Workload.ToString(),
            Options.MemoryWorkingSetMb,
            Options.DictionaryEntries,
            Options.PayloadFrames,
            Options.ConcurrentPermille,
            Options.LatencySampleEvery,
            Options.PrepareSteps,
            Options.CommitSteps,
            Options.PostSteps,
            Options.SemanticWorkersPerLock,
            Options.SemanticOperationsPerLock,
            Options.SemanticSeed,
            Options.PipelineExceptionPermille,
            Options.AdvancedOperationsPerLock,
            Options.AdvancedSeed,
            pipelineStressSeconds = Options.PipelineStressDuration?.TotalSeconds,
            enduranceSeconds = Options.EnduranceDuration?.TotalSeconds,
            contentionDiagnosticSeconds = Options.ContentionDiagnosticDuration?.TotalSeconds,
            Options.UpgradeContentionConcurrentThreads,
            Options.UpgradeContentionExclusiveThreads,
            commandLine = Environment.CommandLine
        });
    }

    public void Write(string kind, object data)
    {
        if (writer is null) return;
        var envelope = new
        {
            schemaVersion = SchemaVersion,
            timestampUtc = DateTimeOffset.UtcNow,
            sessionId = SessionId,
            machineId = Options.MachineId,
            experimentId = Options.ExperimentId,
            processId = Environment.ProcessId,
            executableVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            kind,
            data
        };
        writer.WriteLine(JsonSerializer.Serialize(envelope, jsonOptions));
    }

    public void Dispose() => writer?.Dispose();
}

internal sealed record EnvironmentSnapshot(
    string Framework,
    string RuntimeVersion,
    string OsDescription,
    string OsArchitecture,
    string ProcessArchitecture,
    int LogicalProcessors,
    string? AllowedCpuList,
    int? AllowedLogicalProcessors,
    double? CpuQuotaProcessors,
    string? CpuModel,
    int? PhysicalCores,
    int? Sockets,
    int? NumaNodes,
    bool? SmtActive,
    bool ServerGc,
    string GcLatencyMode,
    long StopwatchFrequency,
    long TotalAvailableMemoryBytes,
    string CommandLine)
{
    public static EnvironmentSnapshot Capture()
    {
        string? allowedList = LinuxTopology.TryReadAllowedCpuList();
        int? allowedCount = LinuxTopology.CountCpuList(allowedList);
        double? quota = LinuxTopology.TryReadCpuQuota();
        bool topologyReliable = !quota.HasValue || !allowedCount.HasValue || quota.Value >= allowedCount.Value - 0.001;

        int? physical = topologyReliable ? LinuxTopology.TryReadPhysicalCoreCount(allowedList) : null;
        int? sockets = topologyReliable ? LinuxTopology.TryReadSocketCount(allowedList) : null;
        int? numa = topologyReliable ? LinuxTopology.TryReadNumaNodeCount() : null;
        bool? smt = physical.HasValue && allowedCount.HasValue
            ? allowedCount.Value > physical.Value
            : null;

        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
        return new EnvironmentSnapshot(
            RuntimeInformation.FrameworkDescription,
            Environment.Version.ToString(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.ProcessorCount,
            allowedList,
            allowedCount,
            quota,
            LinuxTopology.TryReadCpuModel(),
            physical,
            sockets,
            numa,
            smt,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            Stopwatch.Frequency,
            gcInfo.TotalAvailableMemoryBytes,
            Environment.CommandLine);
    }
}

/// <summary>Best-effort Linux-only metadata readers; failures return unknown and never affect execution.</summary>
internal static class LinuxTopology
{
    public static string? TryReadAllowedCpuList()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
                {
                    return line.Split(':', 2)[1].Trim();
                }
            }
        }
        catch { }
        return null;
    }

    public static int? CountCpuList(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return null;
        try
        {
            int count = 0;
            foreach (string part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] bounds = part.Split('-', 2);
                int first = int.Parse(bounds[0]);
                int last = bounds.Length == 1 ? first : int.Parse(bounds[1]);
                count = checked(count + last - first + 1);
            }
            return count;
        }
        catch { return null; }
    }

    public static double? TryReadCpuQuota()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            const string path = "/sys/fs/cgroup/cpu.max";
            if (!File.Exists(path)) return null;
            string[] parts = File.ReadAllText(path).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0] == "max") return null;
            return double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) /
                   double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { return null; }
    }

    public static string? TryReadCpuModel()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Split(':', 2)[1].Trim();
                }
            }
        }
        catch { }
        return null;
    }

    public static int? TryReadPhysicalCoreCount(string? allowedList) =>
        TryReadCpuTopology(allowedList)?.Select(x => (x.Socket, x.Core)).Distinct().Count();

    public static int? TryReadSocketCount(string? allowedList) =>
        TryReadCpuTopology(allowedList)?.Select(x => x.Socket).Distinct().Count();

    public static int? TryReadNumaNodeCount()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            const string path = "/sys/devices/system/node/online";
            return File.Exists(path) ? CountCpuList(File.ReadAllText(path).Trim()) : null;
        }
        catch { return null; }
    }

    private static List<(int Processor, int Socket, int Core)>? TryReadCpuTopology(string? allowedList)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            HashSet<int>? allowed = ExpandCpuList(allowedList);
            List<(int, int, int)> result = new();
            int processor = -1, socket = 0, core = -1;
            void Flush()
            {
                if (processor >= 0 && core >= 0 && (allowed is null || allowed.Contains(processor)))
                {
                    result.Add((processor, socket, core));
                }
            }
            foreach (string raw in File.ReadLines("/proc/cpuinfo").Append(string.Empty))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    Flush();
                    processor = -1; socket = 0; core = -1;
                    continue;
                }
                string[] parts = line.Split(':', 2);
                if (parts.Length != 2) continue;
                string key = parts[0].Trim();
                string value = parts[1].Trim();
                if (key == "processor") processor = int.Parse(value);
                else if (key == "physical id") socket = int.Parse(value);
                else if (key == "core id") core = int.Parse(value);
            }
            return result.Count == 0 ? null : result;
        }
        catch { return null; }
    }

    private static HashSet<int>? ExpandCpuList(string? list)
    {
        if (string.IsNullOrWhiteSpace(list)) return null;
        HashSet<int> set = new();
        foreach (string part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] bounds = part.Split('-', 2);
            int first = int.Parse(bounds[0]);
            int last = bounds.Length == 1 ? first : int.Parse(bounds[1]);
            for (int value = first; value <= last; value++) set.Add(value);
        }
        return set;
    }
}
