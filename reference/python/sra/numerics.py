"""Deterministic numerics shared bit-for-bit (as far as IEEE allows) with the C# engine.

Everything here has a line-for-line twin in src/ScheduleRisk.Core/Numerics/*.cs.
Do not replace these with library calls (random, scipy, numpy.random): the whole
point is that both engines produce the same samples from the same seed.
"""
import math

MASK64 = (1 << 64) - 1


def splitmix64(x):
    """One SplitMix64 step. Returns (new_state, output)."""
    x = (x + 0x9E3779B97F4A7C15) & MASK64
    z = x
    z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK64
    z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK64
    z = z ^ (z >> 31)
    return x, z


def stream_seed(seed, var_index, batch_index, salt=0):
    """Mix (seed, variable, batch, salt) into a 64-bit stream seed."""
    s = seed & MASK64
    for v in (var_index, batch_index, salt):
        s, out = splitmix64(s ^ ((v * 0xD1B54A32D192ED03) & MASK64))
        s = out
    return s


class Rng:
    """xoshiro256** seeded through SplitMix64."""

    __slots__ = ("s0", "s1", "s2", "s3")

    def __init__(self, seed):
        x = seed & MASK64
        x, self.s0 = splitmix64(x)
        x, self.s1 = splitmix64(x)
        x, self.s2 = splitmix64(x)
        x, self.s3 = splitmix64(x)

    def next_u64(self):
        s0, s1, s2, s3 = self.s0, self.s1, self.s2, self.s3
        r = (((s1 * 5) & MASK64) << 7 | ((s1 * 5) & MASK64) >> 57) & MASK64
        result = (r * 9) & MASK64
        t = (s1 << 17) & MASK64
        s2 ^= s0
        s3 ^= s1
        s1 ^= s2
        s0 ^= s3
        s2 ^= t
        s3 = ((s3 << 45) | (s3 >> 19)) & MASK64
        self.s0, self.s1, self.s2, self.s3 = s0, s1, s2, s3
        return result

    def next_double(self):
        """Uniform in [0, 1) with 53 bits."""
        return (self.next_u64() >> 11) * (1.0 / 9007199254740992.0)

    def next_int(self, n):
        """Uniform integer in [0, n)."""
        return (self.next_u64() >> 11) % n


def permutation(rng, n):
    p = list(range(n))
    for i in range(n - 1, 0, -1):
        j = rng.next_int(i + 1)
        p[i], p[j] = p[j], p[i]
    return p


def lhs_uniforms(seed, var_index, batch_index, n):
    """Latin Hypercube sample of n uniforms for one variable in one batch."""
    rng = Rng(stream_seed(seed, var_index, batch_index, 0))
    perm = permutation(rng, n)
    return [(perm[i] + rng.next_double()) / n for i in range(n)]


def mc_uniforms(seed, var_index, batch_index, n):
    """Plain Monte Carlo uniforms (no stratification)."""
    rng = Rng(stream_seed(seed, var_index, batch_index, 0))
    return [rng.next_double() for _ in range(n)]


# ---------------------------------------------------------------- special functions

_LANCZOS = (
    0.99999999999980993, 676.5203681218851, -1259.1392167224028,
    771.32342877765313, -176.61502916214059, 12.507343278686905,
    -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
)


def log_gamma(x):
    """Lanczos approximation (g=7, n=9), x > 0."""
    if x < 0.5:
        return math.log(math.pi / math.sin(math.pi * x)) - log_gamma(1.0 - x)
    x -= 1.0
    a = _LANCZOS[0]
    t = x + 7.5
    for i in range(1, 9):
        a += _LANCZOS[i] / (x + i)
    return 0.5 * math.log(2.0 * math.pi) + (x + 0.5) * math.log(t) - t + math.log(a)


def _betacf(a, b, x):
    maxit, eps, fpmin = 300, 3.0e-15, 1.0e-300
    qab, qap, qam = a + b, a + 1.0, a - 1.0
    c = 1.0
    d = 1.0 - qab * x / qap
    if abs(d) < fpmin:
        d = fpmin
    d = 1.0 / d
    h = d
    for m in range(1, maxit + 1):
        m2 = 2 * m
        aa = m * (b - m) * x / ((qam + m2) * (a + m2))
        d = 1.0 + aa * d
        if abs(d) < fpmin:
            d = fpmin
        c = 1.0 + aa / c
        if abs(c) < fpmin:
            c = fpmin
        d = 1.0 / d
        h *= d * c
        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2))
        d = 1.0 + aa * d
        if abs(d) < fpmin:
            d = fpmin
        c = 1.0 + aa / c
        if abs(c) < fpmin:
            c = fpmin
        d = 1.0 / d
        de = d * c
        h *= de
        if abs(de - 1.0) < eps:
            break
    return h


