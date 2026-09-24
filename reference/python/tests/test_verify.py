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


if __name__ == "__main__":
    unittest.main()
