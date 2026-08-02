using System;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Synthetic large-memory workload that deliberately exercises cache, NUMA, and memory-bandwidth effects.
/// It models resuming a large shared working set after scheduling/permission changes rather than a specific business domain.
/// </summary>
internal sealed class MemoryWork : IWork
{
    private readonly int readSteps;
    private readonly int writeSteps;
    private readonly int workingSetMb;
    private long[] buffer = null!;
    private ThreadLocal<uint> readRandom = null!;
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
        buffer = new long[elementCount];

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
        // Concurrent path reads only the shared buffer; the cursor is thread-local and does not mutate business state.
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
        // Exclusive path updates random positions in place, modeling a large shared index/state table.
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
