using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ScheduleRisk.Core.Risk;

public enum FilterKind { All, Activities, Wbs, Code, NamePattern, Critical, Custom }

/// <summary>An editable activity filter. Compound filters from imported files are kept as Custom JSON.</summary>
public sealed class FilterSpec
{
    public FilterKind Kind { get; set; } = FilterKind.All;
    /// <summary>Comma-separated IDs / WBS segments / code values, or the regex for NamePattern.</summary>
    public string Value { get; set; } = "";
    public string CodeType { get; set; } = "";
    public string CustomJson { get; set; } = "";

    public FilterSpec Clone() => (FilterSpec)MemberwiseClone();

    private static List<string> Split(string v) =>
        v.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public void Write(Utf8JsonWriter w, string property = "filter")
    {
        w.WritePropertyName(property);
        switch (Kind)
        {
            case FilterKind.Activities:
                w.WriteStartObject(); w.WriteStartArray("activities");
                foreach (var s in Split(Value)) w.WriteStringValue(s);
                w.WriteEndArray(); w.WriteEndObject();
                break;
            case FilterKind.Wbs:
                w.WriteStartObject(); w.WriteStartArray("wbs");
                foreach (var s in Split(Value)) w.WriteStringValue(s);
                w.WriteEndArray(); w.WriteEndObject();
                break;
            case FilterKind.Code:
                w.WriteStartObject(); w.WriteStartObject("code"); w.WriteStartArray(CodeType);
                foreach (var s in Split(Value)) w.WriteStringValue(s);
                w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
                break;
            case FilterKind.NamePattern:
                w.WriteStartObject(); w.WriteString("namePattern", Value); w.WriteEndObject();
                break;
            case FilterKind.Critical:
                w.WriteStartObject(); w.WriteBoolean("critical", true); w.WriteEndObject();
                break;
            case FilterKind.Custom:
                w.WriteRawValue(string.IsNullOrWhiteSpace(CustomJson) ? "{\"all\":true}" : CustomJson);
                break;
            default:
                w.WriteStartObject(); w.WriteBoolean("all", true); w.WriteEndObject();
                break;
        }
    }

    private static string Join(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? string.Join(", ", e.EnumerateArray().Select(x => x.ToString())) : e.ToString();

    public static FilterSpec From(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) return new FilterSpec { Kind = FilterKind.Activities, Value = Join(e) };
        if (e.ValueKind != JsonValueKind.Object) return new FilterSpec();
        var props = e.EnumerateObject().ToList();
        if (props.Count == 1)
        {
            var p = props[0];
            switch (p.Name)
            {
                case "all": return new FilterSpec { Kind = FilterKind.All };
                case "activities": return new FilterSpec { Kind = FilterKind.Activities, Value = Join(p.Value) };
                case "wbs": return new FilterSpec { Kind = FilterKind.Wbs, Value = Join(p.Value) };
                case "namePattern": return new FilterSpec { Kind = FilterKind.NamePattern, Value = p.Value.GetString() ?? "" };
                case "critical": return new FilterSpec { Kind = FilterKind.Critical };
                case "code":
                    var codes = p.Value.EnumerateObject().ToList();
                    if (codes.Count == 1) return new FilterSpec { Kind = FilterKind.Code, CodeType = codes[0].Name, Value = Join(codes[0].Value) };
                    break;
            }
        }
        return new FilterSpec { Kind = FilterKind.Custom, CustomJson = e.GetRawText() };
    }

    public string Describe() => Kind switch
    {
        FilterKind.All => "All activities",
        FilterKind.Activities => "Activities: " + Value,
        FilterKind.Wbs => "WBS: " + Value,
        FilterKind.Code => $"{CodeType}: {Value}",
        FilterKind.NamePattern => "Name matches: " + Value,
        FilterKind.Critical => "Deterministic critical path",
        _ => "Custom: " + CustomJson,
    };
}

public sealed class DistSpec
{
    public string Distribution { get; set; } = "triangle";
    public double Min { get; set; }
    public double MostLikely { get; set; }
    public double Max { get; set; }

    public DistSpec Clone() => (DistSpec)MemberwiseClone();

    private bool IsUniform => string.Equals(Distribution, "uniform", StringComparison.OrdinalIgnoreCase);

    /// <summary>Canonical lower-case name ("triangle", "pert", "uniform"); unknown names are kept as given.</summary>
    public static string Canonical(string name)
    {
        try
        {
            return Risk.Distribution.ParseKind(name) switch
            {
                DistributionKind.Pert => "pert",
                DistributionKind.Uniform => "uniform",
                _ => "triangle",
            };
        }
        catch (ArgumentException) { return name; }
    }

