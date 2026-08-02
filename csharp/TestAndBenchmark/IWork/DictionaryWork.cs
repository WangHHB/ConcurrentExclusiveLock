using System.Collections.Generic;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// Business-shaped in-process quote/object-cache workload.
/// Shared data includes string keys, dictionary buckets, and object fields; the hot path performs no intentional allocation.
/// </summary>
internal sealed class DictionaryWork : IWork
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

    private readonly int readSteps;
    private readonly int writeSteps;
    private readonly int entryCount;
    private string[] keys = null!;
    private Dictionary<string, CacheEntry> cache = null!;
    private ThreadLocal<uint> readRandom = null!;
    private int readerSeed;
    private uint writeRandom;
    private long state;

    public DictionaryWork(int readSteps, int writeSteps, int entryCount)
    {
        this.readSteps = readSteps;
        this.writeSteps = writeSteps;
        this.entryCount = entryCount;
    }

    public long StateHash => Volatile.Read(ref state);

    public void Init()
    {
        keys = new string[entryCount];
        cache = new Dictionary<string, CacheEntry>(entryCount, System.StringComparer.Ordinal);

        for (int i = 0; i < entryCount; i++)
        {
            string key = $"tenant-{i % 127:D3}:instrument-{i:D8}";
            keys[i] = key;
            cache.Add(key, new CacheEntry(key, 10_000L + i * 17L, 100L + i % 10_000, i & 3));
        }

        readerSeed = 0;
        readRandom = new ThreadLocal<uint>(() => Seed((uint)Interlocked.Increment(ref readerSeed)));
        writeRandom = 0x94D049BBu;
        state = 0;
    }

    public long TickRead()
    {
        // Cache lookup: string hashing, dictionary probing, object indirection, validation, and notional calculation.
        long result = Volatile.Read(ref state);
        uint random = readRandom.Value;

        for (int i = 0; i < readSteps; i++)
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

        readRandom.Value = random;
        return result;
    }

    public long TickWrite()
    {
        // Cache refresh: locate an entry and update price, quantity, version, and business state.
        long result = state + 1;
        uint random = writeRandom;

        for (int i = 0; i < writeSteps; i++)
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
            result ^= result >> 30;
            result *= 0xBF58476D1CE4E5B9UL;
            result ^= result >> 27;
            result *= 0x94D049BB133111EBUL;
            result ^= result >> 31;
            return (long)result;
        }
    }
}
