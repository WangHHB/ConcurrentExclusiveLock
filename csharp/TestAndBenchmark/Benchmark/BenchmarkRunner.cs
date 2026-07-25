using System;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 标准压测总控：选择 Work、执行预热、遍历场景和锁，并协调结果输出。
/// </summary>
internal static class BenchmarkRunner
{
    private static long sink;

    public static void Run(BenchmarkOptions options)
    {
        WorkDefinition workDefinition = WorkFactory.Create(options);
        BenchmarkReporter.PrintConfiguration(options, workDefinition);
        Warmup(options);

        foreach (BenchmarkScenario scenario in BenchmarkScenarioCatalog.All)
        {
            RunScenario(options, workDefinition, scenario);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        BenchmarkReporter.PrintSink(Volatile.Read(ref sink));
    }

    private static void Warmup(BenchmarkOptions options)
    {
        BenchmarkOptions warmupOptions = BenchmarkOptions.CreateWarmup(options);
        WorkDefinition warmupWork = WorkFactory.Create(warmupOptions);

        foreach (LockStrategyDefinition strategy in LockStrategyCatalog.All)
        {
            BenchmarkCaseRunner.Run(strategy, warmupWork, warmupOptions, readPermille: 900);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void RunScenario(
        BenchmarkOptions options,
        WorkDefinition workDefinition,
        BenchmarkScenario scenario)
    {
        BenchmarkReporter.PrintScenarioHeader(scenario);
        long? expectedState = null;
        bool stateMismatch = false;

        foreach (LockStrategyDefinition strategy in LockStrategyCatalog.All)
        {
            // 每个锁类型开始前清理上一行遗留的锁、Work、ThreadLocal 和线程对象。
            // 回收发生在新案例创建及计时之前，不计入该锁的 elapsed / CPU time。
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(
                GC.MaxGeneration,
                GCCollectionMode.Forced,
                blocking: true,
                compacting: true);

            BenchmarkResult result = BenchmarkCaseRunner.Run(
                strategy,
                workDefinition,
                options,
                scenario.ReadPermille);

            BenchmarkReporter.PrintResult(result, options.LockInstances);
            Volatile.Write(ref sink, result.Checksum ^ result.StateHash);

            if (!expectedState.HasValue)
            {
                expectedState = result.StateHash;
            }
            else if (expectedState.Value != result.StateHash)
            {
                stateMismatch = true;
            }
        }

        if (stateMismatch)
        {
            BenchmarkReporter.PrintStateMismatch();
        }

        BenchmarkReporter.PrintScenarioEnd();
    }
}
