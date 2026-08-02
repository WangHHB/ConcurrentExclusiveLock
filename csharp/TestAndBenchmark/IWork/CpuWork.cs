using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Synthetic lower-bound workload with a tiny scalar working set and integer/bit mixing.
/// It emphasizes synchronization overhead for extremely short critical sections and is not a claim about typical business code.
/// </summary>
internal sealed class CpuWork : IWork
{
    private readonly int readSteps;
    private readonly int writeSteps;
    private long state;

    public CpuWork(int readSteps, int writeSteps)
    {
        this.readSteps = readSteps;
        this.writeSteps = writeSteps;
    }

    public long StateHash => Volatile.Read(ref state);

    public void Init()
    {
        state = 0x243F6A8885A308D3L;
    }

    public long TickRead()
    {
        return Run(Volatile.Read(ref state), readSteps);
    }

    public long TickWrite()
    {
        state = Run(state + 1, writeSteps);
        return state;
    }

    public void Dispose()
    {
    }

    private static long Run(long input, int steps)
    {
        long result = input;
        unchecked
        {
            for (int i = 0; i < steps; i++)
            {
                result ^= result << 7;
                result += (long)0x9E3779B97F4A7C1UL;
                result = (long)(((ulong)result << 11) | ((ulong)result >> 53));
                result ^= result >> 17;
            }
        }

        return result;
    }
}
