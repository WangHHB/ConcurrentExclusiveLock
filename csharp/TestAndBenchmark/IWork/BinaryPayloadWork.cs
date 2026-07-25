using System;
using System.Buffers.Binary;
using System.Threading;

namespace LockBenchmark;

/// <summary>
/// 实际业务模拟：共享二进制消息/网络帧存储。
/// 读路径解析定长协议头并抽样载荷；写路径更新标志、版本、金额和载荷字段。
/// </summary>
internal sealed class BinaryPayloadWork : IWork
{
    public const int FrameSize = 512;

    private readonly int readSteps;
    private readonly int writeSteps;
    private readonly int frameCount;
    private byte[] frames;
    private ThreadLocal<uint> readRandom;
    private int readerSeed;
    private uint writeRandom;
    private long state;

    public BinaryPayloadWork(int readSteps, int writeSteps, int frameCount)
    {
        this.readSteps = readSteps;
        this.writeSteps = writeSteps;
        this.frameCount = frameCount;
    }

    public long StateHash => Volatile.Read(ref state);

    public static int GetFrameCount(int scale)
    {
        return Math.Clamp(scale / 8, 1_024, 16_384);
    }

    public void Init()
    {
        frames = new byte[checked(frameCount * FrameSize)];
        uint random = 0xA341316Cu;

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            Span<byte> frame = frames.AsSpan(frameIndex * FrameSize, FrameSize);
            random = Next(random);
            BinaryPrimitives.WriteInt32LittleEndian(frame, frameIndex);
            BinaryPrimitives.WriteInt32LittleEndian(frame.Slice(4), (int)(random & 7));
            BinaryPrimitives.WriteInt64LittleEndian(frame.Slice(8), 1);
            BinaryPrimitives.WriteInt64LittleEndian(frame.Slice(16), random * 101L);

            for (int offset = 24; offset < FrameSize; offset++)
            {
                random = Next(random);
                frame[offset] = (byte)random;
            }
        }

        readerSeed = 0;
        readRandom = new ThreadLocal<uint>(() => Seed((uint)Interlocked.Increment(ref readerSeed)));
        writeRandom = 0xAD90777Du;
        state = 0;
    }

    public long TickRead()
    {
        // 模拟协议解析：读取消息头、执行业务条件判断，并访问分散的载荷字段。
        long result = Volatile.Read(ref state);
        uint random = readRandom.Value;

        for (int i = 0; i < readSteps; i++)
        {
            random = Next(random);
            int frameIndex = (int)(random % (uint)frameCount);
            ReadOnlySpan<byte> frame = frames.AsSpan(frameIndex * FrameSize, FrameSize);

            int messageId = BinaryPrimitives.ReadInt32LittleEndian(frame);
            int flags = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(4));
            long version = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(8));
            long amount = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(16));
            int sample = frame[31] | frame[127] << 8 | frame[263] << 16 | frame[479] << 24;

            result = (flags & 1) == 0 && amount >= 0
                ? Mix(result + messageId + version + amount + sample)
                : Mix(result ^ flags ^ sample);
        }

        readRandom.Value = random;
        return result;
    }

    public long TickWrite()
    {
        // 模拟消息状态推进：解析旧值后原位写回多个头字段及载荷字节。
        long result = state + 1;
        uint random = writeRandom;

        for (int i = 0; i < writeSteps; i++)
        {
            random = Next(random);
            int frameIndex = (int)(random % (uint)frameCount);
            Span<byte> frame = frames.AsSpan(frameIndex * FrameSize, FrameSize);

            int flags = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(4));
            long version = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(8)) + 1;
            long amount = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(16));
            amount += (random & 1) == 0 ? 1 : -1;

            BinaryPrimitives.WriteInt32LittleEndian(frame.Slice(4), flags ^ (int)(random & 3));
            BinaryPrimitives.WriteInt64LittleEndian(frame.Slice(8), version);
            BinaryPrimitives.WriteInt64LittleEndian(frame.Slice(16), amount);
            frame[31] ^= (byte)random;
            frame[127] ^= (byte)(random >> 8);
            frame[263] ^= (byte)(random >> 16);
            frame[479] ^= (byte)(random >> 24);
            result = Mix(result + version + amount + frame[31] + frame[263]);
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
