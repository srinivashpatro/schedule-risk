using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Risk.Register;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>Promote: approved risks that meet the promote rule become quantified risks in the model. The probability
/// band gives its midpoint, the schedule severity band a triangle of working days on the planned duration.</summary>
public class RegisterPromoteTests
{
    private const double Planned = 500;

    private static void Set(Assessment a, int p, int? schedule, params (string Area, int Level)[] more)
    {
        a.Probability = p;
        a.Severity.Clear();
        if (schedule is int s) a.Severity["schedule"] = s;
        foreach (var (area, level) in more) a.Severity[area] = level;
    }

    private static RegisterRisk Risk(RiskRegister reg, string id, RiskStatus status = RiskStatus.Approved, RiskKind kind = RiskKind.Threat)
    {
        var r = new RegisterRisk { Id = id, Title = "Risk " + id, Event = "e", Status = status, Kind = kind };
        reg.Risks.Add(r);
        return r;
    }

    [Fact]
    public void Eligibility_follows_the_promote_rule_on_the_current_assessment()
    {
        var reg = new RiskRegister();
        Set(Risk(reg, "R01").Current, 2, 3);                              // IV.C Amber, schedule IV
        Set(Risk(reg, "R02").Current, 0, 1);                              // II.A Green
        Set(Risk(reg, "R03").Current, 3, 0, ("safety", 4));                // V.D Red, but schedule I
        Set(Risk(reg, "R04").Current, 3, null, ("cost", 4));               // V.D Red, no schedule severity
        Set(Risk(reg, "R05", RiskStatus.Proposed).Current, 4, 4);         // proposed
        Risk(reg, "R06");                                                 // not assessed
        string? Why(string id) => RegisterPromoter.Ineligibility(reg, reg.Risks.Single(r => r.Id == id));
        Assert.Null(Why("R01"));
        Assert.Contains("Green", Why("R02"));
        Assert.Contains("schedule severity I", Why("R03"));
        Assert.Contains("schedule", Why("R04"));
        Assert.Contains("approved", Why("R05"));
        Assert.Contains("current assessment", Why("R06"));

        reg.Matrix.Promote.MinRating = RiskRating.Red;
        Assert.Contains("Amber", Why("R01"));
        reg.Matrix.Promote.MinRating = RiskRating.Green;
        reg.Matrix.Promote.MinScheduleSeverity = 0;
        Assert.Null(Why("R02"));
        Assert.Null(Why("R03"));
    }

    [Fact]
    public void Probability_is_the_band_midpoint_and_impact_is_the_schedule_band_on_the_planned_duration()
    {
        var reg = new RiskRegister();
        var r = Risk(reg, "R01");
        Set(r.Current, 2, 3);                                             // C (25-50%), IV (6-10%)
        Set(r.Target, 1, 1);                                              // B (5-25%), II (0-3%)
        var q = RegisterPromoter.Quantify(reg.Matrix, r, Planned);
        Assert.Null(q.Problem);
        Assert.Equal(0.375, q.Probability, 9);
        Assert.Equal((30.0, 40.0, 50.0), (q.Impact.Min, q.Impact.MostLikely, q.Impact.Max));
        Assert.True(q.Mitigated);
        Assert.Equal(0.15, q.MitigatedProbability, 9);
        Assert.Equal((0.0, 7.5, 15.0), (q.MitigatedImpact.Min, q.MitigatedImpact.MostLikely, q.MitigatedImpact.Max));
        Assert.Equal("triangle", q.Impact.Distribution);
    }

    [Fact]
    public void Default_probability_midpoints_run_from_2_5_to_82_5_percent()
    {
        var m = MatrixSettings.Default();
        Assert.Equal(new[] { .025, .15, .375, .60, .825 }, Enumerable.Range(0, 5).Select(p => Math.Round(RegisterPromoter.Midpoint(m, p), 9)));
    }

    [Fact]
    public void Days_are_rounded_to_a_tenth()
    {
        var reg = new RiskRegister();
        var r = Risk(reg, "R01");
        Set(r.Current, 2, 2);                                             // III (3-6%)
        var q = RegisterPromoter.Quantify(reg.Matrix, r, 333);
        Assert.Equal((10.0, 15.0, 20.0), (q.Impact.Min, q.Impact.MostLikely, q.Impact.Max));
        q = RegisterPromoter.Quantify(reg.Matrix, r, 337);
        Assert.Equal((10.1, 15.2, 20.2), (q.Impact.Min, q.Impact.MostLikely, q.Impact.Max));
    }

