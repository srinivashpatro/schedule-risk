using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Core.Risk;

public enum UncertaintyUnits { Percent, Days }

public sealed record Uncertainty(Distribution Dist, UncertaintyUnits Units);

public sealed class RiskEvent
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public double Probability { get; init; }
    public Distribution Impact { get; init; } = null!;
    /// <summary>Impact units apply to both pre- and post-mitigation impact.</summary>
    public UncertaintyUnits Units { get; init; } = UncertaintyUnits.Days;
    public List<int> Activities { get; init; } = new();
    public double MitigatedProbability { get; init; }
    public Distribution MitigatedImpact { get; init; } = null!;
}

public sealed class RiskDriver
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public double Probability { get; init; } = 1.0;
    /// <summary>Multiplier in percent (100 = no change).</summary>
    public Distribution Factor { get; init; } = null!;
    public List<int> Activities { get; init; } = new();
}

public sealed record CorrelationGroup(List<int> Activities, double Coefficient);

public sealed class ConvergenceSettings
{
    public bool Enabled { get; set; }
    public int Percentile { get; set; } = 80;
    public double ToleranceDays { get; set; } = 1.0;
    public int BatchSize { get; set; } = 250;
    public int MinIterations { get; set; } = 500;
    public int MaxIterations { get; set; } = 10000;
}

public sealed class SimulationSettings
{
    public int Iterations { get; set; } = 1000;
    public long Seed { get; set; } = 1;
    public bool LatinHypercube { get; set; } = true;
    public ConvergenceSettings Convergence { get; set; } = new();
}

