"""Differential check: our CPM dates versus the dates P6 stored in the XER.

P6 writes its own early/late dates and total float into the TASK table when it
exports, so every scheduled XER is its own test oracle. Dates are compared in
*working time* on the activity's calendar, so 17:00 Monday and 08:00 Tuesday
count as the same instant (P6 prints either depending on context).
"""
from .model import NOT_STARTED, IN_PROGRESS, COMPLETE, START_MILE, FINISH_MILE


class Diff:
    __slots__ = ("code", "field", "p6", "ours", "delta_min")

    def __init__(self, code, field, p6, ours, delta):
        self.code, self.field, self.p6, self.ours, self.delta_min = code, field, p6, ours, delta


class VerifyReport:
    def __init__(self):
        self.compared = 0          # activities compared
        self.fields_compared = 0
        self.fields_matched = 0
        self.diffs = []            # list of Diff
        self.activities_matched = 0
        self.skipped_no_p6 = 0

    @property
    def outcome(self):
        """"matches", "differences", or "nothing" when no activity carries P6-calculated dates."""
        if self.compared == 0:
            return "nothing"
        return "differences" if self.diffs else "matches"

    @property
    def passed(self):
        return self.outcome == "matches"

    def worst(self, n=50):
        return sorted(self.diffs, key=lambda d: -abs(d.delta_min))[:n]


def verify_against_p6(s, res, tolerance_min=0):
    rep = VerifyReport()
    for a in s.activities:
        j = a.idx
        if a.status == COMPLETE or a.is_summary:
            continue
        p = a.p6
        cal = a.cal
        pairs = []
        if a.status == IN_PROGRESS:
            pairs.append(("remaining_start", p.get("restart_date"), res.rs[j]))
            pairs.append(("early_finish", p.get("reend_date") or p.get("early_end_date"), res.ef[j]))
            pairs.append(("late_finish", p.get("rem_late_end_date") or p.get("late_end_date"), res.lf[j] if res.lf else None))
        else:
            if a.type != FINISH_MILE:
                pairs.append(("early_start", p.get("early_start_date"), res.es[j]))
                pairs.append(("late_start", p.get("late_start_date"), res.ls[j] if res.ls else None))
            if a.type != START_MILE:
                pairs.append(("early_finish", p.get("early_end_date"), res.ef[j]))
                pairs.append(("late_finish", p.get("late_end_date"), res.lf[j] if res.lf else None))
        tf_p6 = p.get("total_float_hr")
        pairs = [x for x in pairs if x[1] is not None and x[2] is not None]
        if not pairs and tf_p6 is None:
            rep.skipped_no_p6 += 1
            continue
        rep.compared += 1
        ok = True
        for field, p6v, ours in pairs:
            rep.fields_compared += 1
            delta = cal.work_at(ours) - cal.work_at(p6v)
            if abs(delta) <= tolerance_min:
                rep.fields_matched += 1
            else:
                ok = False
                rep.diffs.append(Diff(a.code, field, p6v, ours, delta))
        if tf_p6 is not None and res.tf is not None and res.tf[j] is not None:
            rep.fields_compared += 1
            p6m = int(round(tf_p6 * 60))
            delta = res.tf[j] - p6m
            if abs(delta) <= max(tolerance_min, 1):
                rep.fields_matched += 1
            else:
                ok = False
                rep.diffs.append(Diff(a.code, "total_float", p6m, res.tf[j], delta))
        if ok:
            rep.activities_matched += 1
    return rep
