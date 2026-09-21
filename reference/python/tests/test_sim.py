import math
import unittest

from fixtures import D
from sra.xerbuild import XerBuilder, clndr_data, FIVE_BY_EIGHT
from sra.xer import parse_xer_text
from sra.model import build_schedule
from sra.risk import load_risk_model, Dist
from sra.sim import Simulation, summarize, percentile
from sra.numerics import spearman, lhs_uniforms, beta_inc


def parallel_xer(n_par=2, days=10):
    return build_schedule(parse_xer_text(parallel_builder(n_par, days).text()))


def parallel_builder(n_par=2, days=10):
    b = XerBuilder()
    b.add("PROJECT", proj_id="1", proj_short_name="PAR", clndr_id="1", last_recalc_date="2026-01-05 08:00",
          sched_calendar_on_relationship_lag="rcal_Predecessor", sched_retained_logic="Y")
    b.add("CALENDAR", clndr_id="1", default_flag="Y", clndr_name="5x8", day_hr_cnt="8", clndr_data=clndr_data(FIVE_BY_EIGHT))
    b.add("PROJWBS", wbs_id="1", proj_id="1", wbs_short_name="PAR", wbs_name="PAR", proj_node_flag="Y")
    b.add("TASK", task_id="0", proj_id="1", wbs_id="1", clndr_id="1", task_code="START", task_type="TT_Mile",
          status_code="TK_NotStart", target_drtn_hr_cnt=0, remain_drtn_hr_cnt=0)
    b.add("TASK", task_id="99", proj_id="1", wbs_id="1", clndr_id="1", task_code="FIN", task_type="TT_FinMile",
          status_code="TK_NotStart", target_drtn_hr_cnt=0, remain_drtn_hr_cnt=0)
    for i in range(n_par):
        b.add("TASK", task_id=str(i + 1), proj_id="1", wbs_id="1", clndr_id="1", task_code=f"P{i + 1}", task_name=f"par {i+1}",
              task_type="TT_Task", status_code="TK_NotStart", target_drtn_hr_cnt=days * 8, remain_drtn_hr_cnt=days * 8)
        b.add("TASKPRED", task_pred_id=f"a{i}", task_id=str(i + 1), pred_task_id="0", proj_id="1", pred_proj_id="1", pred_type="PR_FS", lag_hr_cnt=0)
        b.add("TASKPRED", task_pred_id=f"b{i}", task_id="99", pred_task_id=str(i + 1), proj_id="1", pred_proj_id="1", pred_type="PR_FS", lag_hr_cnt=0)
    return b


def wdays(s, t):
    c = s.settings.project_calendar
    return (c.work_at(t) - c.work_at(s.settings.data_date)) / 480.0


class Numerics(unittest.TestCase):
    def test_lhs_stratified(self):
        u = lhs_uniforms(42, 3, 0, 1000)
        strata = sorted(int(x * 1000) for x in u)
        self.assertEqual(strata, list(range(1000)))

    def test_distribution_quantiles(self):
        t = Dist("triangle", 8, 10, 14)
        # CDF at mode = (10-8)/(14-8) = 1/3
        self.assertAlmostEqual(t.inv(1 / 3), 10.0, places=9)
        # PERT median close to the analytic beta median
        p = Dist("pert", 0, 2, 10)
        a, b = 1 + 4 * 0.2, 1 + 4 * 0.8
        x = p.inv(0.5) / 10.0
        self.assertAlmostEqual(beta_inc(a, b, x), 0.5, places=4)
        self.assertAlmostEqual(Dist("uniform", 5, 5, 15).inv(0.25), 7.5)


