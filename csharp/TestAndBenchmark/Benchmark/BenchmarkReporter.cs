namespace LockBenchmark;

internal static class BenchmarkReporter
{
    public static void PrintEnvironment(EnvironmentSnapshot environment)
    {
        Console.WriteLine($"runtime={environment.Framework}");
        Console.WriteLine($"OS={environment.OsDescription}, process-arch={environment.ProcessArchitecture}, OS-arch={environment.OsArchitecture}");

        List<string> cpu = new() { $"logical={environment.LogicalProcessors}" };
        if (!string.IsNullOrWhiteSpace(environment.CpuModel)) cpu.Add($"model={environment.CpuModel}");
        if (environment.AllowedLogicalProcessors.HasValue) cpu.Add($"cpuset={environment.AllowedLogicalProcessors.Value}");
        if (environment.CpuQuotaProcessors.HasValue) cpu.Add($"quota={environment.CpuQuotaProcessors.Value:0.###}");
        if (environment.PhysicalCores.HasValue) cpu.Add($"physical={environment.PhysicalCores.Value}");
        if (environment.Sockets.HasValue) cpu.Add($"sockets={environment.Sockets.Value}");
        if (environment.NumaNodes.HasValue) cpu.Add($"NUMA={environment.NumaNodes.Value}");
        if (environment.SmtActive.HasValue) cpu.Add($"SMT={environment.SmtActive.Value}");
        Console.WriteLine($"CPU={string.Join(", ", cpu)}");

        Console.WriteLine($"ServerGC={environment.ServerGc}, GC={environment.GcLatencyMode}, StopwatchFrequency={environment.StopwatchFrequency:n0}");
        Console.WriteLine();
    }

    public static string FormatLatency(double nanoseconds)
    {
        if (nanoseconds >= 1_000_000) return $"{nanoseconds / 1_000_000:0.###}ms";
        if (nanoseconds >= 1_000) return $"{nanoseconds / 1_000:0.###}us";
        return $"{nanoseconds:0.###}ns";
    }

    public static string FormatDuration(TimeSpan value)
    {
        if (value.TotalMilliseconds >= 1) return $"{value.TotalMilliseconds:0.###}ms";
        return $"{value.TotalMicroseconds:0.###}us";
    }

}
