using System;
using System.Collections.Generic;

namespace LockBenchmark;

/// <summary>一种固定的读写调用比例；ReadPermille 使用千分比。</summary>
internal readonly struct BenchmarkScenario
{
    public string Name { get; }
    public int ReadPermille { get; }

    public BenchmarkScenario(string name, int readPermille)
    {
        Name = name;
        ReadPermille = readPermille;
    }
}

/// <summary>标准横向压测的读写比例目录。</summary>
internal static class BenchmarkScenarioCatalog
{
    private static readonly BenchmarkScenario[] Scenarios =
    {
        new BenchmarkScenario("read/write 100/0", 1_000),
        new BenchmarkScenario("read/write 99.5/0.5", 995),
        new BenchmarkScenario("read/write 90/10", 900),
        new BenchmarkScenario("read/write 50/50", 500),
        new BenchmarkScenario("read/write 30/70", 300),
        new BenchmarkScenario("read/write 0/100", 0)
    };

    public static IReadOnlyList<BenchmarkScenario> All => Scenarios;
}

/// <summary>单个“锁 + Work + 读写比例”测试案例的完整结果。</summary>
internal readonly struct BenchmarkResult
{
    public string LockName { get; }
    public TimeSpan Elapsed { get; }
    public double CpuPercent { get; }
    public long ReadWorks { get; }
    public long WriteWorks { get; }
    public long StateHash { get; }

    /// <summary>所有业务返回值的聚合，仅用于阻止业务代码被优化消除。</summary>
    public long Checksum { get; }

    public long Works => ReadWorks + WriteWorks;

    public BenchmarkResult(
        string lockName,
        TimeSpan elapsed,
        double cpuPercent,
        long readWorks,
        long writeWorks,
        long stateHash,
        long checksum)
    {
        LockName = lockName;
        Elapsed = elapsed;
        CpuPercent = cpuPercent;
        ReadWorks = readWorks;
        WriteWorks = writeWorks;
        StateHash = stateHash;
        Checksum = checksum;
    }
}
