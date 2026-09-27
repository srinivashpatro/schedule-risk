using System.Globalization;
using System.Text.RegularExpressions;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Core.Reporting;

/// <summary>One plain-language note on the results: a short label and one to three sentences.</summary>
public sealed record Finding(string Key, string Label, string Text);

/// <summary>A glossary entry: the term and one or two plain sentences.</summary>
public sealed record Term(string Name, string Meaning);

/// <summary>
/// "What the results mean": the Summary's figures in plain words for readers with no statistics background. It says
/// what the current finish, the P50 and P80 (and a chosen level), the mean and median, skewness, kurtosis and spread
/// say about the finish, what mitigation gains, what drives it and how far to trust it. Built from templates and the
/// fixed thresholds below (see docs/READING_RESULTS.md), from the same figures as the Summary, so every report and the
/// Results page say the same thing. No model or network is involved.
/// </summary>
public sealed class ResultsNarrative
{
    /// <summary>Chance of meeting a date, whole percent as shown: under 10 "very unlikely", under 50 "less likely than
    /// not", under 80 "more likely than not", else "likely".</summary>
    public const int VeryUnlikelyBelow = 10, MoreLikelyFrom = 50, LikelyFrom = 80;
    /// <summary>Skewness (as Excel's SKEW, as shown to 2 decimals): under 0.5 either way "roughly symmetric"; over 1 "strong".</summary>
    public const double SymmetricBelow = 0.5, StrongAbove = 1.0;
    /// <summary>Excess kurtosis (as Excel's KURT, as shown): over 1 heavier tails than a bell curve; under -1 flatter, or two groups.</summary>
    public const double HeavyAbove = 1.0, FlatBelow = -1.0;
    /// <summary>Mean and median closer than this many working days are "about the same".</summary>
    public const double SameCentreWithin = 1.0;
    /// <summary>A driver dominates when its sensitivity (as shown) is at least this many times the next one's.</summary>
    public const double DominatesRatio = 2.0;
    /// <summary>Fewer outcomes than this make the P-dates rough estimates.</summary>
    public const int RoughBelow = 1000;
    /// <summary>Two groups: kurtosis below <see cref="FlatBelow"/> and fewer than this share of the outcomes within
    /// <see cref="NearMeanWindow"/> of the P10–P90 range (at least 1 working day) of the mean.</summary>
    public const double NearMeanShare = 0.03, NearMeanWindow = 0.05;
    /// <summary>A P-date reads as its stated chance when the share of outcomes finishing by it is within this many
    /// points; otherwise (lumpy results, such as a risk that either happens or not) the actual share is given.</summary>
    public const int LevelTolerance = 2;

    /// <summary>The line under the section heading.</summary>
    public const string Lead = "Plain-language notes on this run's figures.";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public IReadOnlyList<Finding> Findings { get; }

    private ResultsNarrative(List<Finding> findings) => Findings = findings;

    /// <summary>The glossary, one or two plain sentences per term.</summary>
    public static IReadOnlyList<Term> Terms { get; } = new Term[]
    {
        new("P-level", "A date with a stated chance of finishing on or before it. P70, for example, means a 70% chance."),
        new("P50", "The middle outcome: the project is as likely to beat this date as to miss it."),
        new("P80", "The date with an 80% chance of finishing on or before it, often used to set contingency."),
        new("Deterministic finish", "The finish date of the schedule as planned, with no risk or uncertainty, as P6 calculates it."),
        new("Contingency", "The working days between the deterministic finish and a P-level date, also shown as a share of the planned duration."),
        new("Mean", "The average finish of all the simulated outcomes."),
        new("Median", "The middle outcome, with half finishing earlier and half later; the same idea as the P50."),
        new("Standard deviation", "How far outcomes typically fall from the average, in working days. Bigger means less certain."),
        new("Skewness", "Whether outcomes lean to one side. Above 0, there is a longer tail of late finishes; below 0, of early ones."),
        new("Kurtosis", "How often outcomes land far from the middle, compared with a bell curve. It is shown as excess kurtosis, where a bell curve is 0."),
        new("Criticality", "The share of outcomes in which an activity is on the chain of work that sets the finish. Critical: 50% or more; near-critical: 10% to 49%."),
        new("Sensitivity", "How closely a risk's impact, a driver's factor or an activity's duration moves with the finish, from −1 to 1. Drivers are ranked by it."),
        new("Working days", "Durations count working days of the project calendar, from the project start."),
    };

