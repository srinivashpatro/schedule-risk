namespace ScheduleRisk.Core.Numerics;

/// <summary>Special functions, linear algebra and rank statistics (twin of numerics.py).</summary>
public static class MathX
{
    private static readonly double[] Lanczos =
    {
        0.99999999999980993, 676.5203681218851, -1259.1392167224028,
        771.32342877765313, -176.61502916214059, 12.507343278686905,
        -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    };

    public static long RoundHalfUp(double x) => (long)Math.Floor(x + 0.5);

    /// <summary>Lanczos approximation (g=7, n=9).</summary>
    public static double LogGamma(double x)
    {
        if (x < 0.5)
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1.0 - x);
        x -= 1.0;
        double a = Lanczos[0];
        double t = x + 7.5;
        for (int i = 1; i < 9; i++) a += Lanczos[i] / (x + i);
        return 0.5 * Math.Log(2.0 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(a);
    }

    private static double BetaCf(double a, double b, double x)
    {
        const int maxit = 300;
        const double eps = 3.0e-15, fpmin = 1.0e-300;
        double qab = a + b, qap = a + 1.0, qam = a - 1.0;
        double c = 1.0;
        double d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < fpmin) d = fpmin;
        d = 1.0 / d;
        double h = d;
        for (int m = 1; m <= maxit; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1.0 / d;
            h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d;
            if (Math.Abs(d) < fpmin) d = fpmin;
            c = 1.0 + aa / c;
            if (Math.Abs(c) < fpmin) c = fpmin;
            d = 1.0 / d;
            double de = d * c;
            h *= de;
            if (Math.Abs(de - 1.0) < eps) break;
        }
        return h;
    }

    /// <summary>Regularized incomplete beta I_x(a, b).</summary>
    public static double BetaInc(double a, double b, double x)
    {
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;
        double lbt = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1.0 - x);
        double bt = Math.Exp(lbt);
        if (x < (a + 1.0) / (a + b + 2.0)) return bt * BetaCf(a, b, x) / a;
        return 1.0 - bt * BetaCf(b, a, 1.0 - x) / b;
    }

    /// <summary>Inverse regularized incomplete beta by bisection (64 steps).</summary>
    public static double BetaInv(double p, double a, double b)
    {
        if (p <= 0.0) return 0.0;
        if (p >= 1.0) return 1.0;
        double lo = 0.0, hi = 1.0;
        for (int i = 0; i < 64; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (BetaInc(a, b, mid) < p) lo = mid; else hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    /// <summary>Acklam's inverse normal CDF, no refinement step.</summary>
    public static double NormInv(double p)
    {
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double plow = 0.02425;
        if (p <= 0.0) return -1e300;
        if (p >= 1.0) return 1e300;
        double q, r;
        if (p < plow)
        {
            q = Math.Sqrt(-2.0 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }
        if (p > 1.0 - plow)
        {
            q = Math.Sqrt(-2.0 * Math.Log(1.0 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                    ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0);
        }
        q = p - 0.5;
        r = q * q;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
               (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1.0);
    }

    // ------------------------------------------------------------------ linear algebra

    /// <summary>Lower-triangular L with L L^T = m, or null if m is not positive definite.</summary>
    public static double[][]? Cholesky(double[][] m)
    {
        int n = m.Length;
        var L = NewMatrix(n);
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j <= i; j++)
            {
                double s = m[i][j];
                for (int k = 0; k < j; k++) s -= L[i][k] * L[j][k];
                if (i == j)
                {
                    if (s <= 1e-12) return null;
                    L[i][i] = Math.Sqrt(s);
                }
                else
                {
                    L[i][j] = s / L[j][j];
                }
            }
        }
        return L;
    }

    /// <summary>Shrink off-diagonals in 5% steps until Cholesky succeeds.</summary>
    public static (double[][] Matrix, double[][] L, double Shrink) RepairCorrelation(double[][] m)
    {
        int n = m.Length;
        double factor = 1.0;
        for (int iter = 0; iter < 200; iter++)
        {
            var cur = NewMatrix(n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    cur[i][j] = i != j ? m[i][j] * factor : 1.0;
            var L = Cholesky(cur);
            if (L != null) return (cur, L, factor);
            factor *= 0.95;
        }
        var ident = NewMatrix(n);
        for (int i = 0; i < n; i++) ident[i][i] = 1.0;
        return (ident, Cholesky(ident)!, 0.0);
    }

    public static double[][] InvertLower(double[][] L)
    {
        int n = L.Length;
        var inv = NewMatrix(n);
        for (int i = 0; i < n; i++)
        {
            inv[i][i] = 1.0 / L[i][i];
            for (int j = 0; j < i; j++)
            {
                double s = 0.0;
                for (int k = j; k < i; k++) s -= L[i][k] * inv[k][j];
                inv[i][j] = s / L[i][i];
            }
        }
        return inv;
    }

    /// <summary>Correlation matrix of column vectors.</summary>
    public static double[][] PearsonMatrix(double[][] cols)
    {
        int k = cols.Length;
        int n = cols[0].Length;
        var cen = new double[k][];
        var norms = new double[k];
        for (int j = 0; j < k; j++)
        {
            double sum = 0.0;
            for (int t = 0; t < n; t++) sum += cols[j][t];
            double mean = sum / n;
            cen[j] = new double[n];
            double ss = 0.0;
            for (int t = 0; t < n; t++)
            {
                double x = cols[j][t] - mean;
                cen[j][t] = x;
                ss += x * x;
            }
            norms[j] = Math.Sqrt(ss);
        }
        var outM = NewMatrix(k);
        for (int i = 0; i < k; i++)
        {
            outM[i][i] = 1.0;
            for (int j = i + 1; j < k; j++)
            {
                double s = 0.0;
                for (int t = 0; t < n; t++) s += cen[i][t] * cen[j][t];
                double r = norms[i] > 0 && norms[j] > 0 ? s / (norms[i] * norms[j]) : 0.0;
                outM[i][j] = r;
                outM[j][i] = r;
            }
        }
        return outM;
    }

    public static double[][] NewMatrix(int n)
    {
        var m = new double[n][];
        for (int i = 0; i < n; i++) m[i] = new double[n];
        return m;
    }

    // ------------------------------------------------------------------ ranks and correlation

    /// <summary>0-based ranks, ties broken by index.</summary>
    public static int[] Ranks(double[] values)
    {
        int n = values.Length;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            int c = values[x].CompareTo(values[y]);
            return c != 0 ? c : x.CompareTo(y);
        });
        var r = new int[n];
        for (int pos = 0; pos < n; pos++) r[order[pos]] = pos;
        return r;
    }

    /// <summary>1-based average ranks (ties share the mean rank).</summary>
    public static double[] AverageRanks(IReadOnlyList<double> values)
    {
        int n = values.Count;
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (x, y) =>
        {
            int c = values[x].CompareTo(values[y]);
            return c != 0 ? c : x.CompareTo(y);
        });
        var r = new double[n];
        int a = 0;
        while (a < n)
        {
            int b = a;
            while (b + 1 < n && values[order[b + 1]] == values[order[a]]) b++;
            double avg = (a + b) / 2.0 + 1.0;
            for (int k = a; k <= b; k++) r[order[k]] = avg;
            a = b + 1;
        }
        return r;
    }

    public static double Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = x.Count;
        if (n < 2) return 0.0;
        double sx = 0.0, sy = 0.0;
        for (int i = 0; i < n; i++) { sx += x[i]; sy += y[i]; }
        double mx = sx / n, my = sy / n;
        double sxy = 0.0, sxx = 0.0, syy = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = x[i] - mx, dy = y[i] - my;
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        if (sxx <= 0.0 || syy <= 0.0) return 0.0;
        return sxy / Math.Sqrt(sxx * syy);
    }

    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y) =>
        Pearson(AverageRanks(x), AverageRanks(y));
}
