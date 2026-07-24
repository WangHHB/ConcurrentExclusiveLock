using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Order;
using IntomicLib;
using TestAndBenchmark.Common.Workloads;

namespace TestAndBenchmark.Benchmarks.Micro;

[MemoryDiagnoser]
[Config(typeof(MicroBenchmarkConfig))]
[ThreadingDiagnoser]
[RankColumn]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class LowContentionMicroBenchmarks
{
    private const int Workers = 16;
    private const int OperationsPerWorker = 4096;
    private const int TotalOperations = Workers * OperationsPerWorker;

    [Benchmark(Baseline = true, OperationsPerInvoke = TotalOperations)]
    [BenchmarkCategory("LowContention", "ConcurrentOnly")]
    public long ScopeConcurrentOnly()
    {
        return Run(Target.Scope, concurrentPermille: 1000);
    }

    [Benchmark(OperationsPerInvoke = TotalOperations)]
    [BenchmarkCategory("LowContention", "ConcurrentOnly")]
    public long RwlsConcurrentOnly()
    {
        return Run(Target.Rwls, concurrentPermille: 1000);
    }

    [Benchmark(OperationsPerInvoke = TotalOperations)]
    [BenchmarkCategory("LowContention", "ConcurrentOnly")]
    public long MonitorConcurrentOnly()
    {
        return Run(Target.Monitor, concurrentPermille: 1000);
    }

    [Benchmark(OperationsPerInvoke = TotalOperations)]
    [BenchmarkCategory("LowContention", "Mixed995")]
    public long ScopeMixed995()
    {
        return Run(Target.Scope, concurrentPermille: 995);
    }

    [Benchmark(OperationsPerInvoke = TotalOperations)]
    [BenchmarkCategory("LowContention", "Mixed995")]
    public long RwlsMixed995()
    {
        return Run(Target.Rwls, concurrentPermille: 995);
    }

    [Benchmark(OperationsPerInvoke = TotalOperations)]
    [BenchmarkCategory("LowContention", "Mixed995")]
    public long MonitorMixed995()
    {
        return Run(Target.Monitor, concurrentPermille: 995);
    }

    private static long Run(Target target, int concurrentPermille)
    {
        using var entity = new Entity();
        using var startGate = new ManualResetEventSlim(false);
        var sinks = new long[Workers];

        Thread[] threads = Enumerable.Range(0, Workers)
            .Select(workerId => new Thread(() =>
            {
                uint random = CreateWorkerSeed(workerId);
                long sink = 0;

                startGate.Wait();

                for (int operation = 0; operation < OperationsPerWorker; operation++)
                {
                    random = NextRandom(random + (uint)operation);
                    bool isConcurrent = concurrentPermille == 1000 ||
                                        (concurrentPermille != 0 && random % 1000 < concurrentPermille);

                    sink = unchecked(sink + (isConcurrent
                        ? ExecuteConcurrent(entity, target)
                        : ExecuteExclusive(entity, target)));
                }

                sinks[workerId] = sink;
            })
            {
                IsBackground = true,
                Name = $"micro-low-contention-{workerId}",
            })
            .ToArray();

        foreach (Thread thread in threads)
        {
            thread.Start();
        }

        startGate.Set();

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return sinks.Aggregate(0L, static (current, value) => unchecked(current + value));
    }

    private static long ExecuteConcurrent(Entity entity, Target target)
    {
        switch (target)
        {
            case Target.Scope:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireConcurrent();
                    return entity.Work.TickConcurrent();
                }

            case Target.Rwls:
                entity.Rwls.EnterReadLock();
                try
                {
                    return entity.Work.TickConcurrent();
                }
                finally
                {
                    entity.Rwls.ExitReadLock();
                }

            case Target.Monitor:
                lock (entity.MonitorGate)
                {
                    return entity.Work.TickConcurrent();
                }
        }

        throw new ArgumentOutOfRangeException(nameof(target), target, null);
    }

    private static long ExecuteExclusive(Entity entity, Target target)
    {
        switch (target)
        {
            case Target.Scope:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireExclusive();
                    return entity.Work.TickExclusive();
                }

            case Target.Rwls:
                entity.Rwls.EnterWriteLock();
                try
                {
                    return entity.Work.TickExclusive();
                }
                finally
                {
                    entity.Rwls.ExitWriteLock();
                }

            case Target.Monitor:
                lock (entity.MonitorGate)
                {
                    return entity.Work.TickExclusive();
                }
        }

        throw new ArgumentOutOfRangeException(nameof(target), target, null);
    }

    private static uint NextRandom(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return value;
    }

    private static uint CreateWorkerSeed(int workerId)
    {
        unchecked
        {
            uint value = 0x9E3779B9u;
            value ^= (uint)workerId * 0xC2B2AE35u;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            value *= 0x846CA68Bu;
            value ^= value >> 16;
            return value;
        }
    }

    private enum Target
    {
        Scope,
        Rwls,
        Monitor,
    }

    private sealed class Entity : IDisposable
    {
        public ConcurrentExclusiveLock Locker { get; } = ConcurrentExclusiveLock.Create();
        public ReaderWriterLockSlim Rwls { get; } = new();
        public object MonitorGate { get; } = new();
        public IBenchmarkWork Work { get; } = BenchmarkWorkFactory.Create(WorkloadMode.Cpu, 16, 16, 64, 1280);

        public void Dispose()
        {
            Rwls.Dispose();
            Work.Dispose();
        }
    }
}