    public static ResultsNarrative Build(SimulationSummary pre, SimulationSummary? post, int percentile = 80, ResultsSummary? summary = null)
    {
        var sum = summary ?? ResultsSummary.Build(pre, post, percentile);
        var f = new List<Finding>();
        void Add(string key, string label, params string[] sentences) =>
            f.Add(new Finding(key, label, string.Join(' ', sentences.Where(x => x.Length > 0))));
        int n = Math.Max(1, pre.SortedFinish.Length);
        double det = pre.Duration.Deterministic;
        string before = post != null ? "Before mitigation, " : "";
        bool flat = pre.Duration.Stdev == 0;

        Add("finish", "Current finish",
            $"The current schedule finishes on {Day(pre.DeterministicFinish)}, with no allowance for risk.",
            $"Based on this model, that date is {Band(ResultsSummary.Percent(pre.ProbMeetDeterministic))}: {FinishBy(pre.ProbMeetDeterministic, n)}.");
        if (pre.ProbMeetMustFinishBy is double mfb)
            Add("deadline", "Must Finish By",
                $"The project's deadline in P6, its Must Finish By date, is {Day(pre.MustFinishBy)}.",
                $"Based on this model, meeting it is {Band(ResultsSummary.Percent(mfb))}: {FinishBy(mfb, n)}.");

        if (flat)
            Add("flat", "No spread", $"Every simulated outcome finishes on {Day(pre.FinishMin)}: the model adds no variation, so there is no spread to describe.");
        else
        {
            long d50 = Statistics.PercentileSorted(pre.SortedFinish, 50);
            double c50 = pre.DurationAt(50) - det;
            double share50 = ShareBy(pre.SortedFinish, d50);
            bool coin = Math.Abs(ResultsSummary.Percent(share50) - 50) <= LevelTolerance;
            Add("p50", "P50",
                coin ? $"The P50 date, {Day(d50)}, is a coin flip: half the outcomes finish by then, and half finish later."
                     : $"The P50 date, {Day(d50)}, is the middle outcome; in fact {FinishBy(share50, n)}.",
                Whole(c50) == 0 ? $"It falls on the current schedule's finish{(coin ? ", but a coin flip is not a date to promise" : "")}."
                    : $"It is {About(c50)}{Of(c50, det)} {(c50 > 0 ? "after" : "before")} the current schedule's finish{(coin ? ", and not a date to promise" : "")}.");

            long d80 = Statistics.PercentileSorted(pre.SortedFinish, 80);
            double c80 = pre.DurationAt(80) - det;
            double share80 = ShareBy(pre.SortedFinish, d80);
            Add("p80", "P80",
                Math.Abs(ResultsSummary.Percent(share80) - 80) <= LevelTolerance
                    ? $"There is an 80% chance of finishing by {Day(d80)} (the P80), and a 1-in-5 chance of finishing later."
                    : $"The P80 date, {Day(d80)}, is the earliest date with at least an 80% chance: {FinishBy(share80, n)}.",
                Whole(c80) > 0 ? $"Promising that date means adding {About(c80)}{Of(c80, det)} to the current schedule."
                    : Whole(c80) == 0 ? "It falls on the current schedule's finish, so no contingency is needed for 80% confidence."
                    : $"It is {About(c80)}{Of(c80, det)} before the current schedule's finish, so that finish already has at least an 80% chance.");

            int p = Math.Clamp(percentile, 1, 99);
            if (p != 50 && p != 80)
            {
                long dp = Statistics.PercentileSorted(pre.SortedFinish, p);
                double cp = pre.DurationAt(p) - det;
                double shareP = ShareBy(pre.SortedFinish, dp);
                Add("level", $"P{p} (chosen)",
                    Math.Abs(ResultsSummary.Percent(shareP) - p) <= LevelTolerance
                        ? $"At the level chosen on the chart, P{p}, there is {Article(p)} {p}% chance of finishing by {Day(dp)}."
                        : $"At the level chosen on the chart, P{p}, the date is {Day(dp)}; {FinishBy(shareP, n)}.",
                    Whole(cp) == 0 ? "That is the current schedule's finish."
                        : $"That is {About(cp)}{Of(cp, det)} {(cp > 0 ? "after" : "before")} the current schedule's finish.");
            }

            double gap = pre.Duration.Mean - pre.Duration.Median;
            int side = CentreSide(gap);
            string centre = TwoGroups(pre)
                ? side == 0 ? "They are about the same, but few outcomes land near them: they fall into separate groups. So the average is not a likely finish date."
                    : $"The average is {About(gap)} {(side > 0 ? "later" : "earlier")}, but few outcomes land near it: they fall into separate groups. So the average is not a likely finish date."
                : side == 0 ? "They are about the same, which suggests the outcomes spread evenly on both sides."
                : side > 0 ? $"The average is {About(gap)} later because some very late outcomes drag it later."
                : $"The average is {About(gap)} earlier because some very early outcomes drag it earlier.";
            Add("centre", "Mean and median",
                $"The average finish (the mean) is {Day(pre.FinishMean)}, while the middle outcome (the median) is {Day(pre.FinishMedian)}.", centre);

            if (pre.Duration.Skewness is double sk)
                Add("skew", "Skewness", SkewSentence(sk, risks: pre.Risks.Count > 0));
            if (pre.Duration.Kurtosis is double ku)
                Add("tails", "Kurtosis", TailsSentence(ku));

            double width = pre.DurationAt(90) - pre.DurationAt(10);
            string ofLength = det > 0 ? $", {Math.Round(Math.Abs(width) / det * 100, MidpointRounding.AwayFromZero).ToString("F0", Inv)}% of the current schedule's length," : "";
            Add("spread", "Spread",
                $"The middle 80% of outcomes (P10 to P90) finish between {Day(pre.FinishPercentiles[10])} and {Day(pre.FinishPercentiles[90])}.",
                Whole(width) == 0 ? "That range is under 1 working day, so the finish barely varies."
                    : $"That range of {About(width)}{ofLength} shows how uncertain the finish is.");
        }

        if (post != null)
        {
            double gain = pre.DurationAt(80) - post.DurationAt(80);
            long post80 = Statistics.PercentileSorted(post.SortedFinish, 80);
            int a = ResultsSummary.Percent(pre.ProbMeetDeterministic), b = ResultsSummary.Percent(post.ProbMeetDeterministic);
            Add("mitigation", "Mitigation",
                Whole(gain) == 0 ? "The planned mitigation does not move the P80."
                    : $"With the planned mitigation, the P80 moves to {Day(post80)}, {About(gain)} {(gain > 0 ? "earlier" : "later")}.",
                a == b ? $"The current schedule's finish stays {Band(b)} ({b}%)."
                    : $"The chance of meeting the current schedule's finish {(b > a ? "rises" : "falls")} from {a}% to {b}%.");
        }

        Add("drivers", "What drives it", Drivers(sum, before));
        Add("critical", "Critical activities", Critical(sum, before));
        Add("caveat", "About these results",
            $"These results come from {n.ToString("N0", Inv)} simulated outcomes of this schedule and risk model, so they are only as good as its logic and data.",
            n < RoughBelow ? $"With fewer than {RoughBelow.ToString("N0", Inv)} outcomes, the P-dates are rough estimates."
                : pre.Converged == true ? "The simulation ran until the P-dates stopped changing noticeably." : "");
        return new ResultsNarrative(f);
    }

