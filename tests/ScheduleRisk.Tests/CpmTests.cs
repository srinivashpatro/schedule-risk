using System.Net;
using System.Text.Json;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;
using ScheduleRisk.Core.Xer;

namespace ScheduleRisk.Tests;

/// <summary>Hand-computed CPM cases (same expectations as reference/python/tests/test_cpm_hand.py).</summary>
public class HandCpmTests
{
    private static Dictionary<string, (string ES, string EF, string LS, string LF, long TF)> Run(string file)
    {
        var (s, r) = TestData.LoadAndRun(file);
        return s.Activities.ToDictionary(a => a.Code,
            a => (Time.Format(r.ES[a.Index]), Time.Format(r.EF[a.Index]), Time.Format(r.LS[a.Index]), Time.Format(r.LF[a.Index]), r.TF[a.Index]));
    }

    [Fact]
    public void Predecessor_lag_calendar()
    {
        var o = Run("hand_basic.xer");
        Assert.Equal(("2026-01-05 08:00", "2026-01-09 17:00", "2026-01-05 08:00", "2026-01-09 17:00", 0L), o["A"]);
        // FS +3d from Fri 17:00 consumes Mon-Wed -> starts Thu
        Assert.Equal(("2026-01-15 08:00", "2026-01-19 17:00", "2026-01-15 08:00", "2026-01-19 17:00", 0L), o["B"]);
        Assert.Equal(("2026-01-07 08:00", "2026-01-20 17:00", "2026-01-07 08:00", "2026-01-20 17:00", 0L), o["C"]);
        Assert.Equal(("2026-01-19 08:00", "2026-01-20 17:00", "2026-01-19 08:00", "2026-01-20 17:00", 0L), o["D"]);
        Assert.Equal("2026-01-20 17:00", o["M"].EF);
    }

    [Fact]
    public void TwentyFour_hour_lag_calendar()
    {
        var o = Run("hand_24h_lag.xer");
        Assert.Equal(("2026-01-12 08:00", "2026-01-14 17:00"), (o["B"].ES, o["B"].EF));
        Assert.Equal(("2026-01-13 08:00", "2026-01-14 17:00"), (o["D"].ES, o["D"].EF));
        Assert.Equal(("2026-01-06 08:00", "2026-01-19 17:00"), (o["C"].ES, o["C"].EF));
        Assert.Equal(3 * 480L, o["D"].TF);
        Assert.Equal("2026-01-19 09:00", o["B"].LF);
        Assert.Equal(17 * 60L, o["B"].TF);
        Assert.Equal(7 * 60L, o["A"].TF);
    }

    [Fact]
    public void Start_no_earlier_than_constraint()
    {
        var o = Run("hand_constraint.xer");
        Assert.Equal(("2026-01-12 08:00", "2026-01-23 17:00"), (o["C"].ES, o["C"].EF));
        Assert.Equal("2026-01-23 17:00", o["M"].EF);
        Assert.Equal(3 * 480L, o["D"].TF);
    }

    [Fact]
    public void Holiday_is_skipped()
    {
        var o = Run("hand_holiday.xer");
        Assert.Equal(("2025-12-31 08:00", "2026-01-07 17:00"), (o["A"].ES, o["A"].EF));
    }
}

