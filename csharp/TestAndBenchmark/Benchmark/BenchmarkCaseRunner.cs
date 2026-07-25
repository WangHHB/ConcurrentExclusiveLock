using System;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 执行一个独立测试案例并返回纯数据结果，不负责场景遍历、预热或输出。
/// </summary>
internal static class BenchmarkCaseRunner
{
    public static BenchmarkResult Run(
        LockStrategyDefinition strategyDefinition,
        WorkDefinition workDefinition,
        BenchmarkOptions options,
        int readPermille)
    {
        ILockStrategy[] strategies = new ILockStrategy[options.LockInstances];
        IWork[] works = new IWork[options.LockInstances];

        try
        {
            // N 份案例各自拥有独立的锁和 Work，等价于同时运行 N 份阶段一单锁测试。
            for (int lockIndex = 0; lockIndex < options.LockInstances; lockIndex++)
            {
                strategies[lockIndex] = strategyDefinition.Create();
                works[lockIndex] = workDefinition.Create();
                works[lockIndex].Init();
            }

            long totalReadWorks = 0;
            long totalWriteWorks = 0;
            long checksum = 0;
            int threadsPerLock = options.Threads;
            int totalThreads = checked((int)options.TotalWorkerThreads);

            // 只建立一个全局计时区间。这样 elapsed、CPU time 和总 Works 完全对齐，
            // 不会像多个重叠子计时器那样重复累计进程 CPU 时间。
            ThreadRunMeasurement measurement = DedicatedThreadHarness.Run(
                totalThreads,
                "LockBenchmark",
                globalWorkerIndex =>
                {
                    int lockIndex = globalWorkerIndex / threadsPerLock;
                    int localWorkerIndex = globalWorkerIndex % threadsPerLock;
                    ILockStrategy strategy = strategies[lockIndex];
                    IWork work = works[lockIndex];

                    // 不同锁实例使用互相解相关的序列，避免大量独立锁在同一操作位置
                    // 同步发起读/写形成全局竞争波；相同 lock/worker 坐标在各锁实现间
                    // 仍得到相同 seed，因此横向比较保持完全可复现和公平。
                    uint random = CreateWorkerSeed(lockIndex, localWorkerIndex);
                    long localReadWorks = 0;
                    long localWriteWorks = 0;
                    long localChecksum = 0;

                    try
                    {
                        for (int operation = 0; operation < options.OperationsPerThread; operation++)
                        {
                            random = NextRandom(random + (uint)operation);
                            bool isRead = IsRead(random, readPermille);

                            if (isRead)
                            {
                                localChecksum = unchecked(localChecksum + strategy.ExecuteRead(work));
                                localReadWorks++;
                            }
                            else
                            {
                                localChecksum = unchecked(localChecksum + strategy.ExecuteWrite(work));
                                localWriteWorks++;
                            }
                        }
                    }
                    finally
                    {
                        Interlocked.Add(ref totalReadWorks, localReadWorks);
                        Interlocked.Add(ref totalWriteWorks, localWriteWorks);
                        Interlocked.Add(ref checksum, localChecksum);
                    }
                });

            double cpuPercent = measurement.CpuTime.TotalSeconds /
                                Math.Max(0.001, measurement.Elapsed.TotalSeconds) /
                                Environment.ProcessorCount * 100.0;

            return new BenchmarkResult(
                strategies[0].Name,
                measurement.Elapsed,
                cpuPercent,
                totalReadWorks,
                totalWriteWorks,
                CombineStateHashes(works),
                checksum);
        }
        finally
        {
            // 释放发生在全部工作线程结束后，不进入测试计时。
            for (int lockIndex = works.Length - 1; lockIndex >= 0; lockIndex--)
            {
                works[lockIndex]?.Dispose();
                strategies[lockIndex]?.Dispose();
            }
        }
    }

    private static bool IsRead(uint random, int readPermille)
    {
        return readPermille == 1_000 ||
               (readPermille != 0 && random % 1_000 < readPermille);
    }

    private static long CombineStateHashes(IWork[] works)
    {
        if (works.Length == 1)
        {
            return works[0].StateHash;
        }

        unchecked
        {
            ulong combined = 0x6A09E667F3BCC909UL;
            for (int lockIndex = 0; lockIndex < works.Length; lockIndex++)
            {
                ulong state = (ulong)works[lockIndex].StateHash;
                combined ^= state + 0x9E3779B97F4A7C15UL + (combined << 6) + (combined >> 2);
                combined ^= (uint)lockIndex;
            }

            return (long)combined;
        }
    }

    private static uint NextRandom(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return value;
    }

    private static uint CreateWorkerSeed(int lockIndex, int localWorkerIndex)
    {
        unchecked
        {
            uint value = 0x9E3779B9u;
            value ^= (uint)lockIndex * 0x85EBCA6Bu;
            value ^= (uint)localWorkerIndex * 0xC2B2AE35u;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }
    }
}
