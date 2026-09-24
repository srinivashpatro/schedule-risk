"""Monte Carlo schedule risk simulation and results analytics."""
import math

from .numerics import (lhs_uniforms, mc_uniforms, Rng, stream_seed, norm_inv, cholesky, repair_correlation,
                       invert_lower, pearson_matrix, ranks, spearman, round_half_up)
from .cpm import CpmEngine
from .model import COMPLETE
from .calendar import fmt_min

PRE, POST = "pre", "post"
PERCENTILES = (5, 10, 20, 30, 40, 50, 60, 70, 80, 90, 95)


def iman_conover(cols, target, seed, var_ids, batch):
    """Reorder each column of uniforms so their rank correlation approximates `target`."""
    k = len(cols)
    n = len(cols[0])
    if k < 2 or n < 3:
        return cols, 1.0
    scores = [norm_inv((i + 1) / (n + 1)) for i in range(n)]
    S = []
    for c in range(k):
        rng = Rng(stream_seed(seed, var_ids[c], batch, 1))
        p = list(range(n))
        for i in range(n - 1, 0, -1):
            j = rng.next_int(i + 1)
            p[i], p[j] = p[j], p[i]
        S.append([scores[p[i]] for i in range(n)])
    E = pearson_matrix(S)
    F = cholesky(E)
    if F is None:
        return cols, 1.0
    _, P, shrink = repair_correlation(target)
    Finv = invert_lower(F)
    M = [[0.0] * k for _ in range(k)]
    for a in range(k):
        for b in range(k):
            acc = 0.0
            for m in range(k):
                acc += P[a][m] * Finv[m][b]
            M[a][b] = acc
    out = []
    for c in range(k):
        Mc = M[c]
        t = [0.0] * n
        for i in range(n):
            acc = 0.0
            for m in range(k):
                acc += Mc[m] * S[m][i]
            t[i] = acc
        r = ranks(t)
        su = sorted(cols[c])
        out.append([su[r[i]] for i in range(n)])
    return out, shrink


class SimResult:
    def __init__(self):
        self.finish = []            # project finish per iteration (minutes)
        self.milestones = {}        # activity idx -> list of finish minutes
        self.crit_count = None      # per activity
        self.durations = {}         # activity idx -> list of sampled remaining durations (minutes)
        self.risk_impact = []       # per risk: list of realised impact (0 if not occurred)
        self.risk_occ = []          # per risk: list of 0/1
        self.driver_value = []      # per driver: list of factor (%) (100 if not occurred)
        self.iterations = 0
        self.batches = 0
        self.converged = None
        self.deterministic = None
        self.warnings = []


