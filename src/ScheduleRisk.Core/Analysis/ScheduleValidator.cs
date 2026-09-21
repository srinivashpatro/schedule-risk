using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Core.Analysis;

public sealed record ValidationCheck(string Key, string Title, int Count, int Total, double? ThresholdPct,
                                     IReadOnlyList<string> Items, string Note = "")
{
    public double Pct => Total > 0 ? 100.0 * Count / Total : 0.0;

    public bool Passed => ThresholdPct == null || (ThresholdPct == 0 ? Count == 0 : Pct <= ThresholdPct);
}

/// <summary>Schedule health checks (DCMA-14 style) run before trusting a schedule. Twin of validate.py.</summary>
public static class ScheduleValidator
{
    private static readonly string[] Hard = { "CS_MSOB", "CS_MEOB", "CS_MSO", "CS_MEO", "CS_MANDSTART", "CS_MANDFIN" };

    public static List<ValidationCheck> Validate(Schedule s, CpmResult? res = null, int highFloatDays = 44, int highDurationDays = 44)
    {
        var acts = s.Activities.Where(a => a.Status != ActivityStatus.Complete && !a.IsSummary).ToList();
        int n = acts.Count;
        var rels = s.Relationships.Where(r => r.Pred >= 0).ToList();
        var live = rels.Where(r => s.Activities[r.Succ].Status != ActivityStatus.Complete).ToList();
        string Link(Relationship r) => $"{s.Activities[r.Pred].Code}->{s.Activities[r.Succ].Code}";
        var checks = new List<ValidationCheck>();

        var noPred = acts.Where(a => s.Preds[a.Index].Count == 0).Select(a => a.Code).ToList();
        var noSucc = acts.Where(a => s.Succs[a.Index].Count == 0).Select(a => a.Code).ToList();
        var missing = noPred.Union(noSucc).OrderBy(x => x, StringComparer.Ordinal).ToList();
        checks.Add(new ValidationCheck("logic", "Missing predecessor or successor", missing.Count, n, 5.0, missing,
            $"{noPred.Count} without predecessor, {noSucc.Count} without successor (open ends)"));

        var leads = live.Where(r => r.Lag < 0).Select(Link).ToList();
        checks.Add(new ValidationCheck("leads", "Negative lags (leads)", leads.Count, live.Count, 0, leads));
        var lags = live.Where(r => r.Lag > 0).Select(Link).ToList();
        checks.Add(new ValidationCheck("lags", "Positive lags", lags.Count, live.Count, 5.0, lags));
        var nonFs = live.Where(r => r.Type != RelType.FS).Select(Link).ToList();
        checks.Add(new ValidationCheck("rel_types", "Non finish-to-start relationships", nonFs.Count, live.Count, 10.0, nonFs));

        bool IsHard(Constraint? c) => c != null && Hard.Contains(c.Value.Type);
        var hard = acts.Where(a => IsHard(a.Constraint1) || IsHard(a.Constraint2)).Select(a => a.Code).ToList();
        checks.Add(new ValidationCheck("hard_constraints", "Hard constraints", hard.Count, n, 5.0, hard));

        var unknown = new List<string>();
        foreach (var a in acts)
            foreach (var c in new[] { a.Constraint1, a.Constraint2 })
                if (c != null && !CpmEngine.KnownConstraints.Contains(c.Value.Type)) unknown.Add($"{a.Code}:{c.Value.Type}");
        checks.Add(new ValidationCheck("unsupported_constraints", "Constraints not modelled (e.g. ALAP)", unknown.Count, n, 0, unknown));

        var hd = acts.Where(a => a.RemainingDuration > (long)highDurationDays * a.Calendar.MinutesPerDay).Select(a => a.Code).ToList();
        checks.Add(new ValidationCheck("high_duration", $"Remaining duration > {highDurationDays} days", hd.Count, n, 5.0, hd));

        if (res == null)
        {
            try { res = new CpmEngine(s).Run(); }
            catch (Exception e) when (e is ScheduleLoopException || e is CalendarHorizonException)
            {
                checks.Add(new ValidationCheck("cpm", "CPM calculation", 1, 1, 0, new[] { e.Message }));
                return checks;
            }
        }
        var tfArr = res.TF;
        var hf = acts.Where(a => tfArr[a.Index] != Time.None && tfArr[a.Index] > (long)highFloatDays * a.Calendar.MinutesPerDay).Select(a => a.Code).ToList();
        checks.Add(new ValidationCheck("high_float", $"Total float > {highFloatDays} days", hf.Count, n, 5.0, hf));
        var nf = acts.Where(a => tfArr[a.Index] != Time.None && tfArr[a.Index] < 0).Select(a => a.Code).ToList();
        checks.Add(new ValidationCheck("negative_float", "Negative float", nf.Count, n, 0, nf));

        long dd = s.Settings.DataDate;
        var bad = new List<string>();
        foreach (var a in s.Activities)
        {
            if (a.ActualStart != Time.None && a.ActualStart > dd) bad.Add($"{a.Code}: actual start after data date");
            if (a.ActualFinish != Time.None && a.ActualFinish > dd) bad.Add($"{a.Code}: actual finish after data date");
        }
        checks.Add(new ValidationCheck("invalid_dates", "Actual dates after the data date", bad.Count, s.Activities.Count, 0, bad));

        var summ = s.Activities.Where(a => a.IsSummary).Select(a => a.Code).ToList();
        checks.Add(new ValidationCheck("summaries", "LOE / WBS summary activities (non-driving)", summ.Count, s.Activities.Count, null, summ));

        // Critical path test: delay the most critical open activity by 600 days; the finish must move.
        var cands = acts.Where(a => tfArr[a.Index] != Time.None && a.RemainingDuration > 0).ToList();
        if (cands.Count > 0)
        {
            var pick = cands.OrderBy(a => tfArr[a.Index]).ThenBy(a => a.Index).First();
            var dur = s.Activities.Select(a => a.RemainingDuration).ToArray();
            dur[pick.Index] += 600L * pick.Calendar.MinutesPerDay;
            var pcal = s.Settings.ProjectCalendar;
            try
            {
                var r2 = new CpmEngine(s).Run(dur, backward: false);
                long moved = pcal.WorkBetween(res.ProjectFinish, r2.ProjectFinish);
                bool ok = moved > 0;
                checks.Add(new ValidationCheck("cp_test", "Critical path test (finish responds to delay)", ok ? 0 : 1, 1, 0,
                    ok ? Array.Empty<string>() : new[] { pick.Code },
                    $"delaying {pick.Code} by 600d moved the finish by {(double)moved / pcal.MinutesPerDay:F0}d"));
            }
            catch (CalendarHorizonException)
            {
                checks.Add(new ValidationCheck("cp_test", "Critical path test", 0, 1, null, Array.Empty<string>(), "skipped (horizon)"));
            }
        }
        return checks;
    }
}
