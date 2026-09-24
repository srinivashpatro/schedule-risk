import os
import unittest

from sra.xer import parse_xer_text, read_xer
from sra.model import build_schedule
from sra.cpm import CpmEngine
from sra.calendar import parse_p6_date
from sra.risk import load_risk_model
from sra.sim import Simulation

T = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "testdata")


def with_project_fields(file, project, values, horizon_years=30):
    """Schedule from a fixture with PROJECT fields set; a column the fixture lacks is added."""
    lines = open(os.path.join(T, file), encoding="utf-8").read().split("\n")
    table = fields = None
    for i, ln in enumerate(lines):
        cells = ln.rstrip("\r").split("\t")
        if cells[0] == "%T":
            table = cells[1]
        elif cells[0] == "%F" and table == "PROJECT":
            fields = cells + [f for f in values if f not in cells]
            lines[i] = "\t".join(fields)
        elif cells[0] == "%R" and table == "PROJECT":
            cells += [""] * (len(fields) - len(cells))
            if cells[fields.index("proj_short_name")] == project:
                for f, v in values.items():
                    cells[fields.index(f)] = v
            lines[i] = "\t".join(cells)
    return build_schedule(parse_xer_text("\n".join(lines)), horizon_years=horizon_years)


def with_must_finish_by(file, project, date, horizon_years=30):
    """P6's Must Finish By is PROJECT.plan_end_date."""
    return with_project_fields(file, project, {"plan_end_date": date}, horizon_years)


def last_activity(s, r):
    """The first activity (no successors) that finishes the project."""
    return next(a for a in s.activities
                if r.ef[a.idx] == r.project_finish and all(x.pred != a.idx for x in s.relationships))


def simulate(s):
    """synth_500 with the example risk model, whose "critical" filter comes from the engine as in the CLI."""
    m = load_risk_model(s, os.path.join(T, "synth_500.risk.json"), crit=CpmEngine(s).critical_to_project_finish())
    return Simulation(s, m).run(iterations=100, seed=5)


class MustFinishBy(unittest.TestCase):
    """The backward pass starts from the must-finish-by (PROJECT.plan_end_date), so the calendars must
    cover it and the late dates it produces, wherever it lies. PROJECT.scd_end_date is P6's calculated
    scheduled finish, not a constraint. Twin of MustFinishByTests in CpmTests.cs."""

    def test_scheduled_finish_is_not_a_constraint(self):
        s = with_project_fields("synth_200.xer", "SYN200", {"scd_end_date": "2027-07-05 17:00"})
        r = CpmEngine(s).run()
        self.assertIsNone(s.settings.must_finish_by)
        self.assertEqual(r.project_finish, r.project_lf)
        self.assertEqual(0, r.tf[last_activity(s, r).idx])

    def test_must_finish_by_is_read_from_plan_end_date(self):
        s = with_project_fields("synth_200.xer", "SYN200",
                                {"scd_end_date": "2027-06-04 17:00", "plan_end_date": "2027-07-05 17:00"})
        r = CpmEngine(s).run()
        self.assertEqual(parse_p6_date("2027-07-05 17:00"), s.settings.must_finish_by)
        self.assertEqual(s.settings.must_finish_by, r.project_lf)
        self.assertGreater(r.tf[last_activity(s, r).idx], 0)

    def test_must_finish_by_at_midnight_means_by_the_end_of_the_previous_working_day(self):
        s = with_must_finish_by("synth_200.xer", "SYN200", "2027-06-04 00:00")
        r = CpmEngine(s).run()
        last = last_activity(s, r)
        self.assertEqual(parse_p6_date("2027-06-04 17:00"), r.project_finish)
        self.assertEqual(parse_p6_date("2027-06-03 17:00"), r.lf[last.idx])
        self.assertEqual(-last.cal.minutes_per_day(), r.tf[last.idx])

    def test_backward_pass_runs_from_any_must_finish_by(self):
        for date in ("2100-12-31 17:00", "2045-06-30 17:00", "2025-12-05 17:00", "2020-01-31 17:00", "2015-01-30 17:00"):
            with self.subTest(date=date):
                s = with_must_finish_by("synth_200.xer", "SYN200", date)
                r = CpmEngine(s).run()
                mfb = parse_p6_date(date)
                self.assertEqual(mfb, r.project_lf)
                last = last_activity(s, r)
                cal = last.cal
                self.assertEqual(cal.snap_finish(mfb), r.lf[last.idx])
                self.assertEqual(cal.work_at(r.lf[last.idx]) - cal.work_at(r.ef[last.idx]), r.tf[last.idx])
                self.assertEqual(mfb >= r.project_finish, r.tf[last.idx] >= 0)

    def test_monte_carlo_runs_when_iterations_overrun_the_must_finish_by(self):
        s = with_must_finish_by("synth_500.xer", "SYN500", "2027-06-30 17:00")
        m = load_risk_model(s, os.path.join(T, "synth_500.risk.json"), crit=CpmEngine(s).critical_to_project_finish())
        one = Simulation(s, m).run(iterations=100, seed=5)
        two = Simulation(s, m).run(iterations=100, seed=5)
        self.assertEqual(100, one.iterations)
        self.assertEqual(one.finish, two.finish)
        self.assertEqual(one.crit_count, two.crit_count)

    def test_must_finish_by_does_not_change_the_simulation(self):
        # A deadline changes P6's float, not when the project finishes or what drives the finish.
        none = simulate(build_schedule(read_xer(os.path.join(T, "synth_500.xer"))))
        for date in ("2030-06-28 17:00", "2029-03-15 17:00", "2028-06-30 17:00"):
            with self.subTest(date=date):
                r = simulate(with_must_finish_by("synth_500.xer", "SYN500", date))
                self.assertEqual(none.finish, r.finish)
                self.assertEqual(none.crit_count, r.crit_count)
                self.assertEqual(none.milestones, r.milestones)

    def test_critical_filter_does_not_depend_on_the_must_finish_by(self):
        path = CpmEngine(build_schedule(read_xer(os.path.join(T, "synth_500.xer")))).critical_to_project_finish()
        self.assertEqual(6, sum(path))
        for date, p6_critical in (("2030-06-28 17:00", 0), ("2028-06-30 17:00", 315)):
            with self.subTest(date=date):
                s = with_must_finish_by("synth_500.xer", "SYN500", date)
                det = CpmEngine(s).run()
                self.assertEqual(p6_critical, sum(det.critical(s, j) for j in range(len(s.activities))))  # P6's float
                self.assertEqual(path, CpmEngine(s).critical_to_project_finish())

    def test_results_do_not_depend_on_the_calendar_horizon(self):
        a = with_must_finish_by("synth_500.xer", "SYN500", "2027-06-30 17:00", horizon_years=30)
        b = with_must_finish_by("synth_500.xer", "SYN500", "2027-06-30 17:00", horizon_years=60)
        self.assertNotEqual(a.cal24.h0, b.cal24.h0)
        ra, rb = CpmEngine(a).run(), CpmEngine(b).run()
        for f in ("es", "ef", "ls", "lf", "tf"):
            self.assertEqual(getattr(ra, f), getattr(rb, f), f)


if __name__ == "__main__":
    unittest.main()
