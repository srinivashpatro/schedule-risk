using System.Text;
using System.Text.Json;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Numerics;

namespace ScheduleRisk.Core.Simulation;

public static class Statistics
{
    /// <summary>Nearest-rank percentile, p in whole percent.</summary>
    public static long Percentile(IReadOnlyList<long> values, int p)
    {
        var v = values.ToArray();
        Array.Sort(v);
        return PercentileSorted(v, p);
    }

    public static long PercentileSorted(long[] sorted, int p)
    {
        int n = sorted.Length;
        long idx = ((long)p * n + 99) / 100 - 1;
        if (idx < 0) idx = 0;
        if (idx >= n) idx = n - 1;
        return sorted[idx];
    }

    /// <summary>Middle value, or the mean of the middle pair for an even count.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        var v = values.ToArray();
        Array.Sort(v);
        int n = v.Length;
        return n % 2 == 1 ? v[n / 2] : (v[n / 2 - 1] + v[n / 2]) / 2;
    }

    /// <summary>Sample skewness as Excel's SKEW (adjusted Fisher-Pearson); null below 3 values or with no spread.</summary>
    public static double? Skewness(IReadOnlyList<double> x)
    {
        int n = x.Count;
        if (n < 3) return null;
        var (m, sd) = MeanSd(x);
        if (sd == 0) return null;
        double acc = 0;
        foreach (double v in x) acc += Math.Pow((v - m) / sd, 3);
        return (double)n / ((n - 1.0) * (n - 2)) * acc;
    }

    /// <summary>Sample excess kurtosis as Excel's KURT (0 for a normal distribution); null below 4 values or with no spread.</summary>
    public static double? ExcessKurtosis(IReadOnlyList<double> x)
    {
        int n = x.Count;
        if (n < 4) return null;
        var (m, sd) = MeanSd(x);
        if (sd == 0) return null;
        double acc = 0;
        foreach (double v in x) acc += Math.Pow((v - m) / sd, 4);
        return n * (n + 1.0) / ((n - 1.0) * (n - 2) * (n - 3)) * acc - 3.0 * (n - 1.0) * (n - 1) / ((n - 2.0) * (n - 3));
    }

    private static (double Mean, double Sd) MeanSd(IReadOnlyList<double> x)
    {
        double sum = 0;
        foreach (double v in x) sum += v;
        double m = sum / x.Count;
        double ss = 0;
        foreach (double v in x) ss += (v - m) * (v - m);
        return (m, Math.Sqrt(ss / (x.Count - 1)));
    }
}

/// <summary>
/// Project duration in working days of the project calendar, from the project start (the earliest start in the
/// deterministic schedule, actual starts included) to the finish. Contingency is a P-level minus the deterministic
/// duration, also as a percentage of it.
/// </summary>
public sealed record DurationStats(double Deterministic, double Min, double Max, double Mean, double Median, double Stdev,
                                   double? Skewness, double? Kurtosis, SortedDictionary<int, double> Percentiles)
{
    public static readonly DurationStats Empty = new(0, 0, 0, 0, 0, 0, null, null, new SortedDictionary<int, double>());

    public double ContingencyDays(int p) => Math.Round(Percentiles[p] - Deterministic, 6, MidpointRounding.ToEven);

    public double? ContingencyPercent(int p) =>
        Deterministic > 0 ? Math.Round((Percentiles[p] - Deterministic) / Deterministic * 100, 2, MidpointRounding.ToEven) : null;
}

public sealed record MilestoneStats(string Code, string Name, long Deterministic, long P10, long P50, long P80, long P90);

public sealed record ActivityStats(string Code, string Name, double Criticality, double Sensitivity, double Cruciality);

public sealed record RiskStats(string Id, string Title, double Occurrence, double Sensitivity, double? MeanFinishDeltaDays);

public sealed record DriverStats(string Id, string Title, double Sensitivity);

/// <summary>Analytics over a simulation result: P-dates, criticality, sensitivity, tornado data. Twin of sim.py summarize.</summary>
public sealed class SimulationSummary
{
    public static readonly int[] Percentiles = { 5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 95 };

