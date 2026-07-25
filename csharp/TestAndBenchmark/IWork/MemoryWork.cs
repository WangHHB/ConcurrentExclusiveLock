using System;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 极限基线：在大块共享内存中进行随机读写，刻意制造缓存、NUMA 和内存带宽压力。
/// 它用于观察线程切换后恢复巨大工作集的代价，不对应某个具体业务领域。
/// </summary>
internal sealed class MemoryWork : IWork
{
    private readonly int readSteps;
    private readonly int writeSteps;
    private readonly int workingSetMb;
    private long[] buffer;
    private ThreadLocal<uint> readRandom;
    private int readerSeed;
    private uint writeRandom;
    private long state;

    public MemoryWork(int readSteps, int writeSteps, int workingSetMb)
    {
        this.readSteps = readSteps;
        this.writeSteps = writeSteps;
        this.workingSetMb = workingSetMb;
    }

    public long StateHash => Volatile.Read(ref state);

    public void Init()
    {
        long bytes = checked((long)workingSetMb * 1024 * 1024);
        int elementCount = checked((int)(bytes / sizeof(long)));
        buffer = new long[Math.Max(1_024, elementCount)];

        long current = (long)0x6A09E667F3BCC909UL;
        for (int i = 0; i < buffer.Length; i++)
        {
            current = Mix(current + i);
            buffer[i] = current;
        }

        readerSeed = 0;
        readRandom = new ThreadLocal<uint>(() => Seed((uint)Interlocked.Increment(ref readerSeed)));
        writeRandom = 0xC8013EA4u;
        state = 0;
    }

    public long TickRead()
    {
        // 读路径只读取共享 buffer；随机游标保存在 ThreadLocal 中，不污染共享业务状态。
        long result = Volatile.Read(ref state);
        uint random = readRandom.Value;

        for (int i = 0; i < readSteps; i++)
        {
            random = Next(random);
            int index = (int)(random % (uint)buffer.Length);
            result = Mix(result ^ (buffer[index] + i));
        }

        readRandom.Value = random;
        return result;
    }

    public long TickWrite()
    {
        // 写路径原位更新随机位置，模拟大型共享索引或状态表的修改。
        long result = state + 1;
        uint random = writeRandom;

        for (int i = 0; i < writeSteps; i++)
        {
            random = Next(random);
            int index = (int)(random % (uint)buffer.Length);
            long next = Mix(buffer[index] ^ result ^ i);
            buffer[index] = next;
            result = next;
        }

        writeRandom = random;
        state = result;
        return result;
    }

    public void Dispose()
    {
        readRandom?.Dispose();
    }

    private static uint Seed(uint ordinal)
    {
        uint result = ordinal * 747_796_405u + 2_891_336_453u;
        return result == 0 ? 1u : result;
    }

    private static uint Next(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return value;
    }

    private static long Mix(long input)
    {
        unchecked
        {
            ulong result = (ulong)input;
            result ^= result >> 33;
            result *= 0xFF51AFD7ED558CCDUL;
            result ^= result >> 33;
            result *= 0xC4CEB9FE1A85EC53UL;
            result ^= result >> 33;
            return (long)result;
        }
    }
}
