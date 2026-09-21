using ScheduleRisk.Core.Numerics;

namespace ScheduleRisk.Core.Risk;

public enum DistributionKind { Triangle, Pert, Uniform }

/// <summary>Three-point distribution sampled by inverse CDF. Twin of risk.py Dist.</summary>
public sealed class Distribution
{
    public const int TableN = 4096;
    private double[]? _table;
    private readonly object _lock = new();

    public Distribution(DistributionKind kind, double min, double mostLikely, double max)
    {
        if (!(min <= mostLikely && mostLikely <= max))
            throw new ArgumentException($"distribution needs min <= most likely <= max, got {min}, {mostLikely}, {max}");
        Kind = kind; Min = min; MostLikely = mostLikely; Max = max;
    }

    public DistributionKind Kind { get; }
    public double Min { get; }
    public double MostLikely { get; }
    public double Max { get; }

    public static DistributionKind ParseKind(string s)
    {
        switch (s.ToLowerInvariant().Replace(" ", ""))
        {
            case "triangle": case "triangular": case "tri": return DistributionKind.Triangle;
            case "pert": case "betapert": case "beta-pert": case "beta_pert": return DistributionKind.Pert;
            case "uniform": return DistributionKind.Uniform;
            default: throw new ArgumentException($"unknown distribution '{s}'");
        }
    }

    private double[] PertTable()
    {
        lock (_lock)
        {
            if (_table != null) return _table;
            double a = 1.0 + 4.0 * (MostLikely - Min) / (Max - Min);
            double b = 1.0 + 4.0 * (Max - MostLikely) / (Max - Min);
            var t = new double[TableN + 1];
            for (int i = 0; i <= TableN; i++) t[i] = MathX.BetaInv((double)i / TableN, a, b);
            _table = t;
            return t;
        }
    }

    /// <summary>Inverse CDF at u in [0,1).</summary>
    public double Inverse(double u)
    {
        double lo = Min, ml = MostLikely, hi = Max;
        if (hi <= lo) return lo;
        switch (Kind)
        {
            case DistributionKind.Uniform:
                return lo + u * (hi - lo);
            case DistributionKind.Triangle:
            {
                double fc = (ml - lo) / (hi - lo);
                if (u < fc) return lo + Math.Sqrt(u * (hi - lo) * (ml - lo));
                return hi - Math.Sqrt((1.0 - u) * (hi - lo) * (hi - ml));
            }
            default:
            {
                var t = _table ?? PertTable();
                double x = u * TableN;
                int i = (int)x;
                if (i >= TableN) return hi;
                double f = x - i;
                return lo + (hi - lo) * (t[i] + (t[i + 1] - t[i]) * f);
            }
        }
    }

    public double Mean => Kind switch
    {
        DistributionKind.Uniform => (Min + Max) / 2,
        DistributionKind.Triangle => (Min + MostLikely + Max) / 3,
        _ => (Min + 4 * MostLikely + Max) / 6,
    };

    public override string ToString() => $"{Kind}({Min:g}, {MostLikely:g}, {Max:g})";
}
