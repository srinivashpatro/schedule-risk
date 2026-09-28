using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Core.Risk.Register;

/// <summary>The planned project duration: from the project start (earliest start in the deterministic schedule, actual
/// starts included) to the deterministic finish, in working days of the project calendar, as in the Results.</summary>
public sealed record PlannedDuration(long Start, long Finish, double WorkingDays);

/// <summary>A register risk worked out as a model risk. <see cref="Problem"/> says why it cannot be, when it cannot.</summary>
public sealed record QuantifiedRisk(double Probability, DistSpec Impact, bool Mitigated, double MitigatedProbability, DistSpec MitigatedImpact, string? Problem);

public sealed record SkippedRisk(string Id, string Reason);

public sealed class PromoteReport
{
    public List<string> Added { get; } = new();
    public List<string> Updated { get; } = new();
    public List<string> Removed { get; } = new();
    public List<SkippedRisk> Skipped { get; } = new();

    public override string ToString() =>
        $"{Added.Count} added, {Updated.Count} updated, {Removed.Count} removed, {Skipped.Count} skipped";
}

/// <summary>
/// Promote: approved risks that meet the register's promote rule on their current assessment become discrete risks
/// in the risk model, with the register's ids. The probability is the midpoint of the probability band; the impact
/// is a triangle of working days from the schedule band (low, middle and high of its % range) times the planned
/// duration; the current assessment is pre-mitigation and the target post-mitigation. Values set by hand in the
/// risk's <see cref="Promotion"/> replace the worked-out ones. An opportunity's days are negative (time saved).
/// </summary>
public static class RegisterPromoter
{
    public static PlannedDuration PlannedDuration(Schedule s, CpmResult cpm)
    {
        var acts = s.Activities;
        long start = Time.None;
        for (int j = 0; j < acts.Count; j++)
            if (!acts[j].IsSummary && cpm.ES[j] != Time.None && (start == Time.None || cpm.ES[j] < start)) start = cpm.ES[j];
        var pcal = s.Settings.ProjectCalendar;
        double days = start == Time.None ? 0 : (pcal.WorkAt(cpm.ProjectFinish) - pcal.WorkAt(start)) / (double)pcal.MinutesPerDay;
        return new PlannedDuration(start, cpm.ProjectFinish, Math.Round(days, 6, MidpointRounding.ToEven));
    }

    public static double Midpoint(MatrixSettings m, int probability) =>
        (m.Probability[probability].Min + m.Probability[probability].Max) / 2;

    /// <summary>Why a risk is not promoted, or null when it meets the rule. With <paramref name="honourAlways"/>, an approved
    /// risk marked <see cref="Promotion.Always"/> is promoted whatever its rating.</summary>
    public static string? Ineligibility(RiskRegister reg, RegisterRisk r, bool honourAlways = false)
    {
        var m = reg.Matrix;
        var rule = m.Promote;
        if (r.Status != RiskStatus.Approved) return $"only approved risks are promoted; it is {r.Status.ToString().ToLowerInvariant()}.";
        if (honourAlways && r.Promotion.Always) return null;
        if (m.Rate(r.Current) is not RiskRating rating) return "it has no current assessment.";
        if (rating < rule.MinRating)
            return $"it is {rating} today; the rule promotes {(rule.MinRating == RiskRating.Red ? "Red" : "Red and Amber")} risks.";
        if (!r.Current.Severity.TryGetValue(rule.ScheduleDimension, out int level))
            return "it has no schedule severity, so it cannot delay the finish.";
        if (level < rule.MinScheduleSeverity)
            return $"its schedule severity {MatrixSettings.Roman(level)} is below {MatrixSettings.Roman(rule.MinScheduleSeverity)}.";
        return null;
    }

