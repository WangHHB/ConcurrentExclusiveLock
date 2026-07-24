namespace TestAndBenchmark.Common.Workloads;

internal static class WorkloadModeParser
{
    public static WorkloadMode Parse(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "" or "minimal" or "none" or "cpu" => WorkloadMode.Cpu,
            "memory" or "array" => WorkloadMode.Memory,
            "dictionary" or "dict" or "table" => WorkloadMode.Dictionary,
            "ledger" or "account" => WorkloadMode.Ledger,
            "payload" or "binary" => WorkloadMode.Payload,
            _ => throw new ArgumentException($"Unknown workload: {value}"),
        };
    }
}
