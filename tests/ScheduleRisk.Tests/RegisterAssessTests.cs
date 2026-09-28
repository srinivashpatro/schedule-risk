using ScheduleRisk.Core.Risk.Register;

namespace ScheduleRisk.Tests;

/// <summary>Step 03 Assess: the heat map, each risk's path across the matrix, the action list and the checks on assessments.</summary>
public class RegisterAssessTests
{
    private static readonly DateOnly Today = new(2026, 9, 28);

    private static void Set(Assessment a, int p, params (string Area, int Level)[] sev)
    {
        a.Probability = p;
        a.Severity.Clear();
        foreach (var (area, level) in sev) a.Severity[area] = level;
    }

    /// <summary>R01 moves V.D -> V.B -> I.B as in the reference; R02 sits at III.C then II.B; R03 is proposed; R04 is approved but not assessed.</summary>
    private static RiskRegister Sample()
    {
        var reg = new RiskRegister();
        RegisterRisk Add(string id, RiskStatus status)
        {
            var r = new RegisterRisk { Id = id, Title = id, Event = "e", Status = status };
            reg.Risks.Add(r);
            return r;
        }
        var r1 = Add("R01", RiskStatus.Approved);
        Set(r1.Inherent, 3, ("schedule", 4)); Set(r1.Current, 1, ("schedule", 4)); Set(r1.Target, 1, ("schedule", 0));
        var r2 = Add("R02", RiskStatus.Approved);
        Set(r2.Inherent, 2, ("cost", 2)); Set(r2.Current, 2, ("cost", 2), ("safety", 1)); Set(r2.Target, 1, ("cost", 1));
        var r3 = Add("R03", RiskStatus.Proposed);
        Set(r3.Current, 1, ("schedule", 4));
        Add("R04", RiskStatus.Approved);
        return reg;
    }

    [Fact]
    public void The_heat_map_counts_approved_risks_in_their_cells()
    {
        var reg = Sample();
        var current = HeatMap.Build(reg, AssessmentPoint.Current);
        Assert.Equal(new[] { "R01" }, current.Risks(1, 4).Select(r => r.Id));   // V.B
        Assert.Equal(new[] { "R02" }, current.Risks(2, 2).Select(r => r.Id));   // III.C: worst of cost III and safety II
        Assert.Equal(2, current.Placed);
        Assert.Equal(new[] { "R04" }, current.NotAssessed.Select(r => r.Id)); // approved, no assessment; R03 is only proposed
        Assert.Empty(current.Risks(3, 4));

        var inherent = HeatMap.Build(reg, AssessmentPoint.Inherent);
        Assert.Equal(new[] { "R01" }, inherent.Risks(3, 4).Select(r => r.Id)); // V.D
        var target = HeatMap.Build(reg, AssessmentPoint.Target);
        Assert.Equal(new[] { "R01" }, target.Risks(1, 0).Select(r => r.Id));    // I.B
    }

    [Fact]
    public void The_heat_map_counts_ratings()
    {
        var reg = Sample();
        var current = HeatMap.Build(reg, AssessmentPoint.Current);
        Assert.Equal(2, current.ByRating[RiskRating.Amber]);                   // V.B and III.C
        Assert.Equal(0, current.ByRating[RiskRating.Red]);
        var inherent = HeatMap.Build(reg, AssessmentPoint.Inherent);
        Assert.Equal(1, inherent.ByRating[RiskRating.Red]);                    // V.D
        Assert.Equal(1, inherent.ByRating[RiskRating.Amber]);                  // III.C
    }

    [Fact]
    public void A_filter_limits_the_heat_map()
    {
        var reg = Sample();
        var only = HeatMap.Build(reg, AssessmentPoint.Current, new RegisterFilter { Search = "R02" });
        Assert.Equal(1, only.Placed);
        Assert.Equal(new[] { "R02" }, only.Risks(2, 2).Select(r => r.Id));
    }

