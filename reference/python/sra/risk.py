"""Risk model: duration uncertainty, discrete risk register, risk drivers, correlation.

Loaded from a JSON file (schema documented in docs/RISK_MODEL.md). Activity
selection uses filters so a model written once applies to thousands of activities.
"""
import json
import math
import re

from .numerics import beta_inv

TRIANGLE, PERT, UNIFORM = "triangle", "pert", "uniform"
_DIST_ALIASES = {"triangle": TRIANGLE, "triangular": TRIANGLE, "tri": TRIANGLE,
                 "pert": PERT, "betapert": PERT, "beta-pert": PERT, "beta_pert": PERT,
                 "uniform": UNIFORM}


class Dist:
    """Three-point distribution. Values are in whatever unit the owner says (percent or days)."""
    __slots__ = ("kind", "lo", "ml", "hi", "_table")
    TABLE_N = 4096

    def __init__(self, kind, lo, ml, hi):
        k = _DIST_ALIASES.get(str(kind).lower().replace(" ", ""))
        if k is None:
            raise ValueError(f"unknown distribution {kind!r}")
        lo, ml, hi = float(lo), float(ml), float(hi)
        if not (lo <= ml <= hi):
            raise ValueError(f"distribution needs min <= most likely <= max, got {lo}, {ml}, {hi}")
        self.kind, self.lo, self.ml, self.hi = k, lo, ml, hi
        self._table = None

    def _pert_table(self):
        # Inverse CDF tabulated at TABLE_N+1 points; linear interpolation between them.
        a = 1.0 + 4.0 * (self.ml - self.lo) / (self.hi - self.lo)
        b = 1.0 + 4.0 * (self.hi - self.ml) / (self.hi - self.lo)
        n = self.TABLE_N
        self._table = [beta_inv(i / n, a, b) for i in range(n + 1)]
        return self._table

    def inv(self, u):
        lo, ml, hi = self.lo, self.ml, self.hi
        if hi <= lo:
            return lo
        if self.kind == UNIFORM:
            return lo + u * (hi - lo)
        if self.kind == TRIANGLE:
            fc = (ml - lo) / (hi - lo)
            if u < fc:
                return lo + math.sqrt(u * (hi - lo) * (ml - lo))
            return hi - math.sqrt((1.0 - u) * (hi - lo) * (hi - ml))
        t = self._table if self._table is not None else self._pert_table()
        x = u * self.TABLE_N
        i = int(x)
        if i >= self.TABLE_N:
            return hi
        f = x - i
        return lo + (hi - lo) * (t[i] + (t[i + 1] - t[i]) * f)

    def mean(self):
        if self.kind == UNIFORM:
            return (self.lo + self.hi) / 2
        if self.kind == TRIANGLE:
            return (self.lo + self.ml + self.hi) / 3
        return (self.lo + 4 * self.ml + self.hi) / 6

    def to_json(self):
        return {"distribution": self.kind, "min": self.lo, "mostLikely": self.ml, "max": self.hi}


def dist_from(d):
    return Dist(d.get("distribution", "triangle"), d["min"], d.get("mostLikely", d.get("ml", d["min"])), d["max"])


# ------------------------------------------------------------------ filters

def select(s, spec, crit=None):
    """Return activity indexes matching a filter spec (AND of the keys present)."""
    if spec is None:
        return []
    if isinstance(spec, list):  # plain list of activity ids
        spec = {"activities": spec}
    acts = s.activities
    idx = [a.idx for a in acts]
    if "activities" in spec:
        wanted = set(spec["activities"])
        missing = [c for c in spec["activities"] if c not in s.by_code]
        if missing:
            raise ValueError("unknown activity ids in risk model: " + ", ".join(missing[:10]))
        idx = [j for j in idx if acts[j].code in wanted]
    if "wbs" in spec:
        prefs = spec["wbs"] if isinstance(spec["wbs"], list) else [spec["wbs"]]
        idx = [j for j in idx if any(_wbs_match(s.wbs_path(acts[j].wbs_id), p) for p in prefs)]
    if "code" in spec:
        for ctype, cval in spec["code"].items():
            vals = cval if isinstance(cval, list) else [cval]
            idx = [j for j in idx if acts[j].codes.get(ctype) in vals]
    if "namePattern" in spec:
        rx = re.compile(spec["namePattern"], re.IGNORECASE)
        idx = [j for j in idx if rx.search(acts[j].name or "")]
    if spec.get("critical"):
        if crit is None:
            raise ValueError("filter 'critical' needs a deterministic CPM result")
        idx = [j for j in idx if crit[j]]
    if "exclude" in spec:
        ex = set(spec["exclude"])
        idx = [j for j in idx if acts[j].code not in ex]
    if not any(k in spec for k in ("all", "activities", "wbs", "code", "namePattern", "critical")):
        raise ValueError(f"filter selects nothing specific: {spec}")
    return idx


