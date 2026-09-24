using System.Text.Json;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
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
