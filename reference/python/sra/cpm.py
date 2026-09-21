"""Critical Path Method engine (forward + backward pass) with P6 semantics.

Supported: FS/SS/FF/SF with lags on the configured lag calendar, per-activity
calendars, data date, retained logic / progress override, constraints
(start/finish on, on-or-after, on-or-before, mandatory start/finish),
start & finish milestones, LOE / WBS summary (non-driving), external predecessors
held at their P6 dates, must-finish-by date, finish/start/min float.
Not yet supported (flagged by the validator): ALAP, resource levelling,
expected finish, 'make open-ended activities critical'.
"""
from .model import (FS, SS, FF, SF, NOT_STARTED, IN_PROGRESS, COMPLETE, START_MILE, FINISH_MILE,
                    FLOAT_FINISH, FLOAT_START, FLOAT_MIN, lag_calendar)

FWD_START_MIN = ("CS_MSOA", "CS_MSO")
FWD_FINISH_MIN = ("CS_MEOA", "CS_MEO")
BWD_START_MAX = ("CS_MSOB", "CS_MSO")
BWD_FINISH_MAX = ("CS_MEOB", "CS_MEO")
MAND_START = "CS_MANDSTART"
MAND_FINISH = "CS_MANDFIN"
KNOWN_CSTR = set(FWD_START_MIN + FWD_FINISH_MIN + BWD_START_MAX + BWD_FINISH_MAX + (MAND_START, MAND_FINISH))


class ScheduleLoopError(Exception):
    def __init__(self, codes):
        super().__init__("logic loop among: " + ", ".join(codes[:20]) + (" ..." if len(codes) > 20 else ""))
        self.codes = codes


class CpmResult:
    __slots__ = ("es", "ef", "ls", "lf", "rs", "tf", "project_finish", "project_lf")

    def critical(self, s, j):
        tf = self.tf[j]
        return tf is not None and tf <= s.settings.critical_float