def _wbs_match(path, prefix):
    return path == prefix or path.startswith(prefix + ".") or path.endswith("." + prefix) or ("." + prefix + ".") in ("." + path + ".")


# ------------------------------------------------------------------ model

class Uncertainty:
    __slots__ = ("dist", "units")

    def __init__(self, dist, units):
        self.dist, self.units = dist, units


class Risk:
    __slots__ = ("id", "title", "prob", "impact", "units", "acts", "mit_prob", "mit_impact")


class Driver:
    __slots__ = ("id", "title", "prob", "dist", "acts")


class RiskModel:
    def __init__(self):
        self.name = ""
        self.uncertainty = {}   # activity idx -> Uncertainty
        self.risks = []
        self.drivers = []
        self.correlations = []  # list of (activity idx list, coefficient)
        self.settings = {"iterations": 1000, "seed": 1, "sampling": "lhs",
                         "convergence": {"enabled": False, "percentile": 80, "toleranceDays": 1.0,
                                         "batchSize": 250, "minIterations": 500, "maxIterations": 10000}}
        self.warnings = []


def load_risk_model(s, path_or_dict, crit=None):
    data = path_or_dict
    if isinstance(path_or_dict, str):
        with open(path_or_dict, "r", encoding="utf-8-sig") as fh:
            data = json.load(fh)
    m = RiskModel()
    m.name = data.get("name", "")
    sim = data.get("simulation", {})
    for k in ("iterations", "seed", "sampling"):
        if k in sim:
            m.settings[k] = sim[k]
    if "convergence" in sim:
        m.settings["convergence"].update(sim["convergence"])

    def usable(j, what):
        a = s.activities[j]
        if a.is_milestone or a.is_summary or a.status == 2:
            return False
        return True

    for u in data.get("uncertainty", []):
        dist = dist_from(u)
        units = u.get("units", "percent").lower()
        if units not in ("percent", "days"):
            raise ValueError(f"uncertainty units must be percent or days, got {units!r}")
        for j in select(s, u.get("filter", {"all": True}), crit):
            if usable(j, "uncertainty"):
                m.uncertainty[j] = Uncertainty(dist, units)

    for r in data.get("risks", []):
        rk = Risk()
        rk.id = r["id"]
        rk.title = r.get("title", rk.id)
        rk.prob = float(r["probability"])
        rk.impact = dist_from(r["impact"])
        rk.units = r["impact"].get("units", "days").lower()
        acts = select(s, r.get("filter", r.get("activities")), crit)
        rk.acts = [j for j in acts if usable(j, "risk")]
        skipped = len(acts) - len(rk.acts)
        if skipped:
            m.warnings.append(f"risk {rk.id}: {skipped} milestone/summary/complete activities ignored")
        if not rk.acts:
            m.warnings.append(f"risk {rk.id}: maps to no open activities")
        mit = r.get("mitigated")
        rk.mit_prob = float(mit.get("probability", rk.prob)) if mit else rk.prob
        rk.mit_impact = dist_from(mit["impact"]) if mit and "impact" in mit else rk.impact
        if not (0.0 <= rk.prob <= 1.0 and 0.0 <= rk.mit_prob <= 1.0):
            raise ValueError(f"risk {rk.id}: probability must be between 0 and 1")
        m.risks.append(rk)

    for d in data.get("drivers", []):
        dv = Driver()
        dv.id = d["id"]
        dv.title = d.get("title", dv.id)
        dv.prob = float(d.get("probability", 1.0))
        dv.dist = dist_from(d)
        dv.acts = [j for j in select(s, d.get("filter", d.get("activities")), crit) if usable(j, "driver")]
        m.drivers.append(dv)

    for c in data.get("correlations", []):
        acts = [j for j in select(s, c.get("filter", c.get("activities")), crit) if j in m.uncertainty]
        coef = float(c["coefficient"])
        if not -1.0 < coef < 1.0:
            raise ValueError("correlation coefficient must be strictly between -1 and 1")
        if len(acts) >= 2:
            m.correlations.append((acts, coef))
        else:
            m.warnings.append("correlation group with fewer than 2 uncertain activities ignored")
    return m
