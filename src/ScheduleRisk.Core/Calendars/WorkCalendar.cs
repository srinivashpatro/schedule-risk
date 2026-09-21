using System.Globalization;

namespace ScheduleRisk.Core.Calendars;

/// <summary>
/// Instants are whole minutes since 2000-01-01 00:00 (P6 has no time zones).
/// <see cref="None"/> marks "no date".
/// </summary>
public static class Time
{
    public const long None = long.MinValue;
    public static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    public static readonly DateOnly ExcelEpoch = new(1899, 12, 30);
    public const int MinutesPerDay = 1440;

    private static readonly string[] Formats = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyy-MM-dd H:mm" };

    public static long ToMinutes(DateTime dt) => (long)Math.Floor((dt - Epoch).TotalMinutes);

    public static DateTime FromMinutes(long m) => Epoch.AddMinutes(m);

    /// <summary>Parse a P6 date ("2026-01-05 08:00"). Empty returns <see cref="None"/>.</summary>
    public static long ParseP6(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return None;
        if (DateTime.TryParseExact(s, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return ToMinutes(dt);
        throw new FormatException($"unrecognised date '{s}'");
    }

    public static long TryParseP6(string? s)
    {
        try { return ParseP6(s); }
        catch (FormatException) { return None; }
    }

    public static string Format(long m) =>
        m == None ? "" : FromMinutes(m).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>A working period within a day, in minutes from midnight (end may be 1440).</summary>
public readonly record struct WorkShift(int Start, int End);

/// <summary>
/// Working-time calendar compiled to sorted working intervals over a horizon.
/// Start-type instants are when work can begin (08:00 Tue); finish-type instants
/// are when work ends (17:00 Mon). Both are the same point in working time.
/// Twin of calendar.py WorkCalendar - keep the two in step.
/// </summary>
public sealed class WorkCalendar
{
    private long[] _starts = Array.Empty<long>();
    private long[] _ends = Array.Empty<long>();
    private long[] _cum = Array.Empty<long>();
    private long[] _cumEnd = Array.Empty<long>();

    public WorkCalendar(string id, string name, IReadOnlyDictionary<int, List<WorkShift>> week,
                        IReadOnlyDictionary<DateOnly, List<WorkShift>> exceptions,
                        long horizonStart, long horizonEnd, double hoursPerDay = 8.0)
    {
        Id = id;
        Name = name;
        Week = week;
        Exceptions = exceptions;
        HoursPerDay = hoursPerDay > 0 ? hoursPerDay : 8.0;
        HorizonStart = horizonStart;
        HorizonEnd = horizonEnd;
        Compile();
    }

    public string Id { get; }
    public string Name { get; }
    /// <summary>Weekday 0=Monday ... 6=Sunday.</summary>
    public IReadOnlyDictionary<int, List<WorkShift>> Week { get; }
    public IReadOnlyDictionary<DateOnly, List<WorkShift>> Exceptions { get; }
    public double HoursPerDay { get; }
    public long HorizonStart { get; }
    public long HorizonEnd { get; }
    public long Total { get; private set; }

    public int MinutesPerDay => (int)Math.Round(HoursPerDay * 60, MidpointRounding.ToEven);

    public static WorkCalendar TwentyFourHour(long h0, long h1)
    {
        var wk = new Dictionary<int, List<WorkShift>>();
        for (int d = 0; d < 7; d++) wk[d] = new List<WorkShift> { new WorkShift(0, Time.MinutesPerDay) };
        return new WorkCalendar("__24h__", "24 Hour", wk, new Dictionary<DateOnly, List<WorkShift>>(), h0, h1, 24.0);
    }

    private void Compile()
    {
        var starts = new List<long>();
        var ends = new List<long>();
        var d0 = DateOnly.FromDateTime(Time.FromMinutes(HorizonStart));
        var d1 = DateOnly.FromDateTime(Time.FromMinutes(HorizonEnd));
        for (var day = d0; day <= d1; day = day.AddDays(1))
        {
            if (!Exceptions.TryGetValue(day, out var shifts))
            {
                int wd = ((int)day.DayOfWeek + 6) % 7;
                shifts = Week.TryGetValue(wd, out var w) ? w : new List<WorkShift>();
            }
            long baseMin = Time.ToMinutes(day.ToDateTime(TimeOnly.MinValue));
            foreach (var sh in shifts)
            {
                long a = baseMin + sh.Start, b = baseMin + sh.End;
                if (starts.Count > 0 && a <= ends[^1])
                {
                    if (b > ends[^1]) ends[^1] = b;
                }
                else
                {
                    starts.Add(a);
                    ends.Add(b);
                }
            }
        }
        if (starts.Count == 0) throw new InvalidOperationException($"calendar '{Name}' has no working time");
        _starts = starts.ToArray();
        _ends = ends.ToArray();
        _cum = new long[_starts.Length];
        _cumEnd = new long[_starts.Length];
        long total = 0;
        for (int i = 0; i < _starts.Length; i++)
        {
            _cum[i] = total;
            total += _ends[i] - _starts[i];
            _cumEnd[i] = total;
        }
        Total = total;
    }

    // Python bisect equivalents
    private static int BisectRight(long[] a, long x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (x < a[mid]) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    private static int BisectLeft(long[] a, long x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (a[mid] < x) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private void Check(long t)
    {
        if (t < _starts[0] || t > _ends[^1])
            throw new CalendarHorizonException($"date {Time.Format(t)} outside calendar horizon of '{Name}'");
    }

    /// <summary>Cumulative working minutes up to instant t.</summary>
    public long WorkAt(long t)
    {
        if (t <= _starts[0])
        {
            if (t < _starts[0]) Check(t);
            return 0;
        }
        if (t >= _ends[^1])
        {
            Check(t);
            return Total;
        }
        int i = BisectRight(_starts, t) - 1;
        long e = _ends[i];
        return _cum[i] + (t < e ? t : e) - _starts[i];
    }

    /// <summary>Earliest instant with cumulative work w (finish-type).</summary>
    public long TimeFinish(long w)
    {
        if (w <= 0) return _starts[0];
        if (w > Total) throw new CalendarHorizonException($"work beyond calendar horizon of '{Name}'");
        int i = BisectLeft(_cumEnd, w);
        return _starts[i] + (w - _cum[i]);
    }

    /// <summary>Latest working instant with cumulative work w (start-type).</summary>
    public long TimeStart(long w)
    {
        if (w < 0) throw new CalendarHorizonException($"work before calendar horizon of '{Name}'");
        if (w >= Total) return _ends[^1];
        int i = BisectRight(_cumEnd, w);
        return _starts[i] + (w - _cum[i]);
    }

    public long SnapStart(long t)
    {
        Check(t);
        int i = BisectRight(_starts, t) - 1;
        if (i >= 0 && t < _ends[i]) return t;
        if (i + 1 < _starts.Length) return _starts[i + 1];
        return _ends[^1];
    }

    public long SnapFinish(long t)
    {
        Check(t);
        int i = BisectLeft(_starts, t) - 1;
        if (i < 0) return _starts[0];
        if (t <= _ends[i]) return t;
        return _ends[i];
    }

    /// <summary>From a start-type instant, perform minutes of work; returns a finish-type instant.</summary>
    public long AddWork(long start, long minutes)
    {
        if (minutes <= 0)
            return minutes == 0 ? SnapStart(start) : TimeStart(WorkAt(start) + minutes);
        return TimeFinish(WorkAt(start) + minutes);
    }

    /// <summary>From a finish-type instant, go back minutes of work; returns a start-type instant.</summary>
    public long SubWork(long finish, long minutes)
    {
        if (minutes <= 0)
            return minutes == 0 ? SnapFinish(finish) : TimeFinish(WorkAt(finish) - minutes);
        return TimeStart(WorkAt(finish) - minutes);
    }

    /// <summary>Move instant t by lag working minutes (relationship lag, forward pass).</summary>
    public long Shift(long t, long lag)
    {
        if (lag == 0) return t;
        long w = WorkAt(t) + lag;
        return lag > 0 ? TimeFinish(w) : TimeStart(w);
    }

    /// <summary>Backward-pass counterpart of Shift.</summary>
    public long ShiftBack(long t, long lag)
    {
        if (lag == 0) return t;
        long w = WorkAt(t) - lag;
        return lag > 0 ? TimeStart(w) : TimeFinish(w);
    }

    public long WorkBetween(long a, long b) => WorkAt(b) - WorkAt(a);
}

public sealed class CalendarHorizonException : Exception
{
    public CalendarHorizonException(string message) : base(message) { }
}
