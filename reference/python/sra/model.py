"""Schedule model built from an XER document (one project)."""
import datetime as _dt

from .calendar import WorkCalendar, parse_clndr_data, parse_p6_date, to_min, from_min, EPOCH
from .numerics import round_half_up

FS, SS, FF, SF = 0, 1, 2, 3
REL_CODES = {"PR_FS": FS, "PR_SS": SS, "PR_FF": FF, "PR_SF": SF}
REL_NAMES = {FS: "FS", SS: "SS", FF: "FF", SF: "SF"}

NOT_STARTED, IN_PROGRESS, COMPLETE = 0, 1, 2
STATUS_CODES = {"TK_NotStart": NOT_STARTED, "TK_Active": IN_PROGRESS, "TK_Complete": COMPLETE}

TASK, RESOURCE_DEP, START_MILE, FINISH_MILE, LOE, WBS_SUMMARY = range(6)
TYPE_CODES = {"TT_Task": TASK, "TT_Rsrc": RESOURCE_DEP, "TT_Mile": START_MILE,
              "TT_FinMile": FINISH_MILE, "TT_LOE": LOE, "TT_WBS": WBS_SUMMARY}

LAG_PRED, LAG_SUCC, LAG_24H, LAG_PROJ = range(4)
FLOAT_FINISH, FLOAT_START, FLOAT_MIN = range(3)


class Activity:
    __slots__ = ("idx", "task_id", "code", "name", "type", "status", "cal", "wbs_id",
                 "orig_dur", "rem_dur", "act_start", "act_finish",
                 "cstr", "cstr2", "p6", "codes", "raw")

    def __init__(self):
        self.p6 = {}
        self.codes = {}
        self.cstr = None   # (type_code, minutes) or None
        self.cstr2 = None

    @property
    def is_milestone(self):
        return self.type in (START_MILE, FINISH_MILE)

    @property
    def is_summary(self):
        return self.type in (LOE, WBS_SUMMARY)


class Relationship:
    __slots__ = ("pred", "succ", "type", "lag", "ext_pred_dates")

    def __init__(self, pred, succ, rtype, lag, ext=None):
        self.pred, self.succ, self.type, self.lag = pred, succ, rtype, lag
        self.ext_pred_dates = ext  # (early_start, early_finish) for predecessors outside the project


class Settings:
    def __init__(self):
        self.data_date = None
        self.must_finish_by = None
        self.lag_calendar = LAG_PRED
        self.retained_logic = True
        self.float_type = FLOAT_FINISH
        self.critical_float = 0  # minutes
        self.project_calendar = None


class Schedule:
    def __init__(self):
        self.project_id = None
        self.project_code = ""
        self.project_name = ""
        self.activities = []
        self.relationships = []
        self.calendars = {}      # clndr_id -> WorkCalendar
        self.cal24 = None
        self.settings = Settings()
        self.wbs = {}            # wbs_id -> (parent_id, short_name, name)
        self.warnings = []
        self.by_code = {}
        self.preds = []          # per activity: list of relationship indexes
        self.succs = []

    def activity(self, code):
        return self.activities[self.by_code[code]]

    def wbs_path(self, wbs_id):
        parts = []
        seen = set()
        while wbs_id in self.wbs and wbs_id not in seen:
            seen.add(wbs_id)
            parent, short, _ = self.wbs[wbs_id]
            parts.append(short)
            wbs_id = parent
        return ".".join(reversed(parts))


def _f(s, default=0.0):
    try:
        return float(s)
    except (TypeError, ValueError):
        return default


def list_projects(doc):
    return [(p.get("proj_id"), p.get("proj_short_name", ""), p.get("proj_name", p.get("proj_short_name", "")))
            for p in doc.rows("PROJECT")]


