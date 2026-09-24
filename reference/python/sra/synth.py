"""Synthetic but realistic P6 schedules for testing and benchmarking.

The P6 date fields written into these files come from OUR engine, so they are
only a regression oracle (they catch changes), not an independent check. The
independent check is a real XER scheduled by P6 - see verify.py.
"""
import datetime as dt
import math

from .numerics import Rng
from .xerbuild import XerBuilder, clndr_data, FIVE_BY_EIGHT, SIX_BY_TEN, SEVEN_BY_24
from .xer import parse_xer_text
from .model import build_schedule, COMPLETE
from .cpm import CpmEngine
from .calendar import fmt_min

HOLIDAYS = [dt.date(y, m, d) for y in range(2025, 2031) for (m, d) in ((1, 1), (5, 1), (12, 25), (12, 26))]


def float_hours(minutes):
    """Total float for total_float_hr_cnt. Four decimals read back to the exact minute;
    `:g` kept six significant digits, which rounded floats over 10,000 h to the hour."""
    return f"{minutes / 60:.4f}".rstrip("0").rstrip(".")


def generate(n=500, seed=7, start="2026-03-02 08:00", progress_fraction=0.2, name="SYN"):
    rng = Rng(seed)
    b = XerBuilder()
    b.add("PROJECT", proj_id="1", proj_short_name=name, clndr_id="1", plan_start_date=start, last_recalc_date=start,
          sched_calendar_on_relationship_lag="rcal_Predecessor", sched_retained_logic="Y",
          sched_progress_override="N", sched_float_type="FT_FF", critical_drtn_hr_cnt="0")
    hol = {d: [] for d in HOLIDAYS}
    b.add("CALENDAR", clndr_id="1", default_flag="Y", clndr_name="5 Day 8 Hour", clndr_type="CA_Base",
          day_hr_cnt="8", week_hr_cnt="40", clndr_data=clndr_data(FIVE_BY_EIGHT, hol))
    b.add("CALENDAR", clndr_id="2", default_flag="N", clndr_name="6 Day 10 Hour", clndr_type="CA_Base",
          day_hr_cnt="10", week_hr_cnt="60", clndr_data=clndr_data(SIX_BY_TEN, hol))
    b.add("CALENDAR", clndr_id="3", default_flag="N", clndr_name="7 Day 24 Hour", clndr_type="CA_Base",
          day_hr_cnt="24", week_hr_cnt="168", clndr_data=clndr_data(SEVEN_BY_24))
    cal_hours = {"1": 8, "2": 10, "3": 24}
    b.add("PROJWBS", wbs_id="1000", proj_id="1", parent_wbs_id="", wbs_short_name=name, wbs_name=name, proj_node_flag="Y")
    areas = ["ENG", "PROC", "CIV", "MECH", "ELEC", "COMM"]
    for i, ar in enumerate(areas):
        b.add("PROJWBS", wbs_id=str(1001 + i), proj_id="1", parent_wbs_id="1000", wbs_short_name=ar, wbs_name=ar, proj_node_flag="N")
    b.add("ACTVTYPE", actv_code_type_id="1", actv_code_type="Discipline", proj_id="1")
    disc = ["CIV", "MEC", "ELE", "PIP", "INS"]
    for i, d in enumerate(disc):
        b.add("ACTVCODE", actv_code_id=str(i + 1), actv_code_type_id="1", short_name=d, actv_code_name=d)

    tasks = []
    for i in range(n):
        tid = str(10000 + i)
        code = f"A{(i + 1) * 10:05d}"
        if i == 0:
            tt, h, cal = "TT_Mile", 0, "1"
        elif i == n - 1:
            tt, h, cal = "TT_FinMile", 0, "1"
        else:
            u = rng.next_double()
            cal = "1" if u < 0.7 else "2" if u < 0.9 else "3"
            tt = "TT_Mile" if rng.next_double() < 0.03 else "TT_Task"
            days = 0 if tt == "TT_Mile" else max(1, int(math.exp(0.5 + 2.6 * rng.next_double())))
            h = days * cal_hours[cal]
        area = min(len(areas) - 1, i * len(areas) // n)
        tasks.append((tid, code, tt, h, cal, str(1001 + area)))
        cstr_type = cstr_date = ""
        if 0 < i < n - 1 and tt == "TT_Task" and rng.next_double() < 0.02:
            cstr_type = "CS_MSOA"
            d0 = dt.datetime.strptime(start, "%Y-%m-%d %H:%M") + dt.timedelta(days=int(30 + rng.next_double() * 300))
            cstr_date = d0.strftime("%Y-%m-%d 08:00")
        b.add("TASK", task_id=tid, proj_id="1", wbs_id=str(1001 + area), clndr_id=cal, task_code=code,
              task_name=f"{areas[area]} activity {i + 1}", task_type=tt, status_code="TK_NotStart",
              target_drtn_hr_cnt=h, remain_drtn_hr_cnt=h, cstr_type=cstr_type, cstr_date=cstr_date)
        b.add("TASKACTV", task_id=tid, actv_code_type_id="1", actv_code_id=str(1 + rng.next_int(len(disc))), proj_id="1")

    has_succ = [False] * n
    has_pred = [False] * n
    rid = 0
    for i in range(1, n - 1):
        k = 1 + rng.next_int(3)
        chosen = set()
        for _ in range(k):
            lo = max(0, i - 25)
            p = lo + rng.next_int(i - lo)
            chosen.add(p)
        for p in sorted(chosen):
            u = rng.next_double()
            rt = "PR_FS" if u < 0.85 else "PR_SS" if u < 0.94 else "PR_FF"
            lag = 0
            if rng.next_double() < 0.10:
                lag = (1 + rng.next_int(5)) * cal_hours[tasks[p][4]]
            rid += 1
            b.add("TASKPRED", task_pred_id=str(rid), task_id=tasks[i][0], pred_task_id=tasks[p][0], proj_id="1",
                  pred_proj_id="1", pred_type=rt, lag_hr_cnt=lag)
            has_succ[p] = True
            has_pred[i] = True
    for i in range(1, n - 1):
        if not has_succ[i]:
            rid += 1
            b.add("TASKPRED", task_pred_id=str(rid), task_id=tasks[n - 1][0], pred_task_id=tasks[i][0], proj_id="1",
                  pred_proj_id="1", pred_type="PR_FS", lag_hr_cnt=0)

    # ---- progress: schedule once, then move the data date forward and status activities
    s = build_schedule(parse_xer_text(b.text()))
    r = CpmEngine(s).run()
    if progress_fraction > 0:
        cal = s.settings.project_calendar
        # data date = midpoint of the activity at the requested fraction of start order
        order = sorted((j for j in range(len(s.activities)) if s.activities[j].rem_dur >= 3 * 480), key=lambda j: r.es[j])
        pick = order[min(len(order) - 1, int(len(order) * progress_fraction))]
        dd = cal.snap_start((r.es[pick] + r.ef[pick]) // 2)
        dd = cal.snap_start(dd - dd % 1440 + 8 * 60)
        by_tid = {row["task_id"]: row for row in b.rows["TASK"]}
        for a in s.activities:
            row = by_tid[a.task_id]
            j = a.idx
            if r.ef[j] <= dd:
                row["status_code"] = "TK_Complete"
                row["act_start_date"] = fmt_min(r.es[j])
                row["act_end_date"] = fmt_min(r.ef[j])
                row["remain_drtn_hr_cnt"] = "0"
            elif r.es[j] < dd:
                row["status_code"] = "TK_Active"
                row["act_start_date"] = fmt_min(r.es[j])
                rem = a.cal.work_between(dd, r.ef[j])
                # a little slippage on in-progress work
                rem = int(rem * (1.0 + 0.3 * rng.next_double()))
                row["remain_drtn_hr_cnt"] = f"{rem / 60:g}"
        b.rows["PROJECT"][0]["last_recalc_date"] = fmt_min(dd)
    write_engine_dates(b)
    return b


def write_engine_dates(b):
    """Fill the P6 date columns from our own engine (regression oracle only)."""
    s = build_schedule(parse_xer_text(b.text()))
    r = CpmEngine(s).run()
    by_tid = {row["task_id"]: row for row in b.rows["TASK"]}
    for a in s.activities:
        row = by_tid[a.task_id]
        j = a.idx
        if a.status == COMPLETE:
            continue
        row["early_start_date"] = fmt_min(r.es[j])
        row["early_end_date"] = fmt_min(r.ef[j])
        row["late_start_date"] = fmt_min(r.ls[j]) if r.ls[j] is not None else ""
        row["late_end_date"] = fmt_min(r.lf[j]) if r.lf[j] is not None else ""
        row["restart_date"] = fmt_min(r.rs[j])
        row["reend_date"] = fmt_min(r.ef[j])
        row["total_float_hr_cnt"] = float_hours(r.tf[j]) if r.tf[j] is not None else ""
