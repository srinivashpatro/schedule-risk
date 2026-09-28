using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Core.Reporting;

/// <summary>One bar pair of the risk tornado: the risk's rank correlation with the finish before and after mitigation.</summary>
public sealed record TornadoRow(string Id, string Title, double Pre, double? Post);

/// <summary>The risk ranking as a tornado (06 Results and the reports): risks by the size of their pre-mitigation rank
/// correlation with the finish, largest first, with the post-mitigation value beside each; negative values (risks that
/// shorten the finish, such as opportunities) go left of the centre line.</summary>
public static class RiskTornado
{
    public static List<TornadoRow> Build(SimulationSummary pre, SimulationSummary? post, int max) =>
        pre.Risks.OrderByDescending(r => Math.Abs(r.Sensitivity)).ThenBy(r => r.Id, StringComparer.Ordinal).Take(max)
            .Select(r => new TornadoRow(r.Id, r.Title, r.Sensitivity, post?.Risks.FirstOrDefault(x => x.Id == r.Id)?.Sensitivity))
            .ToList();
}