class CpmEngine:
    """Prepared once per schedule; run() may be called many times with different durations."""

    def __init__(self, s):
        self.s = s
        acts = s.activities
        n = len(acts)
        self.n = n
        self.driving = [not a.is_summary for a in acts]
        # relationship tuples between driving activities
        self.pin = [[] for _ in range(n)]    # (pred, type, lag, lagcal) ; pred = -1 external (with dates)
        self.pout = [[] for _ in range(n)]   # (succ, type, lag, lagcal)
        self.summary_links = []
        for r in s.relationships:
            lc = lag_calendar(s, r)
            if r.pred >= 0 and (not self.driving[r.pred] or not self.driving[r.succ]):
                self.summary_links.append((r.pred, r.succ, r.type, r.lag, lc))
                continue
            if r.pred < 0:
                if not self.driving[r.succ]:
                    continue
                self.pin[r.succ].append((-1, r.type, r.lag, lc, r.ext_pred_dates))
            else:
                self.pin[r.succ].append((r.pred, r.type, r.lag, lc, None))
                self.pout[r.pred].append((r.succ, r.type, r.lag, lc))
        # constraints
        self.fwd = [None] * n
        self.bwd = [None] * n
        for a in acts:
            smin = fmin = smax = fmax = ms = mf = None
            for c in (a.cstr, a.cstr2):
                if not c or c[1] is None:
                    continue
                code, d = c
                if code in FWD_START_MIN:
                    smin = d if smin is None else max(smin, d)
                if code in FWD_FINISH_MIN:
                    fmin = d if fmin is None else max(fmin, d)
                if code in BWD_START_MAX:
                    smax = d if smax is None else min(smax, d)
                if code in BWD_FINISH_MAX:
                    fmax = d if fmax is None else min(fmax, d)
                if code == MAND_START:
                    ms = d
                if code == MAND_FINISH:
                    mf = d
            self.fwd[a.idx] = (smin, fmin, ms, mf)
            self.bwd[a.idx] = (smax, fmax, ms, mf)
        self.order = self._topo()

    def _topo(self):
        n = self.n
        indeg = [0] * n
        for j in range(n):
            if self.driving[j]:
                indeg[j] = sum(1 for p in self.pin[j] if p[0] >= 0)
        ready = [j for j in range(n) if self.driving[j] and indeg[j] == 0]
        ready.reverse()
        order = []
        while ready:
            j = ready.pop()
            order.append(j)
            for (k, _, _, _) in self.pout[j]:
                indeg[k] -= 1
                if indeg[k] == 0:
                    ready.append(k)
        expected = sum(1 for d in self.driving if d)
        if len(order) != expected:
            placed = set(order)
            loop = [self.s.activities[j].code for j in range(n) if self.driving[j] and j not in placed]
            raise ScheduleLoopError(loop)
        return order

    # ------------------------------------------------------------------ run
    def run(self, durations=None, backward=True):
        s = self.s
        acts = s.activities
        n = self.n
        dd = s.settings.data_date
        retained = s.settings.retained_logic
        es = [None] * n
        ef = [None] * n
        rs = [None] * n
        dur = durations if durations is not None else [a.rem_dur for a in acts]

        for j in self.order:
            a = acts[j]
            cal = a.cal
            if a.status == COMPLETE:
                es[j] = a.act_start
                ef[j] = a.act_finish
                continue
            start_c = dd
            finish_c = None
            if a.status == NOT_STARTED or retained:
                for (p, rt, lag, lc, ext) in self.pin[j]:
                    if p >= 0:
                        pes = es[p]
                        pef = ef[p]
                    else:
                        pes, pef = ext
                        if pes is None or pef is None:
                            continue
                    if rt == FS:
                        c = lc.shift(pef, lag)
                        if c > start_c:
                            start_c = c
                    elif rt == SS:
                        c = lc.shift(pes, lag)
                        if c > start_c:
                            start_c = c
                    elif rt == FF:
                        c = lc.shift(pef, lag)
                        if finish_c is None or c > finish_c:
                            finish_c = c
                    else:  # SF
                        c = lc.shift(pes, lag)
                        if finish_c is None or c > finish_c:
                            finish_c = c
            d = dur[j]
            if a.status == IN_PROGRESS:
                es[j] = a.act_start
                r0 = cal.snap_start(start_c)
                if finish_c is not None and d > 0:
                    alt = cal.sub_work(finish_c, d)
                    if alt > r0:
                        r0 = alt
                rs[j] = r0
                ef[j] = cal.add_work(r0, d) if d > 0 else cal.snap_finish(max(r0, finish_c) if finish_c else r0)
                continue
            smin, fmin, ms, mf = self.fwd[j]
            if smin is not None and smin > start_c:
                start_c = smin
            if fmin is not None and (finish_c is None or fmin > finish_c):
                finish_c = fmin
            if a.type == FINISH_MILE:
                c = start_c if finish_c is None or start_c > finish_c else finish_c
                if mf is not None:
                    c = mf
                elif ms is not None:
                    c = ms
                t = cal.snap_finish(c)
                es[j] = ef[j] = rs[j] = t
                continue
            if d <= 0:  # start milestone or zero-duration task
                c = start_c if finish_c is None or start_c > finish_c else finish_c
                if ms is not None:
                    c = ms
                elif mf is not None:
                    c = mf
                t = cal.snap_start(c)
                es[j] = ef[j] = rs[j] = t
                continue
            e0 = cal.snap_start(start_c)
            if finish_c is not None:
                alt = cal.sub_work(finish_c, d)
                if alt > e0:
                    e0 = alt
            if ms is not None:
                e0 = cal.snap_start(ms)
            f0 = cal.add_work(e0, d)
            if mf is not None:
                f0 = cal.snap_finish(mf)
                e0 = cal.sub_work(f0, d)
            es[j] = rs[j] = e0
            ef[j] = f0

        res = CpmResult()
        res.es, res.ef, res.rs = es, ef, rs
        pf = None
        for j in self.order:
            if pf is None or ef[j] > pf:
                pf = ef[j]
        res.project_finish = pf
        res.ls = res.lf = res.tf = None
        res.project_lf = None
        self._summaries(res, dur)
        if backward:
            self._backward(res, dur)
        return res

    def _summaries(self, res, dur):
        """LOE / WBS summary: start from predecessors, finish from successors. Non-driving."""
        s = self.s
        acts = s.activities
        dd = s.settings.data_date
        starts = {}
        finishes = {}
        for (p, q, rt, lag, lc) in self.summary_links:
            if self.driving[p] and not self.driving[q] and res.ef[p] is not None:
                # driving predecessor -> summary
                if rt == FS:
                    c = lc.shift(res.ef[p], lag)
                    starts[q] = max(starts.get(q, c), c)
                elif rt == SS:
                    c = lc.shift(res.es[p], lag)
                    starts[q] = max(starts.get(q, c), c)
                elif rt == FF:
                    c = lc.shift(res.ef[p], lag)
                    finishes[q] = max(finishes.get(q, c), c)
                else:
                    c = lc.shift(res.es[p], lag)
                    finishes[q] = max(finishes.get(q, c), c)
            elif self.driving[q] and not self.driving[p] and res.ef[q] is not None:
                # summary -> driving successor: summary finish follows the successor
                if rt == FS:
                    c = lc.shift_back(res.es[q], lag)
                elif rt == FF:
                    c = lc.shift_back(res.ef[q], lag)
                else:
                    continue
                finishes[p] = max(finishes.get(p, c), c)
        for a in acts:
            j = a.idx
            if self.driving[j]:
                continue
            cal = a.cal
            if a.status == COMPLETE:
                res.es[j], res.ef[j] = a.act_start, a.act_finish
                continue
            st = starts.get(j)
            st = cal.snap_start(max(st, dd) if st is not None else dd)
            if a.status == IN_PROGRESS:
                res.es[j] = a.act_start
            else:
                res.es[j] = st
            res.rs[j] = st
            fi = finishes.get(j)
            res.ef[j] = cal.snap_finish(fi) if fi is not None and fi > st else cal.add_work(st, dur[j])

    def _backward(self, res, dur):
        s = self.s
        acts = s.activities
        n = self.n
        ls = [None] * n
        lf = [None] * n
        tf = [None] * n
        plf = s.settings.must_finish_by if s.settings.must_finish_by is not None else res.project_finish
        res.project_lf = plf
        ftype = s.settings.float_type
        for j in reversed(self.order):
            a = acts[j]
            if a.status == COMPLETE:
                continue
            cal = a.cal
            lf_c = None
            ls_c = None
            has_succ = False
            for (q, rt, lag, lc) in self.pout[j]:
                if acts[q].status == COMPLETE or lf[q] is None:
                    continue
                has_succ = True
                if rt == FS:
                    c = lc.shift_back(ls[q], lag)
                    if lf_c is None or c < lf_c:
                        lf_c = c
                elif rt == SS:
                    c = lc.shift_back(ls[q], lag)
                    if ls_c is None or c < ls_c:
                        ls_c = c
                elif rt == FF:
                    c = lc.shift_back(lf[q], lag)
                    if lf_c is None or c < lf_c:
                        lf_c = c
                else:
                    c = lc.shift_back(lf[q], lag)
                    if ls_c is None or c < ls_c:
                        ls_c = c
            if not has_succ:
                if lf_c is None or plf < lf_c:
                    lf_c = plf
            smax, fmax, ms, mf = self.bwd[j]
            if a.status == NOT_STARTED:
                if smax is not None and (ls_c is None or smax < ls_c):
                    ls_c = smax
                if fmax is not None and (lf_c is None or fmax < lf_c):
                    lf_c = fmax
            d = dur[j]
            if a.type == FINISH_MILE or (d <= 0 and a.type != START_MILE):
                c = lf_c
                if ls_c is not None and (c is None or ls_c < c):
                    c = ls_c
                if a.status == NOT_STARTED and mf is not None:
                    c = mf
                t = cal.snap_finish(c)
                lf[j] = ls[j] = t
            elif d <= 0:  # start milestone
                c = lf_c
                if ls_c is not None and (c is None or ls_c < c):
                    c = ls_c
                if a.status == NOT_STARTED and ms is not None:
                    c = ms
                t = cal.time_start(cal.work_at(c))
                lf[j] = ls[j] = t
            else:
                f = cal.snap_finish(lf_c) if lf_c is not None else None
                if ls_c is not None:
                    alt = cal.time_finish(cal.work_at(ls_c) + d)
                    if f is None or alt < f:
                        f = alt
                if a.status == NOT_STARTED:
                    if mf is not None:
                        f = cal.snap_finish(mf)
                    elif ms is not None:
                        f = cal.add_work(cal.snap_start(ms), d)
                lf[j] = f
                ls[j] = cal.sub_work(f, d)
            ff = cal.work_at(lf[j]) - cal.work_at(res.ef[j])
            sf = cal.work_at(ls[j]) - cal.work_at(res.rs[j])
            tf[j] = ff if ftype == FLOAT_FINISH else sf if ftype == FLOAT_START else min(ff, sf)
        res.ls, res.lf, res.tf = ls, lf, tf