class Simulation:
    def __init__(self, s, model, scenario=PRE):
        self.s = s
        self.m = model
        self.scenario = scenario
        self.engine = CpmEngine(s)
        acts = s.activities
        self.base = [a.rem_dur for a in acts]
        self.mpd = [a.cal.minutes_per_day() for a in acts]
        # ---- variables, in a fixed order shared with the C# engine
        self.unc_var = {}
        v = 0
        for j in sorted(model.uncertainty):
            self.unc_var[j] = v
            v += 1
        self.risk_var = []
        for _ in model.risks:
            self.risk_var.append((v, v + 1))
            v += 2
        self.driver_var = []
        for _ in model.drivers:
            self.driver_var.append((v, v + 1))
            v += 2
        self.nvars = v
        # per-activity effect lists
        self.act_drivers = {}
        for di, d in enumerate(model.drivers):
            for j in d.acts:
                self.act_drivers.setdefault(j, []).append(di)
        self.act_risks = {}
        for ri, r in enumerate(model.risks):
            for j in r.acts:
                self.act_risks.setdefault(j, []).append(ri)
        self.affected = sorted(set(self.unc_var) | set(self.act_drivers) | set(self.act_risks))
        # correlation: one matrix over every uncertainty variable that appears in a group
        corr_acts = []
        for acts_, _ in model.correlations:
            for j in acts_:
                if j not in corr_acts:
                    corr_acts.append(j)
        corr_acts.sort()
        self.corr_acts = corr_acts
        pos = {j: i for i, j in enumerate(corr_acts)}
        k = len(corr_acts)
        self.corr_target = [[1.0 if a == b else 0.0 for b in range(k)] for a in range(k)]
        for acts_, coef in model.correlations:
            for a in acts_:
                for b in acts_:
                    if a != b:
                        self.corr_target[pos[a]][pos[b]] = coef
        if k:
            _, _, shrink = repair_correlation(self.corr_target)
            if shrink < 1.0:
                model.warnings.append(f"correlation matrix not consistent; off-diagonals scaled by {shrink:.3f}")

    def _risk_params(self, ri):
        r = self.m.risks[ri]
        if self.scenario == POST:
            return r.mit_prob, r.mit_impact
        return r.prob, r.impact

    def run(self, iterations=None, seed=None, convergence=None, keep_durations=True, progress=None):
        st = self.m.settings
        n_total = int(iterations if iterations is not None else st["iterations"])
        seed = int(seed if seed is not None else st["seed"])
        conv = dict(st["convergence"])
        if convergence is not None:
            conv.update(convergence)
        lhs = str(st.get("sampling", "lhs")).lower() == "lhs"
        s = self.s
        acts = s.activities
        n = len(acts)
        pcal = s.settings.project_calendar
        res = SimResult()
        det = self.engine.run()
        res.deterministic = det.project_finish
        res.crit_count = [0] * n
        mile_idx = [a.idx for a in acts if a.is_milestone and a.status != COMPLETE]
        for j in mile_idx:
            res.milestones[j] = []
        if keep_durations:
            for j in self.affected:
                res.durations[j] = []
        res.risk_impact = [[] for _ in self.m.risks]
        res.risk_occ = [[] for _ in self.m.risks]
        res.driver_value = [[] for _ in self.m.drivers]
        crit_limit = s.settings.critical_float
        driving = self.engine.driving

        if conv.get("enabled"):
            batch = int(conv["batchSize"])
            max_it = int(conv["maxIterations"])
            min_it = int(conv["minIterations"])
            tol = float(conv["toleranceDays"]) * pcal.minutes_per_day()
            pct = int(conv["percentile"])
        else:
            batch = n_total
            max_it = n_total
        prev = None
        stable = 0
        b = 0
        done = 0
        while done < max_it:
            nb = min(batch, max_it - done)
            gen = lhs_uniforms if lhs else mc_uniforms
            U = [gen(seed, v, b, nb) for v in range(self.nvars)]
            if self.corr_acts:
                vids = [self.unc_var[j] for j in self.corr_acts]
                cols, _ = iman_conover([U[v] for v in vids], self.corr_target, seed, vids, b)
                for c, v in enumerate(vids):
                    U[v] = cols[c]
            for i in range(nb):
                dur = list(self.base)
                # realise risks and drivers once per iteration
                rimp = []
                for ri in range(len(self.m.risks)):
                    p, dist = self._risk_params(ri)
                    vo, vi = self.risk_var[ri]
                    if U[vo][i] < p:
                        rimp.append(dist.inv(U[vi][i]))
                    else:
                        rimp.append(None)
                dval = []
                for di, d in enumerate(self.m.drivers):
                    vo, vi = self.driver_var[di]
                    dval.append(d.dist.inv(U[vi][i]) if U[vo][i] < d.prob else None)
                for j in self.affected:
                    base = self.base[j]
                    x = float(base)
                    u = self.m.uncertainty.get(j)
                    if u is not None:
                        val = u.dist.inv(U[self.unc_var[j]][i])
                        x = base * val / 100.0 if u.units == "percent" else val * self.mpd[j]
                    for di in self.act_drivers.get(j, ()):
                        if dval[di] is not None:
                            x = x * dval[di] / 100.0
                    for ri in self.act_risks.get(j, ()):
                        if rimp[ri] is not None:
                            r = self.m.risks[ri]
                            x = x + (rimp[ri] * self.mpd[j] if r.units == "days" else base * rimp[ri] / 100.0)
                    d = round_half_up(x)
                    dur[j] = d if d > 0 else 0
                    if keep_durations:
                        res.durations[j].append(dur[j])
                for ri in range(len(self.m.risks)):
                    res.risk_impact[ri].append(rimp[ri] if rimp[ri] is not None else 0.0)
                    res.risk_occ[ri].append(0 if rimp[ri] is None else 1)
                for di in range(len(self.m.drivers)):
                    res.driver_value[di].append(dval[di] if dval[di] is not None else 100.0)
                # Criticality is measured to this iteration's own finish: a Must Finish By is a deadline, and
                # must not change when the project finishes or what drives the finish.
                r = self.engine.run(dur, backward=True, float_to_project_finish=True)
                res.finish.append(r.project_finish)
                for j in mile_idx:
                    res.milestones[j].append(r.ef[j])
                tf = r.tf
                cc = res.crit_count
                for j in range(n):
                    if driving[j] and tf[j] is not None and tf[j] <= crit_limit:
                        cc[j] += 1
            done += nb
            b += 1
            if progress:
                progress(done, max_it)
            if conv.get("enabled"):
                cur = pcal.work_at(percentile(res.finish, pct))
                if prev is not None and abs(cur - prev) <= tol:
                    stable += 1
                else:
                    stable = 0
                prev = cur
                if done >= min_it and stable >= 2:
                    res.converged = True
                    break
        if conv.get("enabled") and res.converged is None:
            res.converged = False
        res.iterations = done
        res.batches = b
        return res


