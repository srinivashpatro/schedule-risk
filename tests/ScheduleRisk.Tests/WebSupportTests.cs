using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>Engine features the browser app depends on.</summary>
public class WebSupportTests
{
    private static (ScheduleRisk.Core.Model.Schedule S, bool[] Crit) Synth500()
    {
        var s = TestData.Load("synth_500.xer");
        return (s, new CpmEngine(s).CriticalToProjectFinish());
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

    [Fact]
    public void Interactive_finish_chart_carries_per_day_cumulative_counts()
    {
        var (s, crit) = Synth500();
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), crit);
        SimulationSummary Summary(Scenario sc)
        {
            var mc = new MonteCarloEngine(s, m, sc);
            return SimulationSummary.Build(mc, mc.Run(300, 11));
        }
        var pre = Summary(Scenario.PreMitigation);
        var post = Summary(Scenario.PostMitigation);

        Assert.DoesNotContain("data-scurve", HtmlReport.SCurve(pre, post, s)); // the downloadable report stays static
        string html = HtmlReport.SCurve(pre, post, s, interactive: true);
        var attr = Regex.Match(html, "data-scurve=\"([^\"]*)\"");
        Assert.True(attr.Success);
        var d = JsonDocument.Parse(WebUtility.HtmlDecode(attr.Groups[1].Value)).RootElement;

        long day0 = d.GetProperty("day0").GetInt64();
        Assert.Equal(0, day0 % Time.MinutesPerDay);
        Assert.Equal(Time.FromMinutes(day0).ToString("yyyy-MM-dd"), d.GetProperty("date0").GetString());
        Assert.Equal(pre.Iterations, d.GetProperty("bins").EnumerateArray().Sum(b => b.GetInt32()));

        var series = d.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(2, series.Count);
        foreach (var (json, sum) in series.Zip(new[] { pre, post }))
        {
            var cum = json.GetProperty("cum").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            Assert.Equal(sum.Iterations, json.GetProperty("n").GetInt32());
            Assert.Equal(sum.Iterations, cum[^1]);
            for (int k = 0; k < cum.Length; k++)
                Assert.Equal(sum.SortedFinish.Count(t => t < day0 + (k + 1L) * Time.MinutesPerDay), cum[k]);
            // Reading the curve on the P80 date gives at least 80%, and the day before gives less.
            int k80 = (int)((sum.FinishPercentiles[80] - day0) / Time.MinutesPerDay);
            Assert.True(cum[k80] >= 0.8 * sum.Iterations);
            Assert.True(cum[k80 - 1] < 0.8 * sum.Iterations);
        }
    }

    [Theory]
    [InlineData(HistogramBins.Daily, 1)]
    [InlineData(HistogramBins.Weekly, 7)]
    public void Histogram_bars_follow_calendar_days_or_weeks(HistogramBins bins, int days)
    {
        long lo = 10 * Time.MinutesPerDay + 600, hi = 40 * Time.MinutesPerDay + 300;
        long[] e = HtmlReport.BinEdges(lo, hi, bins);
        Assert.Equal(lo, e[0]);
        Assert.Equal(hi, e[^1]);
        for (int k = 1; k < e.Length - 1; k++)
        {
            Assert.Equal(0, e[k] % Time.MinutesPerDay);
            if (k > 1) Assert.Equal(days * Time.MinutesPerDay, e[k] - e[k - 1]);
        }
        Assert.Equal(41, HtmlReport.BinEdges(lo, hi, HistogramBins.Auto).Length);
    }

    [Fact]
    public void Combined_chart_has_frequency_and_percent_axes_and_weekly_bins()
    {
        var (s, crit) = Synth500();
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), crit);
        var mc = new MonteCarloEngine(s, m, Scenario.PreMitigation);
        var pre = SimulationSummary.Build(mc, mc.Run(300, 11));

        string html = HtmlReport.SCurve(pre, null, s, interactive: true, percentile: 80, bins: HistogramBins.Weekly);
        Assert.Contains(">Frequency</text>", html);
        Assert.Contains("class=\"paxis\">100%</text>", html);
        Assert.Contains("<path", html); // the S-curve is drawn over the bars
        var d = JsonDocument.Parse(WebUtility.HtmlDecode(Regex.Match(html, "data-scurve=\"([^\"]*)\"").Groups[1].Value)).RootElement;
        var bins = d.GetProperty("bins").EnumerateArray().Select(b => b.GetInt32()).ToArray();
        var edges = d.GetProperty("edges").EnumerateArray().Select(b => b.GetInt64()).ToArray();
        Assert.Equal(bins.Length + 1, edges.Length);
        Assert.Equal(pre.Iterations, bins.Sum());
        Assert.Equal(bins.Length, Regex.Matches(html, "class=\"bar( in)?\"").Count);
    }
}
