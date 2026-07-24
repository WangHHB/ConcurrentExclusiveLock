using System.Diagnostics;
using System.Globalization;
using IntomicLib;
using TestAndBenchmark.Common.Workloads;

namespace TestAndBenchmark.Benchmarks.SteadyState;

internal static class SteadyStateBenchmarkRunner
{
    private static long sink;

    public static void Run(SteadyStateOptions options)
    {
        PrintConfiguration(options);
        Warmup(options);

        var results = new List<SteadyStateResult>();

        foreach (SteadyStateScenario scenario in options.Scenarios)
        {
            Console.WriteLine($"Scenario: {scenario.Name}");
            PrintHeader();

            foreach (int threadsPerLock in options.ThreadCounts)
            {
                foreach (SteadyStateTarget target in options.Targets)
                {
                    SteadyStateResult result = RunOne(options, target, threadsPerLock, scenario);
                    results.Add(result);
                    PrintResult(result);
                    Volatile.Write(ref sink, result.Sink ^ result.StateHash);
                }
            }

            Console.WriteLine();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        WriteCsv(results, options);
        Console.WriteLine($"sink={Volatile.Read(ref sink)}");
    }

    private static void PrintConfiguration(SteadyStateOptions options)
    {
        string threadText = string.Join(",", options.ThreadCounts);
        long totalThreads = options.ThreadCounts.Length == 1
            ? (long)options.LockInstances * options.ThreadCounts[0]
            : 0;

        Console.WriteLine(
            totalThreads > 0
                ? $"lock-instances={options.LockInstances:N0}, threads/lock={options.ThreadCounts[0]}, total-threads={totalThreads:N0}, works/thread={options.OperationsPerThread:N0}, concurrent-steps={options.ConcurrentWork}, exclusive-steps={options.ExclusiveWork}"
                : $"lock-instances={options.LockInstances:N0}, threads/lock={threadText}, works/thread={options.OperationsPerThread:N0}, concurrent-steps={options.ConcurrentWork}, exclusive-steps={options.ExclusiveWork}");
        Console.WriteLine($"workload={GetWorkloadName(options)}");
        Console.WriteLine("Workers use dedicated Thread instances and start from a common gate.");
        Console.WriteLine("Each lock instance owns a fresh IWork; all worker groups share one start gate.");

        long maxTotalThreads = options.ThreadCounts.Max() * (long)options.LockInstances;
        if (maxTotalThreads > (long)Environment.ProcessorCount * 32)
        {
            Console.WriteLine($"WARNING: preparing {maxTotalThreads:N0} dedicated OS threads may take a long time or exceed system resources.");
        }

        Console.WriteLine();
    }

    private static string GetWorkloadName(SteadyStateOptions options)
    {
        return options.Workload switch
        {
            WorkloadMode.Memory => $"memory ({options.MemoryMb:N0} MiB shared, concurrent-steps={options.ConcurrentWork}, exclusive-steps={options.ExclusiveWork})",
            WorkloadMode.Dictionary => $"dictionary cache ({options.DictionarySize:N0} entries, concurrent-steps={options.ConcurrentWork}, exclusive-steps={options.ExclusiveWork})",
            WorkloadMode.Ledger => $"account ledger ({options.DictionarySize:N0} accounts, concurrent-steps={options.ConcurrentWork}, exclusive-steps={options.ExclusiveWork})",
            WorkloadMode.Payload => $"binary payload ({Math.Clamp(options.DictionarySize / 8, 1024, 16384):N0} frames, concurrent-steps={options.ConcurrentWork}, exclusive-steps={options.ExclusiveWork})",
            _ => $"cpu (concurrent-steps={options.ConcurrentWork}, exclusive-steps={options.ExclusiveWork})",
        };
    }

    private static void Warmup(SteadyStateOptions options)
    {
        var warmupOptions = options with
        {
            LockInstances = 1,
            ThreadCounts = [Math.Min(4, Math.Max(1, Environment.ProcessorCount))],
            OperationsPerThread = 2000,
            ConcurrentWork = Math.Min(8, options.ConcurrentWork),
            ExclusiveWork = Math.Min(8, options.ExclusiveWork),
            MemoryMb = Math.Min(1, options.MemoryMb),
            DictionarySize = Math.Min(1280, options.DictionarySize),
            Scenarios = [new SteadyStateScenario("warmup", 900)],
        };

        foreach (SteadyStateTarget target in options.Targets)
        {
            SteadyStateResult result = RunOne(warmupOptions, target, warmupOptions.ThreadCounts[0], warmupOptions.Scenarios[0]);
            Volatile.Write(ref sink, result.Sink ^ result.StateHash);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static SteadyStateResult RunOne(
        SteadyStateOptions options,
        SteadyStateTarget target,
        int threadsPerLock,
        SteadyStateScenario scenario)
    {
        Entity[] entities = Enumerable
            .Range(0, options.LockInstances)
            .Select(_ => new Entity(options.Workload, options.ConcurrentWork, options.ExclusiveWork, options.MemoryMb, options.DictionarySize))
            .ToArray();

        try
        {
            long totalWorkerThreads = (long)options.LockInstances * threadsPerLock;
            if (totalWorkerThreads > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(options.LockInstances), "lock-instances × threads exceeds the runtime thread-array limit.");
            }

            int totalThreads = (int)totalWorkerThreads;
            var ready = new CountdownEvent(totalThreads);
            var startGate = new ManualResetEventSlim(false);
            var states = Enumerable.Range(0, totalThreads).Select(_ => new WorkerState()).ToArray();

            Thread[] workers = Enumerable.Range(0, totalThreads)
                .Select(globalWorkerIndex => new Thread(() =>
                {
                    RunWorker(
                        globalWorkerIndex,
                        threadsPerLock,
                        entities,
                        scenario.ConcurrentPermille,
                        target,
                        options.OperationsPerThread,
                        ready,
                        startGate,
                        states[globalWorkerIndex]);
                })
                {
                    IsBackground = true,
                    Name = $"steady-state-{globalWorkerIndex}",
                })
                .ToArray();

            foreach (Thread worker in workers)
            {
                worker.Start();
            }

            ready.Wait();

            using Process process = Process.GetCurrentProcess();
            TimeSpan cpuStart = process.TotalProcessorTime;
            var elapsed = Stopwatch.StartNew();

            startGate.Set();

            foreach (Thread worker in workers)
            {
                worker.Join();
            }

            elapsed.Stop();
            process.Refresh();
            TimeSpan cpuElapsed = process.TotalProcessorTime - cpuStart;

            long concurrentWorks = states.Sum(static state => state.ConcurrentWorks);
            long exclusiveWorks = states.Sum(static state => state.ExclusiveWorks);
            long exclusiveLatencyTicks = states.Sum(static state => state.ExclusiveLatencyTicks);
            long stateHash = CombineStateHashes(entities);
            long localSink = states.Aggregate(0L, static (current, state) => unchecked(current + state.Sink));
            double cpuPercent = cpuElapsed.TotalSeconds / Math.Max(0.001, elapsed.Elapsed.TotalSeconds) / Environment.ProcessorCount * 100.0;
            double averageExclusiveLatencyNs = exclusiveWorks == 0
                ? 0
                : exclusiveLatencyTicks * 1_000_000_000.0 / Stopwatch.Frequency / exclusiveWorks;

            return new SteadyStateResult(
                GetTargetName(target),
                scenario.Name,
                options.LockInstances,
                threadsPerLock,
                options.OperationsPerThread,
                elapsed.Elapsed,
                cpuPercent,
                concurrentWorks,
                exclusiveWorks,
                stateHash,
                localSink,
                averageExclusiveLatencyNs);
        }
        finally
        {
            for (int i = entities.Length - 1; i >= 0; i--)
            {
                entities[i].Dispose();
            }
        }
    }

    private static void RunWorker(
        int globalWorkerIndex,
        int threadsPerLock,
        Entity[] entities,
        int concurrentPermille,
        SteadyStateTarget target,
        int operationsPerThread,
        CountdownEvent ready,
        ManualResetEventSlim startGate,
        WorkerState state)
    {
        int lockIndex = globalWorkerIndex / threadsPerLock;
        int localWorkerIndex = globalWorkerIndex % threadsPerLock;
        Entity entity = entities[lockIndex];
        uint random = CreateWorkerSeed(lockIndex, localWorkerIndex);
        long concurrentWorks = 0;
        long exclusiveWorks = 0;
        long exclusiveLatencyTicks = 0;
        long localSink = 0;

        ready.Signal();
        startGate.Wait();

        for (int operation = 0; operation < operationsPerThread; operation++)
        {
            random = NextRandom(random + (uint)operation);
            bool isConcurrent = IsConcurrent(random, concurrentPermille);

            if (isConcurrent)
            {
                localSink = unchecked(localSink + ExecuteConcurrent(entity, target));
                concurrentWorks++;
            }
            else
            {
                long startTimestamp = Stopwatch.GetTimestamp();
                localSink = unchecked(localSink + ExecuteExclusive(entity, target));
                exclusiveLatencyTicks += Stopwatch.GetTimestamp() - startTimestamp;
                exclusiveWorks++;
            }
        }

        state.ConcurrentWorks = concurrentWorks;
        state.ExclusiveWorks = exclusiveWorks;
        state.ExclusiveLatencyTicks = exclusiveLatencyTicks;
        state.Sink = localSink;
    }

    private static long ExecuteConcurrent(Entity entity, SteadyStateTarget target)
    {
        switch (target)
        {
            case SteadyStateTarget.Scope:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireConcurrent();
                    return entity.Work.TickConcurrent();
                }

            case SteadyStateTarget.Rwls:
                entity.Rwls.EnterReadLock();
                try
                {
                    return entity.Work.TickConcurrent();
                }
                finally
                {
                    entity.Rwls.ExitReadLock();
                }

            case SteadyStateTarget.Monitor:
                lock (entity.MonitorGate)
                {
                    return entity.Work.TickConcurrent();
                }
        }

        throw new ArgumentOutOfRangeException(nameof(target), target, null);
    }

    private static long ExecuteExclusive(Entity entity, SteadyStateTarget target)
    {
        switch (target)
        {
            case SteadyStateTarget.Scope:
                using (var scope = new ConcurrentExclusiveLockScope(entity.Locker))
                {
                    scope.AcquireExclusive();
                    return entity.Work.TickExclusive();
                }

            case SteadyStateTarget.Rwls:
                entity.Rwls.EnterWriteLock();
                try
                {
                    return entity.Work.TickExclusive();
                }
                finally
                {
                    entity.Rwls.ExitWriteLock();
                }

            case SteadyStateTarget.Monitor:
                lock (entity.MonitorGate)
                {
                    return entity.Work.TickExclusive();
                }
        }

        throw new ArgumentOutOfRangeException(nameof(target), target, null);
    }

    private static bool IsConcurrent(uint random, int concurrentPermille)
    {
        return concurrentPermille == 1000 ||
               (concurrentPermille != 0 && random % 1000 < concurrentPermille);
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

    private static long CombineStateHashes(Entity[] entities)
    {
        if (entities.Length == 1)
        {
            return entities[0].Work.StateHash;
        }

        unchecked
        {
            ulong combined = 0x6A09E667F3BCC909UL;
            for (int i = 0; i < entities.Length; i++)
            {
                ulong state = (ulong)entities[i].Work.StateHash;
                combined ^= state + 0x9E3779B97F4A7C15UL + (combined << 6) + (combined >> 2);
                combined ^= (uint)i;
            }

            return (long)combined;
        }
    }

    private static void PrintHeader()
    {
        Console.WriteLine(
            $"  {"lock type",-26}  {"elapsed",8}  {"cpu%",9}  {"works/s",12}  {"works/s/lock",12}  {"work/cpu%",11}  {"concurrent",12}  {"exclusive",12}  {"avg excl ns",12}  {"state",16}");
    }

    private static void PrintResult(SteadyStateResult result)
    {
        double elapsedSeconds = Math.Max(0.001, result.Elapsed.TotalSeconds);
        double worksPerSecond = result.Works / elapsedSeconds;
        double worksPerSecondPerLock = worksPerSecond / result.LockInstances;
        double workPerCpuPercent = result.CpuPercent > 0.000001 ? worksPerSecond / result.CpuPercent : 0;
        string elapsed = $"{result.Elapsed.TotalSeconds:0.000}s";
        string cpuPercent = $"{result.CpuPercent:0.0}%";
        string state = $"{unchecked((ulong)result.StateHash):X16}";

        Console.WriteLine(
            $"  {result.LockName,-26}  " +
            $"{elapsed,8}  " +
            $"{cpuPercent,9}  " +
            $"{worksPerSecond,12:0}  " +
            $"{worksPerSecondPerLock,12:0}  " +
            $"{workPerCpuPercent,11:0}  " +
            $"{result.ConcurrentWorks,12:N0}  " +
            $"{result.ExclusiveWorks,12:N0}  " +
            $"{result.AverageExclusiveLatencyNs,12:0.0}  " +
            $"{state,16}");
    }

    private static void WriteCsv(IReadOnlyCollection<SteadyStateResult> results, SteadyStateOptions options)
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "BenchmarkDotNet.Artifacts", "SteadyState");
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, $"steady-state-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.csv");
        using var output = new StreamWriter(path);

