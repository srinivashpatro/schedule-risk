using System.Globalization;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Core.Reporting;

/// <summary>One label/value row of the summary. <see cref="Shared"/> rows have one value for both scenarios.</summary>
public sealed record StatRow(string Group, string Label, string Pre, string? Post, bool Shared = false);

/// <summary>Something that drives the finish: a risk, a risk driver, or (when the model has neither) an activity's duration.</summary>
public sealed record SummaryDriver(string Kind, string Id, string Title, double Sensitivity);

/// <summary>
/// The key results of a run in one place, for the browser app's Results summary, the HTML report and the exported
/// reports: finish dates with durations at the main confidence levels and the contingency they need, the spread of the
/// duration, what drives the finish, and the critical and near-critical activities. Durations are working days of the
/// project calendar from the project start.
/// </summary>
public sealed class ResultsSummary
{
    /// <summary>Critical: on the critical path in at least this percentage of the iterations, to the whole percent shown
    /// (so an activity that reads 50% is never listed as near-critical).</summary>
    public const int CriticalPercent = 50;
    /// <summary>Near-critical: at least this percentage, but less than <see cref="CriticalPercent"/>.</summary>
    public const int NearCriticalPercent = 10;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Deterministic finish, the chance of meeting it (and the Must Finish By), P50, P80 and the chosen level with
    /// their contingency, mean and median: each a finish date with its duration.</summary>
    public List<StatRow> Finish { get; } = new();
    /// <summary>Minimum, maximum, standard deviation, skewness and excess kurtosis of the duration.</summary>
    public List<StatRow> Spread { get; } = new();
    /// <summary>Risks and risk drivers, most influential on the finish first (pre-mitigation).</summary>
    public List<SummaryDriver> Drivers { get; } = new();
    /// <summary>The model has no risks or drivers, so <see cref="Drivers"/> lists the activities whose durations track the finish.</summary>
    public bool DriversAreActivities { get; init; }
    /// <summary>Activities critical in at least half the iterations, most critical first (pre-mitigation).</summary>
    public List<ActivityStats> Critical { get; } = new();
    /// <summary>Activities critical in 10% to 49% of the iterations, most critical first (pre-mitigation).</summary>
    public List<ActivityStats> NearCritical { get; } = new();
    /// <summary>When and how the simulation ran: start, run time, iterations, seed.</summary>
    public string Run { get; init; } = "";
    /// <summary>What was simulated: project, data date, activities, risks.</summary>
    public string Model { get; init; } = "";

