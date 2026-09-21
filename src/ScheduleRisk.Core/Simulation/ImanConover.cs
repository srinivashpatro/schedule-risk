using ScheduleRisk.Core.Numerics;

namespace ScheduleRisk.Core.Simulation;

/// <summary>
/// Iman-Conover rank-correlation induction: reorders each column of samples so the
/// rank correlation approximates a target matrix while keeping every marginal intact.
/// Twin of sim.py iman_conover.
/// </summary>
public static class ImanConover
{
    public static (double[][] Columns, double Shrink) Apply(double[][] cols, double[][] target, long seed, int[] varIds, int batch)
    {
        int k = cols.Length;
        int n = cols[0].Length;
        if (k < 2 || n < 3) return (cols, 1.0);
        var scores = new double[n];
        for (int i = 0; i < n; i++) scores[i] = MathX.NormInv((i + 1) / (double)(n + 1));
        var S = new double[k][];
        for (int c = 0; c < k; c++)
        {
            var rng = new Xoshiro256(RngStreams.StreamSeed(seed, varIds[c], batch, 1));
            var p = new int[n];
            for (int i = 0; i < n; i++) p[i] = i;
            for (int i = n - 1; i > 0; i--)
            {
                int j = rng.NextInt(i + 1);
                (p[i], p[j]) = (p[j], p[i]);
            }
            S[c] = new double[n];
            for (int i = 0; i < n; i++) S[c][i] = scores[p[i]];
        }
        var E = MathX.PearsonMatrix(S);
        var F = MathX.Cholesky(E);
        if (F == null) return (cols, 1.0);
        var (_, P, shrink) = MathX.RepairCorrelation(target);
        var Finv = MathX.InvertLower(F);
        var M = MathX.NewMatrix(k);
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
            {
                double acc = 0.0;
                for (int m = 0; m < k; m++) acc += P[a][m] * Finv[m][b];
                M[a][b] = acc;
            }
        var outCols = new double[k][];
        var t = new double[n];
        for (int c = 0; c < k; c++)
        {
            var Mc = M[c];
            for (int i = 0; i < n; i++)
            {
                double acc = 0.0;
                for (int m = 0; m < k; m++) acc += Mc[m] * S[m][i];
                t[i] = acc;
            }
            int[] r = MathX.Ranks(t);
            var su = (double[])cols[c].Clone();
            Array.Sort(su);
            var o = new double[n];
            for (int i = 0; i < n; i++) o[i] = su[r[i]];
            outCols[c] = o;
        }
        return (outCols, shrink);
    }
}
