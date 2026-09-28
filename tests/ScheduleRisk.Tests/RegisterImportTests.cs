using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Risk.Register;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>
/// The risk model takes its discrete risks from the register (05 Model). "Move risks to the register" turns the risks
/// typed into a model into approved register risks that promote back to exactly the same model risks, so the
/// simulation gives the same results, seed for seed.
/// </summary>
public class RegisterImportTests
{
    private static (ScheduleRisk.Core.Model.Schedule S, CpmResult R, bool[] Crit, double Planned) Synth500()
    {
        var s = TestData.Load("synth_500.xer");
        var engine = new CpmEngine(s);
        var r = engine.Run();
        return (s, r, engine.CriticalToProjectFinish(), RegisterPromoter.PlannedDuration(s, r).WorkingDays);
    }

    private static RiskModelDocument Example() => RiskModelDocument.FromJson(File.ReadAllText(TestData.PathOf("synth_500.risk.json")));

    private static (long P50, long P80, long Mean) Simulate(ScheduleRisk.Core.Model.Schedule s, bool[] crit, RiskModelDocument doc, Scenario sc)
    {
        var m = RiskModelLoader.LoadJson(s, doc.ToJson(), crit);
        var mc = new MonteCarloEngine(s, m, sc);
        var sum = SimulationSummary.Build(mc, mc.Run(300, 11));
        return (sum.FinishPercentiles[50], sum.FinishPercentiles[80], sum.FinishMean);
    }

    [Fact]
    public void Moving_every_risk_keeps_the_results_identical()
    {
        var (s, _, crit, planned) = Synth500();
        var doc = Example();
        var before = (Simulate(s, crit, doc, Scenario.PreMitigation), Simulate(s, crit, doc, Scenario.PostMitigation));
        var reg = new RiskRegister();
        var report = RegisterImporter.MoveToRegister(doc, reg, s, crit, planned);
        Assert.Equal(new[] { "R01", "R02", "R03", "R04" }, report.Moved);
        Assert.Empty(report.NotMoved);
        Assert.All(doc.Risks, r => Assert.Equal(RiskRow.RegisterSource, r.Source));
        var promote = RegisterPromoter.Apply(doc, reg, planned);
        Assert.Equal(new[] { "R01", "R02", "R03", "R04" }, promote.Updated);
        Assert.Empty(promote.Removed);
        var after = (Simulate(s, crit, doc, Scenario.PreMitigation), Simulate(s, crit, doc, Scenario.PostMitigation));
        Assert.Equal(before, after);
    }

    [Fact]
    public void A_moved_risk_is_approved_with_its_numbers_kept_and_its_filter_resolved()
    {
        var (s, _, crit, planned) = Synth500();
        var doc = Example();
        var reg = new RiskRegister();
        RegisterImporter.MoveToRegister(doc, reg, s, crit, planned);
        var r1 = reg.Risks.Single(r => r.Id == "R01");
        Assert.Equal("Late vendor data for long-lead equipment", r1.Title);
        Assert.Equal(RiskStatus.Approved, r1.Status);
        Assert.Equal(RiskKind.Threat, r1.Kind);
        Assert.True(r1.Promotion.Always);
        Assert.Equal(0.4, r1.Promotion.Probability);
        Assert.Equal(("triangle", 10.0, 20.0, 45.0), (r1.Promotion.Impact!.Distribution, r1.Promotion.Impact.Min, r1.Promotion.Impact.MostLikely, r1.Promotion.Impact.Max));
        Assert.Equal(0.15, r1.Promotion.MitigatedProbability);
        Assert.Equal(20, r1.Promotion.MitigatedImpact!.Max);
        var compiled = RiskModelLoader.LoadJson(s, Example().ToJson(), crit).Risks.Single(r => r.Id == "R01");
        Assert.Equal(compiled.Activities.Select(i => s.Activities[i].Code).OrderBy(x => x), r1.Promotion.Activities.OrderBy(x => x));
        Assert.Contains(RegisterImporter.MoveToRegister(Example(), new RiskRegister(), s, crit, planned).Notes, n => n.Contains("R01") && n.Contains("activities"));
        var r4 = reg.Risks.Single(r => r.Id == "R04");
        Assert.Equal(0.2, r4.Promotion.MitigatedProbability);
        Assert.Equal(20, r4.Promotion.MitigatedImpact!.Max);                   // mitigated impact = the unmitigated one
        var r3 = reg.Risks.Single(r => r.Id == "R03");
        Assert.False(r3.Target.Complete);                                       // not mitigated: no target
        Assert.Null(r3.Promotion.MitigatedProbability);
    }

    [Fact]
    public void A_percent_risk_keeps_its_unit()
    {
        var (s, _, crit, planned) = Synth500();
        var doc = Example();
        var reg = new RiskRegister();
        RegisterImporter.MoveToRegister(doc, reg, s, crit, planned);
        var r2 = reg.Risks.Single(r => r.Id == "R02");
        Assert.Equal("percent", r2.Promotion.ImpactUnits);
        RegisterPromoter.Apply(doc, reg, planned);
        Assert.Equal("percent", doc.Risks.Single(r => r.Id == "R02").ImpactUnits);
        Assert.Equal("percent", RiskRegister.FromJson(reg.ToJson()).Risks.Single(r => r.Id == "R02").Promotion.ImpactUnits);
    }

