using System.Buffers.Binary;

namespace TestAndBenchmark.Common.Workloads;

internal interface IBenchmarkWork : IDisposable
{
    long StateHash { get; }

    long TickConcurrent();

    long TickExclusive();
}

internal static class BenchmarkWorkFactory
{
    public static IBenchmarkWork Create(WorkloadMode workload, int concurrentSteps, int exclusiveSteps, int memoryMb, int dictionarySize)
    {
        return workload switch
        {
            WorkloadMode.Memory => new MemoryBenchmarkWork(concurrentSteps, exclusiveSteps, memoryMb),
            WorkloadMode.Dictionary => new DictionaryBenchmarkWork(concurrentSteps, exclusiveSteps, dictionarySize),
            WorkloadMode.Ledger => new LedgerBenchmarkWork(concurrentSteps, exclusiveSteps, dictionarySize),
            WorkloadMode.Payload => new PayloadBenchmarkWork(concurrentSteps, exclusiveSteps, dictionarySize),
            _ => new CpuBenchmarkWork(concurrentSteps, exclusiveSteps),
        };
    }
}

internal sealed class CpuBenchmarkWork : IBenchmarkWork
{
    private readonly int concurrentSteps;
    private readonly int exclusiveSteps;
    private long state = 0x243F6A8885A308D3L;

    public CpuBenchmarkWork(int concurrentSteps, int exclusiveSteps)
    {
        this.concurrentSteps = concurrentSteps;
        this.exclusiveSteps = exclusiveSteps;
    }

    public long StateHash => Volatile.Read(ref state);

    public long TickConcurrent()
    {
        return Run(Volatile.Read(ref state), concurrentSteps);
    }

