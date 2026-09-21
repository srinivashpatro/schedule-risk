using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Numerics;
using ScheduleRisk.Core.Risk;

namespace ScheduleRisk.Core.Simulation;

public enum Scenario { PreMitigation, PostMitigation }

/// <summary>Raw per-iteration outputs of a simulation run.</summary>
public sealed class SimulationResult
{
    public Scenario Scenario { get; init; }
    public long Seed { get; init; }
    public List<long> Finish { get; } = new();
    /// <summary>Open milestones: activity index -> finish per iteration.</summary>
    public Dictionary<int, List<long>> Milestones { get; } = new();
    public long[] CriticalCount { get; set; } = Array.Empty<long>();
    /// <summary>Activities with any uncertainty/risk/driver: sampled remaining duration (minutes) per iteration.</summary>
    public Dictionary<int, List<int>> Durations { get; } = new();
    public List<double>[] RiskImpact { get; set; } = Array.Empty<List<double>>();
    public List<byte>[] RiskOccurred { get; set; } = Array.Empty<List<byte>>();
    public List<double>[] DriverValue { get; set; } = Array.Empty<List<double>>();
    public int Iterations { get; set; }
    public int Batches { get; set; }
    public bool? Converged { get; set; }
    public long Deterministic { get; set; } = Time.None;
    public TimeSpan Elapsed { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Monte Carlo schedule risk engine: Latin Hypercube sampling in batches, Iman-Conover
/// correlation, parallel iterations with results independent of thread count.
/// Twin of sim.py Simulation.
/// </summary>
public sealed class MonteCarloEngine
{
    private readonly Schedule _s;
    private readonly RiskModel _m;
    private readonly CpmEngine _engine;
    private readonly long[] _base;
    private readonly long[] _mpd;
    private readonly int[] _uncVar;          // per activity, -1 if none
    private readonly (int Occ, int Val)[] _riskVar;
    private readonly (int Occ, int Val)[] _driverVar;
    private readonly int _nvars;
    private readonly int[][] _actDrivers;
    private readonly int[][] _actRisks;
    private readonly int[] _affected;
    private readonly int[] _corrActs;
    private readonly double[][] _corrTarget;

    public MonteCarloEngine(Schedule s, RiskModel model, Scenario scenario = Scenario.PreMitigation, CpmEngine? engine = null)
    {
        _s = s;
        _m = model;
        Scenario = scenario;
        _engine = engine ?? new CpmEngine(s);
        var acts = s.Activities;
        int n = acts.Count;
        _base = acts.Select(a => a.RemainingDuration).ToArray();
        _mpd = acts.Select(a => (long)a.Calendar.MinutesPerDay).ToArray();

        _uncVar = new int[n];
        Array.Fill(_uncVar, -1);
        int v = 0;
        for (int j = 0; j < n; j++)
            if (model.Uncertainty.Length > j && model.Uncertainty[j] != null) _uncVar[j] = v++;
        _riskVar = new (int, int)[model.Risks.Count];
        for (int r = 0; r < _riskVar.Length; r++) { _riskVar[r] = (v, v + 1); v += 2; }
        _driverVar = new (int, int)[model.Drivers.Count];
        for (int d = 0; d < _driverVar.Length; d++) { _driverVar[d] = (v, v + 1); v += 2; }
        _nvars = v;

        var ad = new List<int>[n];
        var ar = new List<int>[n];
        for (int j = 0; j < n; j++) { ad[j] = new List<int>(); ar[j] = new List<int>(); }
        for (int di = 0; di < model.Drivers.Count; di++)
            foreach (int j in model.Drivers[di].Activities) ad[j].Add(di);
        for (int ri = 0; ri < model.Risks.Count; ri++)
            foreach (int j in model.Risks[ri].Activities) ar[j].Add(ri);
        _actDrivers = ad.Select(x => x.ToArray()).ToArray();
        _actRisks = ar.Select(x => x.ToArray()).ToArray();
        _affected = Enumerable.Range(0, n).Where(j => _uncVar[j] >= 0 || ad[j].Count > 0 || ar[j].Count > 0).ToArray();

        var corr = new List<int>();
        foreach (var g in model.Correlations)
            foreach (int j in g.Activities)
                if (!corr.Contains(j)) corr.Add(j);
        corr.Sort();
        _corrActs = corr.ToArray();
        var pos = new Dictionary<int, int>();
        for (int i = 0; i < _corrActs.Length; i++) pos[_corrActs[i]] = i;
        int k = _corrActs.Length;
        _corrTarget = MathX.NewMatrix(k);
        for (int i = 0; i < k; i++) _corrTarget[i][i] = 1.0;
        foreach (var g in model.Correlations)
            foreach (int a in g.Activities)
                foreach (int b in g.Activities)
                    if (a != b) _corrTarget[pos[a]][pos[b]] = g.Coefficient;
        if (k > 0)
        {
            var (_, _, shrink) = MathX.RepairCorrelation(_corrTarget);
            if (shrink < 1.0) model.Warnings.Add($"correlation matrix not consistent; off-diagonals scaled by {shrink:F3}");
        }
    }

    public Scenario Scenario { get; }
    public Schedule Schedule => _s;
    public RiskModel Model => _m;
    public CpmEngine Cpm => _engine;
    public int VariableCount => _nvars;
    public IReadOnlyList<int> AffectedActivities => _affected;

    /// <summary>Upper bound on stored duration samples before sensitivity storage is switched off.</summary>
    public long MaxStoredDurationSamples { get; set; } = 100_000_000;

    public int? MaxDegreeOfParallelism { get; set; }

    /// <summary>
    /// Run iterations on several threads. Off automatically in the browser (WebAssembly is single-threaded).
    /// Results are identical either way.
    /// </summary>
    public bool Parallelize { get; set; } = !OperatingSystem.IsBrowser();

    /// <summary>Iterations between yields in <see cref="RunAsync"/>.</summary>
    public int ChunkSize { get; set; } = 50;

    private (double Prob, Distribution Impact) RiskParams(int ri)
    {
        var r = _m.Risks[ri];
        return Scenario == Scenario.PostMitigation ? (r.MitigatedProbability, r.MitigatedImpact) : (r.Probability, r.Impact);
    }

    private sealed class Worker
    {
        public Worker(int n, int nRisks, int nDrivers)
        {
            Dur = new long[n];
            Res = new CpmResult(n);
            Crit = new long[n];
            RImp = new double[nRisks];
            ROcc = new bool[nRisks];
            DVal = new double[nDrivers];
            DOcc = new bool[nDrivers];
        }
        public readonly long[] Dur;
        public readonly CpmResult Res;
        public readonly long[] Crit;
        public readonly double[] RImp;
        public readonly bool[] ROcc;
        public readonly double[] DVal;
        public readonly bool[] DOcc;
    }

    public SimulationResult Run(int? iterations = null, long? seed = null, ConvergenceSettings? convergence = null,
                                IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
        => RunAsync(iterations, seed, convergence, progress, null, cancel).GetAwaiter().GetResult();

    /// <summary>
    /// Same as <see cref="Run"/>, but awaits <paramref name="yieldBetweenChunks"/> after every
    /// <see cref="ChunkSize"/> iterations so a single-threaded host (Blazor WebAssembly) can repaint.
    /// Results are identical to <see cref="Run"/>: chunking never changes the sampling.
    /// </summary>
    public async Task<SimulationResult> RunAsync(int? iterations = null, long? seed = null, ConvergenceSettings? convergence = null,
                                                 IProgress<(int Done, int Total)>? progress = null, Func<Task>? yieldBetweenChunks = null,
                                                 CancellationToken cancel = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var st = _m.Settings;
        int nTotal = iterations ?? st.Iterations;
        long sd = seed ?? st.Seed;
        var conv = convergence ?? st.Convergence;
        var acts = _s.Activities;
        int n = acts.Count;
        var pcal = _s.Settings.ProjectCalendar;
        var res = new SimulationResult { Scenario = Scenario, Seed = sd };
        var det = _engine.Run();
        res.Deterministic = det.ProjectFinish;
        res.CriticalCount = new long[n];
        var mileIdx = acts.Where(a => a.IsMilestone && a.Status != ActivityStatus.Complete).Select(a => a.Index).ToArray();
        foreach (int j in mileIdx) res.Milestones[j] = new List<long>();

        int batch, maxIt, minIt = 0, pct = 80;
        long tol = 0;
        if (conv.Enabled)
        {
            batch = conv.BatchSize;
            maxIt = conv.MaxIterations;
            minIt = conv.MinIterations;
            tol = (long)(conv.ToleranceDays * pcal.MinutesPerDay);
            pct = conv.Percentile;
        }
        else
        {
            batch = nTotal;
            maxIt = nTotal;
        }
        bool keepDur = (long)_affected.Length * maxIt <= MaxStoredDurationSamples;
        if (!keepDur) res.Warnings.Add("too many samples to keep per-activity durations; duration sensitivity not computed");
        if (keepDur) foreach (int j in _affected) res.Durations[j] = new List<int>(maxIt);
        int nr = _m.Risks.Count, ndv = _m.Drivers.Count;
        res.RiskImpact = Enumerable.Range(0, nr).Select(_ => new List<double>(maxIt)).ToArray();
        res.RiskOccurred = Enumerable.Range(0, nr).Select(_ => new List<byte>(maxIt)).ToArray();
        res.DriverValue = Enumerable.Range(0, ndv).Select(_ => new List<double>(maxIt)).ToArray();
        long critLimit = _s.Settings.CriticalFloat;

        long prev = long.MinValue;
        int stable = 0, b = 0, done = 0;
        var lockObj = new object();
        Worker? seqWorker = null;
        while (done < maxIt)
        {
            cancel.ThrowIfCancellationRequested();
            int nb = Math.Min(batch, maxIt - done);
            var U = new double[_nvars][];
            int bb = b;
            if (Parallelize)
                Parallel.For(0, _nvars, v => U[v] = st.LatinHypercube ? RngStreams.LhsUniforms(sd, v, bb, nb) : RngStreams.McUniforms(sd, v, bb, nb));
            else
                for (int v = 0; v < _nvars; v++) U[v] = st.LatinHypercube ? RngStreams.LhsUniforms(sd, v, bb, nb) : RngStreams.McUniforms(sd, v, bb, nb);
            if (_corrActs.Length > 0)
            {
                var vids = _corrActs.Select(j => _uncVar[j]).ToArray();
                var (cols, _) = ImanConover.Apply(vids.Select(v => U[v]).ToArray(), _corrTarget, sd, vids, b);
                for (int c = 0; c < vids.Length; c++) U[vids[c]] = cols[c];
            }

            // per-batch outputs, written by iteration index so results do not depend on threads
            var bFinish = new long[nb];
            var bMile = mileIdx.Select(_ => new long[nb]).ToArray();
            var bDur = keepDur ? _affected.Select(_ => new int[nb]).ToArray() : Array.Empty<int[]>();
            var bRImp = Enumerable.Range(0, nr).Select(_ => new double[nb]).ToArray();
            var bROcc = Enumerable.Range(0, nr).Select(_ => new byte[nb]).ToArray();
            var bDVal = Enumerable.Range(0, ndv).Select(_ => new double[nb]).ToArray();

            var po = new ParallelOptions { CancellationToken = cancel };
            if (MaxDegreeOfParallelism.HasValue) po.MaxDegreeOfParallelism = MaxDegreeOfParallelism.Value;
            int chunk = yieldBetweenChunks == null ? nb : Math.Max(1, ChunkSize);
            for (int c0 = 0; c0 < nb; c0 += chunk)
            {
            int c1 = Math.Min(nb, c0 + chunk);
            void Iterate(int i, Worker w)
            {
                IterationDurations(U, i, w);
                _engine.Run(w.Dur, w.Res, backward: true);
                bFinish[i] = w.Res.ProjectFinish;
                for (int k = 0; k < mileIdx.Length; k++) bMile[k][i] = w.Res.EF[mileIdx[k]];
                if (keepDur)
                    for (int k = 0; k < _affected.Length; k++) bDur[k][i] = (int)w.Dur[_affected[k]];
                for (int ri = 0; ri < nr; ri++) { bRImp[ri][i] = w.ROcc[ri] ? w.RImp[ri] : 0.0; bROcc[ri][i] = w.ROcc[ri] ? (byte)1 : (byte)0; }
                for (int di = 0; di < ndv; di++) bDVal[di][i] = w.DOcc[di] ? w.DVal[di] : 100.0;
                var tf = w.Res.TF;
                for (int j = 0; j < n; j++)
                    if (_engine.IsDriving(j) && tf[j] != Time.None && tf[j] <= critLimit) w.Crit[j]++;
            }
            void Merge(Worker w)
            {
                lock (lockObj)
                    for (int j = 0; j < n; j++) res.CriticalCount[j] += w.Crit[j];
            }
            if (Parallelize)
            {
                Parallel.For(c0, c1, po,
                    () => new Worker(n, nr, ndv),
                    (i, _, w) => { Iterate(i, w); return w; },
                    Merge);
            }
            else
            {
                seqWorker ??= new Worker(n, nr, ndv);
                Array.Clear(seqWorker.Crit);
                for (int i = c0; i < c1; i++) Iterate(i, seqWorker);
                Merge(seqWorker);
            }
            if (yieldBetweenChunks != null)
            {
                progress?.Report((done + c1, maxIt));
                await yieldBetweenChunks();
                cancel.ThrowIfCancellationRequested();
            }
            }

            res.Finish.AddRange(bFinish);
            for (int k = 0; k < mileIdx.Length; k++) res.Milestones[mileIdx[k]].AddRange(bMile[k]);
            if (keepDur) for (int k = 0; k < _affected.Length; k++) res.Durations[_affected[k]].AddRange(bDur[k]);
            for (int ri = 0; ri < nr; ri++) { res.RiskImpact[ri].AddRange(bRImp[ri]); res.RiskOccurred[ri].AddRange(bROcc[ri]); }
            for (int di = 0; di < ndv; di++) res.DriverValue[di].AddRange(bDVal[di]);

            done += nb;
            b++;
            progress?.Report((done, maxIt));
            if (conv.Enabled)
            {
                long cur = pcal.WorkAt(Statistics.Percentile(res.Finish, pct));
                if (prev != long.MinValue && Math.Abs(cur - prev) <= tol) stable++;
                else stable = 0;
                prev = cur;
                if (done >= minIt && stable >= 2)
                {
                    res.Converged = true;
                    break;
                }
            }
        }
        if (conv.Enabled && res.Converged == null) res.Converged = false;
        res.Iterations = done;
        res.Batches = b;
        res.Elapsed = sw.Elapsed;
        return res;
    }

    /// <summary>Sampled remaining durations for iteration i (same arithmetic order as the Python twin).</summary>
    private void IterationDurations(double[][] U, int i, Worker w)
    {
        Array.Copy(_base, w.Dur, _base.Length);
        for (int ri = 0; ri < _m.Risks.Count; ri++)
        {
            var (p, dist) = RiskParams(ri);
            var (vo, vi) = _riskVar[ri];
            if (U[vo][i] < p) { w.ROcc[ri] = true; w.RImp[ri] = dist.Inverse(U[vi][i]); }
            else { w.ROcc[ri] = false; w.RImp[ri] = 0.0; }
        }
        for (int di = 0; di < _m.Drivers.Count; di++)
        {
            var d = _m.Drivers[di];
            var (vo, vi) = _driverVar[di];
            if (U[vo][i] < d.Probability) { w.DOcc[di] = true; w.DVal[di] = d.Factor.Inverse(U[vi][i]); }
            else { w.DOcc[di] = false; w.DVal[di] = 100.0; }
        }
        foreach (int j in _affected)
        {
            long bse = _base[j];
            double x = bse;
            var u = _m.Uncertainty[j];
            if (u != null)
            {
                double val = u.Dist.Inverse(U[_uncVar[j]][i]);
                x = u.Units == UncertaintyUnits.Percent ? bse * val / 100.0 : val * _mpd[j];
            }
            foreach (int di in _actDrivers[j])
                if (w.DOcc[di]) x = x * w.DVal[di] / 100.0;
            foreach (int ri in _actRisks[j])
                if (w.ROcc[ri])
                {
                    var r = _m.Risks[ri];
                    x = x + (r.Units == UncertaintyUnits.Days ? w.RImp[ri] * _mpd[j] : bse * w.RImp[ri] / 100.0);
                }
            long d = MathX.RoundHalfUp(x);
            w.Dur[j] = d > 0 ? d : 0;
        }
    }
}
