using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;

namespace TestAndBenchmark.Benchmarks.Micro;

public class MicroBenchmarkConfig : ManualConfig
{
    public MicroBenchmarkConfig()
    {
        AddColumn(new OperationsPerSecondColumn());
    }
}

internal sealed class OperationsPerSecondColumn : IColumn
{
    public string Id => nameof(OperationsPerSecondColumn);
    public string ColumnName => "Ops/s";
    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Statistics;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;
    public string Legend => "Throughput calculated from BenchmarkDotNet Mean.";

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        return GetValue(summary, benchmarkCase, SummaryStyle.Default);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        Statistics? statistics = summary[benchmarkCase]?.ResultStatistics;
        if (statistics is null || statistics.Mean <= 0)
        {
            return "NA";
        }

        double operationsPerSecond = 1_000_000_000.0 / statistics.Mean;
        return operationsPerSecond.ToString("N0", style.CultureInfo);
    }

    public bool IsAvailable(Summary summary)
    {
        return true;
    }

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase)
    {
        return false;
    }
}
