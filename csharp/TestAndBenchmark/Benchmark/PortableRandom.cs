using System.Security.Cryptography;

namespace LockBenchmark;

/// <summary>
/// Deterministic SplitMix64 generator used only by semantic/correctness tests.
/// </summary>
/// <remarks>
/// Porting contract:
/// - Arithmetic is unsigned 64-bit modulo 2^64; overflow must wrap.
/// - Each call first adds 0x9E3779B97F4A7C15 to the state, then applies the two
///   published SplitMix64 mixing multipliers and right shifts exactly as written.
/// - Bounded values use simple modulo reduction. The tiny bias is intentional and
///   acceptable for path selection; replacing it changes the replay sequence.
/// - Do not substitute a language/runtime default Random implementation.
/// - Seed 0 must begin with these 64-bit outputs:
///   E220A8397B1DCDAF, 6E789E6AA1B965F4, 06C45D188009454F,
///   F88BB8A8724C81EC, 1B39896A51A8749B.
/// - A printed base seed, the documented stream derivation, and the same call order
///   must generate the same semantic path sequence in every language port.
/// </remarks>
internal sealed class PortableRandom
{
    private ulong state;

    public PortableRandom(int seed)
    {
        // Preserve exactly the low 32 bits of the signed command-line seed.
        state = unchecked((uint)seed);
    }

    public int Next(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be greater than zero.");
        }

        return (int)(NextUInt64() % (uint)maxExclusive);
    }

    public int Next(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be greater than minInclusive.");
        }

        return checked(minInclusive + Next(maxExclusive - minInclusive));
    }

    /// <summary>Returns a non-negative 31-bit seed for the next independently replayable batch.</summary>
    public int NextSeed() => (int)(NextUInt64() & 0x7FFF_FFFFUL);

    internal ulong NextUInt64()
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong value = state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}

/// <summary>
/// Defines deterministic stream derivation for lock/worker-local generators.
/// </summary>
internal static class PortableSeed
{
    /// <remarks>
    /// All terms are reduced modulo 2^32 before being reinterpreted as a signed seed.
    /// Ports must not use saturating arithmetic or wider arithmetic without truncation.
    /// </remarks>
    public static int Derive(
        int baseSeed,
        int lockIndex,
        uint lockStride,
        int workerIndex,
        uint workerStride)
    {
        unchecked
        {
            uint value = (uint)baseSeed;
            value += (uint)lockIndex * lockStride;
            value += (uint)workerIndex * workerStride;
            return (int)value;
        }
    }
}

/// <summary>Self-check for the portable random contract; silent on success.</summary>
internal static class PortableRandomContract
{
    private static readonly ulong[] SeedZeroExpected =
    {
        0xE220A8397B1DCDAFUL,
        0x6E789E6AA1B965F4UL,
        0x06C45D188009454FUL,
        0xF88BB8A8724C81ECUL,
        0x1B39896A51A8749BUL
    };

    public static void Validate()
    {
        PortableRandom random = new(0);
        for (int index = 0; index < SeedZeroExpected.Length; index++)
        {
            ulong actual = random.NextUInt64();
            if (actual != SeedZeroExpected[index])
            {
                throw new InvalidOperationException(
                    $"PortableRandom contract mismatch at index {index}: expected={SeedZeroExpected[index]:X16}, actual={actual:X16}.");
            }
        }
    }
}

/// <summary>
/// Creates a base seed only when the caller omitted one. The caller must print that
/// seed; deterministic replay starts from the printed value, not from this entropy source.
/// </summary>
internal static class SeedSource
{
    public static int Create() => RandomNumberGenerator.GetInt32(int.MaxValue);
}