def percentile(values, p):
    """Nearest-rank percentile, p in whole percent."""
    v = sorted(values)
    n = len(v)
    idx = (p * n + 99) // 100 - 1
    if idx < 0:
        idx = 0
    if idx >= n:
        idx = n - 1
    return v[idx]


def summarize(sim, res, top=None):
    s = sim.s
    acts = s.activities
    pcal = s.settings.project_calendar
    mpd = pcal.minutes_per_day()
    n = res.iterations
    fin = res.finish
    out = {"iterations": n, "batches": res.batches, "converged": res.converged, "scenario": sim.scenario,
           "deterministic_finish": fmt_min(res.deterministic)}
    out["prob_meet_deterministic"] = sum(1 for f in fin if f <= res.deterministic) / n
    # A Must Finish By at 00:00 means by the end of the previous day, as in P6, so compare instants.
    mfb = s.settings.must_finish_by
    if mfb is not None:
        out["must_finish_by"] = fmt_min(mfb)
        out["prob_meet_must_finish_by"] = sum(1 for f in fin if f <= mfb) / n
    fw = [pcal.work_at(f) for f in fin]
    mean_w = sum(fw) / n
    var = sum((x - mean_w) ** 2 for x in fw) / (n - 1) if n > 1 else 0.0
    fstats = {f"P{p}": fmt_min(percentile(fin, p)) for p in PERCENTILES}
    fstats["min"] = fmt_min(min(fin))
    fstats["max"] = fmt_min(max(fin))
    fstats["mean"] = fmt_min(pcal.time_finish(round_half_up(mean_w)))
    fstats["stdev_working_days"] = round(math.sqrt(var) / mpd, 4)
    out["finish"] = fstats
    ms = []
    for j, vals in sorted(res.milestones.items()):
        a = acts[j]
        ms.append({"code": a.code, "name": a.name,
                   **{f"P{p}": fmt_min(percentile(vals, p)) for p in (10, 50, 80, 90)}})
    out["milestones"] = ms
    rows = []
    for j in range(len(acts)):
        ci = res.crit_count[j] / n
        sens = spearman(res.durations[j], fin) if j in res.durations else 0.0
        if ci == 0.0 and sens == 0.0:
            continue
        rows.append({"code": acts[j].code, "name": acts[j].name, "criticality": round(ci, 6),
                     "sensitivity": round(sens, 6), "cruciality": round(ci * sens, 6)})
    rows.sort(key=lambda r: (-abs(r["cruciality"]), -r["criticality"], r["code"]))
    out["activities"] = rows if top is None else rows[:top]
    risks = []
    for ri, r in enumerate(sim.m.risks):
        imp = res.risk_impact[ri]
        flags = res.risk_occ[ri]
        occ = [k for k in range(n) if flags[k]]
        nocc = [k for k in range(n) if not flags[k]]
        delta = None
        if occ and nocc:
            delta = (sum(fw[k] for k in occ) / len(occ) - sum(fw[k] for k in nocc) / len(nocc)) / mpd
        risks.append({"id": r.id, "title": r.title, "occurrence": round(len(occ) / n, 6),
                      "sensitivity": round(spearman(imp, fin), 6),
                      "mean_finish_delta_days": None if delta is None else round(delta, 4)})
    risks.sort(key=lambda x: (-abs(x["sensitivity"]), x["id"]))
    out["risks"] = risks
    drivers = []
    for di, d in enumerate(sim.m.drivers):
        drivers.append({"id": d.id, "title": d.title, "sensitivity": round(spearman(res.driver_value[di], fin), 6)})
    drivers.sort(key=lambda x: (-abs(x["sensitivity"]), x["id"]))
    out["drivers"] = drivers
    return out
