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

        var sum = new SimulationSummary
        {
            Iterations = n, Batches = res.Batches, Converged = res.Converged, Scenario = res.Scenario, Seed = res.Seed,
            DeterministicFinish = res.Deterministic,
            ProbMeetDeterministic = (double)meet / n,
            FinishMin = sorted[0], FinishMax = sorted[^1],
            FinishMean = pcal.TimeFinish(MathX.RoundHalfUp(meanW)),
            StdevWorkingDays = R(Math.Sqrt(variance) / mpd, 4),
            SortedFinish = sorted,
            Elapsed = res.Elapsed,
        };
        foreach (int p in Percentiles) sum.FinishPercentiles[p] = Statistics.PercentileSorted(sorted, p);

        var det = sim.Cpm.Run(backward: false);
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
            w.WriteString("deterministic_finish", Time.Format(DeterministicFinish));
            w.WriteNumber("prob_meet_deterministic", ProbMeetDeterministic);
            w.WriteStartObject("finish");
            foreach (var kv in FinishPercentiles) w.WriteString($"P{kv.Key}", Time.Format(kv.Value));
            w.WriteString("min", Time.Format(FinishMin));
            w.WriteString("max", Time.Format(FinishMax));
            w.WriteString("mean", Time.Format(FinishMean));
            w.WriteNumber("stdev_working_days", StdevWorkingDays);
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
