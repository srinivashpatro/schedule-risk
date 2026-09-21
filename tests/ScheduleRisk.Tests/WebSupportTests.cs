using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>Engine features the browser app depends on.</summary>
public class WebSupportTests
{
    private static (ScheduleRisk.Core.Model.Schedule S, bool[] Crit) Synth500()
    {
        var s = TestData.Load("synth_500.xer");
        var det = new CpmEngine(s).Run();
        return (s, Enumerable.Range(0, s.Activities.Count).Select(j => det.IsCritical(s, j)).ToArray());
    }

    [Fact]
    public void Model_document_round_trips_to_the_same_simulation()
    {
        var (s, crit) = Synth500();
        string original = File.ReadAllText(TestData.PathOf("synth_500.risk.json"));
        var doc = RiskModelDocument.FromJson(original);
        Assert.Equal(4, doc.Risks.Count);
        Assert.Equal(2, doc.Drivers.Count);
        Assert.Contains(doc.Risks, r => r.Id == "R01" && r.Mitigated && r.MitigatedImpactDiffers);
        Assert.Contains(doc.Risks, r => r.Filter.Kind == FilterKind.Custom); // compound filters survive as JSON

        var a = RiskModelLoader.LoadJson(s, original, crit);
        var b = RiskModelLoader.LoadJson(s, doc.ToJson(), crit);
        var ra = new MonteCarloEngine(s, a, Scenario.PostMitigation).Run(200, 5);
        var rb = new MonteCarloEngine(s, b, Scenario.PostMitigation).Run(200, 5);
        Assert.Equal(ra.Finish, rb.Finish);
    }

    [Fact]
    public void Simple_filters_become_editable_rows()
    {
        var doc = RiskModelDocument.FromJson("""{"uncertainty":[{"filter":{"wbs":["CIV","MECH"]},"distribution":"Triangular","min":90,"mostLikely":100,"max":120}],"risks":[{"id":"R1","probability":0.2,"activities":["A1","A2"],"impact":{"min":1,"max":3}}]}""");
        Assert.Equal(FilterKind.Wbs, doc.Uncertainty[0].Filter.Kind);
        Assert.Equal("CIV, MECH", doc.Uncertainty[0].Filter.Value);
        Assert.Equal("triangle", doc.Uncertainty[0].Dist.Distribution);
        Assert.Equal(FilterKind.Activities, doc.Risks[0].Filter.Kind);
        Assert.Equal("A1, A2", doc.Risks[0].Filter.Value);
    }

    [Fact]
    public async Task Chunked_sequential_run_equals_parallel_run()
    {
        var (s, crit) = Synth500();
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), crit);
        var parallel = new MonteCarloEngine(s, m) { Parallelize = true }.Run(300, 9);
        int yields = 0;
        var browserLike = new MonteCarloEngine(s, m) { Parallelize = false, ChunkSize = 37 };
        var chunked = await browserLike.RunAsync(300, 9, yieldBetweenChunks: () => { yields++; return Task.CompletedTask; });
        Assert.Equal(parallel.Finish, chunked.Finish);
        Assert.Equal(parallel.CriticalCount, chunked.CriticalCount);
        Assert.Equal((300 + 36) / 37, yields);
    }

    [Fact]
    public void Uniform_ignores_most_likely_when_saved()
    {
        var doc = new RiskModelDocument();
        doc.Uncertainty.Add(new UncertaintyRow { Dist = new DistSpec { Distribution = "uniform", Min = 90, MostLikely = 500, Max = 110 } });
        Assert.Null(doc.Uncertainty[0].Dist.Validate());
        var s = TestData.Load("parallel_1.xer");
        var m = RiskModelLoader.LoadJson(s, doc.ToJson());
        Assert.NotNull(m.Uncertainty[s.ByCode["P1"]]);
    }
}
