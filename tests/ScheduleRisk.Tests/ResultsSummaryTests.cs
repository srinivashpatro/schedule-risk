using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>The Results summary panel: key finish figures, their spread, what drives them, critical and near-critical activities.</summary>
public class ResultsSummaryTests
{
    // P1 is 10 days; the risk adds 10 days in exactly 300 of 1000 iterations: durations are 10 (700x) or 20 (300x),
    // on a 5x8 calendar from Monday 05-Jan-2026 08:00.
    private const string DiscreteRisk = """{"risks":[{"id":"R1","title":"Late permit","probability":0.3,"activities":["P1"],"impact":{"distribution":"uniform","min":10,"mostLikely":10,"max":10,"units":"days"},"mitigated":{"probability":0.0}}]}""";

    private static (SimulationSummary Pre, SimulationSummary Post) Run(string xer, string json, int iterations = 1000, long seed = 5)
    {
        var s = TestData.Load(xer);
        var m = RiskModelLoader.LoadJson(s, json);
        SimulationSummary One(Scenario sc) { var mc = new MonteCarloEngine(s, m, sc); return SimulationSummary.Build(mc, mc.Run(iterations, seed)); }
        return (One(Scenario.PreMitigation), One(Scenario.PostMitigation));
    }

    private static string Val(List<StatRow> rows, string label) => rows.Single(r => r.Label == label).Pre;

    [Fact]
    public void Finish_rows_give_dates_durations_and_contingency()
    {
        var (pre, _) = Run("parallel_1.xer", DiscreteRisk);
        var sum = ResultsSummary.Build(pre, null);
        Assert.Equal(new[] { "Deterministic", "Chance of deterministic", "P50", "P50 − deterministic", "P80", "P80 − deterministic", "Mean", "Median" },
                     sum.Finish.Select(r => r.Label));
        Assert.Equal("16-Jan-2026 · 10.0 d", Val(sum.Finish, "Deterministic"));
        Assert.Equal("70%", Val(sum.Finish, "Chance of deterministic"));
        Assert.Equal("16-Jan-2026 · 10.0 d", Val(sum.Finish, "P50"));
        Assert.Equal("0.0 d (0.0%)", Val(sum.Finish, "P50 − deterministic"));
        Assert.Equal("30-Jan-2026 · 20.0 d", Val(sum.Finish, "P80"));
        Assert.Equal("+10.0 d (+100.0%)", Val(sum.Finish, "P80 − deterministic"));
        Assert.Equal("21-Jan-2026 · 13.0 d", Val(sum.Finish, "Mean"));
        Assert.Equal("16-Jan-2026 · 10.0 d", Val(sum.Finish, "Median"));
        Assert.All(sum.Finish, r => Assert.Null(r.Post));
    }

    [Fact]
    public void Spread_rows_give_the_range_and_shape_of_the_duration()
    {
        var (pre, _) = Run("parallel_1.xer", DiscreteRisk);
        var sum = ResultsSummary.Build(pre, null);
        Assert.Equal(new[] { "Minimum", "Maximum", "Standard deviation", "Skewness", "Kurtosis (excess)" }, sum.Spread.Select(r => r.Label));
        Assert.Equal("16-Jan-2026 · 10.0 d", Val(sum.Spread, "Minimum"));
        Assert.Equal("30-Jan-2026 · 20.0 d", Val(sum.Spread, "Maximum"));
        Assert.Equal("4.6 d", Val(sum.Spread, "Standard deviation"));
        Assert.Equal("0.87", Val(sum.Spread, "Skewness"));
        Assert.Equal("-1.24", Val(sum.Spread, "Kurtosis (excess)"));
    }

    [Fact]
    public void The_chosen_confidence_level_joins_P50_and_P80()
    {
        var (pre, _) = Run("parallel_1.xer", DiscreteRisk);
        var labels = ResultsSummary.Build(pre, null, 70).Finish.Select(r => r.Label).ToList();
        Assert.Equal(new[] { "P50", "P70", "P80" }, labels.Where(l => l.Length == 3 && l[0] == 'P'));
        Assert.Contains("P70 − deterministic", labels);
    }

    [Fact]
    public void Post_mitigation_values_sit_beside_pre_mitigation_ones()
    {
        var (pre, post) = Run("parallel_1.xer", DiscreteRisk);
        var sum = ResultsSummary.Build(pre, post);
        var det = sum.Finish.Single(r => r.Label == "Deterministic");
        Assert.True(det.Shared);
        var p80 = sum.Finish.Single(r => r.Label == "P80 − deterministic");
        Assert.Equal("+10.0 d (+100.0%)", p80.Pre);
        Assert.Equal("0.0 d (0.0%)", p80.Post); // mitigation removes the risk
        Assert.Contains("pre- and post-mitigation", sum.Run);
    }

    [Fact]
    public void Must_finish_by_rows_only_when_the_project_has_one()
    {
        var without = ResultsSummary.Build(Run("parallel_1.xer", DiscreteRisk).Pre, null);
        Assert.DoesNotContain(without.Finish, r => r.Label.Contains("Must Finish By"));

        var s = new XerEdit(File.ReadAllText(TestData.PathOf("parallel_1.xer")))
            .Set("PROJECT", _ => true, "plan_end_date", "2026-01-23 17:00").Run().S;
        var mc = new MonteCarloEngine(s, RiskModelLoader.LoadJson(s, DiscreteRisk));
        var sum = ResultsSummary.Build(SimulationSummary.Build(mc, mc.Run(1000, 5)), null);
        Assert.Equal("23-Jan-2026", Val(sum.Finish, "Must Finish By"));
        Assert.Equal("70%", Val(sum.Finish, "Chance of Must Finish By"));
    }