/// <summary>Every activity's dates and float must equal the Python reference engine's.</summary>
public class GoldenCpmTests
{
    [Theory]
    [InlineData("hand_basic")]
    [InlineData("hand_24h_lag")]
    [InlineData("hand_constraint")]
    [InlineData("hand_holiday")]
    [InlineData("synth_200")]
    [InlineData("synth_500")]
    public void Matches_reference_engine(string name)
    {
        var (s, r) = TestData.LoadAndRun(name + ".xer");
        var g = TestData.Golden(name + ".cpm.json");
        Assert.Equal(g.GetProperty("project_finish").GetString(), Time.Format(r.ProjectFinish));
        int n = 0;
        foreach (var row in g.GetProperty("activities").EnumerateArray())
        {
            var a = s.Find(row.GetProperty("code").GetString()!);
            int j = a.Index;
            Assert.Equal(row.GetProperty("es").GetString(), Time.Format(r.ES[j]));
            Assert.Equal(row.GetProperty("ef").GetString(), Time.Format(r.EF[j]));
            Assert.Equal(row.GetProperty("ls").GetString(), Time.Format(r.LS[j]));
            Assert.Equal(row.GetProperty("lf").GetString(), Time.Format(r.LF[j]));
            var tf = row.GetProperty("tf_min");
            if (tf.ValueKind == JsonValueKind.Null) Assert.Equal(Time.None, r.TF[j]);
            else Assert.Equal(tf.GetInt64(), r.TF[j]);
            n++;
        }
        Assert.Equal(s.Activities.Count, n);

        var checks = ScheduleValidator.Validate(s, r).ToDictionary(c => c.Key, c => c.Count);
        foreach (var p in g.GetProperty("validation").EnumerateObject())
            Assert.Equal(p.Value.GetInt32(), checks[p.Name]);
    }

    [Theory]
    [InlineData("synth_200")]
    [InlineData("synth_500")]
    [InlineData("synth_5000")]
    public void Verifier_accepts_dates_written_by_engine(string name)
    {
        var (s, r) = TestData.LoadAndRun(name + ".xer");
        var rep = P6Verifier.Verify(s, r);
        Assert.True(rep.Compared > 100);
        Assert.Empty(rep.Diffs);
        Assert.Equal(VerifyOutcome.Matches, rep.Outcome);
    }

    [Fact]
    public void Verifier_reports_nothing_to_compare_without_p6_dates()
    {
        var (s, r) = TestData.LoadAndRun("hand_basic.xer");
        var rep = P6Verifier.Verify(s, r);
        Assert.Equal(0, rep.Compared);
        Assert.Equal(s.Activities.Count, rep.SkippedNoP6);
        Assert.Empty(rep.Diffs);
        Assert.Equal(VerifyOutcome.NothingToCompare, rep.Outcome);
        Assert.False(rep.Passed);
    }

    public static IEnumerable<object[]> AllFixtures() =>
        Directory.GetFiles(TestData.Dir, "*.xer").Select(p => new object[] { Path.GetFileName(p) }).OrderBy(x => (string)x[0]);

    /// <summary>CLAUDE.md: `sra verify` must pass on every fixture in testdata/.</summary>
    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_verifies_without_differences(string file)
    {
        var (s, r) = TestData.LoadAndRun(file);
        var rep = P6Verifier.Verify(s, r);
        Assert.Empty(rep.Diffs);
        Assert.NotEqual(VerifyOutcome.Differences, rep.Outcome);
    }

    [Fact]
    public void Verifier_reports_a_changed_date()
    {
        var doc = XerDocument.Load(TestData.PathOf("hand_basic.xer"));
        var s = ScheduleBuilder.Build(doc);
        var r = new CpmEngine(s).Run();
        // pretend P6 had C finishing one day later
        var c = s.Find("C");
        c.P6Dates["early_end_date"] = Time.ParseP6("2026-01-21 17:00");
        var rep = P6Verifier.Verify(s, r);
        Assert.Contains(rep.Diffs, d => d.Code == "C" && d.Field == "early_finish" && d.DeltaMinutes == -480);
        Assert.Equal(VerifyOutcome.Differences, rep.Outcome);
    }

