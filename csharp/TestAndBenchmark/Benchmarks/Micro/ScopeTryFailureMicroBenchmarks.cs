using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Order;
using IntomicLib;

namespace TestAndBenchmark.Benchmarks.Micro;

[MemoryDiagnoser]
[Config(typeof(MicroBenchmarkConfig))]
[ThreadingDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
[RankColumn]
[CategoriesColumn]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ScopeTryFailureMicroBenchmarks
{
    private const int MaxConcurrent = int.MaxValue;
    private const int Operations = 1024;

    private ConcurrentExclusiveLock _exclusiveHeldLocker;
    private ConcurrentExclusiveLock _concurrentHeldLocker;
    private HeldAccess _exclusiveHolder = null!;
    private HeldAccess _concurrentHolder = null!;
    private int _failures;

    [GlobalSetup]
    public void Setup()
    {
        _exclusiveHeldLocker = ConcurrentExclusiveLock.Create();
        _concurrentHeldLocker = ConcurrentExclusiveLock.Create();

        _exclusiveHolder = HeldAccess.StartExclusive(_exclusiveHeldLocker);
        _concurrentHolder = HeldAccess.StartConcurrent(_concurrentHeldLocker, MaxConcurrent);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _exclusiveHolder.Dispose();
        _concurrentHolder.Dispose();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Operations)]
    [BenchmarkCategory("TryFailure")]
    public int ScopeTryConcurrentFailsUnderExclusive()
    {
        for (int i = 0; i < Operations; i++)
        {
            using var scope = new ConcurrentExclusiveLockScope(_exclusiveHeldLocker);
            if (scope.TryAcquireConcurrent(0, MaxConcurrent) == 0)
            {
                _failures++;
            }
        }

        return _failures;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("TryFailure")]
    public int ScopeTryExclusiveNoPreemptFailsUnderConcurrent()
    {
        for (int i = 0; i < Operations; i++)
        {
            using var scope = new ConcurrentExclusiveLockScope(_concurrentHeldLocker);
            if (!scope.TryAcquireExclusive(false))
            {
                _failures++;
            }
        }

        return _failures;
    }

    [Benchmark(OperationsPerInvoke = Operations)]
    [BenchmarkCategory("TryFailure")]
    public int ScopeTryExclusiveTimeoutZeroFailsUnderExclusive()
    {
        for (int i = 0; i < Operations; i++)
        {
            using var scope = new ConcurrentExclusiveLockScope(_exclusiveHeldLocker);
            if (!scope.TryAcquireExclusive(0))
            {
                _failures++;
            }
        }

        return _failures;
    }

    private sealed class HeldAccess : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly Thread _thread;
        private Exception? _exception;

        private HeldAccess(ConcurrentExclusiveLock locker, Action<ConcurrentExclusiveLockScope> acquire)
        {
            _thread = new Thread(() => Run(locker, acquire))
            {
                IsBackground = true,
                Name = "scope-try-failure-holder",
            };

            _thread.Start();
            _ready.Wait();

            if (_exception is not null)
            {
                throw new InvalidOperationException("Failed to prepare held access for TryFailure benchmark.", _exception);
            }
        }

        public static HeldAccess StartExclusive(ConcurrentExclusiveLock locker)
        {
            return new HeldAccess(locker, static scope => scope.AcquireExclusive());
        }

        public static HeldAccess StartConcurrent(ConcurrentExclusiveLock locker, int maxConcurrent)
        {
            return new HeldAccess(locker, scope => scope.AcquireConcurrent(maxConcurrent));
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join();
            _stop.Dispose();
            _ready.Dispose();
        }

        private void Run(ConcurrentExclusiveLock locker, Action<ConcurrentExclusiveLockScope> acquire)
        {
            try
            {
                using var scope = new ConcurrentExclusiveLockScope(locker);
                acquire(scope);
                _ready.Set();
                _stop.Wait();
            }
            catch (Exception ex)
            {
                _exception = ex;
                _ready.Set();
            }
        }
    }
}
