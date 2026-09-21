using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Core.Cpm;

public sealed class ScheduleLoopException : Exception
{
    public ScheduleLoopException(IReadOnlyList<string> codes)
        : base("logic loop among: " + string.Join(", ", codes.Take(20)) + (codes.Count > 20 ? " ..." : ""))
    {
        Codes = codes;
    }

    public IReadOnlyList<string> Codes { get; }
}

/// <summary>
/// Results of one CPM calculation. All arrays are indexed by activity index; instants are
/// minutes (see <see cref="Time"/>), <see cref="Time.None"/> where not applicable.
/// Float is in working minutes on the activity's calendar.
/// </summary>
public sealed class CpmResult
{
    public CpmResult(int n)
    {
        ES = new long[n]; EF = new long[n]; RS = new long[n];
        LS = new long[n]; LF = new long[n]; TF = new long[n];
    }

    public long[] ES { get; }
    public long[] EF { get; }
    /// <summary>Remaining early start (equals ES for not-started work).</summary>
    public long[] RS { get; }
    public long[] LS { get; }
    public long[] LF { get; }
    public long[] TF { get; }
    public bool HasBackward { get; internal set; }
    public long ProjectFinish { get; internal set; } = Time.None;
    public long ProjectLateFinish { get; internal set; } = Time.None;

    public bool IsCritical(Schedule s, int j) => TF[j] != Time.None && TF[j] <= s.Settings.CriticalFloat;
}

/// <summary>
/// Critical Path Method engine with P6 semantics. Prepared once per schedule; <see cref="Run"/>
/// can be called many times (from many threads, each with its own <see cref="CpmResult"/>).
/// Twin of cpm.py - any change must be made in both.
/// </summary>
public sealed class CpmEngine
{
    public static readonly string[] FwdStartMin = { "CS_MSOA", "CS_MSO" };
    public static readonly string[] FwdFinishMin = { "CS_MEOA", "CS_MEO" };
    public static readonly string[] BwdStartMax = { "CS_MSOB", "CS_MSO" };
    public static readonly string[] BwdFinishMax = { "CS_MEOB", "CS_MEO" };
    public const string MandStart = "CS_MANDSTART";
    public const string MandFinish = "CS_MANDFIN";

    public static readonly HashSet<string> KnownConstraints = new(
        FwdStartMin.Concat(FwdFinishMin).Concat(BwdStartMax).Concat(BwdFinishMax).Append(MandStart).Append(MandFinish),
        StringComparer.Ordinal);

    private readonly struct InLink
    {
        public InLink(int pred, RelType type, long lag, WorkCalendar cal, long extStart, long extFinish)
        { Pred = pred; Type = type; Lag = lag; Cal = cal; ExtStart = extStart; ExtFinish = extFinish; }
        public readonly int Pred;
        public readonly RelType Type;
        public readonly long Lag;
        public readonly WorkCalendar Cal;
        public readonly long ExtStart;
        public readonly long ExtFinish;
    }

    private readonly struct OutLink
    {
        public OutLink(int succ, RelType type, long lag, WorkCalendar cal) { Succ = succ; Type = type; Lag = lag; Cal = cal; }
        public readonly int Succ;
        public readonly RelType Type;
        public readonly long Lag;
        public readonly WorkCalendar Cal;
    }

    private readonly struct SummaryLink
    {
        public SummaryLink(int p, int q, RelType type, long lag, WorkCalendar cal) { P = p; Q = q; Type = type; Lag = lag; Cal = cal; }
        public readonly int P, Q;
        public readonly RelType Type;
        public readonly long Lag;
        public readonly WorkCalendar Cal;
    }

    private readonly Schedule _s;
    private readonly int _n;
    private readonly bool[] _driving;
    private readonly InLink[][] _pin;
    private readonly OutLink[][] _pout;
    private readonly SummaryLink[] _summaryLinks;
    private readonly long[] _smin, _fmin, _smax, _fmax, _ms, _mf;
    private readonly int[] _order;

