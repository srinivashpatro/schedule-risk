using System.Text.Json;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

public class SimulationAnalyticTests
{
    private static double WorkDays(Schedule s, long t)
    {
        var c = s.Settings.ProjectCalendar;
        return (c.WorkAt(t) - c.WorkAt(s.Settings.DataDate)) / 480.0;
    }

    private static (Schedule, SimulationResult, MonteCarloEngine) Sim(string xer, string json, int iters, long seed, Scenario sc = Scenario.PreMitigation)
    {
        var s = TestData.Load(xer);
        var m = RiskModelLoader.LoadJson(s, json);
        var mc = new MonteCarloEngine(s, m, sc);
        return (s, mc.Run(iters, seed), mc);
    }

    [Fact]
    public void Single_triangle_reproduces_its_quantiles()
    {
        var (s, r, _) = Sim("parallel_1.xer",
            """{"uncertainty":[{"filter":{"activities":["P1"]},"distribution":"triangle","min":80,"mostLikely":100,"max":140}]}""", 2000, 11);
        var d = new Distribution(DistributionKind.Triangle, 8, 10, 14);
        foreach (int p in new[] { 10, 50, 80, 90 })
            Assert.InRange(WorkDays(s, Statistics.Percentile(r.Finish, p)), d.Inverse(p / 100.0) - 0.02, d.Inverse(p / 100.0) + 0.02);
    }

    [Fact]
    public void Merge_bias_of_two_parallel_paths()
    {
        var (s, r, _) = Sim("parallel_2.xer",
            """{"uncertainty":[{"filter":{"all":true},"distribution":"uniform","min":50,"mostLikely":100,"max":150}]}""", 4000, 3);
        // max of two independent U(5,15): P50 = 5 + 10*sqrt(0.5)
        Assert.InRange(WorkDays(s, Statistics.Percentile(r.Finish, 50)), 5 + 10 * Math.Sqrt(0.5) - 0.1, 5 + 10 * Math.Sqrt(0.5) + 0.1);
    }

    [Fact]
    public void Correlation_is_induced()
    {
        var (s, r, _) = Sim("parallel_2.xer",
            """{"uncertainty":[{"filter":{"all":true},"distribution":"uniform","min":50,"mostLikely":100,"max":150}], "correlations":[{"filter":{"all":true},"coefficient":0.95}]}""", 2000, 3);
        var d1 = r.Durations[s.ByCode["P1"]].Select(x => (double)x).ToArray();
        var d2 = r.Durations[s.ByCode["P2"]].Select(x => (double)x).ToArray();
        Assert.InRange(ScheduleRisk.Core.Numerics.MathX.Spearman(d1, d2), 0.92, 0.98);
        Assert.True(WorkDays(s, Statistics.Percentile(r.Finish, 50)) < 11.0);
    }

    [Fact]
    public void Discrete_risk_occurs_exactly_at_its_probability_with_lhs()
    {
        var (s, r, mc) = Sim("parallel_1.xer",
            """{"risks":[{"id":"R1","probability":0.3,"activities":["P1"],"impact":{"distribution":"uniform","min":10,"mostLikely":10,"max":10,"units":"days"}}]}""", 1000, 5);
        Assert.Equal(300, r.RiskOccurred[0].Count(x => x == 1));
        Assert.Equal(10.0, WorkDays(s, Statistics.Percentile(r.Finish, 70)), 6);
        Assert.Equal(20.0, WorkDays(s, Statistics.Percentile(r.Finish, 71)), 6);
        var sum = SimulationSummary.Build(mc, r);
        Assert.Equal(10.0, sum.Risks[0].MeanFinishDeltaDays!.Value, 6);
    }

