using System.Globalization;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Core.Reporting;

/// <summary>One row of the statistics table. <see cref="Shared"/> rows have one value for both scenarios.</summary>
public sealed record StatRow(string Group, string Label, string Pre, string? Post, bool Shared = false);

/// <summary>
/// The run's statistics as label/value rows, grouped Duration, Statistics, Analysis and Model, for the browser app and the
/// HTML report. Durations are working days of the project calendar from the project start.
/// </summary>
public static class StatisticsTable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string DurationGroup = "Duration (working days)";

    public static List<StatRow> Build(SimulationSummary pre, SimulationSummary? post, int percentile = 80)
    {
        var rows = new List<StatRow>();
        void Row(string group, string label, Func<SimulationSummary, string> value) =>
            rows.Add(new StatRow(group, label, value(pre), post == null ? null : value(post)));
        void Shared(string group, string label, string value) => rows.Add(new StatRow(group, label, value, null, true));

        const string G = DurationGroup;
        Shared(G, "Measured from", Day(pre.ProjectStart));
        Shared(G, "Deterministic", Days(pre.Duration.Deterministic));
        Row(G, "Chance of deterministic", s => Pct(s.ProbMeetDeterministic));
        var levels = new SortedSet<int> { 50, 80, Math.Clamp(percentile, 1, 99) };
        foreach (int p in levels) Row(G, $"P{p}", s => Days(s.DurationAt(p)));
        foreach (int p in levels) Row(G, $"P{p} − deterministic", s => Contingency(s, p));

        const string S = "Statistics";
        Row(S, "Minimum", s => Days(s.Duration.Min));
        Row(S, "Maximum", s => Days(s.Duration.Max));
        Row(S, "Mean", s => Days(s.Duration.Mean));
        Row(S, "Median", s => Days(s.Duration.Median));
        Row(S, "Standard deviation", s => Days(s.Duration.Stdev));
        Row(S, "Skewness", s => Moment(s.Duration.Skewness));
        Row(S, "Kurtosis (excess)", s => Moment(s.Duration.Kurtosis));

        const string A = "Analysis";
        Row(A, "Started", s => s.Started == default ? "" : s.Started.ToLocalTime().ToString("dd-MMM-yyyy HH:mm", Inv));
        Row(A, "Run time", s => RunTime(s.Elapsed));
        Row(A, "Iterations", s => s.Iterations.ToString("N0", Inv));
        Row(A, "Seed", s => s.Seed.ToString(Inv));

        const string M = "Model";
        Shared(M, "Project", pre.ProjectCode);
        Shared(M, "Data date", Day(pre.DataDate));
        Shared(M, "Activities", pre.ActivityCount.ToString("N0", Inv));
        Shared(M, "Risks", pre.RiskCount.ToString("N0", Inv));
        return rows;
    }

    /// <summary>Contingency at a P-level: its duration minus the deterministic one, in days and as a share of it.</summary>
    public static string Contingency(SimulationSummary s, int p)
    {
        double det = s.Duration.Deterministic, c = s.DurationAt(p) - det;
        string days = Signed(c, 1) + " d";
        return det > 0 ? $"{days} ({Signed(c / det * 100, 1)}%)" : days;
    }

    public static string Days(double d) => d.ToString("F1", Inv) + " d";

    private static string Signed(double x, int digits)
    {
        string v = Math.Abs(x).ToString("F" + digits, Inv);
        return Math.Round(x, digits) > 0 ? "+" + v : Math.Round(x, digits) < 0 ? "−" + v : v;
    }

    private static string Moment(double? x) => x is double v ? v.ToString("F2", Inv) : "n/a";

    private static string Pct(double x) => (x * 100).ToString("F0", Inv) + "%";

    private static string Day(long m) => m == Time.None ? "" : Time.FromMinutes(m).ToString("dd-MMM-yyyy", Inv);

    private static string RunTime(TimeSpan t) =>
        t.TotalSeconds < 60 ? t.TotalSeconds.ToString("F1", Inv) + " s" : t.ToString(@"h\:mm\:ss", Inv);
}