    public static ResultsSummary Build(SimulationSummary pre, SimulationSummary? post, int percentile = 80)
    {
        bool activities = pre.Risks.Count == 0 && pre.Drivers.Count == 0;
        var sum = new ResultsSummary
        {
            DriversAreActivities = activities,
            Run = RunLine(pre, post),
            Model = $"{pre.ProjectCode} · data date {Day(pre.DataDate)} · {Count(pre.ActivityCount, "activity", "activities")} · {Count(pre.RiskCount, "risk", "risks")}",
        };
        void Row(List<StatRow> rows, string group, string label, Func<SimulationSummary, string> value) =>
            rows.Add(new StatRow(group, label, value(pre), post == null ? null : value(post)));
        void Shared(List<StatRow> rows, string group, string label, string value) => rows.Add(new StatRow(group, label, value, null, true));

        const string F = "Finish";
        var fin = sum.Finish;
        Shared(fin, F, "Deterministic", When(pre.DeterministicFinish, pre.Duration.Deterministic));
        Row(fin, F, "Chance of deterministic", s => Pct(s.ProbMeetDeterministic));
        if (pre.ProbMeetMustFinishBy.HasValue)
        {
            Shared(fin, F, "Must Finish By", Day(pre.MustFinishBy));
            Row(fin, F, "Chance of Must Finish By", s => Pct(s.ProbMeetMustFinishBy ?? 0));
        }
        foreach (int p in new SortedSet<int> { 50, 80, Math.Clamp(percentile, 1, 99) })
        {
            Row(fin, F, $"P{p}", s => When(Statistics.PercentileSorted(s.SortedFinish, p), s.DurationAt(p)));
            Row(fin, F, $"P{p} − deterministic", s => Contingency(s, p));
        }
        Row(fin, F, "Mean", s => When(s.FinishMean, s.Duration.Mean));
        Row(fin, F, "Median", s => When(s.FinishMedian, s.Duration.Median));

        const string S = "Spread";
        var spread = sum.Spread;
        Row(spread, S, "Minimum", s => When(s.FinishMin, s.Duration.Min));
        Row(spread, S, "Maximum", s => When(s.FinishMax, s.Duration.Max));
        Row(spread, S, "Standard deviation", s => Days(s.Duration.Stdev));
        Row(spread, S, "Skewness", s => Moment(s.Duration.Skewness));
        Row(spread, S, "Kurtosis (excess)", s => Moment(s.Duration.Kurtosis));

        if (activities)
            sum.Drivers.AddRange(pre.Activities.Where(a => a.Sensitivity != 0).Select(a => new SummaryDriver("Activity", a.Code, a.Name, a.Sensitivity)));
        else
        {
            sum.Drivers.AddRange(pre.Risks.Select(r => new SummaryDriver("Risk", r.Id, r.Title, r.Sensitivity)));
            sum.Drivers.AddRange(pre.Drivers.Select(d => new SummaryDriver("Driver", d.Id, d.Title, d.Sensitivity)));
        }
        var ranked = sum.Drivers.OrderByDescending(d => Math.Abs(d.Sensitivity)).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        sum.Drivers.Clear();
        sum.Drivers.AddRange(ranked);

        var byIndex = pre.Activities.OrderByDescending(a => a.Criticality).ThenByDescending(a => Math.Abs(a.Cruciality))
            .ThenBy(a => a.Code, StringComparer.Ordinal).ToList();
        sum.Critical.AddRange(byIndex.Where(a => Percent(a.Criticality) >= CriticalPercent));
        sum.NearCritical.AddRange(byIndex.Where(a => Percent(a.Criticality) is >= NearCriticalPercent and < CriticalPercent));
        return sum;
    }

    /// <summary>Contingency at a P-level: its duration minus the deterministic one, in days and as a share of it.</summary>
    public static string Contingency(SimulationSummary s, int p)
    {
        double det = s.Duration.Deterministic, c = s.DurationAt(p) - det;
        string days = Signed(c, 1) + " d";
        return det > 0 ? $"{days} ({Signed(c / det * 100, 1)}%)" : days;
    }

    public static string Days(double d) => d.ToString("F1", Inv) + " d";

    /// <summary>A share as a whole percent, halves rounded up: what <see cref="Pct"/> shows.</summary>
    public static int Percent(double share) => (int)Math.Round(share * 100, MidpointRounding.AwayFromZero);

    public static string Pct(double share) => Percent(share).ToString(Inv) + "%";

    private static string When(long finish, double days) => $"{Day(finish)} · {Days(days)}";

    private static string Signed(double x, int digits)
    {
        string v = Math.Abs(x).ToString("F" + digits, Inv);
        return Math.Round(x, digits) > 0 ? "+" + v : Math.Round(x, digits) < 0 ? "−" + v : v;
    }

    private static string Moment(double? x) => x is double v ? v.ToString("F2", Inv) : "n/a";

    private static string Day(long m) => m == Time.None ? "" : Time.FromMinutes(m).ToString("dd-MMM-yyyy", Inv);

    private static string Count(int n, string one, string many) => n.ToString("N0", Inv) + " " + (n == 1 ? one : many);

    private static string RunLine(SimulationSummary pre, SimulationSummary? post)
    {
        var parts = new List<string>();
        if (pre.Started != default) parts.Add("Started " + pre.Started.ToLocalTime().ToString("dd-MMM-yyyy HH:mm", Inv));
        var t = post == null ? pre.Elapsed : pre.Elapsed + post.Elapsed;
        parts.Add("run time " + (t.TotalSeconds < 60 ? t.TotalSeconds.ToString("F1", Inv) + " s" : t.ToString(@"h\:mm\:ss", Inv)));
        parts.Add(pre.Iterations.ToString("N0", Inv) + " iterations" + (pre.Converged == true ? " (converged)" : pre.Converged == false ? " (not converged)" : ""));
        parts.Add("seed " + pre.Seed.ToString(Inv));
        if (post != null) parts.Add("pre- and post-mitigation");
        return string.Join(" · ", parts);
    }
}
