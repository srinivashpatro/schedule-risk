using System.Text.RegularExpressions;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>"What the results mean": the Summary's figures in plain words, built from rules and thresholds.</summary>
public class ResultsNarrativeTests
{
    // P1 is 10 days; the risk adds 10 days in 300 of 1000 iterations and mitigation removes it (as ResultsSummaryTests).
    private const string DiscreteRisk = """{"risks":[{"id":"R1","title":"Late permit","probability":0.3,"activities":["P1"],"impact":{"distribution":"uniform","min":10,"mostLikely":10,"max":10,"units":"days"},"mitigated":{"probability":0.0}}]}""";

    private static (SimulationSummary Pre, SimulationSummary? Post) Run(ScheduleRisk.Core.Model.Schedule s, RiskModel m, int iterations, long seed, bool post)
    {
        SimulationSummary One(Scenario sc) { var mc = new MonteCarloEngine(s, m, sc); return SimulationSummary.Build(mc, mc.Run(iterations, seed)); }
        return (One(Scenario.PreMitigation), post ? One(Scenario.PostMitigation) : null);
    }

    /// <summary>The app's sample project: synth_500 with its risk model, 500 iterations, seed 20260921.</summary>
    private static (SimulationSummary Pre, SimulationSummary? Post) Sample()
    {
        var s = TestData.Load("synth_500.xer");
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), new CpmEngine(s).CriticalToProjectFinish());
        return Run(s, m, 500, 20260921, post: true);
    }

    private static (SimulationSummary Pre, SimulationSummary? Post) Json(string xer, string json, int iterations, long seed, bool post)
    {
        var s = TestData.Load(xer);
        return Run(s, RiskModelLoader.LoadJson(s, json), iterations, seed, post);
    }

    private static Finding Get(ResultsNarrative n, string key) => n.Findings.Single(f => f.Key == key);

    [Fact]
    public void The_sample_project_reads_in_plain_words()
    {
        var (pre, post) = Sample();
        var n = ResultsNarrative.Build(pre, post);
        Assert.Equal(new[]
        {
            ("finish", "Current finish", "The current schedule finishes on 13-Sep-2028, with no allowance for risk. Based on this model, that date is very unlikely: none of the 500 simulated outcomes finish by then."),
            ("p50", "P50", "The P50 date, 15-Mar-2029, is a coin flip: half the outcomes finish by then, and half finish later. It is about 128 working days (19%) after the current schedule's finish, and not a date to promise."),
            ("p80", "P80", "There is an 80% chance of finishing by 29-Jun-2029 (the P80), and a 1-in-5 chance of finishing later. Promising that date means adding about 202 working days (31%) to the current schedule."),
            ("centre", "Mean and median", "The average finish (the mean) is 04-Apr-2029, while the middle outcome (the median) is 15-Mar-2029. The average is about 13 working days later because some very late outcomes drag it later."),
            ("skew", "Skewness", "The outcomes have a moderate tail toward late finishes (skewness 0.58): some run much later than the rest, often when risks occur."),
            ("tails", "Kurtosis", "The overall shape is close to a bell curve (excess kurtosis -0.48)."),
            ("spread", "Spread", "The middle 80% of outcomes (P10 to P90) finish between 14-Dec-2028 and 15-Aug-2029. That range of about 170 working days, 26% of the current schedule's length, shows how uncertain the finish is."),
            ("mitigation", "Mitigation", "With the planned mitigation, the P80 moves to 08-Mar-2029, about 80 working days earlier. The current schedule's finish stays very unlikely (0%)."),
            ("drivers", "What drives it", "Before mitigation, one risk dominates the finish: R01 Late vendor data for long-lead equipment. Its influence (sensitivity 0.83) is at least twice that of the next, R04 Commissioning rework (0.40)."),
            ("critical", "Critical activities", "Before mitigation, in half or more of the outcomes, 7 activities are critical, on the chain of work that sets the finish. Another 12 are near-critical (10% to 49% of outcomes); delays to any of these are the most likely to move the finish."),
            ("caveat", "About these results", "These results come from 500 simulated outcomes of this schedule and risk model, so they are only as good as its logic and data. With fewer than 1,000 outcomes, the P-dates are rough estimates."),
        }, n.Findings.Select(f => (f.Key, f.Label, f.Text)));
    }

    [Fact]
    public void A_risk_that_either_happens_or_not_reads_as_two_groups()
    {
        var (pre, post) = Json("parallel_1.xer", DiscreteRisk, 1000, 5, post: true);
        var n = ResultsNarrative.Build(pre, post);
        Assert.Equal(new[]
        {
            ("finish", "The current schedule finishes on 16-Jan-2026, with no allowance for risk. Based on this model, that date is more likely than not: 70% of the 1,000 simulated outcomes finish by then."),
            ("p50", "The P50 date, 16-Jan-2026, is the middle outcome; in fact 70% of the 1,000 simulated outcomes finish by then. It falls on the current schedule's finish."),
            ("p80", "The P80 date, 30-Jan-2026, is the earliest date with at least an 80% chance: all 1,000 simulated outcomes finish by then. Promising that date means adding about 10 working days (100%) to the current schedule."),
            ("centre", "The average finish (the mean) is 21-Jan-2026, while the middle outcome (the median) is 16-Jan-2026. The average is about 3 working days later, but few outcomes land near it: they fall into separate groups. So the average is not a likely finish date."),
            ("skew", "The outcomes have a moderate tail toward late finishes (skewness 0.87): some run much later than the rest, often when risks occur."),
            ("tails", "The outcomes are flatter than a bell curve, or fall into two groups, such as a risk that happens or not (excess kurtosis -1.24). Check the histogram to see which."),
            ("spread", "The middle 80% of outcomes (P10 to P90) finish between 16-Jan-2026 and 30-Jan-2026. That range of about 10 working days, 100% of the current schedule's length, shows how uncertain the finish is."),
            ("mitigation", "With the planned mitigation, the P80 moves to 16-Jan-2026, about 10 working days earlier. The chance of meeting the current schedule's finish rises from 70% to 100%."),
            ("drivers", "Before mitigation, one risk drives the finish: R1 Late permit (sensitivity 1.00)."),
            ("critical", "Before mitigation, in half or more of the outcomes, 3 activities are critical, on the chain of work that sets the finish. Delays to these are the most likely to move the finish."),
            ("caveat", "These results come from 1,000 simulated outcomes of this schedule and risk model, so they are only as good as its logic and data."),
        }, n.Findings.Select(f => (f.Key, f.Text)));
    }

    [Fact]
    public void Only_a_chosen_level_other_than_50_or_80_adds_a_finding()
    {
        var (pre, post) = Sample();
        var p90 = ResultsNarrative.Build(pre, post, 90);
        var level = Get(p90, "level");
        Assert.Equal("P90 (chosen)", level.Label);
        Assert.Equal("At the level chosen on the chart, P90, there is a 90% chance of finishing by 15-Aug-2029. "
                   + "That is about 235 working days (36%) after the current schedule's finish.", level.Text);
        Assert.Equal("p80", p90.Findings[p90.Findings.ToList().IndexOf(level) - 1].Key);
        Assert.DoesNotContain(ResultsNarrative.Build(pre, post, 50).Findings, f => f.Key == "level");
        Assert.DoesNotContain(ResultsNarrative.Build(pre, post, 80).Findings, f => f.Key == "level");
        Assert.Contains("there is an 85% chance", Get(ResultsNarrative.Build(pre, post, 85), "level").Text);
    }

    // ------------------------------------------------------------------ thresholds, each side of the line

    [Theory]
    [InlineData(0, "very unlikely")]
    [InlineData(9, "very unlikely")]
    [InlineData(10, "less likely than not")]
    [InlineData(49, "less likely than not")]
    [InlineData(50, "more likely than not")]
    [InlineData(79, "more likely than not")]
    [InlineData(80, "likely")]
    [InlineData(100, "likely")]
    public void Chances_read_as_words(int percent, string words) => Assert.Equal(words, ResultsNarrative.Band(percent));

    [Theory]
    [InlineData(0, 500, "none of the 500 simulated outcomes finish by then")]
    [InlineData(1, 500, "all 500 simulated outcomes finish by then")]
    [InlineData(0.002, 500, "only 1 of the 500 simulated outcomes finishes by then")]
    [InlineData(0.004, 500, "only 2 of the 500 simulated outcomes finish by then")]
    [InlineData(0.998, 500, "all but 1 of the 500 simulated outcomes finish by then")]
    [InlineData(0.7, 1000, "70% of the 1,000 simulated outcomes finish by then")]
    public void A_share_of_outcomes_is_counted_when_its_percent_would_mislead(double share, int n, string words) =>
        Assert.Equal(words, ResultsNarrative.FinishBy(share, n));

    [Theory]
    [InlineData(0.49, "spread roughly evenly around the middle (skewness 0.49)")]
    [InlineData(-0.49, "spread roughly evenly around the middle (skewness -0.49)")]
    [InlineData(0.4996, "a moderate tail toward late finishes (skewness 0.50)")]   // read as shown
    [InlineData(0.5, "a moderate tail toward late finishes (skewness 0.50)")]
    [InlineData(1.0, "a moderate tail toward late finishes (skewness 1.00)")]
    [InlineData(1.01, "a strong tail toward late finishes (skewness 1.01)")]
    [InlineData(-0.5, "a moderate tail toward early finishes (skewness -0.50)")]
    [InlineData(-1.2, "a strong tail toward early finishes (skewness -1.20)")]
    public void Skewness_reads_as_the_direction_and_strength_of_the_tail(double skewness, string words) =>
        Assert.Contains(words, ResultsNarrative.SkewSentence(skewness, risks: true));

    [Fact]
    public void Without_risks_a_late_tail_is_not_put_down_to_them()
    {
        Assert.EndsWith("often when risks occur.", ResultsNarrative.SkewSentence(0.8, risks: true));
        Assert.EndsWith("some run much later than the rest.", ResultsNarrative.SkewSentence(0.8, risks: false));
    }

    [Theory]
    [InlineData(1.0, "close to a bell curve (excess kurtosis 1.00)")]
    [InlineData(1.01, "more common than in a bell curve (excess kurtosis 1.01)")]
    [InlineData(-1.0, "close to a bell curve (excess kurtosis -1.00)")]
    [InlineData(-1.01, "fall into two groups, such as a risk that happens or not (excess kurtosis -1.01)")]
    [InlineData(-1.004, "close to a bell curve (excess kurtosis -1.00)")]   // read as shown
    public void Kurtosis_compares_the_shape_with_a_bell_curve(double kurtosis, string words) =>
        Assert.Contains(words, ResultsNarrative.TailsSentence(kurtosis));

    [Theory]
    [InlineData(0.99, 0)]
    [InlineData(-0.99, 0)]
    [InlineData(1.0, 1)]
    [InlineData(-1.0, -1)]
    [InlineData(13.4, 1)]
    public void Mean_and_median_less_than_a_working_day_apart_are_about_the_same(double gapDays, int side) =>
        Assert.Equal(side, ResultsNarrative.CentreSide(gapDays));

    [Theory]
    [InlineData(0.80, 0.40, true)]
    [InlineData(0.79, 0.40, false)]
    [InlineData(0.795, 0.40, true)]     // read as shown: 0.80 and 0.40
    [InlineData(-0.83, 0.40, true)]
    [InlineData(0.50, 0.0, true)]
    public void One_driver_dominates_at_twice_the_next(double first, double second, bool dominates) =>
        Assert.Equal(dominates, ResultsNarrative.Dominates(first, second));

    // ------------------------------------------------------------------ edge cases

    [Fact]
    public void Without_mitigation_there_is_no_mitigation_finding_and_no_before_mitigation()
    {
        var (pre, _) = Json("parallel_1.xer", DiscreteRisk, 1000, 5, post: false);
        var n = ResultsNarrative.Build(pre, null);
        Assert.DoesNotContain(n.Findings, f => f.Key == "mitigation");
        Assert.DoesNotContain(n.Findings, f => f.Text.Contains("mitigation"));
        Assert.Equal("One risk drives the finish: R1 Late permit (sensitivity 1.00).", Get(n, "drivers").Text);
        Assert.StartsWith("In half or more of the outcomes, 3 activities are critical", Get(n, "critical").Text);
    }

    [Fact]
    public void Without_risks_the_finish_moves_with_activity_durations()
    {
        var (pre, _) = Json("synth_200.xer", """{"uncertainty":[{"filter":{"all":true},"distribution":"triangle","min":90,"mostLikely":100,"max":130}]}""", 500, 1, post: false);
        var n = ResultsNarrative.Build(pre, null);
        Assert.Equal("The model has no risks or risk drivers, so the finish moves with the activity durations. "
                   + "The one that matters most is A01920 COMM activity 192 (sensitivity 0.42).", Get(n, "drivers").Text);
        Assert.Equal("The outcomes are spread roughly evenly around the middle (skewness -0.07): late surprises are about as likely as early ones.", Get(n, "skew").Text);
        Assert.EndsWith("They are about the same, which suggests the outcomes spread evenly on both sides.", Get(n, "centre").Text);
    }

    [Fact]
    public void A_must_finish_by_gets_its_own_finding()
    {
        var (pre, _) = Json("parallel_1.xer", DiscreteRisk, 1000, 5, post: false);
        Assert.DoesNotContain(ResultsNarrative.Build(pre, null).Findings, f => f.Key == "deadline");

        var s = new XerEdit(File.ReadAllText(TestData.PathOf("parallel_1.xer"))).Set("PROJECT", _ => true, "plan_end_date", "2026-01-23 17:00").Run().S;
        var mc = new MonteCarloEngine(s, RiskModelLoader.LoadJson(s, DiscreteRisk));
        var n = ResultsNarrative.Build(SimulationSummary.Build(mc, mc.Run(1000, 5)), null);
        var deadline = Get(n, "deadline");
        Assert.Equal("Must Finish By", deadline.Label);
        Assert.Equal("The project's deadline in P6, its Must Finish By date, is 23-Jan-2026. "
                   + "Based on this model, meeting it is more likely than not: 70% of the 1,000 simulated outcomes finish by then.", deadline.Text);
        Assert.Equal(new[] { "finish", "deadline", "p50" }, n.Findings.Take(3).Select(f => f.Key));
    }

    [Fact]
    public void Shorter_durations_give_a_certain_finish_and_negative_contingency()
    {
        var (pre, _) = Json("parallel_1.xer", """{"uncertainty":[{"filter":{"all":true},"distribution":"uniform","min":50,"mostLikely":75,"max":100}]}""", 1000, 3, post: false);
        var n = ResultsNarrative.Build(pre, null);
        Assert.EndsWith("that date is likely: all 1,000 simulated outcomes finish by then.", Get(n, "finish").Text);
        Assert.Contains("before the current schedule's finish, so that finish already has at least an 80% chance.", Get(n, "p80").Text);
        Assert.Contains("before the current schedule's finish, and not a date to promise.", Get(n, "p50").Text);
    }

    [Fact]
    public void A_model_with_no_variation_says_so_and_skips_the_shape()
    {
        var (pre, _) = Json("parallel_1.xer", "{}", 200, 1, post: false);
        var n = ResultsNarrative.Build(pre, null);
        Assert.Equal(new[] { "finish", "flat", "drivers", "critical", "caveat" }, n.Findings.Select(f => f.Key));
        Assert.Equal("No spread", Get(n, "flat").Label);
        Assert.Equal("Every simulated outcome finishes on 16-Jan-2026: the model adds no variation, so there is no spread to describe.", Get(n, "flat").Text);
        Assert.Equal("No risk, driver or activity duration moves the finish noticeably.", Get(n, "drivers").Text);
        Assert.EndsWith("With fewer than 1,000 outcomes, the P-dates are rough estimates.", Get(n, "caveat").Text);
    }

    // ------------------------------------------------------------------ plain words

    [Fact]
    public void Every_sentence_is_at_most_25_words()
    {
        var runs = new[]
        {
            Sample(),
            Json("parallel_1.xer", DiscreteRisk, 1000, 5, post: true),
            Json("synth_200.xer", """{"uncertainty":[{"filter":{"all":true},"distribution":"triangle","min":90,"mostLikely":100,"max":130}]}""", 500, 1, post: false),
            Json("parallel_1.xer", "{}", 200, 1, post: false),
        };
        var texts = runs.SelectMany(r => new[] { 50, 80, 90, 35 }.SelectMany(p => ResultsNarrative.Build(r.Pre, r.Post, p).Findings.Select(f => f.Text)))
            .Concat(ResultsNarrative.Terms.Select(t => t.Meaning));
        foreach (var sentence in texts.SelectMany(t => Regex.Split(t, @"(?<=[.!?])\s+")))
            Assert.True(sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 25, $"{sentence.Split(' ').Length} words: {sentence}");
    }

    [Fact]
    public void The_glossary_explains_each_term_the_findings_use()
    {
        Assert.Equal(new[]
        {
            "P-level", "P50", "P80", "Deterministic finish", "Contingency", "Mean", "Median", "Standard deviation",
            "Skewness", "Kurtosis", "Criticality", "Sensitivity", "Working days",
        }, ResultsNarrative.Terms.Select(t => t.Name));
        Assert.All(ResultsNarrative.Terms, t => Assert.EndsWith(".", t.Meaning));
        Assert.Contains("working days of the project calendar", ResultsNarrative.Terms.Single(t => t.Name == "Working days").Meaning);
        Assert.Contains("Critical: 50% or more; near-critical: 10% to 49%.", ResultsNarrative.Terms.Single(t => t.Name == "Criticality").Meaning);
    }

    [Fact]
    public void Hyphenated_words_are_kept_whole_where_layouts_break_at_hyphens()
    {
        Assert.Equal("by 13\u2011Sep\u20112028 (excess kurtosis -0.48), a 1\u2011in\u20115 chance",
                     ResultsNarrative.NoBreakHyphens("by 13-Sep-2028 (excess kurtosis -0.48), a 1-in-5 chance"));
        Assert.Equal("the <span class=\"nw\">P-dates</span> &lt;are&gt; rough", HtmlReport.Unbroken("the P-dates <are> rough"));
    }

    [Fact]
    public void The_html_report_follows_the_owners_order_with_a_linked_contents_list()
    {
        var s = TestData.Load("parallel_1.xer");
        var (pre, post) = Json("parallel_1.xer", DiscreteRisk, 1000, 5, post: true);
        var hc = HealthCheck.Run(s, new ScheduleRisk.Core.Cpm.CpmEngine(s).Run());
        string html = HtmlReport.Build(s, pre, post, hc);
        var heads = Regex.Matches(html, "<h2 id=\"(s\\d+)\">(.*?)</h2>").Select(m => (Id: m.Groups[1].Value, Title: m.Groups[2].Value)).ToList();
        Assert.Equal(new[]
        {
            "Schedule Health Check: P6 Check Schedule", "Schedule Health Check: DCMA 14-Point Assessment", "Summary &amp; Charts",
            "Risk &amp; Activity Breakdown", "Analysis of the Result", "Sensitivity &amp; Criticality", "Finish-Driving Activities",
            "Confidence levels", "Terms used", "Annexure: Milestones",
        }, heads.Select(h => h.Title).Where(t => t != "Risk drivers"));
        // the contents list comes first and links every heading
        int contents = html.IndexOf("<h2 id=\"contents\">Table of Contents</h2>");
        Assert.True(contents >= 0 && contents < html.IndexOf("<h2 id=\"s1\">"));
        foreach (var (id, title) in heads) Assert.Contains($"<li><a href=\"#{id}\">{title}</a></li>", html);
        Assert.Contains("<th>Parameter</th><th>Value</th><th>Analysis Statement</th>", html);
        Assert.Contains("<td><b>Mean and median</b></td><td>Mean <span class=\"nw\">21-Jan-2026</span>", html);
        Assert.Contains("<dt>Working days</dt>", html);
        Assert.DoesNotContain("What the results mean", html);
    }
}
