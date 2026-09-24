"""Regenerate test fixtures and golden results consumed by the C# test suite."""
import json, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), 'tests'))
import fixtures
from sra.synth import generate
from sra.xer import read_xer
from sra.model import build_schedule
from sra.cpm import CpmEngine
from sra.cli import cpm_table
from sra.calendar import fmt_min
from sra.validate import validate
from sra.risk import load_risk_model
from sra.sim import Simulation, summarize

T = fixtures.TESTDATA
G = os.path.join(T, "golden")


def main():
    os.makedirs(G, exist_ok=True)
    fixtures.write_all()
    fixtures.basic("rcal_24Hour").save(os.path.join(T, "hand_24h_lag.xer"))
    b = fixtures.basic()
    for row in b.rows["TASK"]:
        if row["task_code"] == "C":
            row["cstr_type"] = "CS_MSOA"
            row["cstr_date"] = "2026-01-12 08:00"
    b.save(os.path.join(T, "hand_constraint.xer"))
    from test_sim import parallel_builder
    parallel_builder(1).save(os.path.join(T, "parallel_1.xer"))
    parallel_builder(2).save(os.path.join(T, "parallel_2.xer"))
    b = fixtures.basic()
    b.rows["PROJECT"][0]["last_recalc_date"] = "2025-12-31 08:00"
    b.save(os.path.join(T, "hand_holiday.xer"))
    generate(200, seed=3, name="SYN200").save(os.path.join(T, "synth_200.xer"))
    generate(500, seed=7, name="SYN500").save(os.path.join(T, "synth_500.xer"))
    generate(5000, seed=11, name="SYN5000").save(os.path.join(T, "synth_5000.xer"))
    for f in ["hand_basic", "hand_24h_lag", "hand_constraint", "hand_holiday", "synth_200", "synth_500"]:
        s = build_schedule(read_xer(os.path.join(T, f + ".xer")))
        r = CpmEngine(s).run()
        json.dump({"project_finish": fmt_min(r.project_finish), "activities": cpm_table(s, r),
                   "validation": {c.key: c.count for c in validate(s, r)}},
                  open(os.path.join(G, f + ".cpm.json"), "w"), indent=1)
    s = build_schedule(read_xer(os.path.join(T, "synth_500.xer")))
    m = load_risk_model(s, os.path.join(T, "synth_500.risk.json"), crit=CpmEngine(s).critical_to_project_finish())
    for sc in ("pre", "post"):
        sim = Simulation(s, m, sc)
        out = summarize(sim, sim.run())
        json.dump(out, open(os.path.join(G, f"synth_500.sim.{sc}.json"), "w"), indent=1)
        print(sc, "P50", out["finish"]["P50"], "P80", out["finish"]["P80"], "det", out["deterministic_finish"],
              "p(det)", out["prob_meet_deterministic"], "risks", [(x["id"], x["mean_finish_delta_days"]) for x in out["risks"]])


if __name__ == "__main__" and "numerics" not in sys.argv:
    main()


def numerics_golden():
    from sra.numerics import Rng, stream_seed, lhs_uniforms, norm_inv, beta_inv, beta_inc, spearman
    from sra.risk import Dist
    r = Rng(12345)
    out = {
        "rng_seed": 12345,
        "rng_u64": [str(r.next_u64()) for _ in range(5)],
        "stream_seed": str(stream_seed(20260921, 7, 3, 1)),
        "lhs": lhs_uniforms(99, 4, 2, 10),
        "norm_inv": {str(p): norm_inv(p) for p in (0.001, 0.01, 0.2, 0.5, 0.8, 0.975, 0.999)},
        "beta_inv": [[p, a, b, beta_inv(p, a, b)] for (p, a, b) in ((0.5, 2.0, 3.0), (0.1, 1.8, 4.2), (0.9, 3.4, 1.6))],
        "pert_inv": [[u, Dist("pert", 90, 100, 125).inv(u)] for u in (0.0, 0.05, 0.37, 0.5, 0.95, 0.9999)],
        "tri_inv": [[u, Dist("triangle", 8, 10, 14).inv(u)] for u in (0.0, 0.2, 1 / 3, 0.7, 0.99)],
        "spearman": spearman([1, 5, 2, 8, 8, 3], [2, 6, 1, 9, 7, 3]),
    }
    json.dump(out, open(os.path.join(G, "numerics.json"), "w"), indent=1)


if __name__ == "__main__" and "numerics" in sys.argv:
    numerics_golden()