    /// <summary>Sets fields of the rows of <paramref name="table"/> whose <paramref name="keyField"/> is
    /// <paramref name="key"/>, in XER text, addressing columns by name. A column the table lacks is
    /// added (empty in the other rows), as a real P6 export would carry it.</summary>
    internal static string SetFields(string xer, string table, string keyField, string key, params (string Field, string Value)[] values)
    {
        var lines = xer.Split('\n');
        string? current = null;
        string[]? fields = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var cells = lines[i].TrimEnd('\r').Split('\t');
            if (cells[0] == "%T") current = cells[1];
            else if (cells[0] == "%F" && current == table)
            {
                fields = cells.Concat(values.Select(v => v.Field).Where(f => !cells.Contains(f))).ToArray();
                lines[i] = string.Join('\t', fields);
            }
            else if (cells[0] == "%R" && current == table && fields != null)
            {
                if (cells.Length < fields.Length) cells = cells.Concat(Enumerable.Repeat("", fields.Length - cells.Length)).ToArray();
                if (cells[Array.IndexOf(fields, keyField)] == key)
                    foreach (var (f, v) in values) cells[Array.IndexOf(fields, f)] = v;
                lines[i] = string.Join('\t', cells);
            }
        }
        return string.Join('\n', lines);
    }

    [Fact]
    public void Verifier_reports_p6_dates_outside_the_calendar_range()
    {
        // P6 dates far outside the calendars we compile cannot be converted to working time;
        // they must be reported as differences (in elapsed time), not stop the check.
        string xer = SetFields(File.ReadAllText(TestData.PathOf("synth_200.xer")), "TASK", "task_code", "A00290",
            ("late_end_date", "2099-01-01 17:00"), ("late_start_date", "1990-01-02 08:00"));
        var s = ScheduleBuilder.Build(XerDocument.Parse(xer));
        var r = new CpmEngine(s).Run();
        var rep = P6Verifier.Verify(s, r);
        Assert.Equal(VerifyOutcome.Differences, rep.Outcome);
        Assert.Equal(new[] { "late_finish", "late_start" }, rep.Diffs.Select(d => d.Field).OrderBy(f => f));
        Assert.All(rep.Diffs, d =>
        {
            Assert.Equal("A00290", d.Code);
            Assert.True(d.OutsideCalendar);
            Assert.Equal(d.Ours - d.P6, d.DeltaMinutes);
        });
        Assert.Equal(rep.FieldsCompared - 2, rep.FieldsMatched);
    }

    [Fact]
    public void Xer_round_trips()
    {
        var text = File.ReadAllText(TestData.PathOf("synth_200.xer"));
        var a = XerDocument.Parse(text);
        var b = XerDocument.Parse(a.ToXerText());
        Assert.Equal(a.TableOrder, b.TableOrder);
        foreach (var name in a.TableOrder)
        {
            Assert.Equal(a.Tables[name].Fields, b.Tables[name].Fields);
            Assert.Equal(a.Tables[name].Rows.Count, b.Tables[name].Rows.Count);
            for (int i = 0; i < a.Tables[name].Rows.Count; i++) Assert.Equal(a.Tables[name].Rows[i], b.Tables[name].Rows[i]);
        }
    }

    [Fact]
    public void Loop_is_reported()
    {
        var text = File.ReadAllText(TestData.PathOf("hand_basic.xer"));
        // add M -> S, closing a loop S -> A -> C -> M -> S
        text = text.Replace("%T\tTASKPRED", "%T\tTASKPRED").Replace("%E", "%R\t99\t6\t5\t1\t1\tPR_FS\t0\r\n%E");
        var s = ScheduleBuilder.Build(XerDocument.Parse(text));
        var ex = Assert.Throws<ScheduleLoopException>(() => new CpmEngine(s));
        Assert.Contains("S", ex.Codes);
    }
}

/// <summary>
/// The backward pass starts from the project must-finish-by, P6's "Must Finish By" (PROJECT.plan_end_date),
/// so the calendars must cover it, and the late dates it produces, wherever it lies. PROJECT.scd_end_date is
/// P6's calculated scheduled finish, not a constraint.
/// </summary>
public class MustFinishByTests
{
    private static Schedule Build(string file, string project, (string Field, string Value)[] projectFields, double horizonYears = 30)
    {
        string xer = GoldenCpmTests.SetFields(File.ReadAllText(TestData.PathOf(file)), "PROJECT", "proj_short_name", project, projectFields);
        return ScheduleBuilder.Build(XerDocument.Parse(xer), horizonYears: horizonYears);
    }