class SimulationAnalytic(unittest.TestCase):
    def test_single_triangle(self):
        s = parallel_xer(1)
        m = load_risk_model(s, {"uncertainty": [{"filter": {"activities": ["P1"]}, "distribution": "triangle",
                                                 "min": 80, "mostLikely": 100, "max": 140}]})
        sim = Simulation(s, m)
        r = sim.run(iterations=2000, seed=11)
        d = Dist("triangle", 8, 10, 14)
        for p in (10, 50, 80, 90):
            got = wdays(s, percentile(r.finish, p))
            self.assertAlmostEqual(got, d.inv(p / 100), delta=0.02, msg=f"P{p}")

    def test_max_of_two_uniforms(self):
        s = parallel_xer(2)
        spec = {"uncertainty": [{"filter": {"all": True}, "distribution": "uniform", "min": 50, "mostLikely": 100, "max": 150}]}
        r = Simulation(s, load_risk_model(s, spec)).run(iterations=4000, seed=3)
        # max of two independent U(5,15): F(x) = ((x-5)/10)^2 -> P50 = 5 + 10*sqrt(.5)
        self.assertAlmostEqual(wdays(s, percentile(r.finish, 50)), 5 + 10 * math.sqrt(0.5), delta=0.1)
        self.assertAlmostEqual(wdays(s, percentile(r.finish, 90)), 5 + 10 * math.sqrt(0.9), delta=0.1)

    def test_correlation_removes_merge_bias(self):
        s = parallel_xer(2)
        spec = {"uncertainty": [{"filter": {"all": True}, "distribution": "uniform", "min": 50, "mostLikely": 100, "max": 150}],
                "correlations": [{"filter": {"all": True}, "coefficient": 0.95}]}
        sim = Simulation(s, load_risk_model(s, spec))
        r = sim.run(iterations=2000, seed=3)
        rho = spearman(r.durations[s.by_code["P1"]], r.durations[s.by_code["P2"]])
        self.assertAlmostEqual(rho, 0.95, delta=0.03)
        # strongly correlated -> max(U1,U2) ~ U -> P50 near 10 days rather than 12.07
        self.assertLess(wdays(s, percentile(r.finish, 50)), 11.0)

    def test_discrete_risk(self):
        s = parallel_xer(1)
        spec = {"risks": [{"id": "R1", "probability": 0.3, "activities": ["P1"],
                           "impact": {"distribution": "uniform", "min": 10, "mostLikely": 10, "max": 10, "units": "days"}}]}
        sim = Simulation(s, load_risk_model(s, spec))
        r = sim.run(iterations=1000, seed=5)
        self.assertEqual(sum(r.risk_occ[0]), 300)  # LHS: exactly 30% of iterations
        self.assertAlmostEqual(wdays(s, percentile(r.finish, 70)), 10.0)
        self.assertAlmostEqual(wdays(s, percentile(r.finish, 71)), 20.0)
        out = summarize(sim, r)
        self.assertAlmostEqual(out["risks"][0]["mean_finish_delta_days"], 10.0)

    def test_mitigation_scenario(self):
        s = parallel_xer(1)
        spec = {"risks": [{"id": "R1", "probability": 0.5, "activities": ["P1"],
                           "impact": {"distribution": "triangle", "min": 5, "mostLikely": 10, "max": 20, "units": "days"},
                           "mitigated": {"probability": 0.1}}]}
        m = load_risk_model(s, spec)
        pre = Simulation(s, m, "pre").run(iterations=1000, seed=1)
        post = Simulation(s, m, "post").run(iterations=1000, seed=1)
        self.assertEqual(sum(pre.risk_occ[0]), 500)
        self.assertEqual(sum(post.risk_occ[0]), 100)
        self.assertLess(percentile(post.finish, 80), percentile(pre.finish, 80))

    def test_deterministic_and_seeded(self):
        s = parallel_xer(3)
        spec = {"uncertainty": [{"filter": {"all": True}, "distribution": "pert", "min": 90, "mostLikely": 100, "max": 160}]}
        m = load_risk_model(s, spec)
        a = Simulation(s, m).run(iterations=300, seed=9)
        b = Simulation(s, m).run(iterations=300, seed=9)
        c = Simulation(s, m).run(iterations=300, seed=10)
        self.assertEqual(a.finish, b.finish)
        self.assertNotEqual(a.finish, c.finish)

    def test_convergence_stops(self):
        s = parallel_xer(2)
        spec = {"uncertainty": [{"filter": {"all": True}, "distribution": "triangle", "min": 90, "mostLikely": 100, "max": 130}],
                "simulation": {"convergence": {"enabled": True, "percentile": 80, "toleranceDays": 0.5,
                                               "batchSize": 200, "minIterations": 400, "maxIterations": 5000}}}
        r = Simulation(s, load_risk_model(s, spec)).run(seed=2)
        self.assertTrue(r.converged)
        self.assertLess(r.iterations, 5000)
        self.assertEqual(r.iterations % 200, 0)

    def test_criticality(self):
        s = parallel_xer(2)
        spec = {"uncertainty": [{"filter": {"activities": ["P1"]}, "distribution": "uniform", "min": 50, "mostLikely": 100, "max": 150},
                                {"filter": {"activities": ["P2"]}, "distribution": "uniform", "min": 50, "mostLikely": 100, "max": 150}]}
        sim = Simulation(s, load_risk_model(s, spec))
        r = sim.run(iterations=2000, seed=4)
        ci1 = r.crit_count[s.by_code["P1"]] / r.iterations
        ci2 = r.crit_count[s.by_code["P2"]] / r.iterations
        # symmetric: each critical ~50% (ties count for both)
        self.assertAlmostEqual(ci1, 0.5, delta=0.05)
        self.assertAlmostEqual(ci2, 0.5, delta=0.05)


if __name__ == "__main__":
    unittest.main()
