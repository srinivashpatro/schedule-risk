"""Working-time calendars.

Times are integer minutes since 2000-01-01 00:00 (local, no time zones - P6 has none).
A calendar is compiled into sorted, non-overlapping working intervals [start, end)
over a horizon, with the cumulative working minutes before each interval, so every
date calculation is a binary search.

Two kinds of instant matter in CPM:
  start-type  - the moment work can begin (08:00 Tuesday)
  finish-type - the moment work ends     (17:00 Monday)
They are the same point in working time; we keep them distinct so dates print the
way P6 prints them.
"""
import bisect
import datetime as _dt

EPOCH = _dt.datetime(2000, 1, 1)
EXCEL_EPOCH = _dt.date(1899, 12, 30)
MIN_PER_DAY = 1440


def to_min(dt):
    return int((dt - EPOCH).total_seconds() // 60)


def from_min(m):
    return EPOCH + _dt.timedelta(minutes=m)


def parse_p6_date(s):
    """'2026-01-05 08:00' (seconds optional). Empty -> None."""
    s = (s or "").strip()
    if not s:
        return None
    for fmt in ("%Y-%m-%d %H:%M", "%Y-%m-%d %H:%M:%S", "%Y-%m-%d"):
        try:
            return to_min(_dt.datetime.strptime(s, fmt))
        except ValueError:
            pass
    raise ValueError(f"unrecognised date {s!r}")


def fmt_min(m):
    if m is None:
        return ""
    return from_min(m).strftime("%Y-%m-%d %H:%M")


# ---------------------------------------------------------------- clndr_data parser

class _Node:
    __slots__ = ("key", "attrs", "children")

    def __init__(self, key, attrs, children):
        self.key, self.attrs, self.children = key, attrs, children


def _parse_nodes(s, i):
    """Parse a sequence of '(0||key(attrs)(children))' nodes starting at s[i]. Returns (nodes, i)."""
    nodes = []
    n = len(s)
    while i < n:
        while i < n and s[i] in " \t\r\n":
            i += 1
        if i >= n or s[i] != "(":
            break
        # '(' id '||' key '(' attrs ')' '(' children ')' ')'
        i += 1
        bar = s.find("||", i)
        if bar < 0:
            raise ValueError("calendar data: missing '||'")
        i = bar + 2
        p = s.find("(", i)
        key = s[i:p].strip()
        q = s.find(")", p)
        attrs = s[p + 1:q]
        i = q + 1
        while i < n and s[i] in " \t\r\n":
            i += 1
        if i < n and s[i] == "(":
            children, i = _parse_nodes(s, i + 1)
            while i < n and s[i] in " \t\r\n":
                i += 1
            if i < n and s[i] == ")":
                i += 1  # close children
        else:
            children = []
        while i < n and s[i] in " \t\r\n":
            i += 1
        if i < n and s[i] == ")":
            i += 1  # close node
        nodes.append(_Node(key, attrs, children))
    return nodes, i


def _attr_map(attrs):
    parts = attrs.split("|")
    out = {}
    for k in range(0, len(parts) - 1, 2):
        out[parts[k].strip()] = parts[k + 1].strip()
    return out


def _hhmm(s):
    h, m = s.split(":")
    return int(h) * 60 + int(m)


def _shifts(children):
    out = []
    for ch in children:
        a = _attr_map(ch.attrs)
        if "s" in a and "f" in a:
            st, fi = _hhmm(a["s"]), _hhmm(a["f"])
            if fi <= st:
                fi += MIN_PER_DAY  # f|00:00 means midnight at end of day
            out.append((st, fi))
    out.sort()
    return out


def _find(nodes, key):
    for nd in nodes:
        if nd.key == key:
            return nd
    return None


DEFAULT_WEEK = {0: [(480, 720), (780, 1020)], 1: [(480, 720), (780, 1020)], 2: [(480, 720), (780, 1020)],
                3: [(480, 720), (780, 1020)], 4: [(480, 720), (780, 1020)], 5: [], 6: []}


def parse_clndr_data(text):
    """Returns (week, exceptions, warnings).
    week: {weekday 0=Mon..6=Sun: [(start_min, end_min)]}
    exceptions: {date: [(start_min, end_min)]}  (empty list = non-work day)
    """
    warnings = []
    s = "".join(" " if (ch < " " or ch == "\x7f") else ch for ch in (text or ""))
    if not s.strip():
        return dict(DEFAULT_WEEK), {}, ["empty calendar data; Mon-Fri 08:00-17:00 assumed"]
    nodes, _ = _parse_nodes(s, 0)
    root = _find(nodes, "CalendarData")
    top = root.children if root else nodes
    week = {d: [] for d in range(7)}
    dow = _find(top, "DaysOfWeek")
    if dow is None:
        warnings.append("no DaysOfWeek in calendar data; Mon-Fri 08:00-17:00 assumed")
        week = dict(DEFAULT_WEEK)
    else:
        for d in dow.children:
            try:
                p6day = int(d.key)  # 1 = Sunday ... 7 = Saturday
            except ValueError:
                continue
            wd = (p6day + 5) % 7  # Sunday(1)->6, Monday(2)->0
            week[wd] = _shifts(d.children)
    exceptions = {}
    exc = _find(top, "Exceptions")
    if exc is not None:
        for e in exc.children:
            a = _attr_map(e.attrs)
            if "d" not in a:
                continue
            try:
                day = EXCEL_EPOCH + _dt.timedelta(days=int(a["d"]))
            except ValueError:
                warnings.append(f"bad exception date {a['d']!r}")
                continue
            exceptions[day] = _shifts(e.children)
    return week, exceptions, warnings


# ---------------------------------------------------------------- compiled calendar

class WorkCalendar:
    def __init__(self, cal_id, name, week, exceptions, horizon_start, horizon_end, hours_per_day=8.0):
        self.id = cal_id
        self.name = name
        self.week = week
        self.exceptions = exceptions
        self.hours_per_day = hours_per_day or 8.0
        self.h0 = horizon_start
        self.h1 = horizon_end
        self._compile()

    @classmethod
    def twenty_four_hour(cls, horizon_start, horizon_end):
        wk = {d: [(0, MIN_PER_DAY)] for d in range(7)}
        return cls("__24h__", "24 Hour", wk, {}, horizon_start, horizon_end, 24.0)

    def _compile(self):
        starts, ends = [], []
        d0 = from_min(self.h0).date()
        d1 = from_min(self.h1).date()
        day = d0
        one = _dt.timedelta(days=1)
        while day <= d1:
            shifts = self.exceptions.get(day)
            if shifts is None:
                shifts = self.week.get(day.weekday(), [])
            base = to_min(_dt.datetime(day.year, day.month, day.day))
            for st, fi in shifts:
                a, b = base + st, base + fi
                if starts and a <= ends[-1]:  # merge contiguous / overlapping (e.g. 24h, midnight shifts)
                    if b > ends[-1]:
                        ends[-1] = b
                else:
                    starts.append(a)
                    ends.append(b)
            day += one
        if not starts:
            raise ValueError(f"calendar {self.name!r} has no working time")
        cum = [0] * len(starts)
        cum_end = [0] * len(starts)
        total = 0
        for i in range(len(starts)):
            cum[i] = total
            total += ends[i] - starts[i]
            cum_end[i] = total
        self.starts, self.ends, self.cum, self.cum_end = starts, ends, cum, cum_end
        self.total = total

    def _check(self, t):
        if t < self.starts[0] or t > self.ends[-1]:
            raise OverflowError(f"date {fmt_min(t)} outside calendar horizon of {self.name!r}")

    # cumulative working minutes up to instant t
    def work_at(self, t):
        if t <= self.starts[0]:
            if t < self.starts[0] - 0:
                self._check(t)
            return 0
        if t >= self.ends[-1]:
            self._check(t)
            return self.total
        i = bisect.bisect_right(self.starts, t) - 1
        e = self.ends[i]
        return self.cum[i] + (t if t < e else e) - self.starts[i]

    def time_finish(self, w):
        """Earliest instant with cumulative work w (finish-type)."""
        if w <= 0:
            return self.starts[0]
        if w > self.total:
            raise OverflowError(f"work beyond calendar horizon of {self.name!r}")
        i = bisect.bisect_left(self.cum_end, w)
        return self.starts[i] + (w - self.cum[i])

    def time_start(self, w):
        """Latest instant with cumulative work w that is a working moment (start-type)."""
        if w < 0:
            raise OverflowError(f"work before calendar horizon of {self.name!r}")
        if w >= self.total:
            return self.ends[-1]
        i = bisect.bisect_right(self.cum_end, w)
        return self.starts[i] + (w - self.cum[i])

    def snap_start(self, t):
        self._check(t)
        i = bisect.bisect_right(self.starts, t) - 1
        if i >= 0 and t < self.ends[i]:
            return t
        if i + 1 < len(self.starts):
            return self.starts[i + 1]
        return self.ends[-1]

    def snap_finish(self, t):
        self._check(t)
        i = bisect.bisect_left(self.starts, t) - 1
        if i < 0:
            return self.starts[0]
        if t <= self.ends[i]:
            return t
        return self.ends[i]

    def add_work(self, start, minutes):
        """From a start-type instant, perform `minutes` of work; returns finish-type instant."""
        if minutes <= 0:
            return self.snap_start(start) if minutes == 0 else self.time_start(self.work_at(start) + minutes)
        return self.time_finish(self.work_at(start) + minutes)

    def sub_work(self, finish, minutes):
        """From a finish-type instant, go back `minutes` of work; returns start-type instant."""
        if minutes <= 0:
            return self.snap_finish(finish) if minutes == 0 else self.time_finish(self.work_at(finish) - minutes)
        return self.time_start(self.work_at(finish) - minutes)

    def shift(self, t, lag):
        """Move instant t by `lag` working minutes (lag may be negative). Used for relationship lags."""
        if lag == 0:
            return t
        w = self.work_at(t) + lag
        return self.time_finish(w) if lag > 0 else self.time_start(w)

    def shift_back(self, t, lag):
        """Backward-pass counterpart of shift: move instant t earlier by `lag` working minutes."""
        if lag == 0:
            return t
        w = self.work_at(t) - lag
        return self.time_start(w) if lag > 0 else self.time_finish(w)

    def work_between(self, a, b):
        return self.work_at(b) - self.work_at(a)

    def minutes_per_day(self):
        return int(round(self.hours_per_day * 60))
