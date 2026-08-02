using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Small semantic-test sink that prevents the JIT from eliminating extremely short business bodies.
/// </summary>
internal static class AdvancedSemanticSink
{
    private static long value;

    public static long Value => Volatile.Read(ref value);

    public static void Add(long delta)
    {
        Interlocked.Add(ref value, delta);
    }
}
