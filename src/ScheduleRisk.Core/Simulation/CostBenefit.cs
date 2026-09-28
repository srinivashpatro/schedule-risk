using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Risk;

namespace ScheduleRisk.Core.Simulation;

/// <summary>One risk's response weighed against what it saves: working days off the finish at P80, at the chosen level
/// and on average, their value at the cost of delay, and the response cost. Money is null when a figure is missing.</summary>
public sealed record CostBenefitRow(string Id, string Title, double? ResponseCost, double SavedP80, double SavedAtLevel, double SavedMean,
                                    double? Value, double? Net, double? Ratio);

public sealed class CostBenefitResult
{
    public int Level { get; init; }
    public int Iterations { get; init; }
    public long Seed { get; init; }
    public double CostOfDelayPerDay { get; init; }
    public string Currency { get; init; } = "";
    /// <summary>Largest P80 saving first.</summary>
    public List<CostBenefitRow> Rows { get; } = new();
}

/// <summary>
/// Cost-benefit of each risk response (06 Results): a run with only that risk mitigated, on the same seed and
/// iterations as the pre-mitigation run, so the two share every random number and the difference is the response's
/// effect alone, not sampling noise. The saving at P80 is valued at the cost of delay per day and set against the
/// response cost from the register. Reproducible for a seed.
/// </summary>
public static class CostBenefit
{
    /// <summary>The risks with a response, i.e. whose mitigated probability or impact differ from the unmitigated ones.</summary>
    public static IEnumerable<int> Candidates(RiskModel m) =>
        Enumerable.Range(0, m.Risks.Count).Where(i =>
            m.Risks[i].MitigatedProbability != m.Risks[i].Probability || !ReferenceEquals(m.Risks[i].MitigatedImpact, m.Risks[i].Impact));

    /// <summary>The model with risk <paramref name="index"/> at its mitigated values and everything else as it is. Risks keep
    /// their order, so the run draws the same random numbers as the pre-mitigation run.</summary>
    public static RiskModel WithMitigated(RiskModel m, int index)
    {
        var copy = new RiskModel { Name = m.Name, Uncertainty = m.Uncertainty };
        for (int i = 0; i < m.Risks.Count; i++)
        {
            var r = m.Risks[i];
            copy.Risks.Add(i != index ? r : new RiskEvent
            {
                Id = r.Id, Title = r.Title, Probability = r.MitigatedProbability, Impact = r.MitigatedImpact, Units = r.Units,
                Activities = r.Activities, MitigatedProbability = r.MitigatedProbability, MitigatedImpact = r.MitigatedImpact,
            });
        }
        copy.Drivers.AddRange(m.Drivers);
        copy.Correlations.AddRange(m.Correlations);
        copy.Settings.Iterations = m.Settings.Iterations;
        copy.Settings.Seed = m.Settings.Seed;
        copy.Settings.LatinHypercube = m.Settings.LatinHypercube;
        copy.Settings.Convergence = m.Settings.Convergence;
        return copy;
    }

    public static CostBenefitRow Row(RiskEvent r, SimulationSummary pre, SimulationSummary mitigated, int level, double? responseCost, double costOfDelayPerDay)
    {
        static double R(double v) => Math.Round(v, 6, MidpointRounding.ToEven);
        double p80 = R(pre.DurationAt(80) - mitigated.DurationAt(80));
        double atLevel = R(pre.DurationAt(level) - mitigated.DurationAt(level));
        double mean = R(pre.Duration.Mean - mitigated.Duration.Mean);
        double? value = costOfDelayPerDay > 0 ? p80 * costOfDelayPerDay : null;
        double? net = value is double v && responseCost is double c ? v - c : null;
        double? ratio = value is double v2 && responseCost is double c2 && c2 > 0 ? v2 / c2 : null;
        return new CostBenefitRow(r.Id, r.Title, responseCost, p80, atLevel, mean, value, net, ratio);
    }

    /// <summary>How the paired run is made: as the pre-mitigation run was (the same iterations, or the same convergence rule).</summary>
    private static (int? Iterations, ConvergenceSettings? Convergence) Like(RiskModel m, SimulationSummary pre) =>
        m.Settings.Convergence.Enabled ? (null, null) : (pre.Iterations, new ConvergenceSettings { Enabled = false });

    public static CostBenefitResult Run(Schedule s, RiskModel m, CpmEngine engine, SimulationSummary pre, int level,
                                        Func<string, double?> responseCost, double costOfDelayPerDay, string currency) =>
        RunAsync(s, m, engine, pre, level, responseCost, costOfDelayPerDay, currency).GetAwaiter().GetResult();

    public static async Task<CostBenefitResult> RunAsync(Schedule s, RiskModel m, CpmEngine engine, SimulationSummary pre, int level,
        Func<string, double?> responseCost, double costOfDelayPerDay, string currency,
        IProgress<(int Risk, int Risks, int Done, int Total)>? progress = null, Func<Task>? yieldBetweenChunks = null,
        CancellationToken cancel = default, int chunkSize = 0)
    {
        var result = new CostBenefitResult
        {
            Level = level, Iterations = pre.Iterations, Seed = pre.Seed, CostOfDelayPerDay = costOfDelayPerDay, Currency = currency,
        };
        var (iterations, convergence) = Like(m, pre);
        var candidates = Candidates(m).ToList();
        var rows = new List<CostBenefitRow>();
        for (int k = 0; k < candidates.Count; k++)
        {
            int i = candidates[k];
            var mc = new MonteCarloEngine(s, WithMitigated(m, i), Scenario.PreMitigation, engine);
            if (chunkSize > 0) mc.ChunkSize = chunkSize;
            int kk = k;
            var inner = progress == null ? null : new InlineProgress(p => progress.Report((kk + 1, candidates.Count, p.Done, p.Total)));
            var res = await mc.RunAsync(iterations, pre.Seed, convergence, inner, yieldBetweenChunks, cancel);
            rows.Add(Row(m.Risks[i], pre, SimulationSummary.Build(mc, res), level, responseCost(m.Risks[i].Id), costOfDelayPerDay));
        }
        result.Rows.AddRange(rows.OrderByDescending(r => r.SavedP80).ThenByDescending(r => r.SavedMean).ThenBy(r => r.Id, StringComparer.Ordinal));
        return result;
    }

    private sealed class InlineProgress : IProgress<(int Done, int Total)>
    {
        private readonly Action<(int Done, int Total)> _report;
        public InlineProgress(Action<(int Done, int Total)> report) => _report = report;
        public void Report((int Done, int Total) value) => _report(value);
    }
}