    [Fact]
    public void The_assessment_is_inferred_from_the_probability_and_the_mean_impact()
    {
        var (s, _, crit, planned) = Synth500();
        var reg = new RiskRegister();
        RegisterImporter.MoveToRegister(Example(), reg, s, crit, planned);
        var m = reg.Matrix;
        var r1 = reg.Risks.Single(r => r.Id == "R01");
        Assert.Equal("C", m.Probability[r1.Current.Probability!.Value].Letter);    // 40% is in C (25-50%)
        double pct = (10 + 20 + 45) / 3.0 / planned * 100;                            // mean days as % of the planned duration
        int level = r1.Current.Severity["schedule"];
        var band = m.Dimension("schedule")!.Bands[level];
        Assert.True(pct > band.Min && pct <= band.Max, $"{pct}% in level {level}");
        Assert.Equal("B", m.Probability[r1.Target.Probability!.Value].Letter);     // 15% after mitigation
        Assert.Equal(0, RegisterImporter.ProbabilityBand(m, 0));
        Assert.Equal(4, RegisterImporter.ProbabilityBand(m, 0.99));                 // above the top band: the top band
        Assert.Equal(0, RegisterImporter.ScheduleLevel(m.Dimension("schedule")!, 0));
        Assert.Equal(1, RegisterImporter.ScheduleLevel(m.Dimension("schedule")!, 0.5));
        Assert.Equal(4, RegisterImporter.ScheduleLevel(m.Dimension("schedule")!, 50));
    }

    [Fact]
    public void A_moved_risk_stays_in_the_model_even_below_the_promote_rule()
    {
        var (s, _, crit, planned) = Synth500();
        var doc = Example();
        var reg = new RiskRegister();
        RegisterImporter.MoveToRegister(doc, reg, s, crit, planned);
        var r3 = reg.Risks.Single(r => r.Id == "R03");
        Assert.NotNull(RegisterPromoter.Ineligibility(reg, r3));                  // 30% and 17.5 days of 659: II.C, Green
        Assert.Null(RegisterPromoter.Ineligibility(reg, r3, honourAlways: true));
        RegisterPromoter.Apply(doc, reg, planned);
        Assert.Contains(doc.Risks, r => r.Id == "R03");
        r3.Promotion.Always = false;
        var report = RegisterPromoter.Apply(doc, reg, planned);
        Assert.Contains("R03", report.Removed);
    }

    [Fact]
    public void An_id_already_in_the_register_is_renamed_in_both()
    {
        var (s, _, crit, planned) = Synth500();
        var doc = Example();
        var reg = new RiskRegister();
        reg.Risks.Add(new RegisterRisk { Id = "R01", Title = "Workshop risk" });
        var report = RegisterImporter.MoveToRegister(doc, reg, s, crit, planned);
        string newId = report.Renamed.Single(x => x.From == "R01").To;
        Assert.Equal("Late vendor data for long-lead equipment", reg.Risks.Single(r => r.Id == newId).Title);
        Assert.Equal("Workshop risk", reg.Risks.Single(r => r.Id == "R01").Title);
        Assert.Equal(newId, doc.Risks[0].Id);
        Assert.Equal(reg.Risks.Count, reg.Risks.Select(r => r.Id).Distinct().Count());
        Assert.Empty(RegisterPromoter.Apply(doc, reg, planned).Skipped);
    }

    [Fact]
    public void Risks_already_from_the_register_are_left_alone()
    {
        var (s, _, crit, planned) = Synth500();
        var doc = Example();
        var reg = new RiskRegister();
        RegisterImporter.MoveToRegister(doc, reg, s, crit, planned);
        var again = RegisterImporter.MoveToRegister(doc, reg, s, crit, planned);
        Assert.Empty(again.Moved);
        Assert.Equal(4, reg.Risks.Count);
        Assert.Equal(0, RegisterImporter.TypedIn(doc));
        Assert.Equal(4, RegisterImporter.TypedIn(Example()));
    }

    [Fact]
    public void The_register_file_keeps_always_and_units()
    {
        var reg = new RiskRegister();
        var r = new RegisterRisk { Id = "R01" };
        r.Promotion.Always = true;
        r.Promotion.ImpactUnits = "percent";
        r.Promotion.Impact = new DistSpec { Min = 1, MostLikely = 2, Max = 3 };
        reg.Risks.Add(r);
        var back = RiskRegister.FromJson(reg.ToJson()).Risks[0].Promotion;
        Assert.True(back.Always);
        Assert.Equal("percent", back.ImpactUnits);
        Assert.Contains("\"always\": true", reg.ToJson());
        Assert.DoesNotContain("impactUnits", new RiskRegister { Risks = { new RegisterRisk { Id = "R2", Promotion = { Activities = { "A" } } } } }.ToJson());
    }
}
