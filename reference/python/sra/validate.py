"""Schedule health checks a risk analyst runs before trusting a schedule (DCMA-14 style)."""
from .model import (FS, NOT_STARTED, IN_PROGRESS, COMPLETE, START_MILE, FINISH_MILE)
from .cpm import KNOWN_CSTR, CpmEngine

HARD = ("CS_MSOB", "CS_MEOB", "CS_MSO", "CS_MEO", "CS_MANDSTART", "CS_MANDFIN")


class Check:
    __slots__ = ("key", "title", "count", "total", "threshold_pct", "items", "note")

    def __init__(self, key, title, count, total, threshold_pct, items, note=""):
        self.key, self.title, self.count, self.total = key, title, count, total
        self.threshold_pct, self.items, self.note = threshold_pct, items, note

    @property
    def pct(self):
        return 100.0 * self.count / self.total if self.total else 0.0

    @property
    def passed(self):
        if self.threshold_pct is None:
            return True
        if self.threshold_pct == 0:
            return self.count == 0
        return self.pct <= self.threshold_pct


def validate(s, res=None, high_float_days=44, high_duration_days=44, long_lag_days=None):
    acts = [a for a in s.activities if a.status != COMPLETE and not a.is_summary]
    n = len(acts)
    rels = [r for r in s.relationships if r.pred >= 0]
    succ_live = [r for r in rels if s.activities[r.succ].status != COMPLETE]
    checks = []

    no_pred = [a.code for a in acts if not s.preds[a.idx]]
    no_succ = [a.code for a in acts if not s.succs[a.idx]]
    missing = sorted(set(no_pred) | set(no_succ))
    checks.append(Check("logic", "Missing predecessor or successor", len(missing), n, 5.0, missing,
                        f"{len(no_pred)} without predecessor, {len(no_succ)} without successor (open ends)"))

    leads = [f"{s.activities[r.pred].code}->{s.activities[r.succ].code}" for r in succ_live if r.lag < 0]
    checks.append(Check("leads", "Negative lags (leads)", len(leads), len(succ_live), 0, leads))

    lags = [f"{s.activities[r.pred].code}->{s.activities[r.succ].code}" for r in succ_live if r.lag > 0]
    checks.append(Check("lags", "Positive lags", len(lags), len(succ_live), 5.0, lags))

    nonfs = [f"{s.activities[r.pred].code}->{s.activities[r.succ].code}" for r in succ_live if r.type != FS]
    checks.append(Check("rel_types", "Non finish-to-start relationships", len(nonfs), len(succ_live), 10.0, nonfs))

    hard = [a.code for a in acts if any(c and c[0] in HARD for c in (a.cstr, a.cstr2))]
    checks.append(Check("hard_constraints", "Hard constraints", len(hard), n, 5.0, hard))

    unknown = [f"{a.code}:{c[0]}" for a in acts for c in (a.cstr, a.cstr2) if c and c[0] not in KNOWN_CSTR]
    checks.append(Check("unsupported_constraints", "Constraints not modelled (e.g. ALAP)", len(unknown), n, 0, unknown))

    hd = [a.code for a in acts if a.rem_dur > high_duration_days * a.cal.minutes_per_day()]
    checks.append(Check("high_duration", f"Remaining duration > {high_duration_days} days", len(hd), n, 5.0, hd))

    if res is None:
        try:
            res = CpmEngine(s).run()
        except Exception as e:  # loop etc.
            checks.append(Check("cpm", "CPM calculation", 1, 1, 0, [str(e)]))
            return checks
    hf = [a.code for a in acts if res.tf[a.idx] is not None and res.tf[a.idx] > high_float_days * a.cal.minutes_per_day()]
    checks.append(Check("high_float", f"Total float > {high_float_days} days", len(hf), n, 5.0, hf))
    nf = [a.code for a in acts if res.tf[a.idx] is not None and res.tf[a.idx] < 0]
    checks.append(Check("negative_float", "Negative float", len(nf), n, 0, nf))

    dd = s.settings.data_date
    bad = []
    for a in s.activities:
        if a.act_start is not None and a.act_start > dd:
            bad.append(f"{a.code}: actual start after data date")
        if a.act_finish is not None and a.act_finish > dd:
            bad.append(f"{a.code}: actual finish after data date")
    checks.append(Check("invalid_dates", "Actual dates after the data date", len(bad), len(s.activities), 0, bad))

    summ = [a.code for a in s.activities if a.is_summary]
    checks.append(Check("summaries", "LOE / WBS summary activities (non-driving)", len(summ), len(s.activities), None, summ))

    # Critical path test: delay the most critical open activity by 600 days and confirm the finish moves
    cands = [a for a in acts if res.tf[a.idx] is not None and a.rem_dur > 0]
    if cands:
        a = min(cands, key=lambda x: (res.tf[x.idx], x.idx))
        dur = [x.rem_dur for x in s.activities]
        dur[a.idx] += 600 * a.cal.minutes_per_day()
        try:
            r2 = CpmEngine(s).run(dur, backward=False)
            moved = s.settings.project_calendar.work_between(res.project_finish, r2.project_finish)
            ok = moved > 0
            checks.append(Check("cp_test", "Critical path test (finish responds to delay)", 0 if ok else 1, 1, 0,
                                [] if ok else [a.code],
                                f"delaying {a.code} by 600d moved the finish by {moved / s.settings.project_calendar.minutes_per_day():.0f}d"))
        except OverflowError:
            checks.append(Check("cp_test", "Critical path test", 0, 1, None, [], "skipped (horizon)"))
    return checks