    [Fact]
    public void Mitigation_reduces_p80()
    {
        const string json = """{"risks":[{"id":"R1","probability":0.5,"activities":["P1"],"impact":{"distribution":"triangle","min":5,"mostLikely":10,"max":20,"units":"days"},"mitigated":{"probability":0.1}}]}""";
        var (_, pre, _) = Sim("parallel_1.xer", json, 1000, 1);
        var (_, post, _) = Sim("parallel_1.xer", json, 1000, 1, Scenario.PostMitigation);
        Assert.Equal(500, pre.RiskOccurred[0].Count(x => x == 1));
        Assert.Equal(100, post.RiskOccurred[0].Count(x => x == 1));
        Assert.True(Statistics.Percentile(post.Finish, 80) < Statistics.Percentile(pre.Finish, 80));
    }

    [Fact]
    public void Results_do_not_depend_on_thread_count()
    {
        var s = TestData.Load("synth_200.xer");
        const string json = """{"uncertainty":[{"filter":{"all":true},"distribution":"pert","min":90,"mostLikely":100,"max":140}], "correlations":[{"filter":{"code":{"Discipline":"CIV"}},"coefficient":0.5}]}""";
        var m = RiskModelLoader.LoadJson(s, json);
        var one = new MonteCarloEngine(s, m) { MaxDegreeOfParallelism = 1 }.Run(300, 77);
        var many = new MonteCarloEngine(s, m) { MaxDegreeOfParallelism = 8 }.Run(300, 77);
        Assert.Equal(one.Finish, many.Finish);
        Assert.Equal(one.CriticalCount, many.CriticalCount);
    }

    [Fact]
    public void Convergence_stops_on_a_batch_boundary()
    {
        var s = TestData.Load("parallel_2.xer");
        var m = RiskModelLoader.LoadJson(s, """{"uncertainty":[{"filter":{"all":true},"distribution":"triangle","min":90,"mostLikely":100,"max":130}], "simulation":{"convergence":{"enabled":true,"percentile":80,"toleranceDays":0.5,"batchSize":200,"minIterations":400,"maxIterations":5000}}}""");
        var r = new MonteCarloEngine(s, m).Run(seed: 2);
        Assert.True(r.Converged);
        Assert.True(r.Iterations < 5000);
        Assert.Equal(0, r.Iterations % 200);
    }
}