    private static string Drivers(ResultsSummary sum, string before)
    {
        var ds = sum.Drivers;
        static double S(SummaryDriver d) => double.IsFinite(d.Sensitivity) ? d.Sensitivity : 0;
        static string Name(SummaryDriver d) => $"{d.Id} {d.Title}".Trim();
        static string Kind(SummaryDriver d) => d.Kind switch { "Driver" => "risk driver", "Activity" => "activity", _ => "risk" };
        static string V(double v) => v.ToString("F2", Inv);
        if (ds.Count == 0 || Math.Round(Math.Abs(S(ds[0])), 2, MidpointRounding.AwayFromZero) == 0)
            return "No risk, driver or activity duration moves the finish noticeably.";
        if (sum.DriversAreActivities)
            return "The model has no risks or risk drivers, so the finish moves with the activity durations. "
                 + $"The one that matters most is {Name(ds[0])} (sensitivity {V(S(ds[0]))}).";
        if (ds.Count == 1)
            return Cap($"{before}one {Kind(ds[0])} drives the finish: {Name(ds[0])} (sensitivity {V(S(ds[0]))}).");
        if (Dominates(S(ds[0]), S(ds[1])))
            return Cap($"{before}one {Kind(ds[0])} dominates the finish: {Name(ds[0])}.")
                 + $" Its influence (sensitivity {V(S(ds[0]))}) is at least twice that of the next, {Name(ds[1])} ({V(S(ds[1]))}).";
        return Cap($"{before}the biggest influence on the finish is {Kind(ds[0])} {Name(ds[0])} (sensitivity {V(S(ds[0]))}).")
             + (ds.Count == 2 ? $" Next comes {Name(ds[1])} ({V(S(ds[1]))})."
                              : $" Next come {Name(ds[1])} ({V(S(ds[1]))}) and {Name(ds[2])} ({V(S(ds[2]))}).");
    }