def build_schedule(doc, project=None, horizon_years=30):
    """Build a Schedule for one project (id or short name; default: the first project)."""
    s = Schedule()
    projects = doc.rows("PROJECT")
    if not projects:
        raise ValueError("XER contains no PROJECT table")
    proj = None
    if project is None:
        proj = projects[0]
        if len(projects) > 1:
            s.warnings.append(f"{len(projects)} projects in file; using {proj.get('proj_short_name')}")
    else:
        for p in projects:
            if p.get("proj_id") == str(project) or p.get("proj_short_name") == str(project):
                proj = p
        if proj is None:
            raise ValueError(f"project {project!r} not found")
    pid = proj["proj_id"]
    s.project_id = pid
    s.project_code = proj.get("proj_short_name", "")
    s.project_name = proj.get("proj_short_name", "")

    st = s.settings
    st.data_date = parse_p6_date(proj.get("last_recalc_date")) or parse_p6_date(proj.get("plan_start_date"))
    if st.data_date is None:
        raise ValueError("project has no data date (last_recalc_date) or planned start")
    st.must_finish_by = parse_p6_date(proj.get("scd_end_date"))
    lagc = (proj.get("sched_calendar_on_relationship_lag") or "").lower()
    st.lag_calendar = LAG_SUCC if "succ" in lagc else LAG_24H if "24" in lagc else LAG_PROJ if "proj" in lagc else LAG_PRED
    st.retained_logic = (proj.get("sched_progress_override", "N") or "N").upper() != "Y"
    if proj.get("sched_retained_logic", "Y").upper() == "N" and proj.get("sched_progress_override", "N").upper() != "Y":
        s.warnings.append("neither retained logic nor progress override set; using retained logic")
    ft = (proj.get("sched_float_type") or "").lower()
    st.float_type = FLOAT_START if ("sf" in ft or "start" in ft) else FLOAT_MIN if ("min" in ft or "small" in ft) else FLOAT_FINISH
    st.critical_float = round_half_up(_f(proj.get("critical_drtn_hr_cnt"), 0.0) * 60)

    # ---- horizon from every date we can see
    dates = [st.data_date]
    tasks_all = doc.rows("TASK")
    tasks = [t for t in tasks_all if t.get("proj_id") == pid]
    for t in tasks:
        for k in ("act_start_date", "act_end_date", "target_start_date", "early_start_date", "cstr_date", "cstr_date2"):
            try:
                v = parse_p6_date(t.get(k))
            except ValueError:
                v = None
            if v is not None:
                dates.append(v)
    if st.must_finish_by is not None:
        dates.append(st.must_finish_by)
    h0 = min(dates) - 400 * 1440
    h1 = max(dates) + int(horizon_years * 365.25) * 1440
    # The backward pass starts from the must-finish-by. A finish after it (every finish lies before h1)
    # pulls late dates back by as much as the overrun, so the calendars reach that far before it.
    if st.must_finish_by is not None:
        h0 = min(h0, st.must_finish_by - (h1 - st.data_date) - 400 * 1440)
    h0 = to_min(from_min(h0).replace(hour=0, minute=0))

    # ---- calendars
    for c in doc.rows("CALENDAR"):
        week, exc, warns = parse_clndr_data(c.get("clndr_data", ""))
        for w in warns:
            s.warnings.append(f"calendar {c.get('clndr_name')}: {w}")
        try:
            s.calendars[c["clndr_id"]] = WorkCalendar(c["clndr_id"], c.get("clndr_name", ""), week, exc, h0, h1,
                                                      _f(c.get("day_hr_cnt"), 8.0))
        except ValueError as e:
            s.warnings.append(str(e))
    s.cal24 = WorkCalendar.twenty_four_hour(h0, h1)
    default_cal = proj.get("clndr_id")
    if default_cal not in s.calendars:
        defaults = [c["clndr_id"] for c in doc.rows("CALENDAR") if c.get("default_flag") == "Y" and c["clndr_id"] in s.calendars]
        default_cal = defaults[0] if defaults else (next(iter(s.calendars)) if s.calendars else None)
    if default_cal is None:
        from .calendar import DEFAULT_WEEK
        s.calendars["__default__"] = WorkCalendar("__default__", "Default 5x8", dict(DEFAULT_WEEK), {}, h0, h1, 8.0)
        default_cal = "__default__"
        s.warnings.append("no usable calendars in file; default 5x8 used")
    st.project_calendar = s.calendars[default_cal]

    # ---- WBS
    for w in doc.rows("PROJWBS"):
        if w.get("proj_id") == pid:
            s.wbs[w["wbs_id"]] = (w.get("parent_wbs_id"), w.get("wbs_short_name", ""), w.get("wbs_name", ""))

    # ---- activity codes
    code_types = {r["actv_code_type_id"]: r.get("actv_code_type", "") for r in doc.rows("ACTVTYPE")}
    code_vals = {r["actv_code_id"]: (r.get("actv_code_type_id"), r.get("short_name", "")) for r in doc.rows("ACTVCODE")}
    task_codes = {}
    for r in doc.rows("TASKACTV"):
        cv = code_vals.get(r.get("actv_code_id"))
        if cv:
            task_codes.setdefault(r.get("task_id"), {})[code_types.get(cv[0], cv[0])] = cv[1]

    # ---- activities
    idx_by_tid = {}
    for t in tasks:
        a = Activity()
        a.idx = len(s.activities)
        a.task_id = t["task_id"]
        a.code = t.get("task_code", a.task_id)
        a.name = t.get("task_name", "")
        a.type = TYPE_CODES.get(t.get("task_type"), TASK)
        if t.get("task_type") not in TYPE_CODES:
            s.warnings.append(f"{a.code}: unknown task_type {t.get('task_type')!r}, treated as task")
        a.status = STATUS_CODES.get(t.get("status_code"), NOT_STARTED)
        cal_id = t.get("clndr_id")
        if cal_id not in s.calendars:
            s.warnings.append(f"{a.code}: calendar {cal_id} missing; project calendar used")
            a.cal = st.project_calendar
        else:
            a.cal = s.calendars[cal_id]
        a.wbs_id = t.get("wbs_id")
        a.orig_dur = max(0, round_half_up(_f(t.get("target_drtn_hr_cnt")) * 60))
        rem = t.get("remain_drtn_hr_cnt")
        a.rem_dur = max(0, round_half_up(_f(rem) * 60)) if rem not in (None, "") else a.orig_dur
        if a.status == COMPLETE:
            a.rem_dur = 0
        a.act_start = parse_p6_date(t.get("act_start_date"))
        a.act_finish = parse_p6_date(t.get("act_end_date"))
        if a.status != NOT_STARTED and a.act_start is None:
            s.warnings.append(f"{a.code}: status {t.get('status_code')} without actual start; treated as not started")
            a.status = NOT_STARTED
        if a.status == COMPLETE and a.act_finish is None:
            s.warnings.append(f"{a.code}: complete without actual finish; treated as in progress")
            a.status = IN_PROGRESS
        if a.is_milestone:
            a.orig_dur = a.rem_dur = 0
        c1 = t.get("cstr_type") or ""
        if c1:
            a.cstr = (c1, parse_p6_date(t.get("cstr_date")))
        c2 = t.get("cstr_type2") or ""
        if c2:
            a.cstr2 = (c2, parse_p6_date(t.get("cstr_date2")))
        for k in ("early_start_date", "early_end_date", "late_start_date", "late_end_date",
                  "restart_date", "reend_date", "rem_late_start_date", "rem_late_end_date"):
            try:
                a.p6[k] = parse_p6_date(t.get(k))
            except ValueError:
                a.p6[k] = None
        tf = t.get("total_float_hr_cnt")
        a.p6["total_float_hr"] = _f(tf) if tf not in (None, "") else None
        a.codes = task_codes.get(a.task_id, {})
        a.raw = t
        idx_by_tid[a.task_id] = a.idx
        if a.code in s.by_code:
            s.warnings.append(f"duplicate activity id {a.code}")
        s.by_code.setdefault(a.code, a.idx)
        s.activities.append(a)

    # ---- relationships
    ext_tasks = {t["task_id"]: t for t in tasks_all if t.get("proj_id") != pid}
    for r in doc.rows("TASKPRED"):
        succ = idx_by_tid.get(r.get("task_id"))
        if succ is None:
            continue
        rtype = REL_CODES.get(r.get("pred_type"))
        if rtype is None:
            s.warnings.append(f"relationship {r.get('task_pred_id')}: unknown type {r.get('pred_type')!r}, FS assumed")
            rtype = FS
        lag = round_half_up(_f(r.get("lag_hr_cnt")) * 60)
        pred = idx_by_tid.get(r.get("pred_task_id"))
        if pred is None:
            et = ext_tasks.get(r.get("pred_task_id"))
            if et is None:
                s.warnings.append(f"{s.activities[succ].code}: predecessor {r.get('pred_task_id')} not in file; ignored")
                continue
            es = parse_p6_date(et.get("act_start_date")) or parse_p6_date(et.get("early_start_date"))
            ef = parse_p6_date(et.get("act_end_date")) or parse_p6_date(et.get("early_end_date"))
            s.relationships.append(Relationship(-1, succ, rtype, lag, (es, ef)))
            s.warnings.append(f"{s.activities[succ].code}: external predecessor {et.get('task_code')} held at its P6 dates")
            continue
        if pred == succ:
            s.warnings.append(f"{s.activities[succ].code}: self-relationship ignored")
            continue
        s.relationships.append(Relationship(pred, succ, rtype, lag))

    s.preds = [[] for _ in s.activities]
    s.succs = [[] for _ in s.activities]
    for k, r in enumerate(s.relationships):
        s.preds[r.succ].append(k)
        if r.pred >= 0:
            s.succs[r.pred].append(k)
    return s


def lag_calendar(s, rel):
    st = s.settings
    if st.lag_calendar == LAG_24H:
        return s.cal24
    if st.lag_calendar == LAG_PROJ:
        return st.project_calendar
    if st.lag_calendar == LAG_SUCC or rel.pred < 0:
        return s.activities[rel.succ].cal
    return s.activities[rel.pred].cal