    public static QuantifiedRisk Quantify(MatrixSettings m, RegisterRisk r, double plannedDays)
    {
        var none = new DistSpec { Distribution = "triangle" };
        QuantifiedRisk Fail(string why) => new(0, none, false, 0, none, why);
        var dim = m.Dimension(m.Promote.ScheduleDimension);
        var p6 = r.Promotion;
        int p = r.Current.Probability is int cp && cp >= 0 && cp < m.Probability.Count ? cp : -1;
        if (p < 0 && p6.Probability == null) return Fail("it has no current probability.");

        DistSpec? Days(Assessment a, out string? problem)
        {
            problem = null;
            if (dim is not { Quantitative: true })
            {
                problem = $"the schedule area {m.Promote.ScheduleDimension} has no ranges to turn into days.";
                return null;
            }
            if (!a.Severity.TryGetValue(dim.Id, out int level)) return new DistSpec { Distribution = "triangle" };
            if (level < 0 || level >= dim.Bands.Count) { problem = "its schedule severity is not a level of the matrix."; return null; }
            var band = dim.Bands[level];
            if (band.Min is not double lo || band.Max is not double hi)
            {
                problem = $"schedule level {MatrixSettings.Roman(level)} has no upper limit; set one in Setup.";
                return null;
            }
            static double Round(double v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);
            double low = Round(lo * plannedDays / 100), mid = Round((lo + hi) / 2 * plannedDays / 100), high = Round(hi * plannedDays / 100);
            return r.Kind == RiskKind.Opportunity
                ? new DistSpec { Distribution = "triangle", Min = -high, MostLikely = -mid, Max = -low }
                : new DistSpec { Distribution = "triangle", Min = low, MostLikely = mid, Max = high };
        }

        double prob = p6.Probability ?? Midpoint(m, p);
        string? problem = null;
        var impact = p6.Impact?.Clone() ?? Days(r.Current, out problem);
        if (impact == null) return Fail(problem!);

        bool mitigated = r.Target.Probability is int tp && tp >= 0 && tp < m.Probability.Count;
        double mprob = prob;
        var mimpact = impact.Clone();
        if (mitigated)
        {
            mprob = p6.MitigatedProbability ?? Midpoint(m, r.Target.Probability!.Value);
            string? mproblem = null;
            var mi = p6.MitigatedImpact?.Clone() ?? Days(r.Target, out mproblem);
            if (mi == null) return Fail(mproblem!);
            mimpact = mi;
        }
        return new QuantifiedRisk(prob, impact, mitigated, mprob, mimpact, null);
    }

    /// <summary>Writes the promotable risks into the model: adds or updates rows with the register's ids (marked as
    /// coming from the register), removes register rows that no longer qualify, and leaves every other row alone.</summary>
    public static PromoteReport Apply(RiskModelDocument doc, RiskRegister reg, double plannedDays)
    {
        var report = new PromoteReport();
        var promoted = new HashSet<string>();
        foreach (var r in reg.Risks)
        {
            if (Ineligibility(reg, r, honourAlways: true) != null) continue;
            var existing = doc.Risks.FirstOrDefault(x => x.Id == r.Id);
            if (existing != null && existing.Source != RiskRow.RegisterSource)
            {
                report.Skipped.Add(new SkippedRisk(r.Id, $"a risk typed into the model already uses id {r.Id}; rename one of them."));
                promoted.Add(r.Id);   // not ours to remove
                continue;
            }
            var q = Quantify(reg.Matrix, r, plannedDays);
            if (q.Problem != null) { report.Skipped.Add(new SkippedRisk(r.Id, q.Problem)); continue; }
            if (r.Promotion.Activities.Count == 0) { report.Skipped.Add(new SkippedRisk(r.Id, "map it to the activities it affects.")); continue; }

            var row = existing ?? new RiskRow { Id = r.Id };
            row.Source = RiskRow.RegisterSource;
            row.Title = r.Title;
            row.Probability = q.Probability;
            row.Impact = q.Impact;
            row.ImpactUnits = r.Promotion.Impact != null ? r.Promotion.ImpactUnits : "days";
            row.Filter = new FilterSpec { Kind = FilterKind.Activities, Value = string.Join(", ", r.Promotion.Activities) };
            row.Mitigated = q.Mitigated;
            row.MitigatedProbability = q.MitigatedProbability;
            row.MitigatedImpactDiffers = q.Mitigated;
            row.MitigatedImpact = q.MitigatedImpact;
            if (existing == null) { doc.Risks.Add(row); report.Added.Add(r.Id); }
            else report.Updated.Add(r.Id);
            promoted.Add(r.Id);
        }
        foreach (var row in doc.Risks.Where(x => x.Source == RiskRow.RegisterSource && !promoted.Contains(x.Id)).ToList())
        {
            doc.Risks.Remove(row);
            report.Removed.Add(row.Id);
        }
        return report;
    }
}
