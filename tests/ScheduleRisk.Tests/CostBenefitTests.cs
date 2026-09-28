using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>
/// 06 Results: the tornado of the risk ranking (pre and post mitigation) and the cost-benefit of each risk's response,
/// measured by a paired run with only that risk mitigated (same seed and iterations as the pre-mitigation run).
/// </summary>
public class CostBenefitTests
{
    private static (ScheduleRisk.Core.Model.Schedule S, RiskModel M) Example()
    {
        var s = TestData.Load("synth_500.xer");
        var crit = new CpmEngine(s).CriticalToProjectFinish();
        return (s, RiskModelLoader.LoadJson(s, File.ReadAllText(TestData.PathOf("synth_500.risk.json")), crit));
    }

    private static SimulationSummary Run(ScheduleRisk.Core.Model.Schedule s, RiskModel m, Scenario sc, int n = 400, long seed = 5)
    {
        var mc = new MonteCarloEngine(s, m, sc);
        return SimulationSummary.Build(mc, mc.Run(n, seed));
    }

    [Fact]
    public void Only_risks_with_a_response_are_candidates()
    {
        var (_, m) = Example();
        Assert.Equal(new[] { "R01", "R04" }, CostBenefit.Candidates(m).Select(i => m.Risks[i].Id));   // R02, R03 have no mitigation
    }

    [Fact]
    public void Mitigating_one_risk_changes_that_risk_only()
    {
        var (_, m) = Example();
        var one = CostBenefit.WithMitigated(m, 0);
        Assert.Equal(m.Risks[0].MitigatedProbability, one.Risks[0].Probability);
        Assert.Same(m.Risks[0].MitigatedImpact, one.Risks[0].Impact);
        for (int i = 1; i < m.Risks.Count; i++) Assert.Same(m.Risks[i], one.Risks[i]);
        Assert.Same(m.Uncertainty, one.Uncertainty);
        Assert.Equal(m.Drivers, one.Drivers);
        Assert.Equal(m.Settings.Seed, one.Settings.Seed);
    }

    [Fact]
    public void With_every_response_mitigated_one_at_a_time_the_run_is_paired_with_the_post_mitigation_run()
    {
        // A model with one mitigated risk: mitigating it alone must give exactly the post-mitigation run.
        var s = TestData.Load("synth_500.xer");
        var crit = new CpmEngine(s).CriticalToProjectFinish();
        string json = """
        { "uncertainty": [ { "filter": { "all": true }, "distribution": "triangle", "min": 95, "mostLikely": 100, "max": 115 } ],
          "risks": [ { "id": "R01", "title": "x", "probability": 0.5, "impact": { "distribution": "triangle", "min": 20, "mostLikely": 40, "max": 80, "units": "days" },
                       "filter": { "critical": true }, "mitigated": { "probability": 0.1 } } ] }
        """;
        var m = RiskModelLoader.LoadJson(s, json, crit);
        var post = Run(s, m, Scenario.PostMitigation);
        var only = Run(s, CostBenefit.WithMitigated(m, 0), Scenario.PreMitigation);
        Assert.Equal(post.FinishPercentiles[80], only.FinishPercentiles[80]);
        Assert.Equal(post.Duration.Mean, only.Duration.Mean);
    }

    [Fact]
    public void Days_saved_and_their_value_come_from_the_paired_runs()
    {
        var (s, m) = Example();
        var pre = Run(s, m, Scenario.PreMitigation);
        var r01 = Run(s, CostBenefit.WithMitigated(m, 0), Scenario.PreMitigation);
        var row = CostBenefit.Row(m.Risks[0], pre, r01, level: 90, responseCost: 1_000_000, costOfDelayPerDay: 150_000);
        Assert.Equal("R01", row.Id);
        Assert.Equal(Math.Round(pre.DurationAt(80) - r01.DurationAt(80), 6), row.SavedP80, 6);
        Assert.Equal(Math.Round(pre.DurationAt(90) - r01.DurationAt(90), 6), row.SavedAtLevel, 6);
        Assert.Equal(Math.Round(pre.Duration.Mean - r01.Duration.Mean, 6), row.SavedMean, 6);
        Assert.True(row.SavedP80 >= 0 && row.SavedMean > 0);                // a paired mitigation cannot make the finish later
        Assert.Equal(row.SavedP80 * 150_000, row.Value!.Value, 3);
        Assert.Equal(row.Value!.Value - 1_000_000, row.Net!.Value, 3);
        Assert.Equal(row.Value!.Value / 1_000_000, row.Ratio!.Value, 9);
        var noCost = CostBenefit.Row(m.Risks[0], pre, r01, 90, null, 0);
        Assert.Null(noCost.Value);
        Assert.Null(noCost.Net);
        Assert.Null(noCost.Ratio);
    }

    [Fact]
    public void The_whole_analysis_is_reproducible_for_a_seed()
    {
        var (s, m) = Example();
        var engine = new CpmEngine(s);
        var pre = Run(s, m, Scenario.PreMitigation);
        CostBenefitResult Go() => CostBenefit.Run(s, m, engine, pre, 80, id => id == "R01" ? 500_000 : null, 100_000, "INR");
        var a = Go();
        var b = Go();
        Assert.Equal(a.Rows.Select(r => (r.Id, r.SavedP80, r.SavedMean)), b.Rows.Select(r => (r.Id, r.SavedP80, r.SavedMean)));
        Assert.Equal(new[] { "R01", "R04" }, a.Rows.Select(r => r.Id).OrderBy(x => x));
        Assert.Equal((pre.Iterations, pre.Seed), (a.Iterations, a.Seed));
        Assert.Equal(a.Rows.OrderByDescending(r => r.SavedP80).Select(r => r.Id), a.Rows.Select(r => r.Id));   // biggest saving first
        Assert.Equal(500_000, a.Rows.Single(r => r.Id == "R01").ResponseCost);
        Assert.Equal("INR", a.Currency);
    }

    [Fact]
    public void The_tornado_ranks_risks_by_their_pre_mitigation_correlation_with_the_post_value_beside_it()
    {
        var (s, m) = Example();
        var pre = Run(s, m, Scenario.PreMitigation);
        var post = Run(s, m, Scenario.PostMitigation);
        var t = RiskTornado.Build(pre, post, 12);
        Assert.Equal(pre.Risks.OrderByDescending(r => Math.Abs(r.Sensitivity)).Select(r => r.Id), t.Select(r => r.Id));
        Assert.All(t, r => Assert.Equal(post.Risks.Single(x => x.Id == r.Id).Sensitivity, r.Post));
        Assert.All(RiskTornado.Build(pre, null, 12), r => Assert.Null(r.Post));
        Assert.Equal(2, RiskTornado.Build(pre, post, 2).Count);
        Assert.Contains("<svg", HtmlReport.Tornado(t));
    }
}