    [Fact]
    public void A_risk_path_runs_through_its_assessed_points()
    {
        var reg = Sample();
        var path = HeatMap.Path(reg.Matrix, reg.Risks[0]);
        Assert.Equal(new[] { AssessmentPoint.Inherent, AssessmentPoint.Current, AssessmentPoint.Target }, path.Select(s => s.Point));
        Assert.Equal(new[] { "V.D", "V.B", "I.B" }, path.Select(s => reg.Matrix.CellName(s.Probability, s.Severity)));
        Assert.Equal(new[] { RiskRating.Red, RiskRating.Amber, RiskRating.Green }, path.Select(s => s.Rating));
        var partial = new RegisterRisk();
        Set(partial.Current, 2, ("schedule", 1));
        Assert.Equal(new[] { AssessmentPoint.Current }, HeatMap.Path(reg.Matrix, partial).Select(s => s.Point));
    }

    [Fact]
    public void The_action_list_puts_overdue_actions_first_then_by_due_date()
    {
        var reg = Sample();
        reg.Risks[0].Actions.Add(new RiskAction { Text = "later", Due = new DateOnly(2026, 11, 1) });
        reg.Risks[0].Actions.Add(new RiskAction { Text = "done", Due = new DateOnly(2026, 9, 1), Status = ActionStatus.Done });
        reg.Risks[1].Actions.Add(new RiskAction { Text = "overdue", Due = new DateOnly(2026, 9, 20) });
        reg.Risks[1].Actions.Add(new RiskAction { Text = "no date" });
        reg.Risks[1].Actions.Add(new RiskAction { Text = "soon", Due = new DateOnly(2026, 10, 2) });
        var list = reg.ActionList(Today);
        Assert.Equal(new[] { "overdue", "soon", "later", "no date", "done" }, list.Select(a => a.Action.Text));
        Assert.True(list[0].Overdue);
        Assert.Equal("R02", list[0].Risk.Id);
        Assert.False(list[1].Overdue);
        Assert.Equal(1, reg.ActionList(Today).Count(a => a.Overdue));
    }

    [Fact]
    public void Assessments_that_get_worse_or_lack_a_response_are_flagged()
    {
        var reg = Sample();
        var r = reg.Risks[1];
        Set(r.Target, 3, ("cost", 3));                                         // target worse than current
        var issues = reg.AssessmentIssues(r);
        Assert.Contains(issues, i => i.Text.Contains("target") && i.Text.Contains("current"));
        Assert.Contains(issues, i => i.Text.Contains("response"));             // Amber now, no response chosen
        Assert.All(issues, i => Assert.False(i.Error));

        var r1 = reg.Risks[0];
        r1.Response = ResponseStrategy.Mitigate;
        r1.Owner = "Site manager";
        Assert.Empty(reg.AssessmentIssues(r1));
        Set(r1.Current, 4, ("schedule", 4));                                   // current more likely than inherent
        Assert.Contains(reg.AssessmentIssues(r1), i => i.Text.Contains("current") && i.Text.Contains("inherent"));
    }

    [Fact]
    public void An_approved_risk_without_a_current_assessment_or_an_owner_is_flagged()
    {
        var reg = Sample();
        var issues = reg.AssessmentIssues(reg.Risks[3]);
        Assert.Contains(issues, i => i.Text.Contains("current assessment"));
        Assert.Contains(issues, i => i.Text.Contains("owner"));
    }

    [Fact]
    public void Copying_an_assessment_is_a_separate_copy()
    {
        var reg = Sample();
        var r = reg.Risks[1];
        r.CopyAssessment(AssessmentPoint.Current, AssessmentPoint.Target);
        Assert.Equal(r.Current.Severity, r.Target.Severity);
        r.Target.Severity["cost"] = 0;
        Assert.Equal(2, r.Current.Severity["cost"]);
    }
}