    public CpmEngine(Schedule s)
    {
        _s = s;
        var acts = s.Activities;
        _n = acts.Count;
        _driving = new bool[_n];
        for (int j = 0; j < _n; j++) _driving[j] = !acts[j].IsSummary;
        var pin = new List<InLink>[_n];
        var pout = new List<OutLink>[_n];
        for (int j = 0; j < _n; j++) { pin[j] = new List<InLink>(); pout[j] = new List<OutLink>(); }
        var sl = new List<SummaryLink>();
        foreach (var r in s.Relationships)
        {
            var lc = s.LagCalendarFor(r);
            if (r.Pred >= 0 && (!_driving[r.Pred] || !_driving[r.Succ]))
            {
                sl.Add(new SummaryLink(r.Pred, r.Succ, r.Type, r.Lag, lc));
                continue;
            }
            if (r.Pred < 0)
            {
                if (!_driving[r.Succ]) continue;
                pin[r.Succ].Add(new InLink(-1, r.Type, r.Lag, lc, r.ExternalStart, r.ExternalFinish));
            }
            else
            {
                pin[r.Succ].Add(new InLink(r.Pred, r.Type, r.Lag, lc, Time.None, Time.None));
                pout[r.Pred].Add(new OutLink(r.Succ, r.Type, r.Lag, lc));
            }
        }
        _pin = pin.Select(x => x.ToArray()).ToArray();
        _pout = pout.Select(x => x.ToArray()).ToArray();
        _summaryLinks = sl.ToArray();

        _smin = Fill(_n); _fmin = Fill(_n); _smax = Fill(_n); _fmax = Fill(_n); _ms = Fill(_n); _mf = Fill(_n);
        foreach (var a in acts)
        {
            int j = a.Index;
            foreach (var c in new[] { a.Constraint1, a.Constraint2 })
            {
                if (c == null || c.Value.Date == Time.None) continue;
                string code = c.Value.Type;
                long d = c.Value.Date;
                if (FwdStartMin.Contains(code)) _smin[j] = _smin[j] == Time.None ? d : Math.Max(_smin[j], d);
                if (FwdFinishMin.Contains(code)) _fmin[j] = _fmin[j] == Time.None ? d : Math.Max(_fmin[j], d);
                if (BwdStartMax.Contains(code)) _smax[j] = _smax[j] == Time.None ? d : Math.Min(_smax[j], d);
                if (BwdFinishMax.Contains(code)) _fmax[j] = _fmax[j] == Time.None ? d : Math.Min(_fmax[j], d);
                if (code == MandStart) _ms[j] = d;
                if (code == MandFinish) _mf[j] = d;
            }
        }
        _order = Topo();
    }

    public Schedule Schedule => _s;
    public IReadOnlyList<int> Order => _order;
    public bool IsDriving(int j) => _driving[j];

    private static long[] Fill(int n)
    {
        var a = new long[n];
        Array.Fill(a, Time.None);
        return a;
    }

    private int[] Topo()
    {
        var indeg = new int[_n];
        for (int j = 0; j < _n; j++)
            if (_driving[j])
                foreach (var p in _pin[j]) if (p.Pred >= 0) indeg[j]++;
        var ready = new List<int>();
        for (int j = 0; j < _n; j++) if (_driving[j] && indeg[j] == 0) ready.Add(j);
        ready.Reverse();
        var order = new List<int>(_n);
        while (ready.Count > 0)
        {
            int j = ready[^1];
            ready.RemoveAt(ready.Count - 1);
            order.Add(j);
            foreach (var o in _pout[j])
            {
                if (--indeg[o.Succ] == 0) ready.Add(o.Succ);
            }
        }
        int expected = _driving.Count(d => d);
        if (order.Count != expected)
        {
            var placed = new HashSet<int>(order);
            var loop = new List<string>();
            for (int j = 0; j < _n; j++) if (_driving[j] && !placed.Contains(j)) loop.Add(_s.Activities[j].Code);
            throw new ScheduleLoopException(loop);
        }
        return order.ToArray();
    }

    public long[] BaseDurations() => _s.Activities.Select(a => a.RemainingDuration).ToArray();

    public CpmResult Run(long[]? durations = null, bool backward = true)
    {
        var res = new CpmResult(_n);
        Run(durations ?? BaseDurations(), res, backward);
        return res;
    }

