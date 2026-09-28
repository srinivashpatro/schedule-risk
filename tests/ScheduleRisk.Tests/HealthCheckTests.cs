using System.Text.Json;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Tests;

/// <summary>
/// Step 04's health checks: P6 Professional's Check Schedule parameters and the DCMA 14-Point Assessment, as in the
/// owner's health-check report (the p6-schedule-health-check skill, whose health_check.py gave the expected counts
/// below for every fixture). Where the app deliberately differs it says why: float and durations come from the
/// recalculated schedule, DCMA #12 and #13 use the DCMA method on the engine rather than P6's driving_path_flag, and
/// Dangling Start leaves out started activities, as P6's own description says.
/// </summary>
public class HealthCheckTests
{
    private static HealthReport Run(string xer, HealthCheckSettings? settings = null)
    {
        var (s, r) = TestData.LoadAndRun(xer);
        return HealthCheck.Run(s, r, settings);
    }

    private static HealthReport Run((Schedule S, CpmResult R) sr, HealthCheckSettings? settings = null) => HealthCheck.Run(sr.S, sr.R, settings);

    // The skill's counts: P6 parameters in its order, then DCMA 1-11 (null = N/A). "S" = started schedule.
    public static IEnumerable<object[]> Oracle() => new[]
    {
        new object[] { "hand_basic.xer", "0 0 3 0 4 - - 1 0 0 0 0 0 - - 0 0", "0 0 3 4 0 0 0 0 0 - -" },
        new object[] { "hand_24h_lag.xer", "0 0 3 0 4 - - 1 0 0 0 0 0 - - 0 0", "0 0 3 4 0 0 0 0 0 - -" },
        new object[] { "hand_constraint.xer", "0 0 3 0 4 - - 1 0 0 0 0 0 - - 0 1", "0 0 3 4 0 0 0 0 0 - -" },
        new object[] { "hand_holiday.xer", "0 0 3 0 4 - - 1 0 0 0 0 0 - - 0 0", "0 0 3 4 0 0 0 0 0 - -" },
        new object[] { "parallel_1.xer", "0 0 0 0 2 - - 0 0 0 0 0 0 - - 0 0", "0 0 0 2 0 0 0 0 0 - -" },
        new object[] { "parallel_2.xer", "0 0 0 0 4 - - 0 0 0 0 0 0 - - 0 0", "0 0 0 4 0 0 0 0 0 - -" },
        new object[] { "synth_200.xer", "0 0 38 0 359 0 - 2 4 75 0 0 0 0 - 0 4", "0 0 38 359 0 75 0 0 0 - 0" },
        new object[] { "synth_500.xer", "0 0 90 0 873 0 - 6 12 154 0 7 0 0 - 0 14", "0 0 90 873 0 154 0 7 0 - 0" },
        new object[] { "synth_5000.xer", "0 0 934 0 8942 3 - 92 130 1847 0 75 0 0 - 0 90", "0 0 934 8942 0 1847 0 75 0 - 0" },
    };

    private static string Counts(IEnumerable<HealthItem> items) =>
        string.Join(' ', items.Select(i => i.Status == HealthStatus.NotApplicable ? "-" : i.Count?.ToString() ?? "-"));

    [Theory]
    [MemberData(nameof(Oracle))]
    public void Counts_match_the_owners_health_check_on_every_fixture(string xer, string p6, string dcma)
    {
        var h = Run(xer);
        Assert.Equal(p6, Counts(h.P6));
        Assert.Equal(dcma, Counts(h.Dcma.Take(11)));
    }

    [Fact]
    public void The_p6_parameters_are_the_check_schedule_dialogs_in_its_three_tabs()
    {
        var h = Run("synth_500.xer");
        Assert.Equal(new[] { "logic", "negative_lags", "positive_lags", "long_lags", "relationship_types", "out_of_sequence",
            "resources_cost", "dangling_start", "dangling_finish", "large_float", "negative_float", "large_durations",
            "invalid_progress_dates", "late_activities", "bei", "hard_constraints", "soft_constraints" }, h.P6.Select(i => i.Key));
        Assert.Equal(new[] { "Relationships and Assignments", "Dates and Durations", "Constraints" }, h.P6.Select(i => i.Section).Distinct());
        Assert.Equal(Enumerable.Range(1, 14).Select(n => n.ToString()), h.Dcma.Select(i => i.Number));
        Assert.Equal("Critical Path Length Index (CPLI)", h.Dcma[12].Label);
    }

