using System.Threading;
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
[DisassemblyDiagnoser(maxDepth: 3)]
[RankColumn]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ScopeUncontendedMicroBenchmarks
{
    private const int Operations = 1024;

    private readonly object _monitor = new();
    private readonly ReaderWriterLockSlim _rwls = new();

    private ConcurrentExclusiveLock _locker;
    private ConcurrentExclusiveLockPipeline _pipeline;
    private ConcurrentExclusiveLockSegment[] _pipelineSingleConcurrent = [];
    private ConcurrentExclusiveLockSegment[] _pipelineExclusiveThenConcurrent = [];
    private IBenchmarkWork _work = null!;
    private int _value;
    private int _contextId;
    private int _epochId;

    [GlobalSetup]
    public void Setup()
    {
        _locker = ConcurrentExclusiveLock.Create();
        _pipeline = new ConcurrentExclusiveLockPipeline(_locker);
        _pipelineSingleConcurrent =
        [
            ConcurrentExclusiveLockSegment.Concurrent(() => ConsumePayload()),
        ];
        _pipelineExclusiveThenConcurrent =
        [
            ConcurrentExclusiveLockSegment.Exclusive(() => MutatePayload()),
            ConcurrentExclusiveLockSegment.ConvergeConcurrent(() => ConsumePayload()),
        ];
        _work = BenchmarkWorkFactory.Create(WorkloadMode.Cpu, 4, 4, 64, 1280);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _rwls.Dispose();
        _work.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
    [BenchmarkCategory("UpperBound")]
    public int NoLock()
    {
        for (int i = 0; i < Operations; i++)
        {
            ConsumePayload();
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("UpperBound")]
    public int AtomicOnly()
    {
        for (int i = 0; i < Operations; i++)
        {
            Interlocked.Increment(ref _value);
            ConsumePayload();
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("BCL")]
    public int MonitorLock()
    {
        for (int i = 0; i < Operations; i++)
        {
            lock (_monitor)
            {
                MutatePayload();
            }
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("BCL")]
    public int RwlsSharedMode()
    {
        for (int i = 0; i < Operations; i++)
        {
            _rwls.EnterReadLock();
            try
            {
                ConsumePayload();
            }
            finally
            {
                _rwls.ExitReadLock();
            }
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("BCL")]
    public int RwlsExclusiveMode()
    {
        for (int i = 0; i < Operations; i++)
        {
            _rwls.EnterWriteLock();
            try
            {
                MutatePayload();
            }
            finally
            {
                _rwls.ExitWriteLock();
            }
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Scope")]
    public int ScopeConcurrent()
    {
        for (int i = 0; i < Operations; i++)
        {
            using var scope = new ConcurrentExclusiveLockScope(_locker);
            scope.AcquireConcurrent();
            ConsumePayload();
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Scope")]
    public int ScopeExclusive()
    {
        for (int i = 0; i < Operations; i++)
        {
            using var scope = new ConcurrentExclusiveLockScope(_locker);
            scope.AcquireExclusive();
            MutatePayload();
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Scope")]
    public int ScopeExclusiveToConcurrent()
    {
        for (int i = 0; i < Operations; i++)
        {
            using var scope = new ConcurrentExclusiveLockScope(_locker);
            scope.AcquireExclusive();
            MutatePayload();
            scope.ExclusiveToConcurrent();
            ConsumePayload();
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Scope")]
    public int ScopeContextUpgrade()
    {
        for (int i = 0; i < Operations; i++)
        {
            using var scope = new ConcurrentExclusiveLockScope(_locker);
            scope.AcquireConcurrent();
            if (!scope.TryConcurrentToExclusiveWithSwitchContextID(++_contextId))
            {
                throw new InvalidOperationException("Context upgrade unexpectedly failed.");
            }

            MutatePayload();
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Scope")]
    public int ScopeEpochUpgrade()
    {
        for (int i = 0; i < Operations; i++)
        {
            using var scope = new ConcurrentExclusiveLockScope(_locker);
            scope.AcquireConcurrent();
            if (!scope.TryConcurrentToExclusiveWithRaiseEpochID(++_epochId))
            {
                throw new InvalidOperationException("Epoch upgrade unexpectedly failed.");
            }

            MutatePayload();
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Pipeline")]
    public int PipelineSingleConcurrent()
    {
        for (int i = 0; i < Operations; i++)
        {
            _pipeline.DoPipeline(_pipelineSingleConcurrent);
        }

        return _value;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("Pipeline")]
    public int PipelineExclusiveThenConcurrent()
    {
        for (int i = 0; i < Operations; i++)
        {
            _pipeline.DoPipeline(_pipelineExclusiveThenConcurrent);
        }

        return _value;
    }

    private void ConsumePayload()
    {
        _value ^= (int)_work.TickConcurrent();
    }

    private void MutatePayload()
    {
        _value ^= (int)_work.TickExclusive();
    }
}