/// <summary>Project duration statistics: working days of the project calendar from the project start (twin of test_sim.py).</summary>
public class DurationStatisticsTests
{
    [Fact]
    public void Skewness_and_kurtosis_match_excel()
    {
        // Excel: SKEW(1,2,3,4,10) = 1.697056275, KURT(...) = 3.152
        Assert.Equal(1.697056274847714, Statistics.Skewness(new double[] { 1, 2, 3, 4, 10 })!.Value, 12);
        Assert.Equal(3.152, Statistics.ExcessKurtosis(new double[] { 1, 2, 3, 4, 10 })!.Value, 12);
        // Excel: SKEW(2,4,4,4,5,5,7,9) = 0.818487553, KURT(...) = 0.940625
        Assert.Equal(0.8184875533567997, Statistics.Skewness(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 })!.Value, 12);
        Assert.Equal(0.940625, Statistics.ExcessKurtosis(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 })!.Value, 12);
        Assert.Equal(0.0, Statistics.Skewness(new double[] { 1, 2, 3 })!.Value, 12);
    }

    [Fact]
    public void Skewness_and_kurtosis_are_undefined_for_too_few_values_or_no_spread()
    {
        Assert.Null(Statistics.Skewness(new double[] { 1, 2 }));
        Assert.Null(Statistics.ExcessKurtosis(new double[] { 1, 2, 3 }));
        Assert.Null(Statistics.Skewness(new double[] { 5, 5, 5, 5 }));
        Assert.Null(Statistics.ExcessKurtosis(new double[] { 5, 5, 5, 5 }));
    }

    [Fact]
    public void Median_averages_the_middle_pair()
    {
        Assert.Equal(2.0, Statistics.Median(new double[] { 3, 1, 2 }));
        Assert.Equal(2.5, Statistics.Median(new double[] { 4, 1, 3, 2 }));
    }

    [Fact]
    public void Discrete_risk_gives_exact_durations()
    {
        // P1 is 10 days; the risk adds 10 days in exactly 300 of 1000 iterations: durations are 10 (700x) or 20 (300x).
        var s = TestData.Load("parallel_1.xer");
        var m = RiskModelLoader.LoadJson(s, """{"risks":[{"id":"R1","probability":0.3,"activities":["P1"],"impact":{"distribution":"uniform","min":10,"mostLikely":10,"max":10,"units":"days"}}]}""");
        var mc = new MonteCarloEngine(s, m);
        var sum = SimulationSummary.Build(mc, mc.Run(1000, 5));
        var d = sum.Duration;
        Assert.Equal("2026-01-05 08:00", Time.Format(sum.ProjectStart));
        Assert.Equal(10.0, d.Deterministic);
        Assert.Equal(10.0, d.Min);
        Assert.Equal(20.0, d.Max);
        Assert.Equal(13.0, d.Mean);
        Assert.Equal(10.0, d.Median);
        Assert.Equal(10.0, d.Percentiles[70]);
        Assert.Equal(20.0, d.Percentiles[80]);
        Assert.Equal(Math.Round(Math.Sqrt(21000.0 / 999), 4), d.Stdev);
        Assert.Equal(sum.StdevWorkingDays, d.Stdev);
        Assert.Equal(0.874183, d.Skewness);
        Assert.Equal(-1.238284, d.Kurtosis);
        Assert.Equal(0.0, d.ContingencyDays(50));
        Assert.Equal(0.0, d.ContingencyPercent(50));
        Assert.Equal(10.0, d.ContingencyDays(80));
        Assert.Equal(100.0, d.ContingencyPercent(80));
    }

    [Fact]
    public void Statistics_table_groups_rows_and_shows_contingency()
    {
        const string json = """{"risks":[{"id":"R1","probability":0.3,"activities":["P1"],"impact":{"distribution":"uniform","min":10,"mostLikely":10,"max":10,"units":"days"},"mitigated":{"probability":0.0}}]}""";
        var s = TestData.Load("parallel_1.xer");
        var m = RiskModelLoader.LoadJson(s, json);
        SimulationSummary Summ(Scenario sc) { var mc = new MonteCarloEngine(s, m, sc); return SimulationSummary.Build(mc, mc.Run(1000, 5)); }
        var pre = Summ(Scenario.PreMitigation);
        var post = Summ(Scenario.PostMitigation);

        var rows = StatisticsTable.Build(pre, null, 70);
        Assert.Equal(new[] { StatisticsTable.DurationGroup, "Statistics", "Analysis", "Model" }, rows.Select(r => r.Group).Distinct());
        string Val(List<StatRow> rs, string label) => rs.Single(r => r.Label == label).Pre;
        Assert.Equal("10.0 d", Val(rows, "Deterministic"));
        Assert.Equal("10.0 d", Val(rows, "P70"));    // the chosen level is added to P50 and P80
        Assert.Equal("+10.0 d (+100.0%)", Val(rows, "P80 − deterministic"));
        Assert.Equal("0.0 d (0.0%)", Val(rows, "P50 − deterministic"));
        Assert.Equal("0.87", Val(rows, "Skewness"));
        Assert.Equal("PAR", Val(rows, "Project"));
        Assert.All(rows, r => Assert.Null(r.Post));

        var both = StatisticsTable.Build(pre, post);
        Assert.Equal("+10.0 d (+100.0%)", both.Single(r => r.Label == "P80 − deterministic").Pre);
        Assert.Equal("0.0 d (0.0%)", both.Single(r => r.Label == "P80 − deterministic").Post);
        Assert.True(both.Single(r => r.Label == "Project").Shared);
        Assert.Equal("1,000", both.Single(r => r.Label == "Iterations").Post);

        string html = ScheduleRisk.Core.Reporting.HtmlReport.Build(s, pre, post);
        Assert.Contains("Duration statistics", html);
        Assert.Contains("Kurtosis (excess)", html);
    }

    [Fact]
    public void Summary_describes_the_model_and_the_run()
    {
        var s = TestData.Load("parallel_2.xer");
        var m = RiskModelLoader.LoadJson(s, """{"risks":[{"id":"R1","probability":0.3,"activities":["P1"],"impact":{"distribution":"uniform","min":1,"mostLikely":1,"max":1,"units":"days"}}]}""");
        var mc = new MonteCarloEngine(s, m);
        var before = DateTime.UtcNow;
        var sum = SimulationSummary.Build(mc, mc.Run(50, 1));
        Assert.Equal("PAR", sum.ProjectCode);
        Assert.Equal("2026-01-05 08:00", Time.Format(sum.DataDate));
        Assert.Equal(4, sum.ActivityCount);
        Assert.Equal(1, sum.RiskCount);
        Assert.InRange(sum.Started, before.AddSeconds(-1), DateTime.UtcNow);
        Assert.Equal(DateTimeKind.Utc, sum.Started.Kind);

        using var doc = JsonDocument.Parse(sum.ToJson());
        var model = doc.RootElement.GetProperty("model");
        Assert.Equal("PAR", model.GetProperty("project").GetString());
        Assert.Equal("2026-01-05 08:00", model.GetProperty("data_date").GetString());
        Assert.Equal(4, model.GetProperty("activities").GetInt32());
        Assert.Equal(1, model.GetProperty("risks").GetInt32());
    }

    [Fact]
    public void Summary_json_is_the_same_for_the_same_seed()
    {
        // The run's start time is shown in the app and report but kept out of the JSON, which stays reproducible.
        var s = TestData.Load("synth_200.xer");
        var m = RiskModelLoader.LoadJson(s, """{"uncertainty":[{"filter":{"all":true},"distribution":"pert","min":90,"mostLikely":100,"max":140}]}""");
        string Json()
        {
            var mc = new MonteCarloEngine(s, m);
            return SimulationSummary.Build(mc, mc.Run(200, 42)).ToJson();
        }
        string a = Json();
        Assert.Equal(a, Json());
        Assert.Contains("\"skewness\"", a);
    }

    [Fact]
    public void Duration_is_measured_from_the_actual_start_of_started_work()
    {
        var xer = File.ReadAllText(TestData.PathOf("parallel_1.xer"));
        var s = TestData.LoadText(StartedParallel1(xer));
        var mc = new MonteCarloEngine(s, RiskModelLoader.LoadJson(s, "{}"));
        var sum = SimulationSummary.Build(mc, mc.Run(20, 1));
        Assert.Equal("2026-01-05 08:00", Time.Format(sum.ProjectStart));
        Assert.Equal(10.0, sum.Duration.Deterministic); // 5 days done + 5 remaining
    }

    /// <summary>parallel_1 with the data date a week on: START complete, P1 in progress since 05-Jan with 5 days left.</summary>
    private static string StartedParallel1(string xer)
    {
        var lines = xer.Replace("\r\n", "\n").Split('\n');
        string[]? fields = null;
        string table = "";
        for (int i = 0; i < lines.Length; i++)
        {
            var cells = lines[i].Split('\t');
            if (cells[0] == "%T") table = cells[1];
            else if (cells[0] == "%F") fields = cells;
            else if (cells[0] == "%R" && fields != null)
            {
                string Get(string f) => cells[Array.IndexOf(fields, f)];
                void Set(string f, string v) => cells[Array.IndexOf(fields, f)] = v;
                if (table == "PROJECT") Set("last_recalc_date", "2026-01-12 08:00");
                if (table == "TASK" && Get("task_code") == "START")
                {
                    Set("status_code", "TK_Complete");
                    Set("act_start_date", "2026-01-05 08:00");
                    Set("act_end_date", "2026-01-05 08:00");
                }
                if (table == "TASK" && Get("task_code") == "P1")
                {
                    Set("status_code", "TK_Active");
                    Set("act_start_date", "2026-01-05 08:00");
                    Set("remain_drtn_hr_cnt", "40");
                }
                lines[i] = string.Join('\t', cells);
            }
        }
        return string.Join('\n', lines);
    }
}

