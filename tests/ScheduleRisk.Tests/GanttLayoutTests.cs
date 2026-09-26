using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Reporting;
using static ScheduleRisk.Core.Reporting.GanttBarKind;

namespace ScheduleRisk.Tests;

/// <summary>Edits the text of an XER file table by table, for fixtures that differ from a stored one in a few fields.</summary>
internal sealed class XerEdit
{
    private readonly List<string> _lines;

    public XerEdit(string text) => _lines = text.Replace("\r\n", "\n").Split('\n').ToList();

    public static XerEdit Of(string xerName) => new(File.ReadAllText(TestData.PathOf(xerName)));

    /// <summary>Sets <paramref name="field"/> on every matching row of <paramref name="table"/>, adding the column when it is missing.</summary>
    public XerEdit Set(string table, Func<IReadOnlyDictionary<string, string>, bool> match, string field, string value)
    {
        string current = "";
        List<string>? fields = null;
        for (int i = 0; i < _lines.Count; i++)
        {
            var cells = _lines[i].Split('\t');
            if (cells[0] == "%T") { current = cells[1]; fields = null; continue; }
            if (current != table) continue;
            if (cells[0] == "%F")
            {
                fields = cells.ToList();
                if (!fields.Contains(field)) { fields.Add(field); _lines[i] = string.Join('\t', fields); }
            }
            else if (cells[0] == "%R" && fields != null)
            {
                var row = cells.ToList();
                while (row.Count < fields.Count) row.Add("");
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int k = 1; k < fields.Count; k++) values[fields[k]] = row[k];
                if (match(values)) row[fields.IndexOf(field)] = value;
                _lines[i] = string.Join('\t', row);
            }
        }
        return this;
    }

    public (Schedule S, CpmResult R) Run()
    {
        var s = TestData.LoadText(string.Join('\n', _lines));
        return (s, new CpmEngine(s).Run());
    }
}

public class GanttLayoutTests
{
    private static GanttRow RowOf(GanttModel m, Activity a) => m.Rows.Single(x => x.Kind == GanttRowKind.Activity && x.Activity == a.Index);

    private static List<GanttRow> Activities(GanttModel m) => m.Rows.Where(x => x.Kind == GanttRowKind.Activity).ToList();

    [Fact]
    public void Dates_are_shown_as_in_p6()
    {
        Assert.Equal("02-Mar-26", GanttLayout.Date(Time.ParseP6("2026-03-02 08:00")));
        Assert.Equal("5", GanttLayout.Days(2400, 480));
        Assert.Equal("2.5", GanttLayout.Days(1200, 480));
        Assert.Equal("-3", GanttLayout.Days(-1440, 480));
        Assert.Equal("0", GanttLayout.Days(0, 480));
    }