    public int Iterations { get; init; }
    public int Batches { get; init; }
    public bool? Converged { get; init; }
    public Scenario Scenario { get; init; }
    public long Seed { get; init; }
    public long DeterministicFinish { get; init; }
    public double ProbMeetDeterministic { get; init; }
    /// <summary>The project's Must Finish By, or <see cref="Time.None"/> when it has none.</summary>
    public long MustFinishBy { get; init; } = Time.None;
    /// <summary>Share of iterations finishing on or before the Must Finish By; null when it has none.</summary>
    public double? ProbMeetMustFinishBy { get; init; }
    public SortedDictionary<int, long> FinishPercentiles { get; } = new();
    public long FinishMin { get; init; }
    public long FinishMax { get; init; }
    public long FinishMean { get; init; }
    public double StdevWorkingDays { get; init; }
    public List<MilestoneStats> Milestones { get; } = new();
    public List<ActivityStats> Activities { get; } = new();
    public List<RiskStats> Risks { get; } = new();
    public List<DriverStats> Drivers { get; } = new();
    /// <summary>Sorted finish samples, for S-curves and histograms.</summary>
    public long[] SortedFinish { get; init; } = Array.Empty<long>();
    public TimeSpan Elapsed { get; init; }
    /// <summary>When the run started (UTC). Shown in the app and report, kept out of the JSON so it stays reproducible.</summary>
    public DateTime Started { get; init; }
    public string ProjectCode { get; init; } = "";
    public long DataDate { get; init; } = Time.None;
    public int ActivityCount { get; init; }
    public int RiskCount { get; init; }
    /// <summary>Earliest start in the deterministic schedule (actual starts included); the same in every iteration.</summary>
    public long ProjectStart { get; init; } = Time.None;
    public DurationStats Duration { get; init; } = DurationStats.Empty;
    /// <summary>Sorted durations (working days from <see cref="ProjectStart"/>), one per iteration.</summary>
    public double[] SortedDuration { get; init; } = Array.Empty<double>();

    /// <summary>Duration at any whole-percent confidence level (nearest rank, as the P-dates).</summary>
    public double DurationAt(int p)
    {
        int n = SortedDuration.Length;
        long idx = Math.Clamp(((long)p * n + 99) / 100 - 1, 0, n - 1);
        return R(SortedDuration[idx], 6);
    }

    private static double R(double x, int digits) => Math.Round(x, digits, MidpointRounding.ToEven);