        output.WriteLine("scenario,lockType,workload,lockInstances,threadsPerLock,totalThreads,operationsPerThread,concurrentSteps,exclusiveSteps,elapsedSeconds,cpuPercent,worksPerSecond,worksPerSecondPerLock,workPerCpuPercent,concurrentWorks,exclusiveWorks,averageExclusiveLatencyNs,state");
        foreach (SteadyStateResult result in results)
        {
            double elapsedSeconds = Math.Max(0.001, result.Elapsed.TotalSeconds);
            double worksPerSecond = result.Works / elapsedSeconds;
            double worksPerSecondPerLock = worksPerSecond / result.LockInstances;
            double workPerCpuPercent = result.CpuPercent > 0.000001 ? worksPerSecond / result.CpuPercent : 0;

            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{result.Scenario},{result.LockName},{options.Workload},{result.LockInstances},{result.ThreadsPerLock},{result.LockInstances * result.ThreadsPerLock},{result.OperationsPerThread},{options.ConcurrentWork},{options.ExclusiveWork},{result.Elapsed.TotalSeconds:R},{result.CpuPercent:R},{worksPerSecond:R},{worksPerSecondPerLock:R},{workPerCpuPercent:R},{result.ConcurrentWorks},{result.ExclusiveWorks},{result.AverageExclusiveLatencyNs:R},{unchecked((ulong)result.StateHash):X16}"));
        }

        Console.WriteLine($"CSV: {path}");
        Console.WriteLine();
    }

    private static string GetTargetName(SteadyStateTarget target)
    {
        return target switch
        {
            SteadyStateTarget.Scope => "CEL",
            SteadyStateTarget.Rwls => "ReaderWriterLockSlim",
            SteadyStateTarget.Monitor => "lock",
            _ => target.ToString(),
        };
    }

    private sealed class Entity : IDisposable
    {
        public Entity(WorkloadMode workload, int concurrentWork, int exclusiveWork, int memoryMb, int dictionarySize)
        {
            Work = BenchmarkWorkFactory.Create(workload, concurrentWork, exclusiveWork, memoryMb, dictionarySize);
        }

        public ConcurrentExclusiveLock Locker { get; } = ConcurrentExclusiveLock.Create();
        public ReaderWriterLockSlim Rwls { get; } = new();
        public object MonitorGate { get; } = new();
        public IBenchmarkWork Work { get; }

        public void Dispose()
        {
            Rwls.Dispose();
            Work.Dispose();
        }
    }

    private sealed class WorkerState
    {
        public long ConcurrentWorks;
        public long ExclusiveWorks;
        public long ExclusiveLatencyTicks;
        public long Sink;
    }
}