    internal void WriteFields(Utf8JsonWriter w)
    {
        w.WriteString("distribution", Distribution);
        w.WriteNumber("min", Min);
        w.WriteNumber("mostLikely", IsUniform ? Min : MostLikely);
        w.WriteNumber("max", Max);
    }

    internal static DistSpec From(JsonElement e)
    {
        double N(string name, double dflt)
        {
            if (!e.TryGetProperty(name, out var v)) return dflt;
            return v.ValueKind == JsonValueKind.Number ? v.GetDouble()
                : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : dflt;
        }
        double min = N("min", 0);
        return new DistSpec
        {
            Distribution = Canonical(e.TryGetProperty("distribution", out var k) ? k.GetString() ?? "triangle" : "triangle"),
            Min = min,
            MostLikely = N("mostLikely", N("ml", min)),
            Max = N("max", min),
        };
    }

    /// <summary>Error text, or null when valid.</summary>
    public string? Validate()
    {
        double ml = IsUniform ? Min : MostLikely;
        if (!(Min <= ml && ml <= Max)) return IsUniform ? "min must not exceed max" : "min <= most likely <= max required";
        try { Risk.Distribution.ParseKind(Distribution); } catch (ArgumentException e) { return e.Message; }
        return null;
    }
}

public sealed class UncertaintyRow
{
    public FilterSpec Filter { get; set; } = new();
    public DistSpec Dist { get; set; } = new() { Distribution = "pert", Min = 90, MostLikely = 100, Max = 120 };
    public string Units { get; set; } = "percent";
}

public sealed class RiskRow
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public double Probability { get; set; } = 0.3;
    public DistSpec Impact { get; set; } = new() { Distribution = "triangle", Min = 5, MostLikely = 10, Max = 20 };
    public string ImpactUnits { get; set; } = "days";
    public FilterSpec Filter { get; set; } = new() { Kind = FilterKind.Activities };
    public bool Mitigated { get; set; }
    public double MitigatedProbability { get; set; } = 0.1;
    public bool MitigatedImpactDiffers { get; set; }
    public DistSpec MitigatedImpact { get; set; } = new() { Distribution = "triangle", Min = 2, MostLikely = 5, Max = 10 };
}

public sealed class DriverRow
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public double Probability { get; set; } = 1.0;
    public DistSpec Factor { get; set; } = new() { Distribution = "triangle", Min = 95, MostLikely = 100, Max = 115 };
    public FilterSpec Filter { get; set; } = new();
}

public sealed class CorrelationRow
{
    public FilterSpec Filter { get; set; } = new();
    public double Coefficient { get; set; } = 0.5;
}

/// <summary>
/// Editable form of the risk model file (docs/RISK_MODEL.md). <see cref="ToJson"/> produces exactly
/// the JSON that <see cref="RiskModelLoader"/> reads, so the UI and the CLI share one format.
/// </summary>
public sealed class RiskModelDocument
{
    public string Name { get; set; } = "Risk model";
    public int Iterations { get; set; } = 1000;
    public long Seed { get; set; } = 1;
    public bool LatinHypercube { get; set; } = true;
    public ConvergenceSettings Convergence { get; set; } = new();
    public List<UncertaintyRow> Uncertainty { get; } = new();
    public List<RiskRow> Risks { get; } = new();
    public List<DriverRow> Drivers { get; } = new();
    public List<CorrelationRow> Correlations { get; } = new();

    /// <summary>A sensible starting model: -10% / 0 / +20% (PERT) on every activity, nothing else.</summary>
    public static RiskModelDocument Default() => new()
    {
        Uncertainty = { new UncertaintyRow() },
        Seed = Environment.TickCount64 % 1_000_000 + 1,
    };