    /// <summary>Allocation-free run into a caller-owned result (used by the simulation).</summary>
    public void Run(long[] dur, CpmResult res, bool backward)
    {
        var acts = _s.Activities;
        long dd = _s.Settings.DataDate;
        bool retained = _s.Settings.RetainedLogic;
        long[] es = res.ES, ef = res.EF, rs = res.RS;
        Array.Fill(es, Time.None);
        Array.Fill(ef, Time.None);
        Array.Fill(rs, Time.None);

        foreach (int j in _order)
        {
            var a = acts[j];
            var cal = a.Calendar;
            if (a.Status == ActivityStatus.Complete)
            {
                es[j] = a.ActualStart;
                ef[j] = a.ActualFinish;
                continue;
            }
            long startC = dd;
            long finishC = Time.None;
            if (a.Status == ActivityStatus.NotStarted || retained)
            {
                foreach (var p in _pin[j])
                {
                    long pes, pef;
                    if (p.Pred >= 0) { pes = es[p.Pred]; pef = ef[p.Pred]; }
                    else
                    {
                        pes = p.ExtStart; pef = p.ExtFinish;
                        if (pes == Time.None || pef == Time.None) continue;
                    }
                    long c;
                    switch (p.Type)
                    {
                        case RelType.FS:
                            c = p.Cal.Shift(pef, p.Lag);
                            if (c > startC) startC = c;
                            break;
                        case RelType.SS:
                            c = p.Cal.Shift(pes, p.Lag);
                            if (c > startC) startC = c;
                            break;
                        case RelType.FF:
                            c = p.Cal.Shift(pef, p.Lag);
                            if (finishC == Time.None || c > finishC) finishC = c;
                            break;
                        default:
                            c = p.Cal.Shift(pes, p.Lag);
                            if (finishC == Time.None || c > finishC) finishC = c;
                            break;
                    }
                }
            }
            long d = dur[j];
            if (a.Status == ActivityStatus.InProgress)
            {
                es[j] = a.ActualStart;
                long r0 = cal.SnapStart(startC);
                if (finishC != Time.None && d > 0)
                {
                    long alt = cal.SubWork(finishC, d);
                    if (alt > r0) r0 = alt;
                }
                rs[j] = r0;
                if (d > 0) ef[j] = cal.AddWork(r0, d);
                else ef[j] = cal.SnapFinish(finishC != Time.None && finishC != 0 ? Math.Max(r0, finishC) : r0);
                continue;
            }
            long smin = _smin[j], fmin = _fmin[j], ms = _ms[j], mf = _mf[j];
            if (smin != Time.None && smin > startC) startC = smin;
            if (fmin != Time.None && (finishC == Time.None || fmin > finishC)) finishC = fmin;
            if (a.Type == ActivityType.FinishMilestone)
            {
                long c = finishC == Time.None || startC > finishC ? startC : finishC;
                if (mf != Time.None) c = mf;
                else if (ms != Time.None) c = ms;
                long t = cal.SnapFinish(c);
                es[j] = ef[j] = rs[j] = t;
                continue;
            }
            if (d <= 0)
            {
                long c = finishC == Time.None || startC > finishC ? startC : finishC;
                if (ms != Time.None) c = ms;
                else if (mf != Time.None) c = mf;
                long t = cal.SnapStart(c);
                es[j] = ef[j] = rs[j] = t;
                continue;
            }
            long e0 = cal.SnapStart(startC);
            if (finishC != Time.None)
            {
                long alt = cal.SubWork(finishC, d);
                if (alt > e0) e0 = alt;
            }
            if (ms != Time.None) e0 = cal.SnapStart(ms);
            long f0 = cal.AddWork(e0, d);
            if (mf != Time.None)
            {
                f0 = cal.SnapFinish(mf);
                e0 = cal.SubWork(f0, d);
            }
            es[j] = rs[j] = e0;
            ef[j] = f0;
        }

        long pf = Time.None;
        foreach (int j in _order) if (pf == Time.None || ef[j] > pf) pf = ef[j];
        res.ProjectFinish = pf;
        Summaries(res, dur);
        if (backward) Backward(res, dur);
        else
        {
            Array.Fill(res.LS, Time.None);
            Array.Fill(res.LF, Time.None);
            Array.Fill(res.TF, Time.None);
            res.HasBackward = false;
        }
    }

    private void Summaries(CpmResult res, long[] dur)
    {
        if (_summaryLinks.Length == 0 && _driving.All(x => x)) return;
        var acts = _s.Activities;
        long dd = _s.Settings.DataDate;
        var starts = new Dictionary<int, long>();
        var finishes = new Dictionary<int, long>();
        void Max(Dictionary<int, long> m, int k, long v) => m[k] = m.TryGetValue(k, out var cur) ? Math.Max(cur, v) : v;
        foreach (var l in _summaryLinks)
        {
            if (_driving[l.P] && !_driving[l.Q] && res.EF[l.P] != Time.None)
            {
                switch (l.Type)
                {
                    case RelType.FS: Max(starts, l.Q, l.Cal.Shift(res.EF[l.P], l.Lag)); break;
                    case RelType.SS: Max(starts, l.Q, l.Cal.Shift(res.ES[l.P], l.Lag)); break;
                    case RelType.FF: Max(finishes, l.Q, l.Cal.Shift(res.EF[l.P], l.Lag)); break;
                    default: Max(finishes, l.Q, l.Cal.Shift(res.ES[l.P], l.Lag)); break;
                }
            }
            else if (_driving[l.Q] && !_driving[l.P] && res.EF[l.Q] != Time.None)
            {
                if (l.Type == RelType.FS) Max(finishes, l.P, l.Cal.ShiftBack(res.ES[l.Q], l.Lag));
                else if (l.Type == RelType.FF) Max(finishes, l.P, l.Cal.ShiftBack(res.EF[l.Q], l.Lag));
            }
        }
        foreach (var a in acts)
        {
            int j = a.Index;
            if (_driving[j]) continue;
            var cal = a.Calendar;
            if (a.Status == ActivityStatus.Complete)
            {
                res.ES[j] = a.ActualStart;
                res.EF[j] = a.ActualFinish;
                continue;
            }
            long st = starts.TryGetValue(j, out var sv) ? cal.SnapStart(Math.Max(sv, dd)) : cal.SnapStart(dd);
            res.ES[j] = a.Status == ActivityStatus.InProgress ? a.ActualStart : st;
            res.RS[j] = st;
            res.EF[j] = finishes.TryGetValue(j, out var fi) && fi > st ? cal.SnapFinish(fi) : cal.AddWork(st, dur[j]);
        }
    }

