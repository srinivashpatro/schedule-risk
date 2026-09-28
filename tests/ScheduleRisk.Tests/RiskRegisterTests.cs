using ScheduleRisk.Core.Risk.Register;

namespace ScheduleRisk.Tests;

/// <summary>The qualitative risk register and its probability-severity matrix (docs/RISK_REGISTER.md).</summary>
public class RiskRegisterTests
{
    // The default rating grid, rows A (remote) to E (most likely), columns I to V: the user's reference matrix.
    private static readonly string[] DefaultGrid = { "GGGAA", "GGGAA", "GGAAA", "GGAAR", "GAARR" };

    private static RiskRating R(char c) => c switch { 'G' => RiskRating.Green, 'A' => RiskRating.Amber, _ => RiskRating.Red };

    private static Assessment Assess(int probability, params (string Dimension, int Level)[] severity)
    {
        var a = new Assessment { Probability = probability };
        foreach (var (d, l) in severity) a.Severity[d] = l;
        return a;
    }

    [Fact]
    public void Default_matrix_has_five_probability_bands_and_five_severity_levels()
    {
        var m = MatrixSettings.Default();
        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, m.Probability.Select(p => p.Letter));
        Assert.Equal(new[] { "Remote", "Unlikely", "Occasional", "Likely", "Most likely" }, m.Probability.Select(p => p.Label));
        Assert.Equal(new[] { 0, .05, .25, .50, .70 }, m.Probability.Select(p => p.Min));
        Assert.Equal(new[] { .05, .25, .50, .70, .95 }, m.Probability.Select(p => p.Max));
        Assert.Equal(new[] { "Insignificant", "Minor", "Significant", "Major", "Very high" }, m.SeverityLevels);
        Assert.Equal(new[] { "I", "II", "III", "IV", "V" }, Enumerable.Range(0, 5).Select(MatrixSettings.Roman));
        Assert.Equal(new[] { "schedule", "cost", "quality", "safety", "environment", "regulatory" }, m.Dimensions.Select(d => d.Id));
        Assert.Empty(m.Validate());
    }

    [Fact]
    public void Default_schedule_and_cost_bands_are_percentages()
    {
        var m = MatrixSettings.Default();
        var schedule = m.Dimension("schedule")!;
        Assert.Equal("% of planned project duration", schedule.Unit);
        Assert.Equal(new double?[] { 0, 0, 3, 6, 10 }, schedule.Bands.Select(b => b.Min));
        Assert.Equal(new double?[] { 0, 3, 6, 10, 20 }, schedule.Bands.Select(b => b.Max)); // V capped at 20%
        var cost = m.Dimension("cost")!;
        Assert.Equal("% of approved control budget", cost.Unit);
        Assert.Equal(new double?[] { 0, .25, .5, 1, 2 }, cost.Bands.Select(b => b.Min));
        Assert.Equal(new double?[] { .25, .5, 1, 2, null }, cost.Bands.Select(b => b.Max));
        Assert.True(schedule.Quantitative);
        Assert.False(m.Dimension("safety")!.Quantitative);
        Assert.All(m.Dimensions, d => Assert.Equal(5, d.Bands.Count));
        Assert.All(m.Dimensions.SelectMany(d => d.Bands), b => Assert.False(string.IsNullOrWhiteSpace(b.Text)));
    }

    [Fact]
    public void Default_ratings_follow_the_reference_grid_cell_by_cell()
    {
        var m = MatrixSettings.Default();
        for (int p = 0; p < 5; p++)
            for (int s = 0; s < 5; s++)
                Assert.Equal(R(DefaultGrid[p][s]), m.Rate(p, s));
        Assert.Equal("IV.D", m.CellName(3, 3));
        Assert.Equal("I.A", m.CellName(0, 0));
        Assert.Equal("V.E", m.CellName(4, 4));
        Assert.Equal(new[] { RiskRating.Red, RiskRating.Amber, RiskRating.Green }, m.Guidance.Select(g => g.Rating));
        Assert.All(m.Guidance, g => Assert.NotEmpty(g.Actions));
    }

    [Fact]
    public void Default_promote_rule_is_amber_or_red_with_schedule_severity_II_or_more()
    {
        var p = MatrixSettings.Default().Promote;
        Assert.Equal(RiskRating.Amber, p.MinRating);
        Assert.Equal("schedule", p.ScheduleDimension);
        Assert.Equal(1, p.MinScheduleSeverity);
    }

    [Theory]
    [InlineData(3, 1, 3, "IV.D", RiskRating.Amber)]  // likely, schedule II, safety IV: the worst area sets the column
    [InlineData(3, 4, 0, "V.D", RiskRating.Red)]
    [InlineData(1, 4, 2, "V.B", RiskRating.Amber)]
    [InlineData(1, 0, 0, "I.B", RiskRating.Green)]
    [InlineData(4, 1, 1, "II.E", RiskRating.Amber)]
    public void An_assessment_is_rated_by_its_worst_area(int probability, int schedule, int safety, string cell, RiskRating rating)
    {
        var m = MatrixSettings.Default();
        var a = Assess(probability, ("schedule", schedule), ("safety", safety));
        Assert.Equal(Math.Max(schedule, safety), a.OverallSeverity);
        Assert.Equal(cell, m.CellName(a));
        Assert.Equal(rating, m.Rate(a));
    }

    [Fact]
    public void An_incomplete_assessment_has_no_cell_or_rating()
    {
        var m = MatrixSettings.Default();
        var noSeverity = new Assessment { Probability = 2 };
        var noProbability = Assess(-1, ("schedule", 2));
        noProbability.Probability = null;
        foreach (var a in new[] { new Assessment(), noSeverity, noProbability })
        {
            Assert.False(a.Complete);
            Assert.Null(m.CellName(a));
            Assert.Null(m.Rate(a));
        }
        Assert.Null(noSeverity.OverallSeverity);
    }

    [Fact]
    public void Risks_have_three_assessment_points()
    {
        var r = new RegisterRisk();
        r.Inherent.Probability = 3;
        r.Current.Probability = 1;
        r.Target.Probability = 0;
        Assert.Equal(new int?[] { 3, 1, 0 },
            new[] { AssessmentPoint.Inherent, AssessmentPoint.Current, AssessmentPoint.Target }.Select(p => r.At(p).Probability));
    }

    [Fact]
    public void Next_id_skips_ids_in_use()
    {
        var reg = new RiskRegister();
        Assert.Equal("R01", reg.NextId());
        reg.Risks.Add(new RegisterRisk { Id = "R01" });
        reg.Risks.Add(new RegisterRisk { Id = "R03" });
        Assert.Equal("R02", reg.NextId());
        reg.Risks.Add(new RegisterRisk { Id = "R02" });
        Assert.Equal("R04", reg.NextId());
    }

    private static RiskRegister Full()
    {
        var reg = new RiskRegister { Name = "Q3 workshop" };
        reg.Matrix.CostOfDelayPerDay = 125000;
        reg.Matrix.Currency = "INR";
        reg.Matrix.Categories.Add("Logistics");
        var r = new RegisterRisk
        {
            Id = "R01", Title = "Late heavy-lift crane", Cause = "Single crane supplier", Event = "Crane arrives late",
            Effect = "Erection of the converter slips", Category = "Logistics", Kind = RiskKind.Threat, Status = RiskStatus.Approved,
            RaisedBy = "Planner", Raised = new DateOnly(2026, 9, 14), Owner = "Site manager",
            Response = ResponseStrategy.Mitigate, ResponseDescription = "Book a second supplier", ResponseCost = 2500000,
        };
        r.Inherent.Probability = 3; r.Inherent.Severity["schedule"] = 4; r.Inherent.Severity["cost"] = 3;
        r.Current.Probability = 1; r.Current.Severity["schedule"] = 4;
        r.Target.Probability = 1; r.Target.Severity["schedule"] = 0;
        r.Actions.Add(new RiskAction { Text = "Issue enquiry to a second supplier", Owner = "Procurement", Due = new DateOnly(2026, 10, 15) });
        r.Actions.Add(new RiskAction { Text = "Confirm crane slot", Owner = "Site manager", Status = ActionStatus.Done });
        reg.Risks.Add(r);
        reg.Risks.Add(new RegisterRisk { Id = "R02", Title = "Early permit", Kind = RiskKind.Opportunity, Response = ResponseStrategy.Exploit });
        return reg;
    }

    [Fact]
    public void Register_round_trips_through_json()
    {
        var reg = Full();
        string json = reg.ToJson();
        var back = RiskRegister.FromJson(json);
        Assert.Equal(json, back.ToJson());
        var r = back.Risks[0];
        Assert.Equal("Late heavy-lift crane", r.Title);
        Assert.Equal(RiskStatus.Approved, r.Status);
        Assert.Equal(new DateOnly(2026, 9, 14), r.Raised);
        Assert.Equal(4, r.Inherent.Severity["schedule"]);
        Assert.Equal("V.D", back.Matrix.CellName(r.Inherent));
        Assert.Equal("V.B", back.Matrix.CellName(r.Current));
        Assert.Equal("I.B", back.Matrix.CellName(r.Target));
        Assert.Equal(2500000, r.ResponseCost);
        Assert.Equal(ActionStatus.Done, r.Actions[1].Status);
        Assert.Equal(RiskKind.Opportunity, back.Risks[1].Kind);
        Assert.Equal(125000, back.Matrix.CostOfDelayPerDay);
        Assert.Contains("Logistics", back.Matrix.Categories);
        Assert.Contains("\"version\": 1", json);
    }

    [Fact]
    public void A_register_without_a_matrix_gets_the_default_one()
    {
        var reg = RiskRegister.FromJson("""{ "risks": [ { "id": "R01", "title": "x", "status": "proposed", "kind": "THREAT" } ] }""");
        Assert.Equal(5, reg.Matrix.Probability.Count);
        Assert.Equal(RiskStatus.Proposed, reg.Risks[0].Status);
        Assert.Null(reg.Risks[0].Current.Probability);
        Assert.Empty(reg.Validate());
    }

    [Fact]
    public void A_newer_register_version_is_refused()
    {
        var e = Assert.Throws<FormatException>(() => RiskRegister.FromJson("""{ "version": 2, "risks": [] }"""));
        Assert.Contains("version 2", e.Message);
    }

    [Fact]
    public void A_custom_three_by_three_matrix_loads_and_rates()
    {
        string json = """
        { "version": 1, "matrix": {
            "probability": [ { "letter": "L", "label": "Low", "min": 0, "max": 0.3 },
                             { "letter": "M", "label": "Medium", "min": 0.3, "max": 0.6 },
                             { "letter": "H", "label": "High", "min": 0.6, "max": 0.9 } ],
            "severityLevels": [ "Low", "Medium", "High" ],
            "dimensions": [ { "id": "schedule", "name": "Schedule", "unit": "% of planned project duration",
                              "bands": [ { "text": "a", "min": 0, "max": 2 }, { "text": "b", "min": 2, "max": 5 }, { "text": "c", "min": 5, "max": 10 } ] } ],
            "ratings": [ "GGA", "GAR", "ARR" ] },
          "risks": [ { "id": "R01", "current": { "probability": "H", "severity": { "schedule": "II" } } } ] }
        """;
        var reg = RiskRegister.FromJson(json);
        Assert.Empty(reg.Validate());
        Assert.Equal(RiskRating.Red, reg.Matrix.Rate(2, 1));
        Assert.Equal("II.H", reg.Matrix.CellName(reg.Risks[0].Current));
        Assert.Equal(RiskRating.Red, reg.Matrix.Rate(reg.Risks[0].Current));
        Assert.Equal(3, reg.Matrix.Guidance.Count); // default guidance when the file has none
    }

    [Fact]
    public void Validation_reports_matrix_and_register_problems()
    {
        var reg = Full();
        var m = reg.Matrix;
        m.Probability[2].Min = 0.20;                        // overlaps B (5-25%)
        m.Probability[4].Max = 0.99;                        // near certainty
        m.Probability[1].Letter = "A";                      // duplicate letter
        m.Ratings.RemoveAt(4);                              // grid one row short
        m.Dimension("cost")!.Bands.RemoveAt(4);             // four bands for five levels
        m.Dimension("schedule")!.Bands[3].Min = 11;         // min above max
        reg.Risks[1].Id = "R01";                            // duplicate id
        reg.Risks[0].Current.Severity["noise"] = 1;         // unknown area
        reg.Risks[0].Target.Probability = 7;                // no such band
        reg.Risks[0].Category = "Weather";                  // not a category
        var issues = reg.Validate();
        string all = string.Join("\n", issues.Select(i => i.Text));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("overlap"));
        Assert.Contains(issues, i => !i.Error && i.Text.Contains("certainty"));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("letter A"));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("rating grid"));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("Cost") && i.Text.Contains("4 bands"));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("Schedule") && i.Text.Contains("IV"));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("R01") && i.Text.Contains("more than once"));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("noise"));
        Assert.Contains(issues, i => i.Error && i.Text.Contains("target") && i.Text.Contains("probability"));
        Assert.Contains(issues, i => !i.Error && i.Text.Contains("Weather"));
        Assert.True(issues.Count >= 10, all);
    }

    [Fact]
    public void Response_strategies_suit_threats_or_opportunities()
    {
        Assert.Equal(new[] { ResponseStrategy.Avoid, ResponseStrategy.Transfer, ResponseStrategy.Mitigate, ResponseStrategy.Accept },
            RegisterRisk.Responses(RiskKind.Threat));
        Assert.Equal(new[] { ResponseStrategy.Exploit, ResponseStrategy.Share, ResponseStrategy.Enhance, ResponseStrategy.Accept },
            RegisterRisk.Responses(RiskKind.Opportunity));
        var reg = Full();
        reg.Risks[1].Response = ResponseStrategy.Avoid;     // an opportunity cannot be avoided
        Assert.Contains(reg.Validate(), i => i.Error && i.Text.Contains("R02") && i.Text.Contains("Avoid"));
    }

    [Fact]
    public void Actions_past_their_due_date_and_still_open_are_overdue()
    {
        var a = new RiskAction { Due = new DateOnly(2026, 10, 15) };
        Assert.False(a.Overdue(new DateOnly(2026, 10, 15)));
        Assert.True(a.Overdue(new DateOnly(2026, 10, 16)));
        a.Status = ActionStatus.Done;
        Assert.False(a.Overdue(new DateOnly(2026, 11, 1)));
        Assert.False(new RiskAction().Overdue(new DateOnly(2030, 1, 1)));
    }
}