    public string NextRiskId()
    {
        int k = Risks.Count + 1;
        while (Risks.Any(r => r.Id == $"R{k:00}")) k++;
        return $"R{k:00}";
    }

    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("name", Name);
            w.WriteStartObject("simulation");
            w.WriteNumber("iterations", Iterations);
            w.WriteNumber("seed", Seed);
            w.WriteString("sampling", LatinHypercube ? "lhs" : "mc");
            w.WriteStartObject("convergence");
            w.WriteBoolean("enabled", Convergence.Enabled);
            w.WriteNumber("percentile", Convergence.Percentile);
            w.WriteNumber("toleranceDays", Convergence.ToleranceDays);
            w.WriteNumber("batchSize", Convergence.BatchSize);
            w.WriteNumber("minIterations", Convergence.MinIterations);
            w.WriteNumber("maxIterations", Convergence.MaxIterations);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartArray("uncertainty");
            foreach (var u in Uncertainty)
            {
                w.WriteStartObject();
                u.Filter.Write(w);
                u.Dist.WriteFields(w);
                w.WriteString("units", u.Units);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("risks");
            foreach (var r in Risks)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteString("title", r.Title);
                w.WriteNumber("probability", r.Probability);
                w.WriteStartObject("impact");
                r.Impact.WriteFields(w);
                w.WriteString("units", r.ImpactUnits);
                w.WriteEndObject();
                r.Filter.Write(w);
                if (r.Mitigated)
                {
                    w.WriteStartObject("mitigated");
                    w.WriteNumber("probability", r.MitigatedProbability);
                    if (r.MitigatedImpactDiffers)
                    {
                        w.WriteStartObject("impact");
                        r.MitigatedImpact.WriteFields(w);
                        w.WriteString("units", r.ImpactUnits);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("drivers");
            foreach (var d in Drivers)
            {
                w.WriteStartObject();
                w.WriteString("id", d.Id);
                w.WriteString("title", d.Title);
                w.WriteNumber("probability", d.Probability);
                d.Factor.WriteFields(w);
                d.Filter.Write(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("correlations");
            foreach (var c in Correlations)
            {
                w.WriteStartObject();
                c.Filter.Write(w);
                w.WriteNumber("coefficient", c.Coefficient);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static RiskModelDocument FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var m = new RiskModelDocument();
        string S(JsonElement e, string name, string dflt) =>
            e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? dflt : v.ToString()) : dflt;
        double D(JsonElement e, string name, double dflt) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : dflt;
        FilterSpec F(JsonElement e) =>
            e.TryGetProperty("filter", out var f) ? FilterSpec.From(f)
            : e.TryGetProperty("activities", out var a) ? FilterSpec.From(a)
            : new FilterSpec { Kind = FilterKind.Activities };

        m.Name = S(root, "name", "Risk model");
        if (root.TryGetProperty("simulation", out var sim))
        {
            m.Iterations = (int)D(sim, "iterations", 1000);
            m.Seed = (long)D(sim, "seed", 1);
            m.LatinHypercube = S(sim, "sampling", "lhs").ToLowerInvariant() == "lhs";
            if (sim.TryGetProperty("convergence", out var cv))
            {
                m.Convergence.Enabled = cv.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
                m.Convergence.Percentile = (int)D(cv, "percentile", 80);
                m.Convergence.ToleranceDays = D(cv, "toleranceDays", 1);
                m.Convergence.BatchSize = (int)D(cv, "batchSize", 250);
                m.Convergence.MinIterations = (int)D(cv, "minIterations", 500);
                m.Convergence.MaxIterations = (int)D(cv, "maxIterations", 10000);
            }
        }
        if (root.TryGetProperty("uncertainty", out var unc))
            foreach (var u in unc.EnumerateArray())
                m.Uncertainty.Add(new UncertaintyRow
                {
                    Filter = u.TryGetProperty("filter", out var f) ? FilterSpec.From(f) : new FilterSpec(),
                    Dist = DistSpec.From(u),
                    Units = S(u, "units", "percent"),
                });
        if (root.TryGetProperty("risks", out var risks))
            foreach (var r in risks.EnumerateArray())
            {
                var row = new RiskRow
                {
                    Id = S(r, "id", ""),
                    Probability = D(r, "probability", 0),
                    Filter = F(r),
                };
                row.Title = S(r, "title", row.Id);
                if (r.TryGetProperty("impact", out var imp))
                {
                    row.Impact = DistSpec.From(imp);
                    row.ImpactUnits = S(imp, "units", "days");
                }
                if (r.TryGetProperty("mitigated", out var mit) && mit.ValueKind == JsonValueKind.Object)
                {
                    row.Mitigated = true;
                    row.MitigatedProbability = D(mit, "probability", row.Probability);
                    if (mit.TryGetProperty("impact", out var mi))
                    {
                        row.MitigatedImpactDiffers = true;
                        row.MitigatedImpact = DistSpec.From(mi);
                    }
                }
                m.Risks.Add(row);
            }
        if (root.TryGetProperty("drivers", out var drivers))
            foreach (var d in drivers.EnumerateArray())
            {
                var row = new DriverRow
                {
                    Id = S(d, "id", ""),
                    Probability = D(d, "probability", 1.0),
                    Factor = DistSpec.From(d),
                    Filter = F(d),
                };
                row.Title = S(d, "title", row.Id);
                m.Drivers.Add(row);
            }
        if (root.TryGetProperty("correlations", out var corr))
            foreach (var c in corr.EnumerateArray())
                m.Correlations.Add(new CorrelationRow { Filter = F(c), Coefficient = D(c, "coefficient", 0) });
        return m;
    }
}
