"""Hand-built fixtures whose correct CPM dates were worked out by hand (see comments)."""
import datetime as dt
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

from sra.xerbuild import XerBuilder, clndr_data, FIVE_BY_EIGHT, SEVEN_BY_24  # noqa: E402

TESTDATA = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "..", "testdata"))


def D(s):
    return dt.datetime.strptime(s, "%Y-%m-%d %H:%M")


def basic(lag_cal="rcal_Predecessor"):
    """
    5x8 calendar (08-12, 13-17), data date Mon 2026-01-05 08:00.
      A  40h                      ES Mon 01-05 08:00  EF Fri 01-09 17:00  TF 0
      B  24h  FS A +24h           ES Wed 01-14 08:00? no: lag 3d after Fri 17:00 -> Mon,Tue,Wed consumed -> Thu 01-15 08:00
    (the expected values are asserted in test_cpm_hand.py, derived there step by step)
    """
    b = XerBuilder()
    b.add("PROJECT", proj_id="1", proj_short_name="HAND", clndr_id="10",
          plan_start_date="2026-01-05 08:00", last_recalc_date="2026-01-05 08:00",
          sched_calendar_on_relationship_lag=lag_cal, sched_retained_logic="Y", sched_progress_override="N",
          sched_float_type="FT_FF", critical_drtn_hr_cnt="0")
    b.add("CALENDAR", clndr_id="10", default_flag="Y", clndr_name="5x8", clndr_type="CA_Base",
          day_hr_cnt="8", week_hr_cnt="40", clndr_data=clndr_data(FIVE_BY_EIGHT, {dt.date(2026, 1, 1): []}))
    b.add("CALENDAR", clndr_id="11", default_flag="N", clndr_name="7x24", clndr_type="CA_Base",
          day_hr_cnt="24", week_hr_cnt="168", clndr_data=clndr_data(SEVEN_BY_24))
    b.add("PROJWBS", wbs_id="100", proj_id="1", parent_wbs_id="", wbs_short_name="HAND", wbs_name="Hand", proj_node_flag="Y")
    tasks = [
        ("1", "A", "TT_Task", 40), ("2", "B", "TT_Task", 24), ("3", "C", "TT_Task", 80),
        ("4", "D", "TT_Task", 16), ("5", "M", "TT_FinMile", 0), ("6", "S", "TT_Mile", 0),
    ]
    for tid, code, tt, h in tasks:
        b.add("TASK", task_id=tid, proj_id="1", wbs_id="100", clndr_id="10", task_code=code, task_name=code,
              task_type=tt, status_code="TK_NotStart", target_drtn_hr_cnt=h, remain_drtn_hr_cnt=h)
    rels = [("S", "A", "PR_FS", 0), ("A", "B", "PR_FS", 24), ("A", "C", "PR_SS", 16),
            ("B", "D", "PR_FF", 8), ("C", "M", "PR_FS", 0), ("D", "M", "PR_FS", 0)]
    ids = {code: tid for tid, code, _, _ in tasks}
    for k, (p, s, t, lag) in enumerate(rels):
        b.add("TASKPRED", task_pred_id=str(k + 1), task_id=ids[s], pred_task_id=ids[p], proj_id="1", pred_proj_id="1",
              pred_type=t, lag_hr_cnt=lag)
    return b


def write_all():
    os.makedirs(TESTDATA, exist_ok=True)
    basic().save(os.path.join(TESTDATA, "hand_basic.xer"))


if __name__ == "__main__":
    write_all()