    [Fact]
    public void A_target_without_schedule_severity_means_no_delay_after_mitigation()
    {
        var reg = new RiskRegister();
        var r = Risk(reg, "R01");
        Set(r.Current, 2, 3);
        Set(r.Target, 0, null, ("cost", 1));
        var q = RegisterPromoter.Quantify(reg.Matrix, r, Planned);
        Assert.True(q.Mitigated);
        Assert.Equal((0.0, 0.0, 0.0), (q.MitigatedImpact.Min, q.MitigatedImpact.MostLikely, q.MitigatedImpact.Max));

        r.Target = new Assessment();                                      // no target: not mitigated
        Assert.False(RegisterPromoter.Quantify(reg.Matrix, r, Planned).Mitigated);
    }

    [Fact]
    public void An_opportunity_takes_days_off()
    {
        var reg = new RiskRegister();
        var r = Risk(reg, "R01", kind: RiskKind.Opportunity);
        Set(r.Current, 2, 3);
        var q = RegisterPromoter.Quantify(reg.Matrix, r, Planned);
        Assert.Equal((-50.0, -40.0, -30.0), (q.Impact.Min, q.Impact.MostLikely, q.Impact.Max));
    }

    [Fact]
    public void An_open_ended_schedule_band_cannot_be_quantified()
    {
        var reg = new RiskRegister();
        reg.Matrix.Dimension("schedule")!.Bands[4].Max = null;
        var r = Risk(reg, "R01");
        Set(r.Current, 3, 4);
        var q = RegisterPromoter.Quantify(reg.Matrix, r, Planned);
        Assert.Contains("upper", q.Problem);
    }

    [Fact]
    public void Overrides_replace_the_computed_values()
    {
        var reg = new RiskRegister();
        var r = Risk(reg, "R01");
        Set(r.Current, 2, 3);
        Set(r.Target, 1, 1);
        r.Promotion.Probability = 0.4;
        r.Promotion.Impact = new DistSpec { Distribution = "pert", Min = 20, MostLikely = 35, Max = 70 };
        r.Promotion.MitigatedProbability = 0.1;
        var q = RegisterPromoter.Quantify(reg.Matrix, r, Planned);
        Assert.Equal(0.4, q.Probability);
        Assert.Equal(("pert", 20.0, 35.0, 70.0), (q.Impact.Distribution, q.Impact.Min, q.Impact.MostLikely, q.Impact.Max));
        Assert.Equal(0.1, q.MitigatedProbability);
        Assert.Equal(7.5, q.MitigatedImpact.MostLikely);                  // not overridden
    }

    private static RiskRegister Mapped()
    {
        var reg = new RiskRegister();
        var r1 = Risk(reg, "R01");
        Set(r1.Current, 2, 3); Set(r1.Target, 1, 1);
        r1.Promotion.Activities.AddRange(new[] { "A1010", "A1020" });
        var r2 = Risk(reg, "R02");
        Set(r2.Current, 3, 2);
        r2.Promotion.Activities.Add("A1030");
        var r3 = Risk(reg, "R03");                                       // eligible but not mapped
        Set(r3.Current, 3, 2);
        var r4 = Risk(reg, "R04");                                       // not eligible
        Set(r4.Current, 0, 1);
        r4.Promotion.Activities.Add("A1040");
        return reg;
    }

    [Fact]
    public void Promoting_adds_register_risks_to_the_model_with_their_ids()
    {
        var doc = new RiskModelDocument();
        doc.Risks.Add(new RiskRow { Id = "M01", Title = "Manual risk" });
        var report = RegisterPromoter.Apply(doc, Mapped(), Planned);
        Assert.Equal(new[] { "R01", "R02" }, report.Added);
        Assert.Empty(report.Updated);
        Assert.Contains(report.Skipped, s => s.Id == "R03" && s.Reason.Contains("activities"));
        Assert.DoesNotContain(report.Skipped, s => s.Id == "R04");      // not eligible: listed in the preview, not skipped
        Assert.Equal(new[] { "M01", "R01", "R02" }, doc.Risks.Select(r => r.Id));
        var row = doc.Risks[1];
        Assert.Equal(RiskRow.RegisterSource, row.Source);
        Assert.Equal("Risk R01", row.Title);
        Assert.Equal(0.375, row.Probability, 9);
        Assert.Equal((30.0, 40.0, 50.0), (row.Impact.Min, row.Impact.MostLikely, row.Impact.Max));
        Assert.Equal("days", row.ImpactUnits);
        Assert.Equal(FilterKind.Activities, row.Filter.Kind);
        Assert.Equal("A1010, A1020", row.Filter.Value);
        Assert.True(row.Mitigated);
        Assert.True(row.MitigatedImpactDiffers);
        Assert.Equal(0.15, row.MitigatedProbability, 9);
        Assert.False(doc.Risks[2].Mitigated);
        Assert.Equal("", doc.Risks[0].Source);
    }