def beta_inc(a, b, x):
    """Regularized incomplete beta I_x(a, b)."""
    if x <= 0.0:
        return 0.0
    if x >= 1.0:
        return 1.0
    lbt = log_gamma(a + b) - log_gamma(a) - log_gamma(b) + a * math.log(x) + b * math.log(1.0 - x)
    bt = math.exp(lbt)
    if x < (a + 1.0) / (a + b + 2.0):
        return bt * _betacf(a, b, x) / a
    return 1.0 - bt * _betacf(b, a, 1.0 - x) / b


def beta_inv(p, a, b):
    """Inverse regularized incomplete beta by bisection (64 steps)."""
    if p <= 0.0:
        return 0.0
    if p >= 1.0:
        return 1.0
    lo, hi = 0.0, 1.0
    for _ in range(64):
        mid = 0.5 * (lo + hi)
        if beta_inc(a, b, mid) < p:
            lo = mid
        else:
            hi = mid
    return 0.5 * (lo + hi)


def norm_inv(p):
    """Acklam's inverse normal CDF (relative error < 1.15e-9). No refinement step."""
    a = (-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
         1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00)
    b = (-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
         6.680131188771972e+01, -1.328068155288572e+01)
    c = (-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
         -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00)
    d = (7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00,
         3.754408661907416e+00)
    plow = 0.02425
    if p <= 0.0:
        return -1e300
    if p >= 1.0:
        return 1e300
    if p < plow:
        q = math.sqrt(-2.0 * math.log(p))
        return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / \
               ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0)
    if p > 1.0 - plow:
        q = math.sqrt(-2.0 * math.log(1.0 - p))
        return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / \
                ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1.0)
    q = p - 0.5
    r = q * q
    return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / \
           (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1.0)


# ---------------------------------------------------------------- linear algebra

def cholesky(m):
    """Lower-triangular L with L L^T = m, or None if m is not positive definite."""
    n = len(m)
    L = [[0.0] * n for _ in range(n)]
    for i in range(n):
        for j in range(i + 1):
            s = m[i][j]
            for k in range(j):
                s -= L[i][k] * L[j][k]
            if i == j:
                if s <= 1e-12:
                    return None
                L[i][i] = math.sqrt(s)
            else:
                L[i][j] = s / L[j][j]
    return L


def repair_correlation(m):
    """Shrink off-diagonals by 5% steps until Cholesky succeeds. Returns (matrix, L, shrink)."""
    n = len(m)
    factor = 1.0
    for _ in range(200):
        cur = [[m[i][j] * factor if i != j else 1.0 for j in range(n)] for i in range(n)]
        L = cholesky(cur)
        if L is not None:
            return cur, L, factor
        factor *= 0.95
    ident = [[1.0 if i == j else 0.0 for j in range(n)] for i in range(n)]
    return ident, cholesky(ident), 0.0


def invert_lower(L):
    n = len(L)
    inv = [[0.0] * n for _ in range(n)]
    for i in range(n):
        inv[i][i] = 1.0 / L[i][i]
        for j in range(i):
            s = 0.0
            for k in range(j, i):
                s -= L[i][k] * inv[k][j]
            inv[i][j] = s / L[i][i]
    return inv


def pearson_matrix(cols):
    """Correlation matrix of column vectors (list of lists)."""
    k = len(cols)
    n = len(cols[0])
    means = [sum(c) / n for c in cols]
    cen = [[x - means[j] for x in cols[j]] for j in range(k)]
    norms = [math.sqrt(sum(x * x for x in c)) for c in cen]
    out = [[1.0] * k for _ in range(k)]
    for i in range(k):
        for j in range(i + 1, k):
            s = sum(cen[i][t] * cen[j][t] for t in range(n))
            r = s / (norms[i] * norms[j]) if norms[i] > 0 and norms[j] > 0 else 0.0
            out[i][j] = out[j][i] = r
    return out


def ranks(values):
    """0-based ranks, ties broken by index (stable)."""
    order = sorted(range(len(values)), key=lambda i: (values[i], i))
    r = [0] * len(values)
    for pos, i in enumerate(order):
        r[i] = pos
    return r


def average_ranks(values):
    """1-based average ranks for Spearman (ties share the mean rank)."""
    n = len(values)
    order = sorted(range(n), key=lambda i: (values[i], i))
    r = [0.0] * n
    i = 0
    while i < n:
        j = i
        while j + 1 < n and values[order[j + 1]] == values[order[i]]:
            j += 1
        avg = (i + j) / 2.0 + 1.0
        for k in range(i, j + 1):
            r[order[k]] = avg
        i = j + 1
    return r


def pearson(x, y):
    n = len(x)
    if n < 2:
        return 0.0
    mx = sum(x) / n
    my = sum(y) / n
    sxy = sxx = syy = 0.0
    for i in range(n):
        dx = x[i] - mx
        dy = y[i] - my
        sxy += dx * dy
        sxx += dx * dx
        syy += dy * dy
    if sxx <= 0.0 or syy <= 0.0:
        return 0.0
    return sxy / math.sqrt(sxx * syy)


def spearman(x, y):
    return pearson(average_ranks(x), average_ranks(y))


def round_half_up(x):
    return int(math.floor(x + 0.5))