    [Fact]
    public void The_two_backwards_operators_are_reported_as_configured_and_conventionally()
    {
        var h = Run("synth_500.xer");
        var pos = h.Item("positive_lags");
        Assert.Equal((">", 5.0), (pos.Operator, pos.Target));
        Assert.Equal(HealthStatus.Pass, pos.Status);                       // 8.9% > 5%, as configured
        Assert.Contains("'>'", pos.Note);
        Assert.Equal(HealthStatus.Fail, h.Dcma[2].Status);                // DCMA #3: < 5%
        var rel = h.Item("relationship_types");
        Assert.Equal(("<", 90.0), (rel.Operator, rel.Target));
        Assert.Equal(HealthStatus.Pass, rel.Status);                       // 86.4% < 90%, as configured
        Assert.Equal(HealthStatus.Fail, rel.StatusConventional);          // the majority should be FS: >= 90%
        Assert.Equal(873, rel.Breakdown!["PR_FS"]);
        Assert.Equal(HealthStatus.Fail, h.Dcma[3].Status);
    }

    [Fact]
    public void An_unstarted_schedule_without_assignments_gives_na_with_the_reason()
    {
        var h = Run("hand_basic.xer");
        Assert.True(h.Unstarted);
        Assert.False(h.HasResourceData);
        foreach (var key in new[] { "out_of_sequence", "late_activities", "bei", "resources_cost" })
        {
            Assert.Equal(HealthStatus.NotApplicable, h.Item(key).Status);
            Assert.False(string.IsNullOrWhiteSpace(h.Item(key).Note));
        }
        Assert.Equal(new[] { "10", "11", "14" }, h.Dcma.Where(i => i.Status == HealthStatus.NotApplicable).Select(i => i.Number));
    }

    [Fact]
    public void Flagged_items_name_the_activity_and_the_value_that_flagged_it()
    {
        var h = Run("synth_500.xer");
        var lf = h.Item("large_float");
        Assert.Equal(154, lf.Flagged.Count);
        Assert.All(lf.Flagged, f => Assert.True(f.Value > 352));
        Assert.Equal("total_float_hr", lf.Flagged[0].ValueKey);
        Assert.False(string.IsNullOrEmpty(lf.Flagged[0].Code));
        Assert.False(string.IsNullOrEmpty(lf.Flagged[0].Name));
        var lags = h.Item("positive_lags").Flagged;
        Assert.All(lags, f => { Assert.True(f.IsRelationship); Assert.True(f.LagHours > 0); Assert.StartsWith("PR_", f.PredType); });
        Assert.Same(h.Item("large_float").Flagged, h.Dcma[5].Flagged);    // shared list: printed once
        Assert.Equal("large_float", h.Dcma[5].FlagKey);
    }

    [Fact]
    public void Thresholds_and_targets_can_be_changed()
    {
        var settings = new HealthCheckSettings { LongLagHours = 0, LargeFloatHours = 10_000 };
        settings.P6Targets["large_float"] = ("<", 50);
        var h = Run("synth_500.xer", settings);
        Assert.Equal(90, h.Item("long_lags").Count);                      // every positive lag is now long
        Assert.Equal(0, h.Item("large_float").Count);
        Assert.Equal(("<", 50.0), (h.Item("large_float").Operator, h.Item("large_float").Target));
        Assert.Contains("0 h", h.Item("long_lags").Description);
    }

    [Fact]
    public void Resources_are_checked_when_the_file_has_assignments()
    {
        string xer = File.ReadAllText(TestData.PathOf("hand_basic.xer")).Replace("\r\n", "\n").TrimEnd('\n');
        var s0 = TestData.LoadText(xer);
        var task = s0.Activities.First(a => !a.IsMilestone);
        string table = $"%T\tTASKRSRC\n%F\ttaskrsrc_id\ttask_id\tproj_id\trsrc_id\n%R\t1\t{task.TaskId}\t{s0.ProjectId}\t9\n";
        int end = xer.LastIndexOf("%E", StringComparison.Ordinal);
        xer = end >= 0 ? xer[..end] + table + xer[end..] : xer + "\n" + table;       // before the end marker
        var s = TestData.LoadText(xer);
        var h = HealthCheck.Run(s, new CpmEngine(s).Run());
        Assert.True(h.HasResourceData);
        var res = h.Item("resources_cost");
        int workActivities = s.Activities.Count(a => !a.IsMilestone && !a.IsSummary);
        Assert.Equal(workActivities - 1, res.Count);
        Assert.DoesNotContain(res.Flagged, f => f.Code == task.Code);
        Assert.NotEqual(HealthStatus.NotApplicable, h.Dcma[9].Status);
    }