    public static SimulationSummary Build(MonteCarloEngine sim, SimulationResult res)
    {
        var s = sim.Schedule;
        var acts = s.Activities;
        var pcal = s.Settings.ProjectCalendar;
        double mpd = pcal.MinutesPerDay;
        int n = res.Iterations;
        var fin = res.Finish;
        var sorted = fin.ToArray();
        Array.Sort(sorted);
        var fw = new long[n];
        for (int i = 0; i < n; i++) fw[i] = pcal.WorkAt(fin[i]);
        double sumW = 0;
        for (int i = 0; i < n; i++) sumW += fw[i];
        double meanW = sumW / n;
        double variance = 0;
        if (n > 1)
        {
            for (int i = 0; i < n; i++) variance += (fw[i] - meanW) * (fw[i] - meanW);
            variance /= n - 1;
        }
        int meet = 0;
        foreach (var f in fin) if (f <= res.Deterministic) meet++;
        // A Must Finish By at 00:00 means by the end of the previous day, as in P6, so compare instants.
        long mfb = s.Settings.MustFinishBy;
        int meetMfb = 0;
        if (mfb != Time.None) foreach (var f in fin) if (f <= mfb) meetMfb++;

        var det = sim.Cpm.Run(backward: false);
        long start = Time.None;
        for (int j = 0; j < acts.Count; j++)
            if (!acts[j].IsSummary && det.ES[j] != Time.None && (start == Time.None || det.ES[j] < start)) start = det.ES[j];
        long w0 = pcal.WorkAt(start);
        var days = new double[n];
        for (int i = 0; i < n; i++) days[i] = (fw[i] - w0) / mpd;
        var sortedDays = days.ToArray();
        Array.Sort(sortedDays);
        double sumDays = 0;
        foreach (double d in days) sumDays += d;
        var durPct = new SortedDictionary<int, double>();
        double stdev = R(Math.Sqrt(variance) / mpd, 4);
        // 6 decimals: durations are whole minutes over minutes-per-day, so 4 would often round a tie (786.73125)
        var dur = new DurationStats(R((pcal.WorkAt(res.Deterministic) - w0) / mpd, 6), R(sortedDays[0], 6), R(sortedDays[^1], 6),
            R(sumDays / n, 6), R(Statistics.Median(days), 6), stdev,
            Statistics.Skewness(days) is double sk ? R(sk, 6) : null,
            Statistics.ExcessKurtosis(days) is double ku ? R(ku, 6) : null, durPct);

        var sum = new SimulationSummary
        {
            Iterations = n, Batches = res.Batches, Converged = res.Converged, Scenario = res.Scenario, Seed = res.Seed,
            DeterministicFinish = res.Deterministic,
            ProbMeetDeterministic = (double)meet / n,
            MustFinishBy = mfb,
            ProbMeetMustFinishBy = mfb != Time.None ? (double)meetMfb / n : null,
            FinishMin = sorted[0], FinishMax = sorted[^1],
            FinishMean = pcal.TimeFinish(MathX.RoundHalfUp(meanW)),
            StdevWorkingDays = stdev,
            SortedFinish = sorted,
            Elapsed = res.Elapsed,
            Started = res.Started,
            ProjectCode = s.ProjectCode,
            DataDate = s.Settings.DataDate,
            ActivityCount = acts.Count,
            RiskCount = sim.Model.Risks.Count,
            ProjectStart = start,
            Duration = dur,
            SortedDuration = sortedDays,
        };
        foreach (int p in Percentiles) durPct[p] = sum.DurationAt(p);
        foreach (int p in Percentiles) sum.FinishPercentiles[p] = Statistics.PercentileSorted(sorted, p);

        foreach (var kv in res.Milestones.OrderBy(k => k.Key))
        {
            var a = acts[kv.Key];
            var v = kv.Value.ToArray();
            Array.Sort(v);
            sum.Milestones.Add(new MilestoneStats(a.Code, a.Name, det.EF[kv.Key],
                Statistics.PercentileSorted(v, 10), Statistics.PercentileSorted(v, 50),
                Statistics.PercentileSorted(v, 80), Statistics.PercentileSorted(v, 90)));
        }

        var finD = fin.Select(x => (double)x).ToArray();
        var finRanks = MathX.AverageRanks(finD);
        double SpearmanWithFinish(IReadOnlyList<double> x) => MathX.Pearson(MathX.AverageRanks(x), finRanks);

        var rows = new List<ActivityStats>();
        for (int j = 0; j < acts.Count; j++)
        {
            double ci = (double)res.CriticalCount[j] / n;
            double sens = res.Durations.TryGetValue(j, out var dl) ? SpearmanWithFinish(dl.Select(x => (double)x).ToArray()) : 0.0;
            if (ci == 0.0 && sens == 0.0) continue;
            rows.Add(new ActivityStats(acts[j].Code, acts[j].Name, R(ci, 6), R(sens, 6), R(ci * sens, 6)));
        }
        rows.Sort((x, y) =>
        {
            int c = Math.Abs(y.Cruciality).CompareTo(Math.Abs(x.Cruciality));
            if (c != 0) return c;
            c = y.Criticality.CompareTo(x.Criticality);
            return c != 0 ? c : string.CompareOrdinal(x.Code, y.Code);
        });
        sum.Activities.AddRange(rows);

        for (int ri = 0; ri < sim.Model.Risks.Count; ri++)
        {
            var r = sim.Model.Risks[ri];
            var flags = res.RiskOccurred[ri];
            double so = 0, sn = 0;
            int no = 0, nn = 0;
            for (int k = 0; k < n; k++)
            {
                if (flags[k] != 0) { so += fw[k]; no++; }
                else { sn += fw[k]; nn++; }
            }
            double? delta = no > 0 && nn > 0 ? R((so / no - sn / nn) / mpd, 4) : null;
            sum.Risks.Add(new RiskStats(r.Id, r.Title, R((double)no / n, 6), R(SpearmanWithFinish(res.RiskImpact[ri]), 6), delta));
        }
        sum.Risks.Sort((x, y) =>
        {
            int c = Math.Abs(y.Sensitivity).CompareTo(Math.Abs(x.Sensitivity));
            return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
        });

        for (int di = 0; di < sim.Model.Drivers.Count; di++)
        {
            var d = sim.Model.Drivers[di];
            sum.Drivers.Add(new DriverStats(d.Id, d.Title, R(SpearmanWithFinish(res.DriverValue[di]), 6)));
        }
        sum.Drivers.Sort((x, y) =>
        {
            int c = Math.Abs(y.Sensitivity).CompareTo(Math.Abs(x.Sensitivity));
            return c != 0 ? c : string.CompareOrdinal(x.Id, y.Id);
        });
        return sum;
    }

