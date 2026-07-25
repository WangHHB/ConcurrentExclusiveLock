namespace LockBenchmark;

/// <summary>
/// 使用 C# monitor lock 的排他基线；读写业务均串行执行。
/// </summary>
internal sealed class MonitorLockStrategy : ILockStrategy
{
    private readonly object locker = new object();

    public string Name => "lock";

    public long ExecuteRead(IWork work)
    {
        lock (locker)
        {
            return work.TickRead();
        }
    }

    public long ExecuteWrite(IWork work)
    {
        lock (locker)
        {
            return work.TickWrite();
        }
    }

    public void Dispose()
    {
    }
}
