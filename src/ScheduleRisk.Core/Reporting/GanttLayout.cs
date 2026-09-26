using System.Globalization;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Core.Reporting;

/// <summary>What a bar segment shows, after P6's default bars.</summary>
public enum GanttBarKind { Actual, Remaining, Critical, Float, Summary }

/// <summary>A bar segment from <see cref="From"/> to <see cref="To"/>, in <see cref="Time"/> minutes.</summary>
public readonly record struct GanttBar(GanttBarKind Kind, long From, long To);

public enum GanttRowKind { Wbs, Activity }

/// <summary>One row of the Gantt chart: a WBS band or an activity, with its table text and its bars.</summary>
public sealed class GanttRow
{
    public GanttRowKind Kind { get; init; }
    /// <summary>"w:" and the WBS id, or "a:" and the activity index. Stable between builds, so collapse state can use it.</summary>
    public string Key { get; init; } = "";
    public int Depth { get; set; }
    /// <summary>Index of the activity, or -1 for a WBS band.</summary>
    public int Activity { get; init; } = -1;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string OriginalDuration { get; init; } = "";
    public string RemainingDuration { get; init; } = "";
    /// <summary>Start as P6 shows it (29-May-26, with " A" when it is an actual date); blank for a finish milestone.</summary>
    public string Start { get; init; } = "";
    /// <summary>Finish as P6 shows it; blank for a start milestone.</summary>
    public string Finish { get; init; } = "";
    /// <summary>Total float in days of the activity's calendar; blank when there is none (completed work, summaries).</summary>
    public string TotalFloat { get; init; } = "";
    public bool Critical { get; init; }
    public bool NegativeFloat { get; init; }
    public bool Collapsed { get; init; }
    public List<GanttBar> Bars { get; } = new();
    /// <summary>A milestone's diamond, or <see cref="Time.None"/>.</summary>
    public long Milestone { get; init; } = Time.None;
    public GanttBarKind MilestoneKind { get; init; }
    /// <summary>Where the row's work starts and finishes (bars and diamond, not the float line).</summary>
    public long From { get; init; }
    public long To { get; init; }
    /// <summary>The start used to sort activities: actual start, else early start.</summary>
    public long SortKey { get; init; }
    internal bool Started { get; init; }
    internal bool Complete { get; init; }
}

public sealed class GanttOptions
{
    public bool GroupByWbs { get; set; } = true;
    public bool CriticalOnly { get; set; }
    /// <summary>Shows activities whose ID or name contains this text, ignoring case.</summary>
    public string Search { get; set; } = "";
    /// <summary>Keys of collapsed WBS bands.</summary>
    public HashSet<string> Collapsed { get; } = new(StringComparer.Ordinal);
}

public sealed record GanttTick(long From, long To, string Label);

/// <summary>The two tiers of the timescale header, named after P6's ("Quarter / Month").</summary>
public sealed record GanttTimescale(string Name, List<GanttTick> Top, List<GanttTick> Bottom);

public sealed class GanttModel
{
    public List<GanttRow> Rows { get; } = new();
    /// <summary>The timeline: midnight at the start of its first month to midnight after its last day.</summary>
    public long Start { get; init; }
    public long End { get; init; }
    public long DataDate { get; init; } = Time.None;
    /// <summary>Activities that pass the filters, including those inside collapsed bands.</summary>
    public int ActivitiesMatched { get; init; }
    public double Days => (End - Start) / (double)Time.MinutesPerDay;

    /// <summary>Horizontal position of a time at the given zoom.</summary>
    public double X(long t, double pixelsPerDay) => (t - Start) / (double)Time.MinutesPerDay * pixelsPerDay;
}

/// <summary>
/// Lays out a P6-style Gantt chart from the deterministic CPM result: an activity table beside bars on a two-tier timescale,
/// grouped by WBS in P6's order (seq_num, then file order) with activities by start then ID. Bars follow P6's defaults:
/// actual work, remaining work (critical in its own colour), milestones, and a float line from early to late finish.
/// Times stay in minutes; the view converts them to pixels, so zooming needs no new layout. UI-agnostic.
/// </summary>
public static class GanttLayout
{
    public const double MinPixelsPerDay = 0.02, MaxPixelsPerDay = 48;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Date(long t) => t == Time.None ? "" : Time.FromMinutes(t).ToString("dd-MMM-yy", Inv);

