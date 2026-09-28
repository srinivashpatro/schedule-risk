using System.Globalization;
using System.Net;
using System.Text;
using ScheduleRisk.Core.Risk.Register;

namespace ScheduleRisk.Core.Reporting;

/// <summary>
/// The risk register in the reports (07 Review): the heat map drawn as SVG (the same drawing for the HTML report and
/// the PDF, Word and PowerPoint charts), the register's approved risks as table rows and its actions, overdue first.
/// </summary>
public static class RegisterReport
{
    /// <summary>Rating colours for printed reports, the app's light-theme Red, Amber and Green.</summary>
    public const string Styles = ".rag-r{fill:#C62828}.rag-a{fill:#F2A900}.rag-g{fill:#3F8F47}"
        + "text.hm-t{fill:#ffffff;font-size:12px;font-weight:600}text.hm-t.dark{fill:#1a1918}"
        + "text.hm-n{fill:#ffffff;font-size:22px;font-weight:800}text.hm-n.dark{fill:#1a1918}"
        + "text.hm-ids{fill:#ffffff;font-size:11px;font-weight:600}text.hm-ids.dark{fill:#1a1918}"
        + "text.hm-h{fill:#1a1918;font-size:13px;font-weight:600}text.hm-hl{fill:#1a1918;font-size:20px;font-weight:800}text.hm-r{font-size:12px}";

    private const int LabelW = 160, HeadH = 56, CellW = 108, CellH = 62, Gap = 3;

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Rag(RiskRating r) => r switch { RiskRating.Red => "rag-r", RiskRating.Amber => "rag-a", _ => "rag-g" };

    /// <summary>The heat map's SVG elements (without the svg element), probability rows with the most likely at the top and
    /// severity columns I upwards, each cell named, coloured by its rating and holding its count and risk ids.</summary>
    public static (string Body, int Width, int Height) HeatMapBody(MatrixSettings m, HeatMap map)
    {
        int np = m.Probability.Count, ns = m.SeverityLevels.Count;
        int width = LabelW + ns * (CellW + Gap), height = HeadH + np * (CellH + Gap);
        var sb = new StringBuilder();
        for (int s = 0; s < ns; s++)
        {
            double x = LabelW + s * (CellW + Gap) + CellW / 2.0;
            sb.Append($"<text x=\"{F(x)}\" y=\"24\" text-anchor=\"middle\" class=\"hm-hl\">{MatrixSettings.Roman(s)}</text>");
            sb.Append($"<text x=\"{F(x)}\" y=\"44\" text-anchor=\"middle\" class=\"hm-h\">{E(m.SeverityLevels[s])}</text>");
        }
        for (int p = 0; p < np; p++)
        {
            double y = HeadH + (np - 1 - p) * (CellH + Gap);
            var band = m.Probability[p];
            sb.Append($"<text x=\"0\" y=\"{F(y + CellH / 2.0 - 3)}\" class=\"hm-h\">{E(band.Letter + " · " + band.Label)}</text>");
            sb.Append($"<text x=\"0\" y=\"{F(y + CellH / 2.0 + 14)}\" class=\"hm-r\">{E(Pct(band.Min) + "–" + Pct(band.Max))}</text>");
            for (int s = 0; s < ns; s++)
            {
                double x = LabelW + s * (CellW + Gap);
                var rating = p < m.Ratings.Count && s < m.Ratings[p].Length ? m.Ratings[p][s] : RiskRating.Green;
                string dark = rating == RiskRating.Amber ? " dark" : "";
                var risks = map.Risks(p, s);
                sb.Append($"<rect class=\"hm {Rag(rating)}\" x=\"{F(x)}\" y=\"{F(y)}\" width=\"{CellW}\" height=\"{CellH}\"{(risks.Count == 0 ? " opacity=\"0.55\"" : "")}/>");
                sb.Append($"<text x=\"{F(x + 6)}\" y=\"{F(y + 14)}\" class=\"hm-t{dark}\">{m.CellName(p, s)}</text>");
                if (risks.Count > 0)
                {
                    sb.Append($"<text x=\"{F(x + CellW / 2.0)}\" y=\"{F(y + CellH / 2.0 + 7)}\" text-anchor=\"middle\" class=\"hm-n{dark}\">{risks.Count}</text>");
                    string ids = risks.Count <= 3 ? string.Join(" ", risks.Select(r => r.Id)) : string.Join(" ", risks.Take(2).Select(r => r.Id)) + $" +{risks.Count - 2}";
                    sb.Append($"<text x=\"{F(x + CellW / 2.0)}\" y=\"{F(y + CellH - 7)}\" text-anchor=\"middle\" class=\"hm-ids{dark}\">{E(ids)}</text>");
                }
            }
        }
        return (sb.ToString(), width, height);
    }

    /// <summary>A heat map as an inline SVG for the HTML report, carrying its own colours.</summary>
    public static string HeatMapSvg(MatrixSettings m, HeatMap map, string title)
    {
        var (body, w, h) = HeatMapBody(m, map);
        return $"<svg viewBox=\"0 0 {w} {h}\" width=\"100%\" style=\"max-width:{w}px\" role=\"img\" aria-label=\"{E(title)}\">"
            + $"<style>{Styles}text{{font-family:inherit}}</style>{body}</svg>";
    }

    private static string Pct(double f) => (f * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    public static string Cell(MatrixSettings m, Assessment a) =>
        m.Rate(a) is RiskRating r ? $"{m.CellName(a)} {r}" : "–";

    public static string Response(RegisterRisk r) =>
        r.Response == ResponseStrategy.None ? "–" : r.Response + (r.ResponseDescription.Length > 0 ? ": " + r.ResponseDescription : "");

    /// <summary>The register's approved risks, in register order.</summary>
    public static List<RegisterRisk> Listed(RiskRegister reg) => reg.Risks.Where(r => r.Status == RiskStatus.Approved).ToList();

    /// <summary>One sentence on the register: approved risks by rating today, and those proposed, rejected or closed.</summary>
    public static string Lead(RiskRegister reg)
    {
        var now = HeatMap.Build(reg, AssessmentPoint.Current);
        var counts = reg.StatusCounts();
        int approved = counts[RiskStatus.Approved];
        var parts = new List<string> { $"{approved} approved {(approved == 1 ? "risk" : "risks")}: {now.ByRating[RiskRating.Red]} Red, {now.ByRating[RiskRating.Amber]} Amber, {now.ByRating[RiskRating.Green]} Green now" };
        if (now.NotAssessed.Count > 0) parts.Add($"{now.NotAssessed.Count} not assessed");
        foreach (var st in new[] { RiskStatus.Proposed, RiskStatus.Rejected, RiskStatus.Closed })
            if (counts[st] > 0) parts.Add($"{counts[st]} {st.ToString().ToLowerInvariant()}");
        return string.Join("; ", parts) + ".";
    }

    public static string ActionStatus(ActionRow a) => a.Overdue ? "Overdue" : a.Action.Status.ToString();
}
