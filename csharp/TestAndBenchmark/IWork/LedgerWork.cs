using System.Collections.Generic;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 实际业务模拟：账户账本与资金转移。
/// 读路径查询账户及最近审计记录；写路径校验两个账户、完成转账并追加审计记录。
/// </summary>
internal sealed class LedgerWork : IWork
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

    private readonly int readSteps;
    private readonly int writeSteps;
    private readonly int accountCount;
    private Dictionary<int, Account> accounts;
    private AuditEntry[] audit;
    private ThreadLocal<uint> readRandom;
    private int readerSeed;
    private uint writeRandom;
    private int auditPosition;
    private long state;

    public LedgerWork(int readSteps, int writeSteps, int accountCount)
    {
        this.readSteps = readSteps;
        this.writeSteps = writeSteps;
        this.accountCount = accountCount;
    }

    public long StateHash => Volatile.Read(ref state);

    public void Init()
    {
        accounts = new Dictionary<int, Account>(accountCount);
        audit = new AuditEntry[AuditCapacity];

        for (int i = 0; i < accountCount; i++)
        {
            accounts.Add(i, new Account
            {
                Balance = 1_000_000L + i * 101L,
                Reserved = i % 1_000,
                Status = i % 97 == 0 ? 1 : 0
            });
        }

        readerSeed = 0;
        readRandom = new ThreadLocal<uint>(() => Seed((uint)Interlocked.Increment(ref readerSeed)));
        writeRandom = 0x85157AF5u;
        auditPosition = 0;
        state = 0;
    }

    public long TickRead()
    {
        // 模拟余额/可用额查询，同时读取账户对象和审计环中的历史记录。
        long result = Volatile.Read(ref state);
        uint random = readRandom.Value;

        for (int i = 0; i < readSteps; i++)
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

        readRandom.Value = random;
        return result;
    }

    public long TickWrite()
    {
        // 模拟事务更新：双账户查找、余额与状态校验、双边更新以及审计落表。
        long result = state + 1;
        uint random = writeRandom;

        for (int i = 0; i < writeSteps; i++)
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

                int auditIndex = auditPosition++ & (AuditCapacity - 1);
                audit[auditIndex] = new AuditEntry
                {
                    Amount = amount,
                    Version = source.Version + destination.Version
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
            result ^= result >> 33;
            result *= 0xFF51AFD7ED558CCDUL;
            result ^= result >> 33;
            result *= 0xC4CEB9FE1A85EC53UL;
            result ^= result >> 33;
            return (long)result;
        }
    }
}