    private static string Critical(ResultsSummary sum, string before)
    {
        int c = sum.Critical.Count, nc = sum.NearCritical.Count;
        string C(int x) => x.ToString("N0", Inv);
        if (c > 0)
            return Cap($"{before}in half or more of the outcomes, {C(c)} {(c == 1 ? "activity is" : "activities are")} critical, on the chain of work that sets the finish.") + " "
                 + (nc > 1 ? $"Another {C(nc)} are near-critical (10% to 49% of outcomes); delays to any of these are the most likely to move the finish."
                    : nc == 1 ? "One more is near-critical (10% to 49% of outcomes); delays to any of these are the most likely to move the finish."
                    : c == 1 ? "Delays to it are the most likely to move the finish." : "Delays to these are the most likely to move the finish.");
        return Cap($"{before}no activity is critical in half or more of the outcomes: the chain of work that sets the finish keeps changing.") + " "
             + (nc > 1 ? $"The {C(nc)} near-critical ones (10% to 49% of outcomes) are the most likely to move the finish."
                : nc == 1 ? "The one near-critical activity (10% to 49% of outcomes) is the most likely to move the finish."
                : "None is near-critical (10% to 49%) either.");
    }

    // ------------------------------------------------------------------ rules (each threshold, read on the value as shown)

    /// <summary>Words for the chance of meeting a date, from its whole percent.</summary>
    public static string Band(int percent) =>
        percent < VeryUnlikelyBelow ? "very unlikely" : percent < MoreLikelyFrom ? "less likely than not" : percent < LikelyFrom ? "more likely than not" : "likely";

    /// <summary>How many outcomes finish by a date: as a percent, or counted when the percent would read 0% or 100%
    /// without being exactly none or all.</summary>
    public static string FinishBy(double share, int n)
    {
        int count = (int)Math.Round(share * n, MidpointRounding.AwayFromZero);
        string all = n.ToString("N0", Inv);
        int pct = ResultsSummary.Percent(share);
        if (count <= 0) return $"none of the {all} simulated outcomes finish by then";
        if (count >= n) return $"all {all} simulated outcomes finish by then";
        if (pct == 0) return $"only {count.ToString("N0", Inv)} of the {all} simulated outcomes {(count == 1 ? "finishes" : "finish")} by then";
        if (pct == 100) return $"all but {(n - count).ToString("N0", Inv)} of the {all} simulated outcomes finish by then";
        return $"{pct}% of the {all} simulated outcomes finish by then";
    }