/// <summary>The C# engine must reproduce the Python reference simulation (testdata/golden/synth_500.sim.*.json).</summary>
public class GoldenSimulationTests
{
    [Theory]
    [InlineData(Scenario.PreMitigation, "pre")]
    [InlineData(Scenario.PostMitigation, "post")]
    public void Matches_reference_simulation(Scenario sc, string tag)
    {
        var s = TestData.Load("synth_500.xer");
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), new CpmEngine(s).CriticalToProjectFinish());
        var mc = new MonteCarloEngine(s, m, sc);
        var res = mc.Run();
        var sum = SimulationSummary.Build(mc, res);
        var g = TestData.Golden($"synth_500.sim.{tag}.json");

        Assert.Equal(g.GetProperty("iterations").GetInt32(), sum.Iterations);
        Assert.Equal(g.GetProperty("deterministic_finish").GetString(), Time.Format(sum.DeterministicFinish));
        var pcal = s.Settings.ProjectCalendar;
        // P-dates: identical up to floating-point noise in libm (allow one working hour)
        foreach (int p in SimulationSummary.Percentiles)
        {
            long expected = Time.ParseP6(g.GetProperty("finish").GetProperty($"P{p}").GetString());
            Assert.InRange(pcal.WorkBetween(expected, sum.FinishPercentiles[p]), -60, 60);
        }
        Assert.Equal(g.GetProperty("finish").GetProperty("stdev_working_days").GetDouble(), sum.StdevWorkingDays, 1);

        var gd = g.GetProperty("duration");
        Assert.Equal(gd.GetProperty("start").GetString(), Time.Format(sum.ProjectStart));
        Assert.Equal(gd.GetProperty("deterministic").GetDouble(), sum.Duration.Deterministic, 4);
        // durations: the same one-working-hour allowance as the P-dates (1/8 day on this 8-hour calendar)
        foreach (int p in SimulationSummary.Percentiles)
            Assert.InRange(sum.Duration.Percentiles[p] - gd.GetProperty($"P{p}").GetDouble(), -0.125, 0.125);
        foreach (var (key, value) in new[] { ("min", sum.Duration.Min), ("max", sum.Duration.Max), ("mean", sum.Duration.Mean), ("median", sum.Duration.Median) })
            Assert.InRange(value - gd.GetProperty(key).GetDouble(), -0.125, 0.125);
        Assert.Equal(gd.GetProperty("stdev").GetDouble(), sum.Duration.Stdev, 1);
        Assert.Equal(gd.GetProperty("skewness").GetDouble(), sum.Duration.Skewness!.Value, 2);
        Assert.Equal(gd.GetProperty("kurtosis").GetDouble(), sum.Duration.Kurtosis!.Value, 2);
        Assert.Equal(gd.GetProperty("contingency").GetProperty("P80").GetProperty("percent").GetDouble(), sum.Duration.ContingencyPercent(80)!.Value, 1);
        var gm = g.GetProperty("model");
        Assert.Equal(gm.GetProperty("project").GetString(), sum.ProjectCode);
        Assert.Equal(gm.GetProperty("data_date").GetString(), Time.Format(sum.DataDate));
        Assert.Equal(gm.GetProperty("activities").GetInt32(), sum.ActivityCount);
        Assert.Equal(gm.GetProperty("risks").GetInt32(), sum.RiskCount);

        var risks = g.GetProperty("risks").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        foreach (var r in sum.Risks)
        {
            Assert.Equal(risks[r.Id].GetProperty("occurrence").GetDouble(), r.Occurrence, 6);
            Assert.Equal(risks[r.Id].GetProperty("sensitivity").GetDouble(), r.Sensitivity, 2);
        }
        var acts = g.GetProperty("activities").EnumerateArray().ToDictionary(e => e.GetProperty("code").GetString()!);
        foreach (var a in sum.Activities.Take(30))
        {
            Assert.True(acts.ContainsKey(a.Code), $"{a.Code} missing from reference");
            Assert.Equal(acts[a.Code].GetProperty("criticality").GetDouble(), a.Criticality, 2);
            Assert.Equal(acts[a.Code].GetProperty("sensitivity").GetDouble(), a.Sensitivity, 2);
        }
    }
}