    private static Schedule Build(string file, string project, string mustFinishBy, double horizonYears = 30) =>
        Build(file, project, new[] { ("plan_end_date", mustFinishBy) }, horizonYears);

    /// <summary>The first activity (no successors) that finishes the project.</summary>
    private static Activity Last(Schedule s, CpmResult r) =>
        s.Activities.First(a => r.EF[a.Index] == r.ProjectFinish && s.Relationships.All(x => x.Pred != a.Index));

    [Fact]
    public void Scheduled_finish_is_not_a_constraint()
    {
        // synth_200 finishes 2027-06-04 17:00; P6's scheduled finish a month later must not create float.
        var s = Build("synth_200.xer", "SYN200", new[] { ("scd_end_date", "2027-07-05 17:00") });
        var r = new CpmEngine(s).Run();
        Assert.Equal(Time.None, s.Settings.MustFinishBy);
        Assert.Equal(r.ProjectFinish, r.ProjectLateFinish);
        Assert.Equal(0, r.TF[Last(s, r).Index]);
    }

    [Fact]
    public void Must_finish_by_is_read_from_plan_end_date()
    {
        var s = Build("synth_200.xer", "SYN200", new[] { ("scd_end_date", "2027-06-04 17:00"), ("plan_end_date", "2027-07-05 17:00") });
        var r = new CpmEngine(s).Run();
        Assert.Equal(Time.ParseP6("2027-07-05 17:00"), s.Settings.MustFinishBy);
        Assert.Equal(s.Settings.MustFinishBy, r.ProjectLateFinish);
        Assert.True(r.TF[Last(s, r).Index] > 0);
    }

    [Fact]
    public void Must_finish_by_at_midnight_means_by_the_end_of_the_previous_working_day()
    {
        // P6: a Must Finish By of 04-Jun gives one day of negative float to a project finishing that day at 17:00.
        var s = Build("synth_200.xer", "SYN200", "2027-06-04 00:00");
        var r = new CpmEngine(s).Run();
        var last = Last(s, r);
        Assert.Equal(Time.ParseP6("2027-06-04 17:00"), r.ProjectFinish);
        Assert.Equal(Time.ParseP6("2027-06-03 17:00"), r.LF[last.Index]);
        Assert.Equal(-(long)last.Calendar.MinutesPerDay, r.TF[last.Index]);
    }

    [Theory]
    [InlineData("2100-12-31 17:00")] // after the calendar horizon
    [InlineData("2045-06-30 17:00")] // inside it, positive float
    [InlineData("2025-12-05 17:00")] // inside it, but the project (finish 2027-06-04) overruns it by 18 months
    [InlineData("2020-01-31 17:00")] // before the calendar horizon
    [InlineData("2015-01-30 17:00")]
    public void Backward_pass_runs_from_any_must_finish_by(string date)
    {
        var s = Build("synth_200.xer", "SYN200", date);
        var r = new CpmEngine(s).Run();
        long mfb = Time.ParseP6(date);
        Assert.Equal(mfb, r.ProjectLateFinish);
        // The activity that finishes the project has no successors: its late finish is the must-finish-by
        // (snapped to working time) and its float is measured to it.
        var last = Last(s, r);
        var cal = last.Calendar;
        Assert.Equal(cal.SnapFinish(mfb), r.LF[last.Index]);
        Assert.Equal(cal.WorkAt(r.LF[last.Index]) - cal.WorkAt(r.EF[last.Index]), r.TF[last.Index]);
        Assert.Equal(mfb >= r.ProjectFinish, r.TF[last.Index] >= 0);
    }

    [Fact]
    public void Monte_carlo_runs_when_iterations_overrun_the_must_finish_by()
    {
        // synth_500 finishes 2028-09-13 deterministically and later in most iterations.
        var s = Build("synth_500.xer", "SYN500", "2027-06-30 17:00");
        var m = RiskModelLoader.LoadJson(s, File.ReadAllText(TestData.PathOf("synth_500.risk.json")), new CpmEngine(s).CriticalToProjectFinish());
        var one = new MonteCarloEngine(s, m).Run(200, 5);
        var two = new MonteCarloEngine(s, m).Run(200, 5);
        Assert.Equal(200, one.Iterations);
        Assert.Equal(one.Finish, two.Finish);
        Assert.Equal(one.CriticalCount, two.CriticalCount);
    }

