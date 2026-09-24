import os
import unittest

from sra.xer import read_xer, parse_xer_text
from sra.model import build_schedule
from sra.cpm import CpmEngine
from sra.verify import verify_against_p6

T = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "..", "testdata")


class SyntheticFixturesVerify(unittest.TestCase):
    """The synthetic fixtures carry dates and total float written by this engine, so they must verify exactly."""

    def test_synthetic_fixtures_match_their_stored_dates(self):
        for name in ("synth_200", "synth_500", "synth_5000"):
            with self.subTest(name=name):
                s = build_schedule(read_xer(os.path.join(T, name + ".xer")))
                rep = verify_against_p6(s, CpmEngine(s).run())
                self.assertGreater(rep.compared, 100)
                self.assertEqual([], [(d.code, d.field, d.delta_min) for d in rep.diffs])
                self.assertEqual("matches", rep.outcome)

    def test_nothing_to_compare_without_p6_dates(self):
        s = build_schedule(read_xer(os.path.join(T, "hand_basic.xer")))
        rep = verify_against_p6(s, CpmEngine(s).run())
        self.assertEqual(0, rep.compared)
        self.assertEqual(len(s.activities), rep.skipped_no_p6)
        self.assertEqual([], rep.diffs)
        self.assertEqual("nothing", rep.outcome)
        self.assertFalse(rep.passed)

    def test_p6_dates_outside_the_calendar_range(self):
        """Far-off P6 dates are reported as differences in elapsed time, not a crash."""
        lines = open(os.path.join(T, "synth_200.xer"), encoding="utf-8").read().split("\n")
        table = fields = None
        for i, ln in enumerate(lines):
            cells = ln.rstrip("\r").split("\t")
            if cells[0] == "%T":
                table = cells[1]
            elif cells[0] == "%F" and table == "TASK":
                fields = cells
            elif cells[0] == "%R" and table == "TASK" and cells[fields.index("task_code")] == "A00290":
                cells[fields.index("late_end_date")] = "2099-01-01 17:00"
                cells[fields.index("late_start_date")] = "1990-01-02 08:00"
                lines[i] = "\t".join(cells)
        s = build_schedule(parse_xer_text("\n".join(lines)))
        rep = verify_against_p6(s, CpmEngine(s).run())
        self.assertEqual("differences", rep.outcome)
        self.assertEqual(["late_finish", "late_start"], sorted(d.field for d in rep.diffs))
        for d in rep.diffs:
            self.assertEqual("A00290", d.code)
            self.assertTrue(d.outside)
            self.assertEqual(d.ours - d.p6, d.delta_min)
        self.assertEqual(rep.fields_compared - 2, rep.fields_matched)

    def test_every_fixture_verifies_without_differences(self):
        """CLAUDE.md: `sra verify` must pass on every fixture in testdata/."""
        for name in sorted(f for f in os.listdir(T) if f.endswith(".xer")):
            with self.subTest(name=name):
                s = build_schedule(read_xer(os.path.join(T, name)))
                rep = verify_against_p6(s, CpmEngine(s).run())
                self.assertEqual([], [(d.code, d.field, d.delta_min) for d in rep.diffs])
                self.assertNotEqual("differences", rep.outcome)


if __name__ == "__main__":
    unittest.main()