    [Fact]
    public void Constraints_are_split_into_hard_and_soft_with_their_dates()
    {
        var sr = XerEdit.Of("hand_basic.xer")
            .Set("TASK", t => t["task_code"] == "B", "cstr_type", "CS_MANDFIN")
            .Set("TASK", t => t["task_code"] == "B", "cstr_date", "2026-01-30 17:00")
            .Set("TASK", t => t["task_code"] == "C", "cstr_type", "CS_MSOA")
            .Set("TASK", t => t["task_code"] == "C", "cstr_date", "2026-01-12 08:00")
            .Run();
        var h = Run(sr);
        var hard = Assert.Single(h.Item("hard_constraints").Flagged);
        Assert.Equal(("B", "Mandatory Finish (2026-01-30)"), (hard.Code, hard.Constraint));
        var soft = Assert.Single(h.Item("soft_constraints").Flagged);
        Assert.Equal(("C", "Start On or After (2026-01-12)"), (soft.Code, soft.Constraint));
        Assert.Equal(1, h.Dcma[4].Count);
    }

    [Fact]
    public void Invalid_progress_dates_are_found_with_their_reasons()
    {
        var sr = XerEdit.Of("hand_basic.xer")
            .Set("TASK", t => t["task_code"] == "A", "act_start_date", "2026-03-02 08:00")   // after the data date
            .Set("TASK", t => t["task_code"] == "A", "status_code", "TK_Active")
            .Set("TASK", t => t["task_code"] == "B", "status_code", "TK_Complete")            // complete, no actual finish
            .Run();
        var h = Run(sr);
        var bad = h.Item("invalid_progress_dates").Flagged.ToDictionary(f => f.Code, f => f.Reasons!);
        Assert.Contains("actual start is after the data date", bad["A"]);
        Assert.Contains("marked complete with no actual finish date", bad["B"]);
        Assert.Equal(2, h.Dcma[8].Count);
        Assert.Equal(HealthStatus.Fail, h.Dcma[8].Status);
    }

    [Fact]
    public void Baseline_dates_give_late_activities_missed_activities_and_bei()
    {
        var s0 = TestData.Load("synth_200.xer");
        var done = s0.Activities.Where(a => a.Status == ActivityStatus.Complete && !a.IsSummary).Take(3).Select(a => a.Code).ToHashSet();
        var open = s0.Activities.First(a => a.Status == ActivityStatus.NotStarted && !a.IsSummary && !a.IsMilestone).Code;
        var edit = XerEdit.Of("synth_200.xer");
        // Done activities due by the data date (finished on time); one open activity due in January (late, missed).
        string dd = ScheduleRisk.Core.Calendars.Time.Format(s0.Settings.DataDate);
        foreach (var c in done) edit.Set("TASK", t => t["task_code"] == c, "target_end_date", dd);
        edit.Set("TASK", t => t["task_code"] == open, "target_end_date", "2026-01-05 17:00");     // due before the data date, not done
        var h = Run(edit.Run());
        var late = Assert.Single(h.Item("late_activities").Flagged);
        Assert.Equal(open, late.Code);
        Assert.Equal("2026-01-05 17:00", late.BaselineFinish);
        var bei = h.Item("bei");
        Assert.Equal((3, 4), (bei.Count, bei.Denominator));
        Assert.Equal(0.75, bei.Actual!.Value, 9);
        Assert.Equal(HealthStatus.Fail, bei.Status);
        Assert.Equal(HealthStatus.Fail, h.Dcma[13].Status);
        Assert.Equal(1, h.Dcma[10].Count);                                // missed activities
    }

    [Fact]
    public void Out_of_sequence_progress_is_found()
    {
        var s0 = TestData.Load("synth_200.xer");
        var rel = s0.Relationships.First(r => r.Pred >= 0 && r.Type == RelType.FS
            && s0.Activities[r.Pred].Status == ActivityStatus.Complete && s0.Activities[r.Succ].Status == ActivityStatus.NotStarted);
        string succ = s0.Activities[rel.Succ].Code;
        var early = ScheduleRisk.Core.Calendars.Time.Format(s0.Activities[rel.Pred].ActualFinish - 24 * 60);
        var h = Run(XerEdit.Of("synth_200.xer")
            .Set("TASK", t => t["task_code"] == succ, "act_start_date", early)
            .Set("TASK", t => t["task_code"] == succ, "status_code", "TK_Active")
            .Run());
        var oos = h.Item("out_of_sequence").Flagged;
        Assert.Contains(oos, f => f.Code == succ && f.Reason!.StartsWith("progressed before FS predecessor"));
    }