    /// <summary>synth_500 with the example risk model, whose "critical" filter comes from the engine as in the CLI and web app.</summary>
    private static SimulationResult Simulate(Schedule s)
    {
        var crit = new CpmEngine(s).CriticalToProjectFinish();
        var m = RiskModelLoader.LoadJson(s, File.ReadAllText(TestData.PathOf("synth_500.risk.json")), crit);
        return new MonteCarloEngine(s, m).Run(200, 5);
    }

    [Theory]
    [InlineData("2030-06-28 17:00")] // after every iteration's finish
    [InlineData("2029-03-15 17:00")] // about the P50 finish
    [InlineData("2028-06-30 17:00")] // before the deterministic finish (2028-09-13)
    public void Must_finish_by_does_not_change_the_simulation(string date)
    {
        // A deadline changes P6's float, not when the project finishes or what drives the finish.
        var none = Simulate(TestData.Load("synth_500.xer"));
        var mfb = Simulate(Build("synth_500.xer", "SYN500", date));
        Assert.Equal(none.Finish, mfb.Finish);
        Assert.Equal(none.CriticalCount, mfb.CriticalCount);
        Assert.Equal(none.Milestones.Count, mfb.Milestones.Count);
        foreach (var (j, finishes) in none.Milestones) Assert.Equal(finishes, mfb.Milestones[j]);
    }

    [Theory]
    [InlineData("2030-06-28 17:00", 0)]   // P6: every activity has positive float
    [InlineData("2028-06-30 17:00", 315)] // P6: every path within the overrun has negative float
    public void Critical_filter_does_not_depend_on_the_must_finish_by(string date, int p6Critical)
    {
        var path = new CpmEngine(TestData.Load("synth_500.xer")).CriticalToProjectFinish();
        Assert.Equal(6, path.Count(c => c));
        var s = Build("synth_500.xer", "SYN500", date);
        var det = new CpmEngine(s).Run();
        Assert.Equal(p6Critical, s.Activities.Count(a => det.IsCritical(s, a.Index))); // P6's float is unchanged
        Assert.Equal(path, new CpmEngine(s).CriticalToProjectFinish());
    }

    private static SimulationSummary Summarize(Schedule s)
    {
        var m = RiskModelLoader.LoadJson(s, File.ReadAllText(TestData.PathOf("synth_500.risk.json")), new CpmEngine(s).CriticalToProjectFinish());
        var mc = new MonteCarloEngine(s, m);
        return SimulationSummary.Build(mc, mc.Run(200, 5));
    }

    [Fact]
    public void Summary_gives_the_chance_of_meeting_the_must_finish_by()
    {
        var sum = Summarize(Build("synth_500.xer", "SYN500", "2029-03-15 17:00"));
        long mfb = Time.ParseP6("2029-03-15 17:00");
        Assert.Equal(mfb, sum.MustFinishBy);
        double expected = (double)sum.SortedFinish.Count(f => f <= mfb) / sum.Iterations;
        Assert.Equal(expected, sum.ProbMeetMustFinishBy);
        Assert.InRange(expected, 0.05, 0.95); // near the P50 finish
        var json = JsonDocument.Parse(sum.ToJson()).RootElement;
        Assert.Equal("2029-03-15 17:00", json.GetProperty("must_finish_by").GetString());
        Assert.Equal(expected, json.GetProperty("prob_meet_must_finish_by").GetDouble());
    }

