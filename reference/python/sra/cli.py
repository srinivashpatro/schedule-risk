"""Reference CLI:  python -m sra.cli <command> file.xer [options]

Commands: info | cpm | validate | verify | simulate
"""
import argparse
import json
import sys
import time

from .xer import read_xer
from .model import build_schedule, list_projects
from .cpm import CpmEngine
from .calendar import fmt_min
from .validate import validate
from .verify import verify_against_p6
from .risk import load_risk_model
from .sim import Simulation, summarize


def cpm_table(s, r):
    rows = []
    for a in s.activities:
        j = a.idx
        rows.append({"code": a.code, "es": fmt_min(r.es[j]), "ef": fmt_min(r.ef[j]),
                     "ls": fmt_min(r.ls[j]) if r.ls[j] is not None else "",
                     "lf": fmt_min(r.lf[j]) if r.lf[j] is not None else "",
                     "tf_min": r.tf[j]})
    return rows


def main(argv=None):
    ap = argparse.ArgumentParser(prog="sra")
    ap.add_argument("command", choices=["info", "cpm", "validate", "verify", "simulate"])
    ap.add_argument("xer")
    ap.add_argument("--project")
    ap.add_argument("--risk")
    ap.add_argument("--iterations", type=int)
    ap.add_argument("--seed", type=int)
    ap.add_argument("--scenario", default="pre", choices=["pre", "post"])
    ap.add_argument("--json", action="store_true")
    a = ap.parse_args(argv)
    doc = read_xer(a.xer)
    if a.command == "info":
        for p in list_projects(doc):
            print("project", *p)
        for name, t in doc.tables.items():
            print(f"{name:12s} {len(t.rows):7d} rows  {len(t.fields):3d} fields")
        return 0
    s = build_schedule(doc, a.project)
    for w in s.warnings:
        print("warning:", w, file=sys.stderr)
    t0 = time.time()
    r = CpmEngine(s).run()
    if a.command == "cpm":
        rows = cpm_table(s, r)
        if a.json:
            json.dump(rows, sys.stdout, indent=1)
        else:
            for x in rows:
                print(f"{x['code']:12s} {x['es']} {x['ef']} {x['ls']:16s} {x['lf']:16s} {x['tf_min']}")
            print("project finish", fmt_min(r.project_finish), f"({(time.time() - t0) * 1000:.0f} ms)")
    elif a.command == "validate":
        for c in validate(s, r):
            print(f"{'PASS' if c.passed else 'FAIL'} {c.title:50s} {c.count:5d}/{c.total:<6d} {c.pct:5.1f}%  {c.note}")
    elif a.command == "verify":
        rep = verify_against_p6(s, r)
        print(f"compared {rep.compared} activities, {rep.fields_matched}/{rep.fields_compared} fields match, "
              f"{rep.activities_matched} activities fully match")
        for d in rep.worst(30):
            print(f"  {d.code:12s} {d.field:16s} p6={d.p6} ours={d.ours} delta={d.delta_min / 60:+.1f}h")
        if rep.outcome == "nothing":
            print("nothing to compare - no P6-calculated dates in this file")
        return 1 if rep.outcome == "differences" else 0
    elif a.command == "simulate":
        if not a.risk:
            ap.error("simulate needs --risk model.json")
        m = load_risk_model(s, a.risk, crit=[r.critical(s, j) for j in range(len(s.activities))])
        for w in m.warnings:
            print("warning:", w, file=sys.stderr)
        sim = Simulation(s, m, a.scenario)
        res = sim.run(iterations=a.iterations, seed=a.seed)
        out = summarize(sim, res, top=25)
        json.dump(out, sys.stdout, indent=1)
        print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
