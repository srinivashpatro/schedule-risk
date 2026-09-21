"""Programmatic XER writer, used for test fixtures and the synthetic schedule generator.

Produces the subset of P6 tables the engine reads, in P6's own layout, so the
files also open in other XER tools.
"""
import datetime as _dt

from .calendar import EXCEL_EPOCH

P6_DAY = {0: 2, 1: 3, 2: 4, 3: 5, 4: 6, 5: 7, 6: 1}  # Mon=0 -> P6 2 ... Sun=6 -> P6 1

TABLES = {
    "PROJECT": ["proj_id", "proj_short_name", "clndr_id", "plan_start_date", "last_recalc_date", "scd_end_date",
                "sched_calendar_on_relationship_lag", "sched_retained_logic", "sched_progress_override",
                "sched_float_type", "critical_drtn_hr_cnt"],
    "CALENDAR": ["clndr_id", "default_flag", "clndr_name", "proj_id", "base_clndr_id", "clndr_type",
                 "day_hr_cnt", "week_hr_cnt", "clndr_data"],
    "PROJWBS": ["wbs_id", "proj_id", "parent_wbs_id", "wbs_short_name", "wbs_name", "proj_node_flag"],
    "ACTVTYPE": ["actv_code_type_id", "actv_code_type", "proj_id"],
    "ACTVCODE": ["actv_code_id", "actv_code_type_id", "short_name", "actv_code_name"],
    "TASK": ["task_id", "proj_id", "wbs_id", "clndr_id", "task_code", "task_name", "task_type", "status_code",
             "target_drtn_hr_cnt", "remain_drtn_hr_cnt", "act_start_date", "act_end_date",
             "cstr_type", "cstr_date", "cstr_type2", "cstr_date2",
             "early_start_date", "early_end_date", "late_start_date", "late_end_date",
             "restart_date", "reend_date", "total_float_hr_cnt"],
    "TASKPRED": ["task_pred_id", "task_id", "pred_task_id", "proj_id", "pred_proj_id", "pred_type", "lag_hr_cnt"],
    "TASKACTV": ["task_id", "actv_code_type_id", "actv_code_id", "proj_id"],
}


def clndr_data(week, exceptions=None):
    """week: {0..6 (Mon..Sun): [("08:00","12:00"), ...]}, exceptions: {date: [...]}"""
    sep = "\x7f"
    out = ["(0||CalendarData()(", sep, "  (0||DaysOfWeek()(", sep]
    for p6 in range(1, 8):
        wd = next(k for k, v in P6_DAY.items() if v == p6)
        shifts = week.get(wd, [])
        if not shifts:
            out.append(f"    (0||{p6}()())" + sep)
        else:
            out.append(f"    (0||{p6}()(" + sep)
            for i, (a, b) in enumerate(shifts):
                out.append(f"      (0||{i}(s|{a}|f|{b})())" + sep)
            out.append("    ))" + sep)
    out.append("  ))" + sep)
    out.append("  (0||VIEW(ShowTotal|Y)())" + sep)
    out.append("  (0||Exceptions()(" + sep)
    for i, (day, shifts) in enumerate(sorted((exceptions or {}).items())):
        serial = (day - EXCEL_EPOCH).days
        if not shifts:
            out.append(f"    (0||{i}(d|{serial})())" + sep)
        else:
            out.append(f"    (0||{i}(d|{serial})(" + sep)
            for k, (a, b) in enumerate(shifts):
                out.append(f"      (0||{k}(s|{a}|f|{b})())" + sep)
            out.append("    ))" + sep)
    out.append("  ))))")
    return "".join(out)


FIVE_BY_EIGHT = {d: [("08:00", "12:00"), ("13:00", "17:00")] for d in range(5)}
SEVEN_BY_24 = {d: [("00:00", "00:00")] for d in range(7)}
SIX_BY_TEN = {d: [("07:00", "12:00"), ("12:30", "17:30")] for d in range(6)}


def fmt(dt):
    if dt is None:
        return ""
    if isinstance(dt, str):
        return dt
    return dt.strftime("%Y-%m-%d %H:%M")


class XerBuilder:
    def __init__(self):
        self.rows = {k: [] for k in TABLES}

    def add(self, table, **vals):
        row = {k: "" for k in TABLES[table]}
        for k, v in vals.items():
            if k not in row:
                raise KeyError(f"{table}.{k}")
            row[k] = fmt(v) if isinstance(v, (_dt.datetime,)) else ("" if v is None else str(v))
        self.rows[table].append(row)
        return row

    def text(self):
        lines = ["ERMHDR\t19.12\t2026-09-21\tProject\tadmin\tadmin\tdbxDatabaseNoName\tProject Management\tUSD"]
        for t, fields in TABLES.items():
            if not self.rows[t]:
                continue
            lines.append("%T\t" + t)
            lines.append("%F\t" + "\t".join(fields))
            for r in self.rows[t]:
                lines.append("%R\t" + "\t".join(r[f] for f in fields))
        lines.append("%E")
        return "\r\n".join(lines) + "\r\n"

    def save(self, path):
        with open(path, "w", encoding="cp1252", newline="") as fh:
            fh.write(self.text())
