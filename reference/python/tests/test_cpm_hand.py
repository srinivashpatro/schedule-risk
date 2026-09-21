import unittest

from fixtures import basic
from sra.xer import parse_xer_text
from sra.model import build_schedule
from sra.cpm import CpmEngine
from sra.calendar import fmt_min


def run(builder):
    s = build_schedule(parse_xer_text(builder.text()))
    r = CpmEngine(s).run()
    out = {}
    for a in s.activities:
        j = a.idx
        out[a.code] = (fmt_min(r.es[j]), fmt_min(r.ef[j]), fmt_min(r.ls[j]), fmt_min(r.lf[j]), r.tf[j])
    return s, r, out


class HandBasic(unittest.TestCase):
    """Expected values derived by hand in the comments of this test."""

    def test_predecessor_lag_calendar(self):
        s, r, o = run(basic())
        # S start milestone at the data date
        self.assertEqual(o["S"][:2], ("2026-01-05 08:00", "2026-01-05 08:00"))
        # A 5 days
        self.assertEqual(o["A"], ("2026-01-05 08:00", "2026-01-09 17:00", "2026-01-05 08:00", "2026-01-09 17:00", 0))
        # B: FS +3d from Fri 17:00 consumes Mon-Wed -> starts Thu; 3 days -> Mon 17:00; critical via FF to D
        self.assertEqual(o["B"], ("2026-01-15 08:00", "2026-01-19 17:00", "2026-01-15 08:00", "2026-01-19 17:00", 0))
        # C: SS +2d -> Wed 01-07; 10 days -> Tue 01-20
        self.assertEqual(o["C"], ("2026-01-07 08:00", "2026-01-20 17:00", "2026-01-07 08:00", "2026-01-20 17:00", 0))
        # D: FF +1d from B finish Mon 17:00 -> must finish Tue 17:00, 2 days -> starts Mon 08:00
        self.assertEqual(o["D"], ("2026-01-19 08:00", "2026-01-20 17:00", "2026-01-19 08:00", "2026-01-20 17:00", 0))
        self.assertEqual(o["M"][:2], ("2026-01-20 17:00", "2026-01-20 17:00"))
        self.assertEqual(fmt_min(r.project_finish), "2026-01-20 17:00")

    def test_24h_lag_calendar(self):
        s, r, o = run(basic("rcal_24Hour"))
        # B: Fri 17:00 + 24 elapsed hours = Sat 17:00 -> next work Mon 01-12 08:00; 3 days -> Wed 17:00
        self.assertEqual(o["B"][:2], ("2026-01-12 08:00", "2026-01-14 17:00"))
        # D: FF +8 elapsed hours from Wed 17:00 = Thu 01:00 -> 2 days of work ending there -> starts Tue 08:00
        self.assertEqual(o["D"][:2], ("2026-01-13 08:00", "2026-01-14 17:00"))
        # C: SS +16 elapsed hours from Mon 08:00 = Tue 00:00 -> Tue 08:00; 10 days -> Mon 01-19 17:00
        self.assertEqual(o["C"][:2], ("2026-01-06 08:00", "2026-01-19 17:00"))
        self.assertEqual(o["M"][1], "2026-01-19 17:00")
        # D float: LF Mon 01-19 17:00 vs EF Wed 01-14 17:00 = Thu, Fri, Mon = 3 days
        self.assertEqual(o["D"][4], 3 * 480)
        # B late finish = D LF (Mon 17:00) - 8 elapsed hours = Mon 09:00; float = Thu+Fri + 1h = 17h
        self.assertEqual(o["B"][3], "2026-01-19 09:00")
        self.assertEqual(o["B"][4], 17 * 60)
        # A: SS link to C late start Tue 08:00 - 16 elapsed h = Mon 16:00 -> 7h of float
        self.assertEqual(o["A"][4], 7 * 60)

    def test_holiday_and_constraint(self):
        b = basic()
        # Put an SNET constraint on C at Mon 01-12 and move the data date before New Year holiday
        for row in b.rows["TASK"]:
            if row["task_code"] == "C":
                row["cstr_type"] = "CS_MSOA"
                row["cstr_date"] = "2026-01-12 08:00"
        s, r, o = run(b)
        # C 10 days from Mon 01-12 -> Fri 01-23 17:00 ; becomes the driver of M
        self.assertEqual(o["C"][:2], ("2026-01-12 08:00", "2026-01-23 17:00"))
        self.assertEqual(o["M"][1], "2026-01-23 17:00")
        # D now has 3 days of float (Wed, Thu, Fri)
        self.assertEqual(o["D"][4], 3 * 480)

    def test_holiday_skipped(self):
        b = basic()
        b.rows["PROJECT"][0]["last_recalc_date"] = "2025-12-31 08:00"
        s, r, o = run(b)
        # data date Wed 12-31; Jan 1 is a holiday -> A: Wed, Fri, Mon, Tue, Wed = 5 days -> Wed 01-07 17:00
        self.assertEqual(o["A"][:2], ("2025-12-31 08:00", "2026-01-07 17:00"))


if __name__ == "__main__":
    unittest.main()
