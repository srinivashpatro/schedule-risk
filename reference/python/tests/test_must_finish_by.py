import os
import unittest

from sra.xer import parse_xer_text
from sra.model import build_schedule
from sra.cpm import CpmEngine
from sra.calendar import parse_p6_date
from sra.risk import load_risk_model
from sra.sim import Simulation

T = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "testdata")


def with_must_finish_by(file, project, date, horizon_years=30):
    """Schedule from a fixture with PROJECT.scd_end_date (the must-finish-by) set to `date`."""
    lines = open(os.path.join(T, file), encoding="utf-8").read().split("\n")
    table = fields = None
    for i, ln in enumerate(lines):
        cells = ln.rstrip("\r").split("\t")
        if cells[0] == "%T":
            table = cells[1]
        elif cells[0] == "%F" and table == "PROJECT":
            fields = cells
        elif cells[0] == "%R" and table == "PROJECT" and cells[fields.index("proj_short_name")] == project:
            cells[fields.index("scd_end_date")] = date
            lines[i] = "\t".join(cells)
    return build_schedule(parse_xer_text("\n".join(lines)), horizon_years=horizon_years)


class MustFinishBy(unittest.TestCase):
    """The backward pass starts from the must-finish-by, so the calendars must cover it and the
    late dates it produces, wherever it lies. Twin of MustFinishByTests in CpmTests.cs."""

    def test_backward_pass_runs_from_any_must_finish_by(self):
        for date in ("2100-12-31 17:00", "2045-06-30 17:00", "2025-12-05 17:00", "2020-01-31 17:00", "2015-01-30 17:00"):
            with self.subTest(date=date):
                s = with_must_finish_by("synth_200.xer", "SYN200", date)
                r = CpmEngine(s).run()
                mfb = parse_p6_date(date)
                self.assertEqual(mfb, r.project_lf)
                last = next(a for a in s.activities
                            if r.ef[a.idx] == r.project_finish and all(x.pred != a.idx for x in s.relationships))
                cal = last.cal
                self.assertEqual(cal.snap_finish(mfb), r.lf[last.idx])
                self.assertEqual(cal.work_at(r.lf[last.idx]) - cal.work_at(r.ef[last.idx]), r.tf[last.idx])
                self.assertEqual(mfb >= r.project_finish, r.tf[last.idx] >= 0)

    def test_monte_carlo_runs_when_iterations_overrun_the_must_finish_by(self):
        s = with_must_finish_by("synth_500.xer", "SYN500", "2027-06-30 17:00")
        det = CpmEngine(s).run()
        m = load_risk_model(s, os.path.join(T, "synth_500.risk.json"), crit=[det.critical(s, j) for j in range(len(s.activities))])
        one = Simulation(s, m).run(iterations=100, seed=5)
        two = Simulation(s, m).run(iterations=100, seed=5)
        self.assertEqual(100, one.iterations)
        self.assertEqual(one.finish, two.finish)
        self.assertEqual(one.crit_count, two.crit_count)

    def test_results_do_not_depend_on_the_calendar_horizon(self):
        a = with_must_finish_by("synth_500.xer", "SYN500", "2027-06-30 17:00", horizon_years=30)
        b = with_must_finish_by("synth_500.xer", "SYN500", "2027-06-30 17:00", horizon_years=60)
        self.assertNotEqual(a.cal24.h0, b.cal24.h0)
        ra, rb = CpmEngine(a).run(), CpmEngine(b).run()
        for f in ("es", "ef", "ls", "lf", "tf"):
            self.assertEqual(getattr(ra, f), getattr(rb, f), f)


if __name__ == "__main__":
    unittest.main()
