using System.Text.Json;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
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
