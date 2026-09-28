using ScheduleRisk.Core.Model;

namespace ScheduleRisk.Core.Risk.Register;

public sealed class ImportReport
{
    public List<string> Moved { get; } = new();
    public List<SkippedRisk> NotMoved { get; } = new();
    public List<(string From, string To)> Renamed { get; } = new();
    public List<string> Notes { get; } = new();

    public override string ToString() => $"{Moved.Count} moved to the register, {NotMoved.Count} left in the model";
}

/// <summary>
/// "Move risks to the register": the model's discrete risks come from the register (05 Model), so a model whose risks
/// were typed into it hands them over. Each becomes an approved register risk that promotes back to exactly the same
/// model risk, so results do not change: its numbers are kept as values set by hand (in their own unit, days or %),
/// its filter is resolved to the activities it applies to, and it is marked to be promoted whatever its rating. Its
/// place on the matrix (current, and target when it is mitigated) is worked out from its probability and its mean
/// impact as a share of the planned duration, for the heat map.
/// </summary>
public static class RegisterImporter
{
    /// <summary>How many of the model's risks were typed into it rather than promoted from the register.</summary>
    public static int TypedIn(RiskModelDocument doc) => doc.Risks.Count(r => r.Source != RiskRow.RegisterSource);

    /// <summary>The probability band a probability falls in (on a boundary, the lower band); above the top band, the top band.</summary>
    public static int ProbabilityBand(MatrixSettings m, double p)
    {
        for (int i = 0; i < m.Probability.Count; i++)
            if (p <= m.Probability[i].Max + 1e-12) return i;
        return m.Probability.Count - 1;
    }

    /// <summary>The severity level of a measured area that a value (e.g. % of the planned duration) falls in; above the
    /// top band, the top level.</summary>
    public static int ScheduleLevel(SeverityDimension d, double value)
    {
        for (int i = 0; i < d.Bands.Count; i++)
        {
            var b = d.Bands[i];
            if ((value > 0 || i == 0) && (b.Max is not double max || value <= max + 1e-12)) return i;
        }
        return d.Bands.Count - 1;
    }

    private static double Mean(DistSpec d) => DistSpec.Canonical(d.Distribution) switch
    {
        "uniform" => (d.Min + d.Max) / 2,
        "pert" => (d.Min + 4 * d.MostLikely + d.Max) / 6,
        _ => (d.Min + d.MostLikely + d.Max) / 3,
    };

    public static ImportReport MoveToRegister(RiskModelDocument doc, RiskRegister reg, Schedule s, bool[] critical, double plannedDays)
    {
        var report = new ImportReport();
        var typed = doc.Risks.Where(r => r.Source != RiskRow.RegisterSource).ToList();
        if (typed.Count == 0) return report;
        var compiled = RiskModelLoader.LoadJson(s, doc.ToJson(), critical);
        var m = reg.Matrix;
        var dim = m.Dimension(m.Promote.ScheduleDimension);

        foreach (var row in typed)
        {
            var ev = compiled.Risks.FirstOrDefault(x => x.Id == row.Id);
            if (ev == null) { report.NotMoved.Add(new SkippedRisk(row.Id, "it could not be read.")); continue; }
            var acts = ev.Activities.Select(i => s.Activities[i]).ToList();
            if (acts.Count == 0) { report.NotMoved.Add(new SkippedRisk(row.Id, "it applies to no open activity.")); continue; }

            string id = row.Id;
            if (reg.Risks.Any(x => x.Id == id))
            {
                id = reg.NextId();
                while (doc.Risks.Any(x => x.Id == id) || reg.Risks.Any(x => x.Id == id)) id = NextAfter(id);
                report.Renamed.Add((row.Id, id));
            }

            // Mean impact in working days: a % impact on the mean remaining duration of the activities it hits.
            double DaysMean(DistSpec d) => row.ImpactUnits == "percent"
                ? Mean(d) / 100 * acts.Average(a => a.RemainingDuration / (double)a.Calendar.MinutesPerDay)
                : Mean(d);
            Assessment Place(double probability, DistSpec impact)
            {
                var a = new Assessment { Probability = ProbabilityBand(m, probability) };
                if (dim is { Quantitative: true } && plannedDays > 0)
                    a.Severity[dim.Id] = ScheduleLevel(dim, Math.Abs(DaysMean(impact)) / plannedDays * 100);
                return a;
            }

            var mitImpact = row.MitigatedImpactDiffers ? row.MitigatedImpact : row.Impact;
            var risk = new RegisterRisk
            {
                Id = id, Title = row.Title, Event = row.Title, Status = RiskStatus.Approved,
                Kind = Mean(row.Impact) < 0 ? RiskKind.Opportunity : RiskKind.Threat,
                RaisedBy = "Moved from the risk model",
                Current = Place(row.Probability, row.Impact),
                Promotion = new Promotion
                {
                    Always = true, Activities = acts.Select(a => a.Code).ToList(),
                    Probability = row.Probability, Impact = row.Impact.Clone(), ImpactUnits = row.ImpactUnits,
                    MitigatedProbability = row.Mitigated ? row.MitigatedProbability : null,
                    MitigatedImpact = row.Mitigated ? mitImpact.Clone() : null,
                },
            };
            risk.Inherent = risk.Current.Clone();
            if (row.Mitigated) risk.Target = Place(row.MitigatedProbability, mitImpact);
            reg.Risks.Add(risk);

            if (row.Filter.Kind != FilterKind.Activities)
                report.Notes.Add($"{id}: its filter ({row.Filter.Describe()}) became a list of {acts.Count} activities, which no longer follows changes to the schedule.");
            row.Id = id;
            row.Source = RiskRow.RegisterSource;
            report.Moved.Add(id);
        }
        return report;
    }

    private static string NextAfter(string id)
    {
        int k = int.TryParse(id.TrimStart('R'), out int n) ? n + 1 : 1;
        return $"R{k:00}";
    }
}