    public long TickExclusive()
    {
        state = Run(state + 1, exclusiveSteps);
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

internal sealed class MemoryBenchmarkWork : IBenchmarkWork
{
    private readonly int concurrentSteps;
    private readonly int exclusiveSteps;
    private readonly int workingSetMb;
    private long[] buffer = [];
    private ThreadLocal<uint>? concurrentRandom;
    private int concurrentSeed;
    private uint exclusiveRandom;
    private long state;

    public MemoryBenchmarkWork(int concurrentSteps, int exclusiveSteps, int workingSetMb)
    {
        this.concurrentSteps = concurrentSteps;
        this.exclusiveSteps = exclusiveSteps;
        this.workingSetMb = workingSetMb;
        Init();
    }

    public long StateHash => Volatile.Read(ref state);

    public long TickConcurrent()
    {
        long result = Volatile.Read(ref state);
        uint random = concurrentRandom!.Value;

        for (int i = 0; i < concurrentSteps; i++)
        {
            random = Next(random);
            int index = (int)(random % (uint)buffer.Length);
            result = Mix(result ^ (Volatile.Read(ref buffer[index]) + i));
        }

        concurrentRandom.Value = random;
        return result;
    }

    public long TickExclusive()
    {
        long result = state + 1;
        uint random = exclusiveRandom;

        for (int i = 0; i < exclusiveSteps; i++)
        {
            random = Next(random);
            int index = (int)(random % (uint)buffer.Length);
            long next = Mix(buffer[index] ^ result ^ i);
            buffer[index] = next;
            result = next;
        }

        exclusiveRandom = random;
        state = result;
        return result;
    }

    public void Dispose()
    {
        concurrentRandom?.Dispose();
    }

    private void Init()
    {
        long bytes = checked((long)Math.Max(1, workingSetMb) * 1024 * 1024);
        int elementCount = checked((int)(bytes / sizeof(long)));
        buffer = new long[Math.Max(1_024, elementCount)];

        long current = (long)0x6A09E667F3BCC909UL;
        for (int i = 0; i < buffer.Length; i++)
        {
            current = Mix(current + i);
            buffer[i] = current;
        }

        concurrentRandom = new ThreadLocal<uint>(() => Seed((uint)Interlocked.Increment(ref concurrentSeed)));
        exclusiveRandom = 0xC8013EA4u;
        state = 0;
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

internal sealed class DictionaryBenchmarkWork : IBenchmarkWork
{
    private sealed class CacheEntry
    {
        public readonly string Symbol;
        public long Price;
        public long Quantity;
        public long Version;
        public int Status;

        public CacheEntry(string symbol, long price, long quantity, int status)
        {
            Symbol = symbol;
            Price = price;
            Quantity = quantity;
            Status = status;
        }
    }

    private readonly int concurrentSteps;
    private readonly int exclusiveSteps;
    private readonly int entryCount;
    private string[] keys = [];
    private Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);
    private ThreadLocal<uint>? concurrentRandom;
    private int concurrentSeed;
    private uint exclusiveRandom;
    private long state;

    public DictionaryBenchmarkWork(int concurrentSteps, int exclusiveSteps, int entryCount)
    {
        this.concurrentSteps = concurrentSteps;
        this.exclusiveSteps = exclusiveSteps;
        this.entryCount = Math.Max(1, entryCount);
        Init();
    }

    public long StateHash => Volatile.Read(ref state);

    public long TickConcurrent()
    {
        long result = Volatile.Read(ref state);
        uint random = concurrentRandom!.Value;

        for (int i = 0; i < concurrentSteps; i++)
        {
            random = Next(random);
            string key = keys[(int)(random % (uint)keys.Length)];

            if (cache.TryGetValue(key, out CacheEntry? entry) && entry.Status != 3)
            {
                result = Mix(result + entry.Price * entry.Quantity + entry.Version + entry.Symbol.Length);
            }
            else
            {
                result = Mix(result ^ key.Length);
            }
        }

        concurrentRandom.Value = random;
        return result;
    }

    public long TickExclusive()
    {
        long result = state + 1;
        uint random = exclusiveRandom;

        for (int i = 0; i < exclusiveSteps; i++)
        {
            random = Next(random);
            string key = keys[(int)(random % (uint)keys.Length)];
            CacheEntry entry = cache[key];
            long delta = (random & 1) == 0 ? 1 : -1;

            entry.Price += delta;
            entry.Quantity = 1 + ((entry.Quantity + (random & 31)) % 100_000);
            entry.Version++;
            entry.Status = (entry.Status + (int)(random >> 30)) & 3;
            result = Mix(result ^ entry.Price ^ entry.Quantity ^ entry.Version);
        }

        exclusiveRandom = random;
        state = result;
        return result;
    }

    public void Dispose()
    {
        concurrentRandom?.Dispose();
    }

    private void Init()
    {
        keys = new string[entryCount];
        cache = new Dictionary<string, CacheEntry>(entryCount, StringComparer.Ordinal);

        for (int i = 0; i < entryCount; i++)
        {
            string key = $"tenant-{i % 127:D3}:instrument-{i:D8}";
            keys[i] = key;
            cache.Add(key, new CacheEntry(key, 10_000L + i * 17L, 100L + i % 10_000, i & 3));
        }

        concurrentRandom = new ThreadLocal<uint>(() => Seed((uint)Interlocked.Increment(ref concurrentSeed)));
        exclusiveRandom = 0x94D049BBu;
        state = 0;
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
            result ^= result >> 30;
            result *= 0xBF58476D1CE4E5B9UL;
            result ^= result >> 27;
            result *= 0x94D049BB133111EBUL;
            result ^= result >> 31;
            return (long)result;
        }
    }
}

internal sealed class LedgerBenchmarkWork : IBenchmarkWork
{
    private sealed class Account
    {
        public long Balance;
        public long Reserved;
        public long Version;
        public int Status;
    }

    private struct AuditEntry
    {
        public long Amount;
        public long Version;
    }

    private const int AuditCapacity = 16_384;

    private readonly int concurrentSteps;
    private readonly int exclusiveSteps;
    private readonly int accountCount;
    private Dictionary<int, Account> accounts = [];
    private AuditEntry[] audit = [];
    private ThreadLocal<uint>? concurrentRandom;
    private int concurrentSeed;
    private uint exclusiveRandom;
    private int auditPosition;
    private long state;

    public LedgerBenchmarkWork(int concurrentSteps, int exclusiveSteps, int accountCount)
    {
        this.concurrentSteps = concurrentSteps;
        this.exclusiveSteps = exclusiveSteps;
        this.accountCount = Math.Max(1, accountCount);
        Init();
    }

    public long StateHash => Volatile.Read(ref state);

    public long TickConcurrent()
    {
        long result = Volatile.Read(ref state);
        uint random = concurrentRandom!.Value;

        for (int i = 0; i < concurrentSteps; i++)
        {
            random = Next(random);
            int accountId = (int)(random % (uint)accounts.Count);
            Account account = accounts[accountId];
            AuditEntry recent = audit[(int)((random >> 8) & (AuditCapacity - 1))];
            long available = account.Balance - account.Reserved;

            result = account.Status == 0 && available >= 0
                ? Mix(result + available + account.Version + recent.Amount)
                : Mix(result ^ account.Status ^ recent.Version);
        }

        concurrentRandom.Value = random;
        return result;
    }

    public long TickExclusive()
    {
        long result = state + 1;
        uint random = exclusiveRandom;

        for (int i = 0; i < exclusiveSteps; i++)
        {
            random = Next(random);
            int sourceId = (int)(random % (uint)accounts.Count);
            random = Next(random);
            int destinationId = (int)(random % (uint)accounts.Count);
            if (destinationId == sourceId && accounts.Count > 1)
            {
                destinationId = (destinationId + 1) % accounts.Count;
            }

            Account source = accounts[sourceId];
            Account destination = accounts[destinationId];
            long amount = 1 + (random & 1_023);

            if (source.Status == 0 && destination.Status == 0 && source.Balance - source.Reserved >= amount)
            {
                source.Balance -= amount;
                destination.Balance += amount;
                source.Version++;
                destination.Version++;

                audit[auditPosition++ & (AuditCapacity - 1)] = new AuditEntry
                {
                    Amount = amount,
                    Version = source.Version + destination.Version,
                };
                result = Mix(result + amount + sourceId - destinationId);
            }
            else
            {
                source.Reserved = (source.Reserved + amount) % 100_000;
                source.Version++;
                result = Mix(result ^ source.Reserved ^ source.Version);
            }
        }

        exclusiveRandom = random;
        state = result;
        return result;
    }

    public void Dispose()
    {
        concurrentRandom?.Dispose();
    }

    private void Init()
    {
        accounts = new Dictionary<int, Account>(accountCount);
        audit = new AuditEntry[AuditCapacity];

        for (int i = 0; i < accountCount; i++)
        {
            accounts.Add(i, new Account
            {
                Balance = 1_000_000L + i * 101L,
                Reserved = i % 1_000,
                Status = i % 97 == 0 ? 1 : 0,
            });
        }

        concurrentRandom = new ThreadLocal<uint>(() => Seed((uint)Interlocked.Increment(ref concurrentSeed)));
        exclusiveRandom = 0x85157AF5u;
        auditPosition = 0;
        state = 0;
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

internal sealed class PayloadBenchmarkWork : IBenchmarkWork
{
    private const int FrameSize = 512;

    private readonly int concurrentSteps;
    private readonly int exclusiveSteps;
    private readonly int frameCount;
    private byte[] frames = [];
    private ThreadLocal<uint>? concurrentRandom;
    private int concurrentSeed;
    private uint exclusiveRandom;
    private long state;

    public PayloadBenchmarkWork(int concurrentSteps, int exclusiveSteps, int dictionarySize)
    {
        this.concurrentSteps = concurrentSteps;
        this.exclusiveSteps = exclusiveSteps;
        frameCount = Math.Clamp(Math.Max(1, dictionarySize) / 8, 1_024, 16_384);
        Init();
    }

    public long StateHash => Volatile.Read(ref state);

    public long TickConcurrent()
    {
        long result = Volatile.Read(ref state);
        uint random = concurrentRandom!.Value;

        for (int i = 0; i < concurrentSteps; i++)
        {
            random = Next(random);
            int frameIndex = (int)(random % (uint)frameCount);
            ReadOnlySpan<byte> frame = frames.AsSpan(frameIndex * FrameSize, FrameSize);

            int messageId = BinaryPrimitives.ReadInt32LittleEndian(frame);
            int flags = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
            long version = BinaryPrimitives.ReadInt64LittleEndian(frame[8..]);
            long amount = BinaryPrimitives.ReadInt64LittleEndian(frame[16..]);
            int sample = frame[31] | frame[127] << 8 | frame[263] << 16 | frame[479] << 24;

            result = (flags & 1) == 0 && amount >= 0
                ? Mix(result + messageId + version + amount + sample)
                : Mix(result ^ flags ^ sample);
        }

        concurrentRandom.Value = random;
        return result;
    }

    public long TickExclusive()
    {
        long result = state + 1;
        uint random = exclusiveRandom;

        for (int i = 0; i < exclusiveSteps; i++)
        {
            random = Next(random);
            int frameIndex = (int)(random % (uint)frameCount);
            Span<byte> frame = frames.AsSpan(frameIndex * FrameSize, FrameSize);

            int flags = BinaryPrimitives.ReadInt32LittleEndian(frame[4..]);
            long version = BinaryPrimitives.ReadInt64LittleEndian(frame[8..]) + 1;
            long amount = BinaryPrimitives.ReadInt64LittleEndian(frame[16..]);
            amount += (random & 1) == 0 ? 1 : -1;

            BinaryPrimitives.WriteInt32LittleEndian(frame[4..], flags ^ (int)(random & 3));
            BinaryPrimitives.WriteInt64LittleEndian(frame[8..], version);
            BinaryPrimitives.WriteInt64LittleEndian(frame[16..], amount);
            frame[31] ^= (byte)random;
            frame[127] ^= (byte)(random >> 8);
            frame[263] ^= (byte)(random >> 16);
            frame[479] ^= (byte)(random >> 24);
            result = Mix(result + version + amount + frame[31] + frame[263]);
        }

        exclusiveRandom = random;
        state = result;
        return result;
    }

    public void Dispose()
    {
        concurrentRandom?.Dispose();
    }

    private void Init()
    {
        frames = new byte[checked(frameCount * FrameSize)];
        uint random = 0xA341316Cu;

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            Span<byte> frame = frames.AsSpan(frameIndex * FrameSize, FrameSize);
            random = Next(random);
            BinaryPrimitives.WriteInt32LittleEndian(frame, frameIndex);
            BinaryPrimitives.WriteInt32LittleEndian(frame[4..], (int)(random & 7));
            BinaryPrimitives.WriteInt64LittleEndian(frame[8..], 1);
            BinaryPrimitives.WriteInt64LittleEndian(frame[16..], random * 101L);

            for (int offset = 24; offset < FrameSize; offset++)
            {
                random = Next(random);
                frame[offset] = (byte)random;
            }
        }

        concurrentRandom = new ThreadLocal<uint>(() => Seed((uint)Interlocked.Increment(ref concurrentSeed)));
        exclusiveRandom = 0xAD90777Du;
        state = 0;
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
            result ^= result >> 30;
            result *= 0xBF58476D1CE4E5B9UL;
            result ^= result >> 27;
            result *= 0x94D049BB133111EBUL;
            result ^= result >> 31;
            return (long)result;
        }
    }
}
