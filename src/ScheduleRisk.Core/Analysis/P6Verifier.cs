using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Core.Analysis;

public sealed record DateDiff(string Code, string Field, long P6, long Ours, long DeltaMinutes);

public sealed class VerifyReport
{
    public int Compared { get; set; }
    public int FieldsCompared { get; set; }
    public int FieldsMatched { get; set; }
    public int ActivitiesMatched { get; set; }
    public int SkippedNoP6 { get; set; }
    public List<DateDiff> Diffs { get; } = new();

    public bool Passed => Compared > 0 && Diffs.Count == 0;

    public IEnumerable<DateDiff> Worst(int n = 50) => Diffs.OrderByDescending(d => Math.Abs(d.DeltaMinutes)).Take(n);
}

/// <summary>
/// Differential check of our CPM dates against the dates P6 stored in the XER. Dates are compared
/// in working time on the activity calendar (17:00 Mon == 08:00 Tue). Twin of verify.py.
/// </summary>
public static class P6Verifier
{
    public static VerifyReport Verify(Schedule s, CpmResult res, long toleranceMinutes = 0)
    {
        var rep = new VerifyReport();
        foreach (var a in s.Activities)
        {
            int j = a.Index;
            if (a.Status == ActivityStatus.Complete || a.IsSummary) continue;
            var cal = a.Calendar;
            var pairs = new List<(string Field, long P6, long Ours)>();
            long First(string k1, string k2) => a.P6(k1) != Time.None ? a.P6(k1) : a.P6(k2);
            long Late(long[] arr) => res.HasBackward ? arr[j] : Time.None;
            if (a.Status == ActivityStatus.InProgress)
            {
                pairs.Add(("remaining_start", a.P6("restart_date"), res.RS[j]));
                pairs.Add(("early_finish", First("reend_date", "early_end_date"), res.EF[j]));
                pairs.Add(("late_finish", First("rem_late_end_date", "late_end_date"), Late(res.LF)));
            }
            else
            {
                if (a.Type != ActivityType.FinishMilestone)
                {
                    pairs.Add(("early_start", a.P6("early_start_date"), res.ES[j]));
                    pairs.Add(("late_start", a.P6("late_start_date"), Late(res.LS)));
                }
                if (a.Type != ActivityType.StartMilestone)
                {
                    pairs.Add(("early_finish", a.P6("early_end_date"), res.EF[j]));
                    pairs.Add(("late_finish", a.P6("late_end_date"), Late(res.LF)));
                }
            }
            pairs.RemoveAll(p => p.P6 == Time.None || p.Ours == Time.None);
            double? tfP6 = a.P6TotalFloatHours;
            if (pairs.Count == 0 && tfP6 == null)
            {
                rep.SkippedNoP6++;
                continue;
            }
            rep.Compared++;
            bool ok = true;
            foreach (var (field, p6v, ours) in pairs)
            {
                rep.FieldsCompared++;
                long delta = cal.WorkAt(ours) - cal.WorkAt(p6v);
                if (Math.Abs(delta) <= toleranceMinutes) rep.FieldsMatched++;
                else
                {
                    ok = false;
                    rep.Diffs.Add(new DateDiff(a.Code, field, p6v, ours, delta));
                }
            }
            if (tfP6 != null && res.HasBackward && res.TF[j] != Time.None)
            {
                rep.FieldsCompared++;
                long p6m = (long)Math.Round(tfP6.Value * 60, MidpointRounding.ToEven);
                long delta = res.TF[j] - p6m;
                if (Math.Abs(delta) <= Math.Max(toleranceMinutes, 1)) rep.FieldsMatched++;
                else
                {
                    ok = false;
                    rep.Diffs.Add(new DateDiff(a.Code, "total_float", p6m, res.TF[j], delta));
                }
            }
            if (ok) rep.ActivitiesMatched++;
        }
        return rep;
    }
}
