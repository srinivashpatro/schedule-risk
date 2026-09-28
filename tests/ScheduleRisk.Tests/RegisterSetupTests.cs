using ScheduleRisk.Core.Risk.Register;

namespace ScheduleRisk.Tests;

/// <summary>Editing the matrix in step 01 Setup: adding and removing bands, levels and areas keeps the grid, the
/// areas' bands and the risks' assessments in step.</summary>
public class RegisterSetupTests
{
    private static RiskRegister WithRisk(int probability, int schedule, int safety)
    {
        var reg = new RiskRegister();
        var r = new RegisterRisk { Id = "R01" };
        r.Current.Probability = probability;
        r.Current.Severity["schedule"] = schedule;
        r.Current.Severity["safety"] = safety;
        reg.Risks.Add(r);
        return reg;
    }

    [Fact]
    public void Cycling_a_cell_goes_green_amber_red_green()
    {
        var m = MatrixSettings.Default();
        Assert.Equal(RiskRating.Green, m.Rate(0, 0));
        m.CycleRating(0, 0); Assert.Equal(RiskRating.Amber, m.Rate(0, 0));
        m.CycleRating(0, 0); Assert.Equal(RiskRating.Red, m.Rate(0, 0));
        m.CycleRating(0, 0); Assert.Equal(RiskRating.Green, m.Rate(0, 0));
    }

    [Fact]
    public void Adding_a_probability_band_adds_a_grid_row_above_the_top_band()
    {
        var reg = new RiskRegister();
        reg.AddProbabilityBand();
        var m = reg.Matrix;
        Assert.Equal(6, m.Probability.Count);
        Assert.Equal(6, m.Ratings.Count);
        Assert.Equal("F", m.Probability[5].Letter);
        Assert.Equal(m.Ratings[4], m.Ratings[5]);           // copies the row below it
        Assert.NotSame(m.Ratings[4], m.Ratings[5]);
        Assert.Equal(.95, m.Probability[5].Min);
        Assert.DoesNotContain(reg.Validate(), i => i.Error);
    }

    [Fact]
    public void Removing_a_probability_band_clears_or_shifts_the_risks_on_it()
    {
        var on = WithRisk(2, 1, 1);
        on.RemoveProbabilityBand(2);
        Assert.Null(on.Risks[0].Current.Probability);
        Assert.Equal(4, on.Matrix.Probability.Count);
        Assert.Equal(4, on.Matrix.Ratings.Count);
        Assert.Equal(new[] { "A", "B", "D", "E" }, on.Matrix.Probability.Select(p => p.Letter));

        var above = WithRisk(4, 1, 1);
        above.RemoveProbabilityBand(0);
        Assert.Equal(3, above.Risks[0].Current.Probability);
        Assert.Equal("E", above.Matrix.Probability[3].Letter);
    }

    [Fact]
    public void Adding_a_severity_level_adds_a_band_to_every_area_and_a_grid_column()
    {
        var reg = new RiskRegister();
        reg.AddSeverityLevel();
        var m = reg.Matrix;
        Assert.Equal(6, m.SeverityLevels.Count);
        Assert.All(m.Ratings, row => Assert.Equal(6, row.Length));
        Assert.All(m.Ratings, row => Assert.Equal(row[4], row[5]));
        Assert.All(m.Dimensions, d => Assert.Equal(6, d.Bands.Count));
        var schedule = m.Dimension("schedule")!;
        Assert.Equal(20, schedule.Bands[5].Min);            // continues from the band below
        Assert.Null(schedule.Bands[5].Max);
        Assert.DoesNotContain(reg.Validate(), i => i.Error);
    }

    [Fact]
    public void Removing_a_severity_level_clears_or_shifts_the_risks_on_it()
    {
        var reg = WithRisk(3, 2, 4);
        reg.RemoveSeverityLevel(2);
        var a = reg.Risks[0].Current;
        Assert.False(a.Severity.ContainsKey("schedule"));
        Assert.Equal(3, a.Severity["safety"]);
        Assert.Equal(4, reg.Matrix.SeverityLevels.Count);
        Assert.All(reg.Matrix.Ratings, row => Assert.Equal(4, row.Length));
        Assert.All(reg.Matrix.Dimensions, d => Assert.Equal(4, d.Bands.Count));
    }

    [Fact]
    public void Removing_the_promote_rules_level_moves_the_rule_down()
    {
        var reg = new RiskRegister();
        reg.Matrix.Promote.MinScheduleSeverity = 4;
        reg.RemoveSeverityLevel(4);
        Assert.Equal(3, reg.Matrix.Promote.MinScheduleSeverity);
        reg.RemoveSeverityLevel(0);
        Assert.Equal(2, reg.Matrix.Promote.MinScheduleSeverity);
    }

    [Fact]
    public void The_matrix_keeps_at_least_two_bands_and_two_levels()
    {
        var reg = new RiskRegister();
        for (int i = 0; i < 10; i++) { reg.RemoveProbabilityBand(0); reg.RemoveSeverityLevel(0); }
        Assert.Equal(2, reg.Matrix.Probability.Count);
        Assert.Equal(2, reg.Matrix.SeverityLevels.Count);
        for (int i = 0; i < 20; i++) reg.AddSeverityLevel();
        Assert.Equal(10, reg.Matrix.SeverityLevels.Count);
    }

    [Fact]
    public void Adding_and_removing_areas()
    {
        var reg = WithRisk(3, 2, 4);
        var d = reg.AddDimension();
        Assert.Equal("area7", d.Id);
        Assert.Equal(5, d.Bands.Count);
        Assert.False(d.Quantitative);
        reg.RemoveDimension("safety");
        Assert.Null(reg.Matrix.Dimension("safety"));
        Assert.False(reg.Risks[0].Current.Severity.ContainsKey("safety"));
        Assert.Equal(2, reg.Risks[0].Current.OverallSeverity);
        Assert.DoesNotContain(reg.Validate(), i => i.Error);
    }

    [Fact]
    public void Renaming_an_area_id_carries_the_risks_with_it()
    {
        var reg = WithRisk(3, 2, 4);
        reg.RenameDimension("safety", "hse");
        Assert.Equal("hse", reg.Matrix.Dimensions.Single(x => x.Name == "Health and safety").Id);
        Assert.Equal(4, reg.Risks[0].Current.Severity["hse"]);
        reg.RenameDimension("schedule", "time");
        Assert.Equal("time", reg.Matrix.Promote.ScheduleDimension);
        Assert.Throws<ArgumentException>(() => reg.RenameDimension("time", "cost")); // already used
    }

    [Fact]
    public void Resetting_the_matrix_keeps_the_risks_and_reports_what_no_longer_fits()
    {
        var reg = WithRisk(4, 4, 4);
        reg.RemoveProbabilityBand(0);                       // the risk moves to band 3
        reg.ResetMatrix();
        Assert.Equal(5, reg.Matrix.Probability.Count);
        Assert.Single(reg.Risks);
        Assert.Empty(reg.Validate());
    }
}
