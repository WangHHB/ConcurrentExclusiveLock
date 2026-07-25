using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 高级语义测试的小型黑洞，避免 JIT 把极短业务段完全优化掉。
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
