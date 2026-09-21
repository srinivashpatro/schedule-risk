using System.Globalization;
using ScheduleRisk.Core.Numerics;
using ScheduleRisk.Core.Risk;

namespace ScheduleRisk.Tests;

/// <summary>Bit-level parity with the Python reference (testdata/golden/numerics.json).</summary>
public class NumericsParityTests
{
    private static readonly System.Text.Json.JsonElement G = TestData.Golden("numerics.json");

    [Fact]
    public void Xoshiro_matches_reference()
    {
        var rng = new Xoshiro256(12345UL);
        foreach (var e in G.GetProperty("rng_u64").EnumerateArray())
            Assert.Equal(ulong.Parse(e.GetString()!, CultureInfo.InvariantCulture), rng.NextU64());
    }

    [Fact]
    public void Stream_seed_matches_reference()
    {
        Assert.Equal(ulong.Parse(G.GetProperty("stream_seed").GetString()!, CultureInfo.InvariantCulture),
                     RngStreams.StreamSeed(20260921, 7, 3, 1));
    }

    [Fact]
    public void Lhs_matches_reference_and_is_stratified()
    {
        var u = RngStreams.LhsUniforms(99, 4, 2, 10);
        var expected = G.GetProperty("lhs").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        Assert.Equal(expected.Length, u.Length);
        for (int i = 0; i < u.Length; i++) Assert.Equal(expected[i], u[i], 15);
        var big = RngStreams.LhsUniforms(42, 3, 0, 1000);
        Assert.Equal(Enumerable.Range(0, 1000), big.Select(x => (int)(x * 1000)).OrderBy(x => x));
    }

    [Fact]
    public void Special_functions_match_reference()
    {
        foreach (var p in G.GetProperty("norm_inv").EnumerateObject())
            Assert.Equal(p.Value.GetDouble(), MathX.NormInv(double.Parse(p.Name, CultureInfo.InvariantCulture)), 12);
        foreach (var row in G.GetProperty("beta_inv").EnumerateArray())
        {
            var v = row.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            Assert.Equal(v[3], MathX.BetaInv(v[0], v[1], v[2]), 12);
        }
    }

    [Fact]
    public void Distributions_match_reference()
    {
        var pert = new Distribution(DistributionKind.Pert, 90, 100, 125);
        foreach (var row in G.GetProperty("pert_inv").EnumerateArray())
        {
            var v = row.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            Assert.Equal(v[1], pert.Inverse(v[0]), 9);
        }
        var tri = new Distribution(DistributionKind.Triangle, 8, 10, 14);
        foreach (var row in G.GetProperty("tri_inv").EnumerateArray())
        {
            var v = row.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            Assert.Equal(v[1], tri.Inverse(v[0]), 12);
        }
        Assert.Equal(10.0, tri.Inverse(1.0 / 3.0), 9);
    }

    [Fact]
    public void Spearman_handles_ties_like_reference()
    {
        double r = MathX.Spearman(new double[] { 1, 5, 2, 8, 8, 3 }, new double[] { 2, 6, 1, 9, 7, 3 });
        Assert.Equal(G.GetProperty("spearman").GetDouble(), r, 12);
    }

    [Fact]
    public void Correlation_repair_shrinks_inconsistent_matrix()
    {
        var m = new[] { new[] { 1.0, 0.9, -0.9 }, new[] { 0.9, 1.0, 0.9 }, new[] { -0.9, 0.9, 1.0 } };
        Assert.Null(MathX.Cholesky(m));
        var (_, L, shrink) = MathX.RepairCorrelation(m);
        Assert.NotNull(L);
        Assert.True(shrink < 1.0);
    }
}