    private void Backward(CpmResult res, long[] dur)
    {
        var acts = _s.Activities;
        long[] ls = res.LS, lf = res.LF, tf = res.TF;
        Array.Fill(ls, Time.None);
        Array.Fill(lf, Time.None);
        Array.Fill(tf, Time.None);
        long plf = _s.Settings.MustFinishBy != Time.None ? _s.Settings.MustFinishBy : res.ProjectFinish;
        res.ProjectLateFinish = plf;
        var ftype = _s.Settings.FloatType;
        for (int oi = _order.Length - 1; oi >= 0; oi--)
        {
            int j = _order[oi];
            var a = acts[j];
            if (a.Status == ActivityStatus.Complete) continue;
            var cal = a.Calendar;
            long lfC = Time.None, lsC = Time.None;
            bool hasSucc = false;
            foreach (var o in _pout[j])
            {
                int q = o.Succ;
                if (acts[q].Status == ActivityStatus.Complete || lf[q] == Time.None) continue;
                hasSucc = true;
                long c;
                switch (o.Type)
                {
                    case RelType.FS:
                        c = o.Cal.ShiftBack(ls[q], o.Lag);
                        if (lfC == Time.None || c < lfC) lfC = c;
                        break;
                    case RelType.SS:
                        c = o.Cal.ShiftBack(ls[q], o.Lag);
                        if (lsC == Time.None || c < lsC) lsC = c;
                        break;
                    case RelType.FF:
                        c = o.Cal.ShiftBack(lf[q], o.Lag);
                        if (lfC == Time.None || c < lfC) lfC = c;
                        break;
                    default:
                        c = o.Cal.ShiftBack(lf[q], o.Lag);
                        if (lsC == Time.None || c < lsC) lsC = c;
                        break;
                }
            }
            if (!hasSucc && (lfC == Time.None || plf < lfC)) lfC = plf;
            long smax = _smax[j], fmax = _fmax[j], ms = _ms[j], mf = _mf[j];
            bool notStarted = a.Status == ActivityStatus.NotStarted;
            if (notStarted)
            {
                if (smax != Time.None && (lsC == Time.None || smax < lsC)) lsC = smax;
                if (fmax != Time.None && (lfC == Time.None || fmax < lfC)) lfC = fmax;
            }
            long d = dur[j];
            if (a.Type == ActivityType.FinishMilestone || (d <= 0 && a.Type != ActivityType.StartMilestone))
            {
                long c = lfC;
                if (lsC != Time.None && (c == Time.None || lsC < c)) c = lsC;
                if (notStarted && mf != Time.None) c = mf;
                long t = cal.SnapFinish(c);
                lf[j] = ls[j] = t;
            }
            else if (d <= 0)
            {
                long c = lfC;
                if (lsC != Time.None && (c == Time.None || lsC < c)) c = lsC;
                if (notStarted && ms != Time.None) c = ms;
                long t = cal.TimeStart(cal.WorkAt(c));
                lf[j] = ls[j] = t;
            }
            else
            {
                long f = lfC != Time.None ? cal.SnapFinish(lfC) : Time.None;
                if (lsC != Time.None)
                {
                    long alt = cal.TimeFinish(cal.WorkAt(lsC) + d);
                    if (f == Time.None || alt < f) f = alt;
                }
                if (notStarted)
                {
                    if (mf != Time.None) f = cal.SnapFinish(mf);
                    else if (ms != Time.None) f = cal.AddWork(cal.SnapStart(ms), d);
                }
                lf[j] = f;
                ls[j] = cal.SubWork(f, d);
            }
            long ff = cal.WorkAt(lf[j]) - cal.WorkAt(res.EF[j]);
            long sf = cal.WorkAt(ls[j]) - cal.WorkAt(res.RS[j]);
            tf[j] = ftype == FloatType.Finish ? ff : ftype == FloatType.Start ? sf : Math.Min(ff, sf);
        }
        res.HasBackward = true;
    }
}