    [Fact]
    public void Critical_and_near_critical_follow_the_criticality_index()
    {
        var s = TestData.Load("synth_500.xer");
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), new CpmEngine(s).CriticalToProjectFinish());
        var mc = new MonteCarloEngine(s, m);
        var pre = SimulationSummary.Build(mc, mc.Run(300, 7));
        var sum = ResultsSummary.Build(pre, null);

        static int Shown(ActivityStats a) => int.Parse(ResultsSummary.Pct(a.Criticality).TrimEnd('%'));
        Assert.NotEmpty(sum.Critical);
        Assert.NotEmpty(sum.NearCritical);
        Assert.All(sum.Critical, a => Assert.True(Shown(a) >= ResultsSummary.CriticalPercent));
        Assert.All(sum.NearCritical, a => Assert.InRange(Shown(a), ResultsSummary.NearCriticalPercent, ResultsSummary.CriticalPercent - 1));
        Assert.Equal(pre.Activities.Count(a => Shown(a) >= 50), sum.Critical.Count);
        Assert.Equal(pre.Activities.Count(a => Shown(a) >= 10 && Shown(a) < 50), sum.NearCritical.Count);
        Assert.Equal(sum.Critical.OrderByDescending(a => a.Criticality).Select(a => a.Code), sum.Critical.Select(a => a.Code));
        Assert.Equal(sum.NearCritical.OrderByDescending(a => a.Criticality).Select(a => a.Code), sum.NearCritical.Select(a => a.Code));
    }

    [Fact]
    public void Critical_and_near_critical_agree_with_the_whole_percent_shown()
    {
        // An activity critical in 49.8% of the iterations reads "50%", so it is listed as critical, not near-critical.
        var (pre, _) = Run("parallel_1.xer", DiscreteRisk);
        pre.Activities.Clear();
        foreach (var (code, ci) in new[] { ("A", 0.5), ("B", 0.498), ("C", 0.494), ("D", 0.1), ("E", 0.097), ("F", 0.094) })
            pre.Activities.Add(new ActivityStats(code, code, ci, 0, 0));
        var sum = ResultsSummary.Build(pre, null);
        Assert.Equal(new[] { "A", "B" }, sum.Critical.Select(a => a.Code));
        Assert.Equal(new[] { "C", "D", "E" }, sum.NearCritical.Select(a => a.Code));
        Assert.Equal(new[] { "50%", "50%", "49%", "10%", "10%", "9%" }, pre.Activities.Select(a => ResultsSummary.Pct(a.Criticality)));
    }

    [Fact]
    public void Top_drivers_are_risks_and_drivers_by_influence_on_the_finish()
    {
        var s = TestData.Load("synth_500.xer");
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), new CpmEngine(s).CriticalToProjectFinish());
        var mc = new MonteCarloEngine(s, m);
        var pre = SimulationSummary.Build(mc, mc.Run(300, 7));
        var sum = ResultsSummary.Build(pre, null);

        Assert.False(sum.DriversAreActivities);
        Assert.Equal(pre.Risks.Count + pre.Drivers.Count, sum.Drivers.Count);
        Assert.Equal(sum.Drivers.OrderByDescending(d => Math.Abs(d.Sensitivity)).Select(d => d.Id), sum.Drivers.Select(d => d.Id));
        Assert.Contains(sum.Drivers, d => d.Kind == "Risk");
        Assert.Contains(sum.Drivers, d => d.Kind == "Driver");
        var top = pre.Risks.Select(r => (r.Id, r.Sensitivity)).Concat(pre.Drivers.Select(d => (d.Id, d.Sensitivity)))
            .OrderByDescending(x => Math.Abs(x.Sensitivity)).First();
        Assert.Equal(top.Id, sum.Drivers[0].Id);
    }

    [Fact]
    public void Without_risks_or_drivers_activity_durations_drive_the_finish()
    {
        var (pre, _) = Run("parallel_2.xer", """{"uncertainty":[{"filter":{"all":true},"distribution":"uniform","min":50,"mostLikely":100,"max":150}]}""", 500, 3);
        var sum = ResultsSummary.Build(pre, null);
        Assert.True(sum.DriversAreActivities);
        Assert.NotEmpty(sum.Drivers);
        Assert.All(sum.Drivers, d => Assert.Equal("Activity", d.Kind));
    }

    [Fact]
    public void Run_and_model_lines_describe_the_simulation()
    {
        var (pre, _) = Run("parallel_1.xer", DiscreteRisk);
        var sum = ResultsSummary.Build(pre, null);
        Assert.Contains("1,000 iterations", sum.Run);
        Assert.Contains("seed 5", sum.Run);
        Assert.Equal("PAR · data date 05-Jan-2026 · 3 activities · 1 risk", sum.Model);
    }

    [Fact]
    public void The_html_report_shows_the_summary_instead_of_tiles_and_duration_tables()
    {
        var s = TestData.Load("parallel_1.xer");
        var (pre, post) = Run("parallel_1.xer", DiscreteRisk);
        string html = HtmlReport.Build(s, pre, post);
        Assert.Contains("<h2>Summary</h2>", html);
        Assert.Contains("Top risk drivers", html);
        Assert.Contains("Near-critical", html);
        Assert.Contains("Kurtosis (excess)", html);
        Assert.DoesNotContain("<h2>Duration statistics</h2>", html);
        Assert.DoesNotContain("class=\"tiles\"", html);
    }
}