    /// <summary>Working minutes as days of a calendar: whole numbers without decimals, else one decimal.</summary>
    public static string Days(long minutes, int minutesPerDay)
    {
        double d = Math.Round(minutes / (double)minutesPerDay, 1, MidpointRounding.AwayFromZero);
        return Math.Abs(d - Math.Round(d)) < 1e-9 ? ((long)Math.Round(d)).ToString(Inv) : d.ToString("0.0", Inv);
    }

    public static double FitPixelsPerDay(GanttModel m, double width) => Math.Clamp(width / m.Days, MinPixelsPerDay, MaxPixelsPerDay);

    public static GanttModel Build(Schedule s, CpmResult r, GanttOptions? options = null)
    {
        var o = options ?? new GanttOptions();
        var acts = s.Activities;
        var rows = acts.Select(a => ActivityRow(s, r, a)).ToArray();

        // The timeline covers every activity, not only those shown, so filtering never moves it.
        long dd = s.Settings.DataDate, lo = dd, hi = dd;
        foreach (var row in rows)
        {
            if (row.From == Time.None) continue;
            if (lo == Time.None || row.From < lo) lo = row.From;
            if (hi == Time.None || row.To > hi) hi = row.To;
            foreach (var b in row.Bars) if (b.To > hi) hi = b.To;
        }
        if (lo == Time.None) lo = hi = 0;
        var first = Time.FromMinutes(lo);
        var last = Time.FromMinutes(hi + 10L * Time.MinutesPerDay); // room past the last date for a label
        long start = Time.ToMinutes(new DateTime(first.Year, first.Month, 1));
        long end = Time.ToMinutes(new DateTime(last.Year, last.Month, 1).AddMonths(1));

        string q = o.Search.Trim();
        var matched = rows.Where(x => x.From != Time.None && (!o.CriticalOnly || x.Critical)
            && (q.Length == 0 || x.Id.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(x => x.SortKey).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();

        var m = new GanttModel { Start = start, End = end, DataDate = dd, ActivitiesMatched = matched.Count };
        if (!o.GroupByWbs)
        {
            m.Rows.AddRange(matched);
            return m;
        }

        // WBS tree in P6's order; activities under their WBS element, still sorted by start then ID
        var order = s.Wbs.OrderBy(kv => kv.Value.Seq).ThenBy(kv => kv.Value.FileOrder).Select(kv => kv.Key).ToList();
        Dictionary<string, List<string>> children = order.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var id in order)
        {
            string parent = s.Wbs[id].ParentId;
            if (parent != id && children.TryGetValue(parent, out var list)) list.Add(id);
            else roots.Add(id);
        }
        var own = new Dictionary<string, List<GanttRow>>(StringComparer.Ordinal);
        var loose = new List<GanttRow>();
        foreach (var row in matched)
        {
            string wbs = acts[row.Activity].WbsId;
            if (!children.ContainsKey(wbs)) { loose.Add(row); continue; }
            if (!own.TryGetValue(wbs, out var list)) own[wbs] = list = new List<GanttRow>();
            list.Add(row);
        }

        // Roll-ups per WBS element over the matched activities below it (guarded against loops in bad files).
        var agg = new Dictionary<string, Rollup>(StringComparer.Ordinal);
        Rollup Agg(string id, HashSet<string> path)
        {
            if (agg.TryGetValue(id, out var done)) return done;
            var acc = new Rollup();
            path.Add(id);
            if (own.TryGetValue(id, out var list)) foreach (var row in list) acc.Add(row);
            foreach (string c in children[id])
                if (!path.Contains(c)) acc.Add(Agg(c, path));
            path.Remove(id);
            return agg[id] = acc;
        }

        foreach (var row in loose) { row.Depth = 0; m.Rows.Add(row); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Emit(string id, int depth)
        {
            if (!seen.Add(id)) return;
            var sum = Agg(id, new HashSet<string>(StringComparer.Ordinal));
            if (sum.Count == 0) return;
            var w = s.Wbs[id];
            string key = "w:" + id;
            bool collapsed = o.Collapsed.Contains(key);
            var band = new GanttRow
            {
                Kind = GanttRowKind.Wbs, Key = key, Depth = depth, Id = w.ShortName, Name = w.Name, Collapsed = collapsed,
                Start = Date(sum.From) + (sum.StartActual ? " A" : ""), Finish = Date(sum.To) + (sum.AllComplete ? " A" : ""),
                From = sum.From, To = sum.To, SortKey = sum.From,
            };
            band.Bars.Add(new GanttBar(GanttBarKind.Summary, sum.From, sum.To));
            m.Rows.Add(band);
            if (collapsed) { foreach (var c in children[id]) Visit(c); return; }
            if (own.TryGetValue(id, out var list))
                foreach (var row in list) { row.Depth = depth + 1; m.Rows.Add(row); }
            foreach (var c in children[id]) Emit(c, depth + 1);
        }
        // marks a collapsed band's descendants as seen, so they are not shown again as roots
        void Visit(string id)
        {
            if (!seen.Add(id)) return;
            foreach (var c in children[id]) Visit(c);
        }
        foreach (var id in roots) Emit(id, 0);
        foreach (var id in order) if (!seen.Contains(id)) Emit(id, 0); // elements caught in a parent loop
        return m;
    }

    /// <summary>
    /// The rows of a built layout whose activity ID or name contains <paramref name="search"/> (ignoring case), each with
    /// the WBS bands above it: the rows <see cref="Build"/> gives for that search, without laying the schedule out again.
    /// The bands keep their bars for the whole group. Lets a picker filter thousands of activities as the user types.
    /// </summary>
    public static List<GanttRow> Filter(IReadOnlyList<GanttRow> rows, string search)
    {
        string q = search.Trim();
        if (q.Length == 0) return rows.ToList();
        var result = new List<GanttRow>();
        var bands = new List<(GanttRow Row, bool Shown)>(); // the bands above the current row, outermost first
        foreach (var row in rows)
        {
            while (bands.Count > 0 && bands[^1].Row.Depth >= row.Depth) bands.RemoveAt(bands.Count - 1);
            if (row.Kind == GanttRowKind.Wbs)
            {
                bands.Add((row, false));
                continue;
            }
            if (!row.Id.Contains(q, StringComparison.OrdinalIgnoreCase) && !row.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            for (int k = 0; k < bands.Count; k++)
                if (!bands[k].Shown)
                {
                    result.Add(bands[k].Row);
                    bands[k] = (bands[k].Row, true);
                }
            result.Add(row);
        }
        return result;
    }

    private sealed class Rollup
    {
        public int Count;
        public long From = Time.None, To = Time.None;
        public bool StartActual, AllComplete = true;

        public void Add(GanttRow row) => Take(1, row.From, row.To, row.Started, row.Complete);

        public void Add(Rollup other)
        {
            if (other.Count > 0) Take(other.Count, other.From, other.To, other.StartActual, other.AllComplete);
        }

        private void Take(int count, long from, long to, bool startActual, bool complete)
        {
            if (From == Time.None || from < From) { From = from; StartActual = startActual; }
            else if (from == From) StartActual |= startActual;
            if (To == Time.None || to > To) To = to;
            AllComplete &= complete;
            Count += count;
        }
    }

    private static GanttRow ActivityRow(Schedule s, CpmResult r, Activity a)
    {
        int j = a.Index;
        int mpd = a.Calendar.MinutesPerDay;
        bool complete = a.Status == ActivityStatus.Complete, started = a.Status != ActivityStatus.NotStarted;
        bool critical = r.IsCritical(s, j);
        long tf = r.TF[j];
        var rest = critical ? GanttBarKind.Critical : GanttBarKind.Remaining;
        long es = r.ES[j], ef = r.EF[j];
        long aStart = a.ActualStart != Time.None ? a.ActualStart : es;
        long aFinish = a.ActualFinish != Time.None ? a.ActualFinish : ef;

        var bars = new List<GanttBar>();
        long milestone = Time.None, from, to;
        string startText, finishText;
        if (a.IsMilestone)
        {
            bool atStart = a.Type == ActivityType.StartMilestone;
            milestone = complete ? (atStart ? aStart : aFinish) : (atStart ? es : ef);
            from = to = milestone;
            string when = Date(milestone) + (complete ? " A" : "");
            startText = atStart ? when : "";
            finishText = atStart ? "" : when;
        }
        else if (complete)
        {
            from = aStart;
            to = aFinish;
            bars.Add(new GanttBar(GanttBarKind.Actual, from, to));
            startText = Date(from) + " A";
            finishText = Date(to) + " A";
        }
        else if (started)
        {
            from = aStart;
            to = ef;
            long dd = s.Settings.DataDate;
            if (dd != Time.None && dd > from) bars.Add(new GanttBar(GanttBarKind.Actual, from, Math.Min(dd, ef)));
            if (ef > r.RS[j]) bars.Add(new GanttBar(rest, r.RS[j], ef));
            startText = Date(from) + " A";
            finishText = Date(ef);
        }
        else
        {
            from = es;
            to = ef;
            if (ef > es) bars.Add(new GanttBar(rest, es, ef));
            startText = Date(es);
            finishText = Date(ef);
        }
        if (!complete && tf != Time.None && tf > 0 && r.LF[j] != Time.None && r.LF[j] > ef)
            bars.Add(new GanttBar(GanttBarKind.Float, ef, r.LF[j]));

        var row = new GanttRow
        {
            Kind = GanttRowKind.Activity, Key = "a:" + j.ToString(Inv), Activity = j, Id = a.Code, Name = a.Name,
            OriginalDuration = Days(a.OriginalDuration, mpd), RemainingDuration = Days(complete ? 0 : a.RemainingDuration, mpd),
            Start = startText, Finish = finishText, TotalFloat = complete || tf == Time.None ? "" : Days(tf, mpd),
            Critical = critical, NegativeFloat = !complete && tf != Time.None && tf < 0,
            Milestone = milestone, MilestoneKind = complete ? GanttBarKind.Actual : rest,
            From = from, To = to, SortKey = from, Started = started, Complete = complete,
        };
        row.Bars.AddRange(bars);
        return row;
    }

    /// <summary>The timescale for a zoom level: finer tiers as the pixels per day grow, as P6 switches its timescale.</summary>
    public static GanttTimescale Timescale(long start, long end, double pixelsPerDay)
    {
        static DateTime Day(DateTime d) => d.Date;
        static DateTime Week(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // weeks start on Monday
        static DateTime Month(DateTime d) => new(d.Year, d.Month, 1);
        static DateTime Quarter(DateTime d) => new(d.Year, (d.Month - 1) / 3 * 3 + 1, 1);
        static DateTime Year(DateTime d) => new(d.Year, 1, 1);
        static DateTime Decade(DateTime d) => new(d.Year / 10 * 10, 1, 1);
        static string Q(DateTime d) => "Q" + ((d.Month - 1) / 3 + 1).ToString(Inv);

        List<GanttTick> Ticks(Func<DateTime, DateTime> floor, Func<DateTime, DateTime> next, Func<DateTime, string> label)
        {
            var list = new List<GanttTick>();
            for (var t = floor(Time.FromMinutes(start)); Time.ToMinutes(t) < end; t = next(t))
            {
                long a = Math.Max(start, Time.ToMinutes(t)), b = Math.Min(end, Time.ToMinutes(next(t)));
                if (b > a) list.Add(new GanttTick(a, b, label(t)));
            }
            return list;
        }

        if (pixelsPerDay >= 14)
            return new("Week / Day", Ticks(Week, d => d.AddDays(7), d => d.ToString("dd-MMM-yy", Inv)),
                Ticks(Day, d => d.AddDays(1), d => "MTWTFSS"[((int)d.DayOfWeek + 6) % 7].ToString()));
        if (pixelsPerDay >= 2.5)
            return new("Month / Week", Ticks(Month, d => d.AddMonths(1), d => d.ToString("MMM yyyy", Inv)),
                Ticks(Week, d => d.AddDays(7), d => d.ToString("dd", Inv)));
        if (pixelsPerDay >= 0.75)
            return new("Quarter / Month", Ticks(Quarter, d => d.AddMonths(3), d => Q(d) + " " + d.Year.ToString(Inv)),
                Ticks(Month, d => d.AddMonths(1), d => d.ToString("MMM", Inv)));
        if (pixelsPerDay >= 0.18)
            return new("Year / Quarter", Ticks(Year, d => d.AddYears(1), d => d.Year.ToString(Inv)),
                Ticks(Quarter, d => d.AddMonths(3), Q));
        bool narrow = 365 * pixelsPerDay < 34; // too little room for four digits: '26
        return new("Decade / Year", Ticks(Decade, d => d.AddYears(10), d => d.Year.ToString(Inv) + "s"),
            Ticks(Year, d => d.AddYears(1), d => narrow ? "’" + (d.Year % 100).ToString("00", Inv) : d.Year.ToString(Inv)));
    }
}