    [Fact]
    public void Must_finish_by_at_midnight_counts_finishes_up_to_the_end_of_the_previous_day()
    {
        var none = Summarize(TestData.Load("synth_500.xer"));
        long day = none.FinishPercentiles[50] / Time.MinutesPerDay * Time.MinutesPerDay; // midnight starting the P50 day
        var atStart = Summarize(Build("synth_500.xer", "SYN500", Time.Format(day)));
        var atEnd = Summarize(Build("synth_500.xer", "SYN500", Time.Format(day + Time.MinutesPerDay)));
        int onTheDay = none.SortedFinish.Count(f => f >= day && f < day + Time.MinutesPerDay);
        Assert.True(onTheDay > 0);
        Assert.Equal((double)none.SortedFinish.Count(f => f < day) / none.Iterations, atStart.ProbMeetMustFinishBy);
        Assert.Equal((double)onTheDay / none.Iterations, atEnd.ProbMeetMustFinishBy!.Value - atStart.ProbMeetMustFinishBy!.Value, 9);
    }

    [Fact]
    public void Without_a_must_finish_by_the_summary_and_report_are_unchanged()
    {
        var s = TestData.Load("synth_500.xer");
        var sum = Summarize(s);
        Assert.Equal(Time.None, sum.MustFinishBy);
        Assert.Null(sum.ProbMeetMustFinishBy);
        Assert.DoesNotContain("must_finish_by", sum.ToJson());
        Assert.DoesNotContain("Must Finish By", HtmlReport.Build(s, sum));
        Assert.DoesNotContain("Must Finish By", HtmlReport.SCurve(sum, null, s, interactive: true, percentile: 80));
    }

    [Fact]
    public void Report_and_chart_show_the_must_finish_by()
    {
        var s = Build("synth_500.xer", "SYN500", "2029-03-15 17:00");
        var sum = Summarize(s);
        string report = HtmlReport.Build(s, sum);
        Assert.Contains($"{sum.ProbMeetMustFinishBy!.Value * 100:F0}%", report);
        Assert.Contains("Chance of meeting the Must Finish By (15-Mar-2029)", report);
        Assert.Contains("class=\"mline\"", report);                            // the static report draws the line
        string chart = HtmlReport.SCurve(sum, null, s, interactive: true, percentile: 80);
        Assert.Contains("class=\"mline\"", chart);
        Assert.Contains(">Must Finish By 15-Mar-2029</text>", chart);
        Assert.Contains($"\"mfb\":{sum.MustFinishBy}", WebUtility.HtmlDecode(chart)); // for the hover readout
    }

    [Fact]
    public void A_must_finish_by_far_off_the_chart_is_labelled_at_its_edge()
    {
        // synth_500 finishes 2028-2030; a deadline in 2040 would squash the curve, so it is named at the edge.
        var s = Build("synth_500.xer", "SYN500", "2040-06-29 17:00");
        var sum = Summarize(s);
        Assert.Equal(1.0, sum.ProbMeetMustFinishBy);
        string chart = HtmlReport.SCurve(sum, null, s, interactive: true, percentile: 80);
        Assert.DoesNotContain("class=\"mline\"", chart);
        Assert.Contains(">Must Finish By 29-Jun-2040 →</text>", chart);
        Assert.Contains("Must Finish By 29-Jun-2040, off the chart", HtmlReport.Build(s, sum));
    }

    [Fact]
    public void Results_do_not_depend_on_the_calendar_horizon()
    {
        // With a must-finish-by the horizon start depends on its end, so this moves both ends.
        var a = Build("synth_500.xer", "SYN500", "2027-06-30 17:00", horizonYears: 30);
        var b = Build("synth_500.xer", "SYN500", "2027-06-30 17:00", horizonYears: 60);
        Assert.NotEqual(a.Cal24.HorizonStart, b.Cal24.HorizonStart);
        var ra = new CpmEngine(a).Run();
        var rb = new CpmEngine(b).Run();
        Assert.Equal(ra.ES, rb.ES);
        Assert.Equal(ra.EF, rb.EF);
        Assert.Equal(ra.LS, rb.LS);
        Assert.Equal(ra.LF, rb.LF);
        Assert.Equal(ra.TF, rb.TF);
    }
}