    [Fact]
    public void Complete_work_is_one_actual_bar_with_actual_dates_and_no_float()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        var a = s.Activities.First(x => x.Status == ActivityStatus.Complete && !x.IsMilestone);
        var row = RowOf(m, a);
        Assert.Equal(new[] { new GanttBar(Actual, a.ActualStart, a.ActualFinish) }, row.Bars);
        Assert.Equal(GanttLayout.Date(a.ActualStart) + " A", row.Start);
        Assert.Equal(GanttLayout.Date(a.ActualFinish) + " A", row.Finish);
        Assert.Equal("0", row.RemainingDuration);
        Assert.Equal("", row.TotalFloat);
        Assert.False(row.Critical);
    }

    [Fact]
    public void Work_in_progress_is_actual_to_the_data_date_then_remaining_from_the_remaining_start()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        var a = s.Activities.First(x => x.Status == ActivityStatus.InProgress);
        int j = a.Index;
        var row = RowOf(m, a);
        var rest = r.IsCritical(s, j) ? Critical : Remaining;
        var expected = new List<GanttBar> { new(Actual, a.ActualStart, s.Settings.DataDate), new(rest, r.RS[j], r.EF[j]) };
        if (r.TF[j] > 0) expected.Add(new GanttBar(Float, r.EF[j], r.LF[j]));
        Assert.Equal(expected, row.Bars);
        Assert.Equal(GanttLayout.Date(a.ActualStart) + " A", row.Start);
        Assert.Equal(GanttLayout.Date(r.EF[j]), row.Finish);
    }

    [Fact]
    public void Work_not_started_is_remaining_with_a_float_line_to_its_late_finish()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        var a = s.Activities.First(x => x.Status == ActivityStatus.NotStarted && !x.IsMilestone && r.TF[x.Index] > 0);
        int j = a.Index;
        var row = RowOf(m, a);
        Assert.Equal(new[] { new GanttBar(Remaining, r.ES[j], r.EF[j]), new GanttBar(Float, r.EF[j], r.LF[j]) }, row.Bars);
        Assert.Equal(GanttLayout.Days(r.TF[j], a.Calendar.MinutesPerDay), row.TotalFloat);
        Assert.Equal(GanttLayout.Date(r.ES[j]), row.Start);
        Assert.False(row.Critical);
    }

    [Fact]
    public void Critical_work_is_drawn_as_critical_remaining()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        var a = s.Activities.First(x => x.Status == ActivityStatus.NotStarted && !x.IsMilestone && r.IsCritical(s, x.Index));
        var row = RowOf(m, a);
        Assert.True(row.Critical);
        Assert.Equal(new[] { new GanttBar(Critical, r.ES[a.Index], r.EF[a.Index]) }, row.Bars);
    }

    [Fact]
    public void Milestones_are_diamonds_with_one_date()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        var start = s.Activities.First(x => x.Type == ActivityType.StartMilestone && x.Status == ActivityStatus.NotStarted);
        var finish = s.Activities.First(x => x.Type == ActivityType.FinishMilestone);
        var done = s.Activities.First(x => x.Type == ActivityType.StartMilestone && x.Status == ActivityStatus.Complete);

        var rs = RowOf(m, start);
        Assert.Equal(r.ES[start.Index], rs.Milestone);
        Assert.DoesNotContain(rs.Bars, b => b.Kind != Float);
        Assert.Equal(GanttLayout.Date(r.ES[start.Index]), rs.Start);
        Assert.Equal("", rs.Finish);

        var rf = RowOf(m, finish);
        Assert.Equal(r.EF[finish.Index], rf.Milestone);
        Assert.Equal("", rf.Start);
        Assert.Equal(GanttLayout.Date(r.EF[finish.Index]), rf.Finish);
        Assert.Equal(r.IsCritical(s, finish.Index) ? Critical : Remaining, rf.MilestoneKind);

        var rd = RowOf(m, done);
        Assert.Equal(done.ActualStart, rd.Milestone);
        Assert.Equal(Actual, rd.MilestoneKind);
        Assert.Equal(GanttLayout.Date(done.ActualStart) + " A", rd.Start);
    }

    [Fact]
    public void Durations_are_in_days_of_each_activity_calendar()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        foreach (var a in s.Activities.Where(x => x.Calendar.MinutesPerDay != 480).Take(5))
            Assert.Equal(GanttLayout.Days(a.OriginalDuration, a.Calendar.MinutesPerDay), RowOf(m, a).OriginalDuration);
        Assert.Contains(s.Activities, x => x.Calendar.MinutesPerDay != 480);
    }

    [Fact]
    public void Negative_float_has_no_float_line()
    {
        var (s0, r0) = TestData.LoadAndRun("hand_basic.xer");
        string early = Time.Format(r0.ProjectFinish - 3 * Time.MinutesPerDay);
        var (s, r) = XerEdit.Of("hand_basic.xer").Set("PROJECT", _ => true, "plan_end_date", early).Run();
        var m = GanttLayout.Build(s, r);
        var late = Activities(m).Where(x => x.NegativeFloat).ToList();
        Assert.NotEmpty(late);
        Assert.All(late, x => Assert.DoesNotContain(x.Bars, b => b.Kind == Float));
        Assert.All(late, x => Assert.StartsWith("-", x.TotalFloat));
    }

    [Fact]
    public void Wbs_bands_follow_the_file_order_without_seq_num()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var bands = GanttLayout.Build(s, r).Rows.Where(x => x.Kind == GanttRowKind.Wbs).ToList();
        Assert.Equal(new[] { "SYN500", "ENG", "PROC", "CIV", "MECH", "ELEC", "COMM" }, bands.Select(b => b.Id));
        Assert.Equal(new[] { 0, 1, 1, 1, 1, 1, 1 }, bands.Select(b => b.Depth));
    }

    [Fact]
    public void Wbs_bands_follow_p6_seq_num()
    {
        string[] order = { "COMM", "ELEC", "MECH", "CIV", "PROC", "ENG" };
        var edit = XerEdit.Of("synth_500.xer");
        for (int i = 0; i < order.Length; i++)
        {
            string code = order[i];
            edit.Set("PROJWBS", w => w["wbs_short_name"] == code, "seq_num", (10 * (i + 1)).ToString());
        }
        var (s, r) = edit.Run();
        Assert.Equal(60, s.Wbs.Values.Single(w => w.ShortName == "ENG").Seq);
        var bands = GanttLayout.Build(s, r).Rows.Where(x => x.Kind == GanttRowKind.Wbs).Select(b => b.Id);
        Assert.Equal(new[] { "SYN500" }.Concat(order), bands);
    }

    [Fact]
    public void A_wbs_band_spans_its_activities_and_rolls_up_their_dates()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        int i = m.Rows.FindIndex(x => x.Kind == GanttRowKind.Wbs && x.Id == "PROC");
        var band = m.Rows[i];
        var children = m.Rows.Skip(i + 1).TakeWhile(x => x.Kind == GanttRowKind.Activity).ToList();
        Assert.NotEmpty(children);
        Assert.All(children, c => Assert.Equal(band.Depth + 1, c.Depth));
        Assert.Equal(new[] { new GanttBar(Summary, children.Min(c => c.From), children.Max(c => c.To)) }, band.Bars);
        Assert.EndsWith(" A", band.Start);       // procurement started before the data date
        Assert.DoesNotContain(" A", band.Finish); // and is not finished
        var eng = m.Rows.Single(x => x.Kind == GanttRowKind.Wbs && x.Id == "ENG");
        Assert.EndsWith(" A", eng.Finish);        // every engineering activity is complete

        var root = m.Rows[0];
        Assert.Equal("SYN500", root.Id);
        Assert.Equal(Activities(m).Min(c => c.From), root.Bars[0].From);
        Assert.Equal(Activities(m).Max(c => c.To), root.Bars[0].To);
    }

    [Fact]
    public void Activities_in_a_band_are_sorted_by_start_then_id()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        var run = new List<GanttRow>();
        void Check()
        {
            for (int k = 1; k < run.Count; k++)
            {
                int c = run[k - 1].SortKey.CompareTo(run[k].SortKey);
                Assert.True(c < 0 || (c == 0 && string.CompareOrdinal(run[k - 1].Id, run[k].Id) < 0), $"{run[k - 1].Id} before {run[k].Id}");
            }
            run.Clear();
        }
        foreach (var row in m.Rows)
        {
            if (row.Kind == GanttRowKind.Wbs) Check();
            else run.Add(row);
        }
        Check();
    }

    [Fact]
    public void A_collapsed_band_hides_its_activities_but_keeps_its_bar()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var open = GanttLayout.Build(s, r);
        var eng = open.Rows.Single(x => x.Id == "ENG");
        var options = new GanttOptions();
        options.Collapsed.Add(eng.Key);
        var shut = GanttLayout.Build(s, r, options);
        int i = shut.Rows.FindIndex(x => x.Key == eng.Key);
        Assert.True(shut.Rows[i].Collapsed);
        Assert.Equal("PROC", shut.Rows[i + 1].Id);
        Assert.Equal(eng.Bars, shut.Rows[i].Bars);
        Assert.Equal(open.ActivitiesMatched, shut.ActivitiesMatched);
        Assert.True(shut.Rows.Count < open.Rows.Count);
    }

    [Fact]
    public void Critical_only_keeps_critical_activities_and_their_bands()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r, new GanttOptions { CriticalOnly = true });
        int critical = s.Activities.Count(a => r.IsCritical(s, a.Index));
        Assert.Equal(critical, Activities(m).Count);
        Assert.Equal(critical, m.ActivitiesMatched);
        Assert.All(Activities(m), x => Assert.True(x.Critical));
        // every band shown holds at least one of them
        for (int i = 0; i < m.Rows.Count; i++)
            if (m.Rows[i].Kind == GanttRowKind.Wbs)
                Assert.True(i + 1 < m.Rows.Count && m.Rows[i + 1].Depth > m.Rows[i].Depth, $"empty band {m.Rows[i].Id}");
    }

    [Fact]
    public void Search_matches_id_or_name_ignoring_case()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var byId = GanttLayout.Build(s, r, new GanttOptions { Search = "a00020" });
        Assert.Equal(new[] { "A00020" }, Activities(byId).Select(x => x.Id));
        var byName = GanttLayout.Build(s, r, new GanttOptions { Search = "eng ACTIVITY 1" });
        Assert.All(Activities(byName), x => Assert.StartsWith("ENG activity 1", x.Name));
        Assert.True(Activities(byName).Count > 1);
    }

    [Fact]
    public void Without_grouping_rows_are_flat_and_sorted_by_start_then_id()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r, new GanttOptions { GroupByWbs = false });
        Assert.Equal(s.Activities.Count, m.Rows.Count);
        Assert.All(m.Rows, x => Assert.Equal(0, x.Depth));
        Assert.Equal(m.Rows.OrderBy(x => x.SortKey).ThenBy(x => x.Id, StringComparer.Ordinal).Select(x => x.Key), m.Rows.Select(x => x.Key));
    }

    [Fact]
    public void Timeline_runs_from_the_first_of_the_month_to_past_the_last_date()
    {
        var (s, r) = TestData.LoadAndRun("synth_500.xer");
        var m = GanttLayout.Build(s, r);
        Assert.Equal("2026-03-01 00:00", Time.Format(m.Start)); // earliest actual start 02-Mar-26
        Assert.Equal("2028-10-01 00:00", Time.Format(m.End));   // latest late finish 14-Sep-28, plus room for its label
        Assert.Equal(s.Settings.DataDate, m.DataDate);
        Assert.Equal(800 / m.Days, GanttLayout.FitPixelsPerDay(m, 800), 9);
    }

    [Fact]
    public void Timescale_tiers_follow_the_zoom()
    {
        long a = Time.ParseP6("2026-01-15 00:00"), b = Time.ParseP6("2026-04-10 00:00");
        Assert.Equal("Week / Day", GanttLayout.Timescale(a, b, 20).Name);
        Assert.Equal("Month / Week", GanttLayout.Timescale(a, b, 4).Name);
        Assert.Equal("Quarter / Month", GanttLayout.Timescale(a, b, 1.2).Name);
        Assert.Equal("Year / Quarter", GanttLayout.Timescale(a, b, 0.4).Name);
        Assert.Equal("Decade / Year", GanttLayout.Timescale(a, b, 0.1).Name);
    }

    [Fact]
    public void Timescale_ticks_are_calendar_periods_clipped_to_the_timeline()
    {
        long a = Time.ParseP6("2026-01-15 00:00"), b = Time.ParseP6("2026-04-10 00:00");
        long T(string d) => Time.ParseP6(d + " 00:00");
        var qm = GanttLayout.Timescale(a, b, 1.2);
        Assert.Equal(new[] { new GanttTick(a, T("2026-04-01"), "Q1 2026"), new GanttTick(T("2026-04-01"), b, "Q2 2026") }, qm.Top);
        Assert.Equal(new[] { "Jan", "Feb", "Mar", "Apr" }, qm.Bottom.Select(t => t.Label));
        Assert.Equal(T("2026-02-01"), qm.Bottom[1].From);

        var mw = GanttLayout.Timescale(a, b, 4);
        Assert.Equal("Jan 2026", mw.Top[0].Label);
        Assert.Equal(new GanttTick(a, T("2026-01-19"), "12"), mw.Bottom[0]);        // the week of Monday 12 Jan, clipped
        Assert.Equal(new GanttTick(T("2026-01-19"), T("2026-01-26"), "19"), mw.Bottom[1]);

        var wd = GanttLayout.Timescale(a, T("2026-01-20"), 20);
        Assert.Equal(new[] { "T", "F", "S", "S", "M" }, wd.Bottom.Select(t => t.Label)); // Thu 15 to Mon 19
        Assert.Equal("12-Jan-26", wd.Top[0].Label);

        var dy = GanttLayout.Timescale(T("2026-03-01"), T("2047-03-01"), 0.1);
        Assert.Equal(new[] { "2020s", "2030s", "2040s" }, dy.Top.Select(t => t.Label));
        Assert.Equal("2026", dy.Bottom[0].Label);
        Assert.Equal(22, dy.Bottom.Count);
        // years too narrow for four digits (under ~34 px) are labelled '26
        Assert.Equal("’26", GanttLayout.Timescale(T("2026-03-01"), T("2047-03-01"), 0.05).Bottom[0].Label);
    }

    [Fact]
    public void Every_activity_of_a_large_schedule_is_placed_once()
    {
        var (s, r) = TestData.LoadAndRun("synth_5000.xer");
        var m = GanttLayout.Build(s, r);
        Assert.Equal(s.Activities.Count, Activities(m).Select(x => x.Activity).Distinct().Count());
        Assert.Equal(s.Activities.Count, Activities(m).Count);
        Assert.Equal(s.Wbs.Count, m.Rows.Count(x => x.Kind == GanttRowKind.Wbs));
        Assert.True(m.Days > 365 * 20);
    }
}
