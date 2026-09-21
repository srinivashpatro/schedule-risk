namespace ScheduleRisk.Core.Numerics;

/// <summary>
/// Deterministic random numbers shared bit-for-bit with the Python reference
/// (reference/python/sra/numerics.py). Never replace with System.Random: results
/// must be reproducible from a seed across versions, machines and thread counts.
/// </summary>
public static class RngStreams
{
    public static (ulong State, ulong Output) SplitMix64(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (x, z);
        }
    }

    /// <summary>Mix (seed, variable, batch, salt) into a 64-bit stream seed.</summary>
    public static ulong StreamSeed(long seed, long varIndex, long batchIndex, long salt = 0)
    {
        unchecked
        {
            ulong s = (ulong)seed;
            foreach (long v in new[] { varIndex, batchIndex, salt })
            {
                var (_, output) = SplitMix64(s ^ ((ulong)v * 0xD1B54A32D192ED03UL));
                s = output;
            }
            return s;
        }
    }

    public static int[] Permutation(Xoshiro256 rng, int n)
    {
        var p = new int[n];
        for (int i = 0; i < n; i++) p[i] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = rng.NextInt(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }
        return p;
    }

    /// <summary>Latin Hypercube sample of n uniforms for one variable in one batch.</summary>
    public static double[] LhsUniforms(long seed, int varIndex, int batchIndex, int n)
    {
        var rng = new Xoshiro256(StreamSeed(seed, varIndex, batchIndex, 0));
        int[] perm = Permutation(rng, n);
        var u = new double[n];
        for (int i = 0; i < n; i++) u[i] = (perm[i] + rng.NextDouble()) / n;
        return u;
    }

    /// <summary>Plain Monte Carlo uniforms.</summary>
    public static double[] McUniforms(long seed, int varIndex, int batchIndex, int n)
    {
        var rng = new Xoshiro256(StreamSeed(seed, varIndex, batchIndex, 0));
        var u = new double[n];
        for (int i = 0; i < n; i++) u[i] = rng.NextDouble();
        return u;
    }
}

/// <summary>xoshiro256** seeded through SplitMix64.</summary>
public sealed class Xoshiro256
{
    private ulong _s0, _s1, _s2, _s3;

    public Xoshiro256(ulong seed)
    {
        ulong x = seed;
        (x, _s0) = RngStreams.SplitMix64(x);
        (x, _s1) = RngStreams.SplitMix64(x);
        (x, _s2) = RngStreams.SplitMix64(x);
        (_, _s3) = RngStreams.SplitMix64(x);
    }

    public ulong NextU64()
    {
        unchecked
        {
            ulong result = RotL(_s1 * 5UL, 7) * 9UL;
            ulong t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = RotL(_s3, 45);
            return result;
        }
    }

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));

    /// <summary>Uniform in [0, 1) with 53 bits.</summary>
    public double NextDouble() => (NextU64() >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>Uniform integer in [0, n).</summary>
    public int NextInt(int n) => (int)((NextU64() >> 11) % (ulong)n);
}