    public static string SkewSentence(double skewness, bool risks)
    {
        double v = Math.Round(skewness, 2, MidpointRounding.AwayFromZero);
        string shown = v.ToString("F2", Inv);
        if (Math.Abs(v) < SymmetricBelow)
            return $"The outcomes are spread roughly evenly around the middle (skewness {shown}): late surprises are about as likely as early ones.";
        string strength = Math.Abs(v) > StrongAbove ? "strong" : "moderate";
        return v > 0
            ? $"The outcomes have a {strength} tail toward late finishes (skewness {shown}): some run much later than the rest{(risks ? ", often when risks occur" : "")}."
            : $"The outcomes have a {strength} tail toward early finishes (skewness {shown}): some finish much earlier than the rest.";
    }

    public static string TailsSentence(double kurtosis)
    {
        double v = Math.Round(kurtosis, 2, MidpointRounding.AwayFromZero);
        string shown = v.ToString("F2", Inv);
        return v > HeavyAbove ? $"Very early or very late outcomes are more common than in a bell curve (excess kurtosis {shown})."
            : v < FlatBelow ? $"The outcomes are flatter than a bell curve, or fall into two groups, such as a risk that happens or not (excess kurtosis {shown}). Check the histogram to see which."
            : $"The overall shape is close to a bell curve (excess kurtosis {shown}).";
    }

    /// <summary>+1 when the mean is at least a working day later than the median, −1 when earlier, 0 when about the same.</summary>
    public static int CentreSide(double gapDays) => Math.Abs(gapDays) < SameCentreWithin ? 0 : Math.Sign(gapDays);

    public static bool Dominates(double first, double second)
    {
        double a = Math.Abs(Math.Round(first, 2, MidpointRounding.AwayFromZero)), b = Math.Abs(Math.Round(second, 2, MidpointRounding.AwayFromZero));
        return a > 0 && a >= DominatesRatio * b;
    }

    /// <summary>Kurtosis below the flat line, and few outcomes near the mean: the results fall into separate groups.</summary>
    internal static bool TwoGroups(SimulationSummary s)
    {
        if (s.Duration.Kurtosis is not double ku || Math.Round(ku, 2, MidpointRounding.AwayFromZero) >= FlatBelow) return false;
        var d = s.SortedDuration;
        if (d.Length == 0) return false;
        double window = Math.Max(1, NearMeanWindow * (s.DurationAt(90) - s.DurationAt(10))), mean = s.Duration.Mean;
        int near = d.Count(x => Math.Abs(x - mean) <= window);
        return near < NearMeanShare * d.Length;
    }

    // ------------------------------------------------------------------ wording helpers

    /// <summary>Words joined by hyphens, such as 13-Sep-2028, 1-in-5 and P-dates, which should not break over two lines.</summary>
    public static readonly Regex Hyphenated = new(@"\w+(?:-\w+)+", RegexOptions.CultureInvariant);

    /// <summary>The text with the hyphens inside words made non-breaking (U+2011), for layouts that break at hyphens.</summary>
    public static string NoBreakHyphens(string text) => Hyphenated.Replace(text, m => m.Value.Replace('-', '\u2011'));

    /// <summary>The share of outcomes finishing on or before a time.</summary>
    private static double ShareBy(long[] sorted, long t)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (sorted[mid] <= t) lo = mid + 1; else hi = mid; }
        return sorted.Length == 0 ? 0 : (double)lo / sorted.Length;
    }

    private static int Whole(double days) => (int)Math.Round(days, MidpointRounding.AwayFromZero);

    private static string About(double days)
    {
        int w = Math.Abs(Whole(days));
        return $"about {w.ToString("N0", Inv)} working day{(w == 1 ? "" : "s")}";
    }

    /// <summary>" (19%)": days as a share of the planned duration, or nothing when there is none.</summary>
    private static string Of(double days, double planned) =>
        planned > 0 ? $" ({Math.Round(Math.Abs(days) / planned * 100, MidpointRounding.AwayFromZero).ToString("F0", Inv)}%)" : "";

    private static string Article(int p) => p.ToString(Inv).StartsWith('8') ? "an" : "a";

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Day(long m) => m == Time.None ? "" : Time.FromMinutes(m).ToString("dd-MMM-yyyy", Inv);
}