    [Fact]
    public void Dangling_start_leaves_out_started_activities()
    {
        var h = Run("synth_500.xer");
        var (s, _) = TestData.LoadAndRun("synth_500.xer");
        Assert.All(h.Item("dangling_start").Flagged, f => Assert.Equal(ActivityStatus.NotStarted, s.Find(f.Code).Status));
        Assert.All(h.Item("dangling_finish").Flagged, f =>
            Assert.DoesNotContain(s.Relationships, r => r.Pred == s.ByCode[f.Code] && r.Type is RelType.FS or RelType.FF));
    }

    [Fact]
    public void The_critical_path_test_delays_a_critical_activity_and_the_finish_follows()
    {
        var h = Run("synth_500.xer");
        var cpt = h.Dcma[11];
        Assert.Equal(HealthStatus.Pass, cpt.Status);                      // the skill fails it: no driving_path_flag in the file
        Assert.True(cpt.ActualBool);
        Assert.Contains("600 working days", cpt.Note);
        Assert.True(cpt.Count > 0);
    }

    [Fact]
    public void Cpli_is_one_without_a_must_finish_by_and_grows_with_float_to_it()
    {
        var h = Run("synth_500.xer");
        Assert.Equal(1.0, h.Dcma[12].Actual!.Value, 9);
        Assert.Equal(HealthStatus.Pass, h.Dcma[12].Status);
        var later = Run(XerEdit.Of("synth_500.xer").Set("PROJECT", _ => true, "plan_end_date", "2029-06-29 17:00").Run());
        Assert.True(later.Dcma[12].Actual > 1.0);
        var earlier = Run(XerEdit.Of("synth_500.xer").Set("PROJECT", _ => true, "plan_end_date", "2027-06-30 17:00").Run());
        Assert.True(earlier.Dcma[12].Actual < 0.95);
        Assert.Equal(HealthStatus.Fail, earlier.Dcma[12].Status);
    }

    [Fact]
    public void The_json_has_the_owners_report_schema()
    {
        var h = Run("synth_500.xer");
        using var doc = JsonDocument.Parse(h.ToJson("synth_500.xer"));
        var root = doc.RootElement;
        var meta = root.GetProperty("meta");
        Assert.Equal("SYN500", meta.GetProperty("project_short_name").GetString());
        Assert.Equal(500, meta.GetProperty("schedulable_activities").GetInt32());
        Assert.Equal(1010, meta.GetProperty("total_relationships").GetInt32());
        Assert.False(meta.GetProperty("has_resource_cost_data").GetBoolean());
        Assert.True(meta.GetProperty("task_type_breakdown").TryGetProperty("Task Dependent", out _));
        var p6 = root.GetProperty("p6_check_schedule");
        Assert.Equal(17, p6.GetArrayLength());
        var first = p6[0];
        foreach (var k in new[] { "section", "key", "label", "description", "count", "denominator", "actual", "unit", "operator",
                                  "target", "status", "note", "flag_key", "flagged", "flagged_total" })
            Assert.True(first.TryGetProperty(k, out _), k);
        var rel = p6.EnumerateArray().Single(x => x.GetProperty("key").GetString() == "relationship_types");
        Assert.Equal("FAIL", rel.GetProperty("status_conventional").GetString());
        var lag = p6.EnumerateArray().Single(x => x.GetProperty("key").GetString() == "positive_lags").GetProperty("flagged")[0];
        foreach (var k in new[] { "pred_task_code", "pred_task_name", "succ_task_code", "succ_task_name", "pred_type", "lag_hr" })
            Assert.True(lag.TryGetProperty(k, out _), k);
        var dcma = root.GetProperty("dcma_14_point");
        Assert.Equal(14, dcma.GetArrayLength());
        Assert.Equal("12", dcma[11].GetProperty("number").GetString());
        Assert.Equal(JsonValueKind.True, dcma[11].GetProperty("actual").ValueKind);
        Assert.Equal("N/A", dcma[13].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, dcma[13].GetProperty("actual").ValueKind);
    }

    [Fact]
    public void Unsupported_constraints_stay_as_an_app_note()
    {
        var h = Run(XerEdit.Of("hand_basic.xer").Set("TASK", t => t["task_code"] == "B", "cstr_type", "CS_ALAP").Run());
        Assert.Contains(h.AppNotes, n => n.Contains("B") && n.Contains("not modelled"));
        Assert.Empty(Run("hand_basic.xer").AppNotes);
    }
}