/// <summary>Duration uncertainty, risk register, risk drivers and correlation for one schedule.</summary>
public sealed class RiskModel
{
    public string Name { get; set; } = "";
    /// <summary>Per activity index; null where the activity has no uncertainty.</summary>
    public Uncertainty?[] Uncertainty { get; set; } = Array.Empty<Uncertainty?>();
    public List<RiskEvent> Risks { get; } = new();
    public List<RiskDriver> Drivers { get; } = new();
    public List<CorrelationGroup> Correlations { get; } = new();
    public SimulationSettings Settings { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>Loads the JSON risk model (docs/RISK_MODEL.md). Twin of risk.py load_risk_model.</summary>
public static class RiskModelLoader
{
    public static RiskModel Load(Schedule s, string path, bool[]? critical = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return Load(s, doc.RootElement, critical);
    }

    public static RiskModel LoadJson(Schedule s, string json, bool[]? critical = null)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return Load(s, doc.RootElement, critical);
    }

    private static double Num(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number) return e.GetDouble();
        if (e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        throw new FormatException($"expected a number, got {e}");
    }

    private static string Str(JsonElement obj, string name, string dflt) =>
        obj.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : dflt;

    private static Distribution Dist(JsonElement d)
    {
        var kind = Distribution.ParseKind(Str(d, "distribution", "triangle"));
        double min = Num(d.GetProperty("min"));
        double ml = d.TryGetProperty("mostLikely", out var m1) ? Num(m1) : d.TryGetProperty("ml", out var m2) ? Num(m2) : min;
        double max = Num(d.GetProperty("max"));
        return new Distribution(kind, min, ml, max);
    }

    private static UncertaintyUnits Units(JsonElement obj, string dflt)
    {
        string u = Str(obj, "units", dflt).ToLowerInvariant();
        return u switch
        {
            "percent" => UncertaintyUnits.Percent,
            "days" => UncertaintyUnits.Days,
            _ => throw new FormatException($"units must be percent or days, got '{u}'"),
        };
    }

    private static bool Usable(Schedule s, int j)
    {
        var a = s.Activities[j];
        return !(a.IsMilestone || a.IsSummary || a.Status == ActivityStatus.Complete);
    }

    public static RiskModel Load(Schedule s, JsonElement root, bool[]? critical = null)
    {
        var m = new RiskModel { Name = Str(root, "name", "") };
        m.Uncertainty = new Uncertainty?[s.Activities.Count];
        if (root.TryGetProperty("simulation", out var sim))
        {
            if (sim.TryGetProperty("iterations", out var it)) m.Settings.Iterations = (int)Num(it);
            if (sim.TryGetProperty("seed", out var sd)) m.Settings.Seed = (long)Num(sd);
            if (sim.TryGetProperty("sampling", out var sp)) m.Settings.LatinHypercube = (sp.GetString() ?? "lhs").ToLowerInvariant() == "lhs";
            if (sim.TryGetProperty("convergence", out var cv))
            {
                var c = m.Settings.Convergence;
                if (cv.TryGetProperty("enabled", out var en)) c.Enabled = en.ValueKind == JsonValueKind.True;
                if (cv.TryGetProperty("percentile", out var pc)) c.Percentile = (int)Num(pc);
                if (cv.TryGetProperty("toleranceDays", out var tl)) c.ToleranceDays = Num(tl);
                if (cv.TryGetProperty("batchSize", out var bs)) c.BatchSize = (int)Num(bs);
                if (cv.TryGetProperty("minIterations", out var mi)) c.MinIterations = (int)Num(mi);
                if (cv.TryGetProperty("maxIterations", out var mx)) c.MaxIterations = (int)Num(mx);
            }
        }

        if (root.TryGetProperty("uncertainty", out var unc))
            foreach (var u in unc.EnumerateArray())
            {
                var dist = Dist(u);
                var units = Units(u, "percent");
                IEnumerable<int> sel = u.TryGetProperty("filter", out var f) ? ActivityFilter.Select(s, f, critical) : Enumerable.Range(0, s.Activities.Count);
                foreach (int j in sel)
                    if (Usable(s, j)) m.Uncertainty[j] = new Uncertainty(dist, units);
            }

        if (root.TryGetProperty("risks", out var risks))
            foreach (var r in risks.EnumerateArray())
            {
                string id = Str(r, "id", "");
                if (id.Length == 0 && r.TryGetProperty("id", out var idNum)) id = idNum.ToString();
                var impEl = r.GetProperty("impact");
                var impact = Dist(impEl);
                var units = Units(impEl, "days");
                var sel = SelectFor(s, r, critical);
                var usable = sel.Where(j => Usable(s, j)).ToList();
                if (sel.Count - usable.Count > 0) m.Warnings.Add($"risk {id}: {sel.Count - usable.Count} milestone/summary/complete activities ignored");
                if (usable.Count == 0) m.Warnings.Add($"risk {id}: maps to no open activities");
                double prob = Num(r.GetProperty("probability"));
                double mitProb = prob;
                var mitImpact = impact;
                if (r.TryGetProperty("mitigated", out var mit) && mit.ValueKind == JsonValueKind.Object)
                {
                    if (mit.TryGetProperty("probability", out var mp)) mitProb = Num(mp);
                    if (mit.TryGetProperty("impact", out var mi)) mitImpact = Dist(mi);
                }
                if (prob < 0 || prob > 1 || mitProb < 0 || mitProb > 1)
                    throw new FormatException($"risk {id}: probability must be between 0 and 1");
                m.Risks.Add(new RiskEvent
                {
                    Id = id, Title = Str(r, "title", id), Probability = prob, Impact = impact, Units = units,
                    Activities = usable, MitigatedProbability = mitProb, MitigatedImpact = mitImpact,
                });
            }

        if (root.TryGetProperty("drivers", out var drivers))
            foreach (var d in drivers.EnumerateArray())
            {
                string id = Str(d, "id", "");
                m.Drivers.Add(new RiskDriver
                {
                    Id = id, Title = Str(d, "title", id),
                    Probability = d.TryGetProperty("probability", out var p) ? Num(p) : 1.0,
                    Factor = Dist(d),
                    Activities = SelectFor(s, d, critical).Where(j => Usable(s, j)).ToList(),
                });
            }

        if (root.TryGetProperty("correlations", out var corr))
            foreach (var c in corr.EnumerateArray())
            {
                var acts = SelectFor(s, c, critical).Where(j => m.Uncertainty[j] != null).ToList();
                double coef = Num(c.GetProperty("coefficient"));
                if (!(coef > -1.0 && coef < 1.0)) throw new FormatException("correlation coefficient must be strictly between -1 and 1");
                if (acts.Count >= 2) m.Correlations.Add(new CorrelationGroup(acts, coef));
                else m.Warnings.Add("correlation group with fewer than 2 uncertain activities ignored");
            }
        return m;
    }

    /// <summary>"filter" object, or a plain "activities" list; neither selects nothing.</summary>
    private static List<int> SelectFor(Schedule s, JsonElement obj, bool[]? critical)
    {
        if (obj.TryGetProperty("filter", out var f)) return ActivityFilter.Select(s, f, critical);
        if (obj.TryGetProperty("activities", out var a)) return ActivityFilter.Select(s, a, critical);
        return new List<int>();
    }
}

/// <summary>Activity selection by id list, WBS, activity code, name pattern, criticality (AND of keys).</summary>
public static class ActivityFilter
{
    private static List<string> Strings(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string> { e.GetString() ?? "" };

    public static List<int> Select(Schedule s, JsonElement spec, bool[]? critical)
    {
        var acts = s.Activities;
        if (spec.ValueKind == JsonValueKind.Array)
            return Select(s, JsonDocument.Parse("{\"activities\":" + spec.GetRawText() + "}").RootElement, critical);
        IEnumerable<int> idx = Enumerable.Range(0, acts.Count);
        bool any = false;
        if (spec.TryGetProperty("all", out _)) any = true;
        if (spec.TryGetProperty("activities", out var ids))
        {
            any = true;
            var wanted = new HashSet<string>(Strings(ids), StringComparer.Ordinal);
            var missing = wanted.Where(c => !s.ByCode.ContainsKey(c)).ToList();
            if (missing.Count > 0) throw new FormatException("unknown activity ids in risk model: " + string.Join(", ", missing.Take(10)));
            idx = idx.Where(j => wanted.Contains(acts[j].Code));
        }
        if (spec.TryGetProperty("wbs", out var wbs))
        {
            any = true;
            var prefs = Strings(wbs);
            idx = idx.Where(j => prefs.Any(p => WbsMatch(s.WbsPath(acts[j].WbsId), p)));
        }
        if (spec.TryGetProperty("code", out var code))
        {
            any = true;
            foreach (var prop in code.EnumerateObject())
            {
                string ctype = prop.Name;
                var vals = new HashSet<string>(Strings(prop.Value), StringComparer.Ordinal);
                idx = idx.Where(j => acts[j].Codes.TryGetValue(ctype, out var v) && vals.Contains(v));
            }
        }
        if (spec.TryGetProperty("namePattern", out var np))
        {
            any = true;
            var rx = new Regex(np.GetString() ?? "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            idx = idx.Where(j => rx.IsMatch(acts[j].Name ?? ""));
        }
        if (spec.TryGetProperty("critical", out var cr) && cr.ValueKind == JsonValueKind.True)
        {
            any = true;
            if (critical == null) throw new InvalidOperationException("filter 'critical' needs a deterministic CPM result");
            idx = idx.Where(j => critical[j]);
        }
        if (spec.TryGetProperty("exclude", out var ex))
        {
            var exs = new HashSet<string>(Strings(ex), StringComparer.Ordinal);
            idx = idx.Where(j => !exs.Contains(acts[j].Code));
        }
        if (!any) throw new FormatException($"filter selects nothing specific: {spec.GetRawText()}");
        return idx.ToList();
    }

    public static bool WbsMatch(string path, string prefix) =>
        path == prefix || path.StartsWith(prefix + ".", StringComparison.Ordinal) || path.EndsWith("." + prefix, StringComparison.Ordinal)
        || ("." + path + ".").Contains("." + prefix + ".", StringComparison.Ordinal);
}
