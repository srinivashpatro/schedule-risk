using System.Globalization;
using System.Net;
using System.Text;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Core.Reporting;

/// <summary>Self-contained HTML risk report (inline SVG charts, no external files).</summary>
public static class HtmlReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string D(long m) => m == Time.None ? "" : Time.FromMinutes(m).ToString("dd-MMM-yyyy", Inv);
    private static string F(double x, int d = 1) => x.ToString("F" + d, Inv);

    public static string Build(Schedule s, SimulationSummary pre, SimulationSummary? post = null,
                               IReadOnlyList<ValidationCheck>? checks = null, VerifyReport? verify = null, string? modelName = null)
    {
        var pcal = s.Settings.ProjectCalendar;
        double mpd = pcal.MinutesPerDay;
        double WorkDaysBetween(long a, long b) => pcal.WorkBetween(a, b) / mpd;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>Schedule Risk Report - ").Append(E(s.ProjectCode)).Append("</title><style>");
        sb.Append(@":root{--bg:#fbfaf7;--fg:#1d1f23;--muted:#61656d;--line:#dedad2;--card:#fff;--a:#2f6db5;--b:#c2572b;--ok:#2e7d4f;--bad:#b3261e}
@media (prefers-color-scheme:dark){:root{--bg:#16181b;--fg:#e8e6e1;--muted:#a3a6ab;--line:#33363b;--card:#1e2024;--a:#6ea4e6;--b:#e98a5e;--ok:#6cc08f;--bad:#f07f76}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif}
main{max-width:1060px;margin:0 auto;padding:28px 16px 64px}h1{font-size:26px;margin:0 0 4px}h2{font-size:18px;margin:36px 0 10px}
.muted{color:var(--muted)}.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px;margin:18px 0}
.tile{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}.tile b{display:block;font-size:22px;font-variant-numeric:tabular-nums}
.tile span{color:var(--muted);font-size:13px}table{border-collapse:collapse;width:100%;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden;font-size:14px}
th,td{padding:7px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{font-weight:600;color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.03em}
td.n{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}.wrap{overflow-x:auto}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px}
svg text{fill:var(--muted);font-size:11px}.pass{color:var(--ok);font-weight:600}.fail{color:var(--bad);font-weight:600}
.legend span{display:inline-block;margin-right:14px;font-size:13px}.sw{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:5px;vertical-align:middle}");
        sb.Append("</style></head><body><main>");
        sb.Append("<h1>Schedule risk analysis: ").Append(E(s.ProjectCode)).Append("</h1>");
        sb.Append("<div class=\"muted\">Data date ").Append(D(s.Settings.DataDate)).Append(" &middot; ")
          .Append(s.Activities.Count).Append(" activities &middot; ").Append(pre.Iterations).Append(" iterations (")
          .Append(pre.Converged == true ? "converged" : pre.Converged == false ? "not converged" : "fixed count")
          .Append(", seed ").Append(pre.Seed).Append(")");
        if (!string.IsNullOrEmpty(modelName)) sb.Append(" &middot; model: ").Append(E(modelName));
        sb.Append("</div>");

        long p50 = pre.FinishPercentiles[50], p80 = pre.FinishPercentiles[80], p90 = pre.FinishPercentiles[90];
        sb.Append("<div class=\"tiles\">");
        Tile(sb, D(pre.DeterministicFinish), "Deterministic finish (CPM)");
        Tile(sb, $"{pre.ProbMeetDeterministic * 100:F0}%", "Chance of meeting it");
        if (pre.ProbMeetMustFinishBy is double pm)
        {
            double gap = Math.Round(WorkDaysBetween(pre.MustFinishBy, p80));
            string where = gap > 0 ? $"P80 is {F(gap, 0)} working days after it" : gap < 0 ? $"P80 is {F(-gap, 0)} working days before it" : "P80 is on it";
            Tile(sb, $"{pm * 100:F0}%", $"Chance of meeting the Must Finish By ({D(pre.MustFinishBy)}); {where}");
        }
        Tile(sb, D(p50), $"P50 (+{F(WorkDaysBetween(pre.DeterministicFinish, p50), 0)} working days)");
        Tile(sb, D(p80), $"P80 (+{F(WorkDaysBetween(pre.DeterministicFinish, p80), 0)} working days)");
        if (post != null)
            Tile(sb, D(post.FinishPercentiles[80]), $"P80 after mitigation ({F(WorkDaysBetween(p80, post.FinishPercentiles[80]), 0)} days)");
        else
            Tile(sb, D(p90), "P90");
        sb.Append("</div>");

        sb.Append("<h2>Project finish distribution</h2><div class=\"card\">");
        sb.Append(SCurve(pre, post, s));
        sb.Append("<div class=\"legend\"><span><i class=\"sw\" style=\"background:var(--a)\"></i>Pre-mitigation</span>");
        if (post != null) sb.Append("<span><i class=\"sw\" style=\"background:var(--b)\"></i>Post-mitigation</span>");
        sb.Append("<span>Dashed line: deterministic finish</span>");
        if (pre.MustFinishBy != Time.None)
        {
            var (_, _, onChart) = ChartRange(pre, post);
            sb.Append("<span>").Append(onChart ? "Dotted line: Must Finish By " + D(pre.MustFinishBy)
                : $"Must Finish By {D(pre.MustFinishBy)}, off the chart ({(pre.MustFinishBy > pre.SortedFinish[^1] ? "later" : "earlier")})").Append("</span>");
        }
        sb.Append("</div></div>");

        sb.Append("<h2>Confidence levels</h2><div class=\"wrap\"><table><tr><th>Percentile</th><th>Pre-mitigation</th>");
        if (post != null) sb.Append("<th>Post-mitigation</th><th>Improvement (working days)</th>");
        sb.Append("</tr>");
        foreach (var kv in pre.FinishPercentiles)
        {
            sb.Append("<tr><td>P").Append(kv.Key).Append("</td><td class=\"n\">").Append(D(kv.Value)).Append("</td>");
            if (post != null)
            {
                long pv = post.FinishPercentiles[kv.Key];
                sb.Append("<td class=\"n\">").Append(D(pv)).Append("</td><td class=\"n\">").Append(F(WorkDaysBetween(pv, kv.Value), 1)).Append("</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table></div>");

        if (pre.Risks.Count > 0)
        {
            sb.Append("<h2>Risk ranking</h2><p class=\"muted\">Sorted by rank correlation between the risk's realised impact and the project finish. The delta is the mean finish when the risk occurs minus when it does not.</p>");
            sb.Append("<div class=\"card\">").Append(Tornado(pre.Risks.Select(r => (r.Id + " " + r.Title, r.Sensitivity)).Take(15).ToList())).Append("</div>");
            sb.Append("<div class=\"wrap\" style=\"margin-top:10px\"><table><tr><th>Risk</th><th>Occurred</th><th>Sensitivity</th><th>Finish delta (working days)</th></tr>");
            foreach (var r in pre.Risks)
                sb.Append("<tr><td>").Append(E(r.Id)).Append(" ").Append(E(r.Title)).Append("</td><td class=\"n\">").Append(F(r.Occurrence * 100, 0))
                  .Append("%</td><td class=\"n\">").Append(F(r.Sensitivity, 2)).Append("</td><td class=\"n\">")
                  .Append(r.MeanFinishDeltaDays.HasValue ? F(r.MeanFinishDeltaDays.Value, 1) : "").Append("</td></tr>");
            sb.Append("</table></div>");
        }
        if (pre.Drivers.Count > 0)
        {
            sb.Append("<h2>Risk drivers</h2><div class=\"card\">")
              .Append(Tornado(pre.Drivers.Select(d => (d.Id + " " + d.Title, d.Sensitivity)).ToList())).Append("</div>");
        }

        sb.Append("<h2>Activities that drive the finish</h2><p class=\"muted\">Criticality: share of iterations on the critical path. Sensitivity: rank correlation of duration with finish. Cruciality = criticality &times; sensitivity.</p>");
        sb.Append("<div class=\"wrap\"><table><tr><th>Activity</th><th>Criticality</th><th>Sensitivity</th><th>Cruciality</th></tr>");
        foreach (var a in pre.Activities.Take(25))
            sb.Append("<tr><td>").Append(E(a.Code)).Append(" <span class=\"muted\">").Append(E(a.Name)).Append("</span></td><td class=\"n\">")
              .Append(F(a.Criticality * 100, 0)).Append("%</td><td class=\"n\">").Append(F(a.Sensitivity, 2)).Append("</td><td class=\"n\">")
              .Append(F(a.Cruciality, 3)).Append("</td></tr>");
        sb.Append("</table></div>");

        if (pre.Milestones.Count > 0)
        {
            sb.Append("<h2>Milestones</h2><div class=\"wrap\"><table><tr><th>Milestone</th><th>Deterministic</th><th>P10</th><th>P50</th><th>P80</th><th>P90</th></tr>");
            foreach (var m in pre.Milestones)
                sb.Append("<tr><td>").Append(E(m.Code)).Append(" <span class=\"muted\">").Append(E(m.Name)).Append("</span></td><td class=\"n\">")
                  .Append(D(m.Deterministic)).Append("</td><td class=\"n\">").Append(D(m.P10)).Append("</td><td class=\"n\">").Append(D(m.P50))
                  .Append("</td><td class=\"n\">").Append(D(m.P80)).Append("</td><td class=\"n\">").Append(D(m.P90)).Append("</td></tr>");
            sb.Append("</table></div>");
        }

        if (checks != null)
        {
            sb.Append("<h2>Schedule health checks</h2><div class=\"wrap\"><table><tr><th>Check</th><th>Result</th><th>Count</th><th>Notes</th></tr>");
            foreach (var c in checks)
                sb.Append("<tr><td>").Append(E(c.Title)).Append("</td><td class=\"").Append(c.Passed ? "pass\">Pass" : "fail\">Review")
                  .Append("</td><td class=\"n\">").Append(c.Count).Append(" / ").Append(c.Total).Append("</td><td>").Append(E(c.Note)).Append("</td></tr>");
            sb.Append("</table></div>");
        }
        if (verify != null)
        {
            sb.Append("<h2>Engine check against P6</h2><p>").Append(verify.FieldsMatched).Append(" of ").Append(verify.FieldsCompared)
              .Append(" date and float fields match the values P6 stored in the file (").Append(verify.ActivitiesMatched).Append(" of ")
              .Append(verify.Compared).Append(" activities fully match).</p>");
        }
        sb.Append("<p class=\"muted\" style=\"margin-top:40px\">Generated ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv))
          .Append(" by ScheduleRisk ").Append(typeof(HtmlReport).Assembly.GetName().Version?.ToString(3)).Append(".</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static void Tile(StringBuilder sb, string value, string label) =>
        sb.Append("<div class=\"tile\"><b>").Append(E(value)).Append("</b><span>").Append(E(label)).Append("</span></div>");

    /// <summary>
    /// Cumulative finish curve with histogram, as inline SVG (theme via CSS variables --a, --b, --fg, --line).
    /// <paramref name="interactive"/> wraps it in a focusable <c>div.scurve</c> whose <c>data-scurve</c> JSON holds, for every
    /// calendar day, how many iterations finished by the end of that day; the browser app's js/app.js draws the hover readout.
    /// <paramref name="percentile"/> (browser app) marks that confidence level with a labelled line, gives the bars up to it
    /// the class <c>in</c>, and labels the deterministic line and, with two scenarios, each curve.
    /// <paramref name="histogram"/> draws the bars at full height, without the curves and the percent axis.
    /// </summary>
    public static string SCurve(SimulationSummary pre, SimulationSummary? post, Schedule s, bool interactive = false,
                                int? percentile = null, bool histogram = false)
    {
        const int W = 960, H = 300, L = 48, R = 16, B = 34;
        long mfb = pre.MustFinishBy;
        // room above the plot for the line labels: deterministic, P-level and, with one, the Must Finish By
        int T = percentile == null ? 12 : mfb != Time.None ? 62 : 44;
        var (lo, hi, mfbOnChart) = ChartRange(pre, post);
        double X(long t) => L + (double)(t - lo) / (hi - lo) * (W - L - R);
        double Y(double p) => T + (1 - p) * (H - T - B);
        string what = histogram ? "Distribution of project finish dates" : "Cumulative probability of project finish";
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" width=\"100%\" role=\"img\"");
        sb.Append(interactive
            ? $" tabindex=\"0\" aria-label=\"{what}. Focus and use the arrow keys to read the chance of finishing by each date.\">"
            : $" aria-label=\"{what}\">");
        if (!histogram)
        {
            for (int k = 0; k <= 4; k++)
            {
                double p = k / 4.0;
                sb.Append($"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{F(Y(p))}\" y2=\"{F(Y(p))}\" stroke=\"var(--line)\"/>");
                sb.Append($"<text x=\"{L - 6}\" y=\"{F(Y(p) + 4)}\" text-anchor=\"end\">{p * 100:F0}%</text>");
            }
        }
        // histogram (pre) behind the curve
        const int bins = 40;
        var counts = new int[bins];
        foreach (var t in pre.SortedFinish)
        {
            int bi = (int)((double)(t - lo) / (hi - lo) * bins);
            counts[Math.Clamp(bi, 0, bins - 1)]++;
        }
        int cmax = Math.Max(1, counts.Max());
        double bw = (W - L - R) / (double)bins;
        long cut = percentile is int pc ? Statistics.PercentileSorted(pre.SortedFinish, pc) : 0;
        for (int k = 0; k < bins; k++)
        {
            double h = counts[k] / (double)cmax * (H - T - B) * (histogram ? 1 : 0.45);
            string cls = percentile != null && lo + (hi - lo) * (k + 0.5) / bins <= cut ? "bar in" : "bar";
            sb.Append($"<rect x=\"{F(L + k * bw + 1)}\" y=\"{F(H - B - h)}\" width=\"{F(bw - 2)}\" height=\"{F(h)}\" fill=\"var(--a)\" opacity=\".15\" class=\"{cls}\"/>");
        }
        void Curve(long[] sorted, string color)
        {
            var path = new StringBuilder();
            int n = sorted.Length;
            int step = Math.Max(1, n / 400);
            for (int i = 0; i < n; i += step)
                path.Append(i == 0 ? "M" : "L").Append(F(X(sorted[i]))).Append(',').Append(F(Y((i + 1) / (double)n)));
            path.Append('L').Append(F(X(sorted[^1]))).Append(',').Append(F(Y(1)));
            sb.Append($"<path d=\"{path}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"2.2\"/>");
        }
        if (!histogram)
        {
            Curve(pre.SortedFinish, "var(--a)");
            if (post != null) Curve(post.SortedFinish, "var(--b)");
        }
        double xd = X(pre.DeterministicFinish);
        sb.Append($"<line x1=\"{F(xd)}\" x2=\"{F(xd)}\" y1=\"{(percentile == null ? T : 4)}\" y2=\"{H - B}\" stroke=\"var(--fg)\" stroke-dasharray=\"4 4\" opacity=\".6\"/>");
        if (mfb != Time.None && mfbOnChart)
            sb.Append($"<line x1=\"{F(X(mfb))}\" x2=\"{F(X(mfb))}\" y1=\"{(percentile == null ? T : 40)}\" y2=\"{H - B}\" stroke=\"var(--fg)\" stroke-width=\"2\" stroke-dasharray=\"1 4\" stroke-linecap=\"round\" class=\"mline\"/>");
        if (percentile is int pct)
        {
            void Label(double x, int y, string cls, string text)
            {
                bool right = x < W - R - 170;
                sb.Append($"<text x=\"{F(right ? x + 6 : x - 6)}\" y=\"{y}\" text-anchor=\"{(right ? "start" : "end")}\" class=\"{cls}\">{E(text)}</text>");
            }
            double xc = X(cut);
            sb.Append($"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{H - B}\" y2=\"{H - B}\" stroke=\"var(--fg)\" stroke-width=\"2\"/>");
            sb.Append($"<line x1=\"{F(xc)}\" x2=\"{F(xc)}\" y1=\"20\" y2=\"{H - B}\" stroke=\"var(--b)\" stroke-width=\"2\" class=\"pline\"/>");
            Label(xd, 14, "dlabel", "Deterministic " + D(pre.DeterministicFinish));
            Label(xc, 32, "plabel", $"P{pct} " + D(cut));
            if (mfb != Time.None)
            {
                if (mfbOnChart) Label(X(mfb), 50, "mlabel", "Must Finish By " + D(mfb));
                else if (mfb > hi) sb.Append($"<text x=\"{W - R}\" y=\"50\" text-anchor=\"end\" class=\"mlabel\">{E("Must Finish By " + D(mfb) + " →")}</text>");
                else sb.Append($"<text x=\"{L}\" y=\"50\" text-anchor=\"start\" class=\"mlabel\">{E("← Must Finish By " + D(mfb))}</text>");
            }
            if (post != null && !histogram)
            {
                // Name each curve beside its median: the later one on its right, the earlier one on its left.
                long m1 = Statistics.PercentileSorted(pre.SortedFinish, 50), m2 = Statistics.PercentileSorted(post.SortedFinish, 50);
                double y = Y(0.5) + 4;
                bool preLater = m1 >= m2;
                sb.Append($"<text x=\"{F(X(m1) + (preLater ? 8 : -8))}\" y=\"{F(y)}\" text-anchor=\"{(preLater ? "start" : "end")}\" class=\"slabel\">Pre-mitigation</text>");
                sb.Append($"<text x=\"{F(X(m2) + (preLater ? -8 : 8))}\" y=\"{F(y)}\" text-anchor=\"{(preLater ? "end" : "start")}\" class=\"slabel\">Post-mitigation</text>");
            }
        }
        for (int k = 0; k <= 5; k++)
        {
            long t = lo + (hi - lo) * k / 5;
            sb.Append($"<text x=\"{F(X(t))}\" y=\"{H - 12}\" text-anchor=\"{(k == 5 ? "end" : "middle")}\">{D(t)}</text>");
        }
        sb.Append("</svg>");
        if (!interactive) return sb.ToString();

        long day0 = lo / Time.MinutesPerDay, day1 = hi / Time.MinutesPerDay;
        var js = new StringBuilder();
        js.Append(string.Create(Inv, $"{{\"w\":{W},\"h\":{H},\"l\":{L},\"r\":{R},\"t\":{T},\"b\":{B},\"lo\":{lo},\"hi\":{hi},\"det\":{pre.DeterministicFinish},"));
        if (mfb != Time.None) js.Append(string.Create(Inv, $"\"mfb\":{mfb},"));
        js
          .Append(string.Create(Inv, $"\"day0\":{day0 * Time.MinutesPerDay},\"date0\":\"{Time.FromMinutes(day0 * Time.MinutesPerDay).ToString("yyyy-MM-dd", Inv)}\","))
          .Append("\"bins\":[").Append(string.Join(',', counts)).Append("],\"series\":[");
        void Series(string name, string color, long[] sorted)
        {
            js.Append("{\"name\":\"").Append(name).Append("\",\"color\":\"").Append(color).Append("\",\"n\":").Append(sorted.Length).Append(",\"cum\":[");
            int i = 0;
            for (long d = day0; d <= day1; d++)
            {
                long end = (d + 1) * Time.MinutesPerDay; // finished before the next midnight
                while (i < sorted.Length && sorted[i] < end) i++;
                if (d > day0) js.Append(',');
                js.Append(i);
            }
            js.Append("]}");
        }
        Series("Pre-mitigation", "var(--a)", pre.SortedFinish);
        if (post != null) { js.Append(','); Series("Post-mitigation", "var(--b)", post.SortedFinish); }
        js.Append("]}");
        return $"<div class=\"scurve{(histogram ? " hist" : "")}\" data-scurve=\"{E(js.ToString())}\">{sb}</div>";
    }

    /// <summary>Horizontal tornado bars as inline SVG.</summary>
    /// <summary>
    /// The chart's date range: every finish and the deterministic finish, plus the Must Finish By when it lies within a
    /// fifth of the range beyond it. A deadline further off would squash the curve, so it is labelled at the edge instead.
    /// </summary>
    private static (long Lo, long Hi, bool MfbOnChart) ChartRange(SimulationSummary pre, SimulationSummary? post)
    {
        long lo = pre.SortedFinish[0], hi = pre.SortedFinish[^1];
        if (post != null) { lo = Math.Min(lo, post.SortedFinish[0]); hi = Math.Max(hi, post.SortedFinish[^1]); }
        lo = Math.Min(lo, pre.DeterministicFinish);
        if (hi <= lo) hi = lo + 1440;
        long mfb = pre.MustFinishBy;
        if (mfb == Time.None) return (lo, hi, false);
        long margin = (hi - lo) / 5;
        if (mfb < lo - margin || mfb > hi + margin) return (lo, hi, false);
        return (Math.Min(lo, mfb), Math.Max(hi, mfb), true);
    }

    public static string Tornado(List<(string Label, double Value)> rows)
    {
        if (rows.Count == 0) return "";
        const int W = 960, rowH = 26, labelW = 360;
        int H = rows.Count * rowH + 10;
        double mid = labelW + (W - labelW) / 2.0;
        double scale = (W - labelW) / 2.0 - 40;
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" width=\"100%\" role=\"img\" aria-label=\"Tornado chart\">");
        sb.Append($"<line x1=\"{F(mid)}\" x2=\"{F(mid)}\" y1=\"0\" y2=\"{H}\" stroke=\"var(--line)\"/>");
        for (int i = 0; i < rows.Count; i++)
        {
            var (label, v) = rows[i];
            double y = 5 + i * rowH;
            double w = Math.Abs(v) * scale;
            double x = v >= 0 ? mid : mid - w;
            string lab = label.Length > 52 ? label.Substring(0, 51) + "…" : label;
            sb.Append($"<text x=\"{labelW - 10}\" y=\"{F(y + 16)}\" text-anchor=\"end\" style=\"fill:var(--fg)\">{E(lab)}</text>");
            sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y + 4)}\" width=\"{F(Math.Max(w, 1))}\" height=\"{rowH - 9}\" rx=\"3\" fill=\"{(v >= 0 ? "var(--b)" : "var(--a)")}\"/>");
            double tx = v >= 0 ? x + w + 6 : x - 6;
            sb.Append($"<text x=\"{F(tx)}\" y=\"{F(y + 16)}\" text-anchor=\"{(v >= 0 ? "start" : "end")}\">{F(v, 2)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
