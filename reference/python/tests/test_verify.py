import os
import unittest

from sra.xer import read_xer
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
