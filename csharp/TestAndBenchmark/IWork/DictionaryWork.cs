using System.Collections.Generic;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 实际业务模拟：进程内行情/对象缓存。
/// 共享数据包含字符串业务键、Dictionary 哈希桶和对象字段，热路径不额外分配对象。
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
    private string[] keys;
    private Dictionary<string, CacheEntry> cache;
    private ThreadLocal<uint> readRandom;
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
        // 模拟缓存查询：字符串哈希、字典探测、对象跳转、状态校验和名义金额计算。
        long result = Volatile.Read(ref state);
        uint random = readRandom.Value;

        for (int i = 0; i < readSteps; i++)
        {
            random = Next(random);
            string key = keys[(int)(random % (uint)keys.Length)];

            if (cache.TryGetValue(key, out CacheEntry entry) && entry.Status != 3)
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
        // 模拟缓存刷新：定位对象后更新价格、数量、版本及业务状态。
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