    /// <summary>JSON in the same shape as the Python reference (plus a few extra fields).</summary>
    public string ToJson(int? topActivities = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("iterations", Iterations);
            w.WriteNumber("batches", Batches);
            if (Converged.HasValue) w.WriteBoolean("converged", Converged.Value); else w.WriteNull("converged");
            w.WriteString("scenario", Scenario == Scenario.PostMitigation ? "post" : "pre");
            w.WriteNumber("seed", Seed);
            w.WriteStartObject("model");
            w.WriteString("project", ProjectCode);
            w.WriteString("data_date", Time.Format(DataDate));
            w.WriteNumber("activities", ActivityCount);
            w.WriteNumber("risks", RiskCount);
            w.WriteEndObject();
            w.WriteString("deterministic_finish", Time.Format(DeterministicFinish));
            w.WriteNumber("prob_meet_deterministic", ProbMeetDeterministic);
            if (ProbMeetMustFinishBy is double pm)
            {
                w.WriteString("must_finish_by", Time.Format(MustFinishBy));
                w.WriteNumber("prob_meet_must_finish_by", pm);
            }
            w.WriteStartObject("finish");
            foreach (var kv in FinishPercentiles) w.WriteString($"P{kv.Key}", Time.Format(kv.Value));
            w.WriteString("min", Time.Format(FinishMin));
            w.WriteString("max", Time.Format(FinishMax));
            w.WriteString("mean", Time.Format(FinishMean));
            w.WriteNumber("stdev_working_days", StdevWorkingDays);
            w.WriteEndObject();
            var du = Duration;
            w.WriteStartObject("duration");
            w.WriteString("unit", "working days");
            w.WriteString("start", Time.Format(ProjectStart));
            w.WriteNumber("deterministic", du.Deterministic);
            foreach (var kv in du.Percentiles) w.WriteNumber($"P{kv.Key}", kv.Value);
            w.WriteNumber("min", du.Min);
            w.WriteNumber("max", du.Max);
            w.WriteNumber("mean", du.Mean);
            w.WriteNumber("median", du.Median);
            w.WriteNumber("stdev", du.Stdev);
            if (du.Skewness is double sk) w.WriteNumber("skewness", sk); else w.WriteNull("skewness");
            if (du.Kurtosis is double ku) w.WriteNumber("kurtosis", ku); else w.WriteNull("kurtosis");
            w.WriteStartObject("contingency");
            foreach (int p in new[] { 50, 80 })
            {
                w.WriteStartObject($"P{p}");
                w.WriteNumber("days", du.ContingencyDays(p));
                if (du.ContingencyPercent(p) is double cp) w.WriteNumber("percent", cp); else w.WriteNull("percent");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartArray("milestones");
            foreach (var m in Milestones)
            {
                w.WriteStartObject();
                w.WriteString("code", m.Code);
                w.WriteString("name", m.Name);
                w.WriteString("deterministic", Time.Format(m.Deterministic));
                w.WriteString("P10", Time.Format(m.P10));
                w.WriteString("P50", Time.Format(m.P50));
                w.WriteString("P80", Time.Format(m.P80));
                w.WriteString("P90", Time.Format(m.P90));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("activities");
            foreach (var a in topActivities.HasValue ? Activities.Take(topActivities.Value) : Activities)
            {
                w.WriteStartObject();
                w.WriteString("code", a.Code);
                w.WriteString("name", a.Name);
                w.WriteNumber("criticality", a.Criticality);
                w.WriteNumber("sensitivity", a.Sensitivity);
                w.WriteNumber("cruciality", a.Cruciality);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("risks");
            foreach (var r in Risks)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteString("title", r.Title);
                w.WriteNumber("occurrence", r.Occurrence);
                w.WriteNumber("sensitivity", r.Sensitivity);
                if (r.MeanFinishDeltaDays.HasValue) w.WriteNumber("mean_finish_delta_days", r.MeanFinishDeltaDays.Value);
                else w.WriteNull("mean_finish_delta_days");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("drivers");
            foreach (var d in Drivers)
            {
                w.WriteStartObject();
                w.WriteString("id", d.Id);
                w.WriteString("title", d.Title);
                w.WriteNumber("sensitivity", d.Sensitivity);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