    [Fact]
    public void Promoting_again_updates_and_removes_what_left_the_rule()
    {
        var reg = Mapped();
        var doc = new RiskModelDocument();
        RegisterPromoter.Apply(doc, reg, Planned);
        reg.Risks[0].Current.Probability = 3;                             // R01 now more likely
        reg.Risks[1].Status = RiskStatus.Closed;                           // R02 closed
        var report = RegisterPromoter.Apply(doc, reg, Planned);
        Assert.Equal(new[] { "R01" }, report.Updated);
        Assert.Equal(new[] { "R02" }, report.Removed);
        Assert.Equal(new[] { "R01" }, doc.Risks.Select(r => r.Id));
        Assert.Equal(0.60, doc.Risks[0].Probability, 9);
    }

    [Fact]
    public void A_model_risk_with_the_same_id_that_did_not_come_from_the_register_is_left_alone()
    {
        var doc = new RiskModelDocument();
        doc.Risks.Add(new RiskRow { Id = "R01", Title = "Typed in the model", Probability = 0.9 });
        var report = RegisterPromoter.Apply(doc, Mapped(), Planned);
        Assert.Contains(report.Skipped, s => s.Id == "R01" && s.Reason.Contains("already"));
        Assert.Equal("Typed in the model", doc.Risks.Single(r => r.Id == "R01").Title);
        Assert.Equal(0.9, doc.Risks.Single(r => r.Id == "R01").Probability);
    }

    [Fact]
    public void The_source_and_the_promotion_survive_their_files()
    {
        var reg = Mapped();
        reg.Risks[0].Promotion.Probability = 0.42;
        reg.Risks[0].Promotion.MitigatedImpact = new DistSpec { Min = 0, MostLikely = 2, Max = 4 };
        var back = RiskRegister.FromJson(reg.ToJson());
        Assert.Equal(new[] { "A1010", "A1020" }, back.Risks[0].Promotion.Activities);
        Assert.Equal(0.42, back.Risks[0].Promotion.Probability);
        Assert.Null(back.Risks[0].Promotion.Impact);
        Assert.Equal(4, back.Risks[0].Promotion.MitigatedImpact!.Max);
        Assert.Equal(reg.ToJson(), back.ToJson());
        Assert.DoesNotContain("\"promotion\"", RiskRegister.FromJson("""{ "risks": [ { "id": "R09" } ] }""").ToJson());

        var doc = new RiskModelDocument();
        RegisterPromoter.Apply(doc, reg, Planned);
        var again = RiskModelDocument.FromJson(doc.ToJson());
        Assert.Equal(RiskRow.RegisterSource, again.Risks[0].Source);
        Assert.Contains("\"source\": \"register\"", doc.ToJson());
    }

    [Fact]
    public void The_planned_duration_is_the_deterministic_duration_of_the_results()
    {
        var s = TestData.Load("synth_500.xer");
        var engine = new CpmEngine(s);
        var cpm = engine.Run();
        var planned = RegisterPromoter.PlannedDuration(s, cpm);
        var m = RiskModelLoader.LoadJson(s, """{ "uncertainty": [ { "filter": { "all": true }, "distribution": "triangle", "min": 90, "mostLikely": 100, "max": 120 } ] }""");
        var mc = new MonteCarloEngine(s, m);
        var sum = SimulationSummary.Build(mc, mc.Run(20, 1));
        Assert.Equal(sum.ProjectStart, planned.Start);
        Assert.Equal(sum.DeterministicFinish, planned.Finish);
        Assert.Equal(sum.Duration.Deterministic, planned.WorkingDays, 6);
        Assert.True(planned.WorkingDays > 100);
    }

    [Fact]
    public void A_promoted_model_loads_and_simulates()
    {
        var s = TestData.Load("synth_500.xer");
        var cpm = new CpmEngine(s).Run();
        var ids = s.Activities.Where(a => !a.IsSummary && !a.IsMilestone && a.Status != ScheduleRisk.Core.Model.ActivityStatus.Complete).Take(3).Select(a => a.Code).ToArray();
        var reg = new RiskRegister();
        var r = Risk(reg, "R01");
        Set(r.Current, 3, 3); Set(r.Target, 1, 1);
        r.Promotion.Activities.AddRange(ids);
        var doc = new RiskModelDocument { Seed = 7, Iterations = 200 };
        var report = RegisterPromoter.Apply(doc, reg, RegisterPromoter.PlannedDuration(s, cpm).WorkingDays);
        Assert.Equal(new[] { "R01" }, report.Added);
        var model = RiskModelLoader.LoadJson(s, doc.ToJson());
        Assert.Single(model.Risks);
        Assert.Equal(3, model.Risks[0].Activities.Count);
        Assert.Equal(0.60, model.Risks[0].Probability, 9);
        var res = new MonteCarloEngine(s, model).Run(200, 7);
        Assert.Equal(200, res.Iterations);
    }
}
