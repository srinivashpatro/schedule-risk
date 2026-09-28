using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Risk.Register;
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
                               HealthReport? health = null, VerifyReport? verify = null, string? modelName = null,
                               CostBenefitResult? costBenefit = null, RiskRegister? register = null, DateTime? generated = null)
    {
        var pcal = s.Settings.ProjectCalendar;
        double mpd = pcal.MinutesPerDay;
        double WorkDaysBetween(long a, long b) => pcal.WorkBetween(a, b) / mpd;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(Brand.Name).Append(" Report - ").Append(E(s.ProjectCode)).Append("</title><style>");
        sb.Append(@":root{--bg:#fbfaf7;--fg:#1d1f23;--muted:#61656d;--line:#dedad2;--card:#fff;--a:#2f6db5;--b:#c2572b;--ok:#2e7d4f;--bad:#b3261e}
@media (prefers-color-scheme:dark){:root{--bg:#16181b;--fg:#e8e6e1;--muted:#a3a6ab;--line:#33363b;--card:#1e2024;--a:#6ea4e6;--b:#e98a5e;--ok:#6cc08f;--bad:#f07f76}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,'Segoe UI',sans-serif}
main{max-width:1060px;margin:0 auto;padding:28px 16px 64px}h1{font-size:26px;margin:0 0 4px}h2{font-size:18px;margin:36px 0 10px}
.muted{color:var(--muted)}.sum{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,440px),1fr));gap:0 28px}.sum>div{min-width:0}
.sum h3{margin:18px 0 6px;font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:var(--muted)}.sum p{margin:6px 0 0;font-size:13px}.small{font-size:13px}td.n.c{text-align:center}table{border-collapse:collapse;width:100%;background:var(--card);border:1px solid var(--line);border-radius:10px;overflow:hidden;font-size:14px}
th,td{padding:7px 10px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}th{font-weight:600;color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.03em}
td.n{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}.wrap{overflow-x:auto}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px}
svg text{fill:var(--muted);font-size:11px}.pass{color:var(--ok);font-weight:600}.fail{color:var(--bad);font-weight:600}
.legend span{display:inline-block;margin-right:14px;font-size:13px}.sw{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:5px;vertical-align:middle}
h2+.lead{margin:-4px 0 10px}dl.notes{margin:0;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:2px 16px}
dl.notes>div{display:grid;grid-template-columns:minmax(0,170px) minmax(0,1fr);gap:4px 20px;padding:10px 0;border-top:1px solid var(--line)}dl.notes>div:first-child{border-top:0}
dl.notes dt{font-weight:600;font-size:12px;letter-spacing:.04em;text-transform:uppercase;color:var(--muted);padding-top:2px}dl.notes dd{margin:0;max-width:68ch}
dl.terms dd{font-size:14px}main>h3{margin:22px 0 8px;font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:var(--muted)}
.nw{white-space:nowrap}@media (max-width:560px){dl.notes>div{grid-template-columns:minmax(0,1fr)}}");
        sb.Append("</style></head><body><main>");
        sb.Append("<h1>Project risk analysis: ").Append(E(s.ProjectCode)).Append("</h1>");
        var summary = ResultsSummary.Build(pre, post);
        sb.Append("<div class=\"muted\">").Append(E(summary.Model));
        if (!string.IsNullOrEmpty(modelName)) sb.Append(" &middot; risk model: ").Append(E(modelName));
        sb.Append("<br>").Append(E(summary.Run)).Append("</div>");

        Summary(sb, summary, post != null);
        Notes(sb, ResultsNarrative.Build(pre, post, summary: summary));

        sb.Append("<h2>Project finish distribution</h2><div class=\"card\">");
        sb.Append(SCurve(pre, post, s, bins: HistogramBins.Daily));
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
            sb.Append("<div class=\"card\">").Append(Tornado(RiskTornado.Build(pre, post, 15))).Append("</div>");
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

        if (costBenefit is { Rows.Count: > 0 } cb)
        {
            string cur = cb.Currency.Length > 0 ? cb.Currency + " " : "";
            string M(double? v) => v is double x ? cur + x.ToString("N0", Inv) : "–";
            sb.Append("<h2>Cost-benefit of responses</h2><p class=\"muted\">For each risk with a response, a run with only that risk mitigated on the same seed and iterations")
              .Append(cb.CostOfDelayPerDay > 0 ? $"; days saved valued at {E(cur)}{cb.CostOfDelayPerDay.ToString("N0", Inv)} per working day." : ".")
              .Append("</p><div class=\"wrap\"><table><tr><th>Risk</th><th>Response cost</th><th>Days saved at P80</th><th>at P").Append(cb.Level)
              .Append("</th><th>on average</th><th>Value</th><th>Net benefit</th><th>Benefit / cost</th></tr>");
            foreach (var r in cb.Rows)
                sb.Append("<tr><td>").Append(E(r.Id + " " + r.Title)).Append("</td><td class=\"n\">").Append(E(M(r.ResponseCost)))
                  .Append("</td><td class=\"n\">").Append(F(r.SavedP80, 1)).Append("</td><td class=\"n\">").Append(F(r.SavedAtLevel, 1))
                  .Append("</td><td class=\"n\">").Append(F(r.SavedMean, 1)).Append("</td><td class=\"n\">").Append(E(M(r.Value)))
                  .Append("</td><td class=\"n\">").Append(E(M(r.Net))).Append("</td><td class=\"n\">").Append(r.Ratio is double x ? F(x, 1) + "×" : "–")
                  .Append("</td></tr>");
            sb.Append("</table></div>");
        }
        if (register != null && RegisterReport.Listed(register).Count > 0)
        {
            var m = register.Matrix;
            var today = DateOnly.FromDateTime(generated ?? DateTime.Now);
            sb.Append("<h2>Risk register</h2><p class=\"muted\">").Append(E(RegisterReport.Lead(register))).Append("</p>");
            sb.Append("<div class=\"card\"><h3>Heat map now (current assessment)</h3>")
              .Append(RegisterReport.HeatMapSvg(m, HeatMap.Build(register, AssessmentPoint.Current), "Heat map now"))
              .Append("</div><div class=\"card\"><h3>Heat map after the responses (target assessment)</h3>")
              .Append(RegisterReport.HeatMapSvg(m, HeatMap.Build(register, AssessmentPoint.Target), "Heat map after the responses")).Append("</div>");
            sb.Append("<div class=\"wrap\"><table><tr><th>ID</th><th>Risk</th><th>Category</th><th>Owner</th><th>Now</th><th>After response</th><th>Response</th></tr>");
            foreach (var r in RegisterReport.Listed(register))
                sb.Append("<tr><td>").Append(E(r.Id)).Append("</td><td><b>").Append(E(r.Title)).Append("</b>")
                  .Append(r.Statement.Length > 0 ? "<br><span class=\"muted\">" + E(r.Statement) + "</span>" : "").Append("</td><td>").Append(E(r.Category))
                  .Append("</td><td>").Append(E(r.Owner)).Append("</td><td>").Append(E(RegisterReport.Cell(m, r.Current))).Append("</td><td>")
                  .Append(E(RegisterReport.Cell(m, r.Target))).Append("</td><td>").Append(E(RegisterReport.Response(r))).Append("</td></tr>");
            sb.Append("</table></div>");
            var actions = register.ActionList(today);
            if (actions.Count > 0)
            {
                sb.Append("<h2>Risk actions</h2><p class=\"muted\">").Append(actions.Count(a => a.Overdue)).Append(" overdue and ")
                  .Append(actions.Count(a => a.Action.Status == ActionStatus.Open)).Append(" open, as of ").Append(today.ToString("dd-MMM-yyyy", Inv)).Append(".</p>")
                  .Append("<div class=\"wrap\"><table><tr><th>Risk</th><th>Action</th><th>Owner</th><th>Due</th><th>Status</th></tr>");
                foreach (var a in actions)
                    sb.Append("<tr><td>").Append(E(a.Risk.Id)).Append("</td><td>").Append(E(a.Action.Text)).Append("</td><td>").Append(E(a.Action.Owner))
                      .Append("</td><td>").Append(a.Action.Due?.ToString("dd-MMM-yyyy", Inv) ?? "").Append("</td><td class=\"").Append(a.Overdue ? "fail" : "")
                      .Append("\">").Append(E(RegisterReport.ActionStatus(a))).Append("</td></tr>");
                sb.Append("</table></div>");
            }
        }
        if (health != null)
        {
            void HealthTable(string title, IEnumerable<HealthItem> items)
            {
                var list = items.ToList();
                var (p, f, na) = health.Score(list);
                sb.Append("<h2>").Append(E(title)).Append("</h2><p class=\"muted\">").Append(p).Append(" passed, ").Append(f).Append(" failed, ")
                  .Append(na).Append(" not applicable.</p><div class=\"wrap\"><table><tr><th>Check</th><th>Status</th><th>Actual</th><th>Target</th><th>Count</th><th>Notes</th></tr>");
                foreach (var i in list)
                {
                    bool pass = i.Status == HealthStatus.Pass, na1 = i.Status == HealthStatus.NotApplicable;
                    string note = i.Status is HealthStatus.Fail or HealthStatus.FailInformational && i.Flagged.Count > 0 ? i.Examples(5) : i.Note ?? "";
                    if (i.StatusConventional is HealthStatus sc) note = $"Conventional reading: {HealthCheck.StatusText(sc)}. " + note;
                    sb.Append("<tr><td>").Append(E((i.Number != null ? i.Number + ". " : "") + i.Label)).Append("</td><td class=\"")
                      .Append(pass ? "pass" : na1 ? "muted" : "fail").Append("\">").Append(E(i.StatusText)).Append("</td><td class=\"n\">")
                      .Append(E(i.ActualText)).Append("</td><td class=\"n\">").Append(E(i.TargetText)).Append("</td><td class=\"n\">")
                      .Append(E(i.CountText)).Append("</td><td>").Append(E(note)).Append("</td></tr>");
                }
                sb.Append("</table></div>");
            }
            HealthTable("Schedule health: P6 Check Schedule", health.P6);
            HealthTable("Schedule health: DCMA 14-Point Assessment", health.Dcma);
        }
        if (verify != null)
        {
            sb.Append("<h2>Engine check against P6</h2><p>").Append(verify.FieldsMatched).Append(" of ").Append(verify.FieldsCompared)
              .Append(" date and float fields match the values P6 stored in the file (").Append(verify.ActivitiesMatched).Append(" of ")
              .Append(verify.Compared).Append(" activities fully match).</p>");
        }
        sb.Append("<p class=\"muted\" style=\"margin-top:40px\">Generated ").Append((generated ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm", Inv))
          .Append(" by ").Append(Brand.Name).Append(' ').Append(Brand.Version).Append(".</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>The Results summary, laid out as in the browser app: finish figures and the spread of the duration (pre and
    /// post) on the left; top drivers, critical and near-critical activities on the right.</summary>
    private static void Summary(StringBuilder sb, ResultsSummary sum, bool both)
    {
        const int Top = 5;
        void Rows(string title, List<StatRow> rows)
        {
            sb.Append("<h3>").Append(E(title)).Append("</h3><div class=\"wrap\"><table><tr><th></th>")
              .Append(both ? "<th class=\"n\">Pre-mitigation</th><th class=\"n\">Post-mitigation</th>" : "<th></th>").Append("</tr>");
            foreach (var r in rows)
            {
                sb.Append("<tr><td>").Append(E(r.Label)).Append("</td>");
                if (r.Shared && both) sb.Append("<td class=\"n c\" colspan=\"2\">").Append(E(r.Pre)).Append("</td>");
                else
                {
                    sb.Append("<td class=\"n\">").Append(E(r.Pre)).Append("</td>");
                    if (both) sb.Append("<td class=\"n\">").Append(E(r.Post)).Append("</td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</table></div>");
        }
        void Named(string title, string valueHead, IEnumerable<(string Id, string Name, string Value)> rows, int count)
        {
            sb.Append("<h3>").Append(E(title));
            if (count >= 0) sb.Append(": ").Append(count.ToString("N0", Inv));
            sb.Append("</h3>");
            var list = rows.Take(Top).ToList();
            if (list.Count == 0) { sb.Append("<p class=\"muted\">None.</p>"); return; }
            sb.Append("<div class=\"wrap\"><table><tr><th>").Append(E(valueHead == "Criticality" || sum.DriversAreActivities ? "Activity" : "Risk or driver"))
              .Append("</th><th class=\"n\">").Append(E(valueHead)).Append("</th></tr>");
            foreach (var (id, name, value) in list)
                sb.Append("<tr><td>").Append(E(id)).Append(" <span class=\"muted\">").Append(E(name)).Append("</span></td><td class=\"n\">")
                  .Append(E(value)).Append("</td></tr>");
            sb.Append("</table></div>");
            if (count > Top) sb.Append("<p class=\"muted\">and ").Append((count - Top).ToString("N0", Inv)).Append(" more</p>");
        }

        sb.Append("<h2>Summary</h2><div class=\"sum\"><div>");
        Rows("Finish", sum.Finish);
        Rows("Spread of the duration", sum.Spread);
        sb.Append("</div><div>");
        if (sum.Drivers.Count == 0) sb.Append("<h3>Top risk drivers</h3><p class=\"muted\">Nothing in the model varies the finish.</p>");
        else Named("Top risk drivers", "Sensitivity", sum.Drivers.Select(d => (d.Id, d.Title, F(d.Sensitivity, 2))), -1);
        Named("Critical activities", "Criticality", sum.Critical.Select(a => (a.Code, a.Name, ResultsSummary.Pct(a.Criticality))), sum.Critical.Count);
        Named("Near-critical activities", "Criticality", sum.NearCritical.Select(a => (a.Code, a.Name, ResultsSummary.Pct(a.Criticality))), sum.NearCritical.Count);
        sb.Append("</div></div>");
    }

    /// <summary>Encoded text with each hyphenated word (a date such as 13-Sep-2028, or 1-in-5) kept on one line.</summary>
    public static string Unbroken(string text)
    {
        var sb = new StringBuilder();
        int at = 0;
        foreach (Match m in ResultsNarrative.Hyphenated.Matches(text))
        {
            sb.Append(E(text[at..m.Index])).Append("<span class=\"nw\">").Append(E(m.Value)).Append("</span>");
            at = m.Index + m.Length;
        }
        return sb.Append(E(text[at..])).ToString();
    }

    /// <summary>"What the results mean": the plain-language findings, then the glossary (the browser app shows the same).</summary>
    private static void Notes(StringBuilder sb, ResultsNarrative notes)
    {
        void List(string cls, IEnumerable<(string Dt, string Dd)> items)
        {
            sb.Append("<dl class=\"").Append(cls).Append("\">");
            foreach (var (dt, dd) in items) sb.Append("<div><dt>").Append(E(dt)).Append("</dt><dd>").Append(Unbroken(dd)).Append("</dd></div>");
            sb.Append("</dl>");
        }
        sb.Append("<h2>What the results mean</h2><p class=\"muted lead\">").Append(E(ResultsNarrative.Lead)).Append("</p>");
        List("notes", notes.Findings.Select(f => (f.Label, f.Text)));
        sb.Append("<h3>Terms used</h3>");
        List("notes terms", ResultsNarrative.Terms.Select(t => (t.Name, t.Meaning)));
    }

    /// <summary>
    /// Combined finish date chart as inline SVG (theme via CSS variables --a, --b, --fg, --line): the histogram of finish
    /// dates on the primary (left, frequency) axis and the cumulative probability S-curve on the secondary (right, percent)
    /// axis. <paramref name="bins"/> groups the bars by calendar day, by 7-day week, or (Auto) into 40 equal bins.
    /// <paramref name="interactive"/> wraps it in a focusable <c>div.scurve</c> whose <c>data-scurve</c> JSON holds, for every
    /// calendar day, how many iterations finished by the end of that day, plus the bar counts and edges; the browser app's
    /// js/app.js draws the hover readout.
    /// <paramref name="percentile"/> (browser app) marks that confidence level with a labelled line, gives the bars up to it
    /// the class <c>in</c>, and labels the deterministic line and, with two scenarios, each curve.
    /// <paramref name="histogram"/> draws the bars only, without the curves and the percent axis.
    /// </summary>
    public static string SCurve(SimulationSummary pre, SimulationSummary? post, Schedule s, bool interactive = false,
                                int? percentile = null, bool histogram = false, HistogramBins bins = HistogramBins.Auto)
    {
        const int W = 960, H = 300, L = 56, B = 34;
        int R = histogram ? 16 : 48;
        long mfb = pre.MustFinishBy;
        // room above the plot for the line labels: deterministic, P-level and, with one, the Must Finish By
        int T = percentile == null ? 12 : mfb != Time.None ? 62 : 44;
        var (lo, hi, mfbOnChart) = ChartRange(pre, post);
        double X(long t) => L + (double)(t - lo) / (hi - lo) * (W - L - R);
        double Y(double p) => T + (1 - p) * (H - T - B);
        string what = histogram ? "Distribution of project finish dates"
            : "Distribution and cumulative probability of project finish";
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" width=\"100%\" role=\"img\"");
        sb.Append(interactive
            ? $" tabindex=\"0\" aria-label=\"{what}. Focus and use the arrow keys to read the chance of finishing by each date.\">"
            : $" aria-label=\"{what}\">");

        // histogram (pre) behind the curve, on the primary axis
        long[] edges = BinEdges(lo, hi, bins);
        int nb = edges.Length - 1;
        var counts = new int[nb];
        foreach (var t in pre.SortedFinish)
        {
            int bi = Array.BinarySearch(edges, t);
            bi = bi >= 0 ? bi : ~bi - 1;
            counts[Math.Clamp(bi, 0, nb - 1)]++;
        }
        int cmax = Math.Max(1, counts.Max());
        int fstep = NiceStep(cmax / 4.0);
        int fmax = Math.Max(fstep, (cmax + fstep - 1) / fstep * fstep);
        for (int f = 0; f <= fmax; f += fstep)
        {
            double y = Y(f / (double)fmax);
            sb.Append($"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{F(y)}\" y2=\"{F(y)}\" stroke=\"var(--line)\"/>");
            sb.Append($"<text x=\"{L - 6}\" y=\"{F(y + 4)}\" text-anchor=\"end\" class=\"faxis\">{f}</text>");
        }
        sb.Append($"<text x=\"12\" y=\"{F((T + H - B) / 2.0)}\" text-anchor=\"middle\" transform=\"rotate(-90 12 {F((T + H - B) / 2.0)})\" class=\"atitle\">Frequency</text>");
        if (!histogram)
        {
            for (int k = 0; k <= 4; k++)
            {
                double p = k / 4.0;
                sb.Append($"<text x=\"{W - R + 6}\" y=\"{F(Y(p) + 4)}\" text-anchor=\"start\" class=\"paxis\">{p * 100:F0}%</text>");
            }
        }
        long cut = percentile is int pc ? Statistics.PercentileSorted(pre.SortedFinish, pc) : 0;
        for (int k = 0; k < nb; k++)
        {
            double x0 = X(edges[k]), x1 = X(edges[k + 1]);
            double gap = x1 - x0 > 4 ? 1 : 0;
            double h = counts[k] / (double)fmax * (H - T - B);
            string cls = percentile != null && (edges[k] + edges[k + 1]) / 2 <= cut ? "bar in" : "bar";
            sb.Append($"<rect x=\"{F(x0 + gap)}\" y=\"{F(H - B - h)}\" width=\"{F(Math.Max(0.5, x1 - x0 - 2 * gap))}\" height=\"{F(h)}\" fill=\"var(--a)\" opacity=\".15\" class=\"{cls}\"/>");
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
          .Append("\"bins\":[").Append(string.Join(',', counts)).Append("],\"edges\":[").Append(string.Join(',', edges)).Append("],\"series\":[");
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

    /// <summary>
    /// Bar edges in minutes for the finish date histogram over [lo, hi]: 40 equal bins (Auto), or one bar per calendar
    /// day or per 7-day week starting at midnight on the day of <paramref name="lo"/>. The outer edges are clamped to the
    /// chart range so the bars stay inside the plot.
    /// </summary>
    public static long[] BinEdges(long lo, long hi, HistogramBins bins)
    {
        if (bins == HistogramBins.Auto)
        {
            const int n = 40;
            var e = new long[n + 1];
            for (int k = 0; k <= n; k++) e[k] = lo + (hi - lo) * k / n;
            return e;
        }
        long width = (bins == HistogramBins.Weekly ? 7 : 1) * Time.MinutesPerDay;
        long start = lo / Time.MinutesPerDay * Time.MinutesPerDay;
        var list = new List<long> { lo };
        for (long t = start + width; t < hi; t += width) list.Add(t);
        list.Add(hi);
        return list.ToArray();
    }

    // 1, 2 or 5 times a power of ten, at least v.
    private static int NiceStep(double v)
    {
        if (v <= 1) return 1;
        double p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (int m in new[] { 1, 2, 5, 10 })
            if (m * p >= v) return (int)(m * p);
        return (int)(10 * p);
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

    /// <summary>The risk tornado with pre- (ink) and post-mitigation (accent) bars side by side for each risk.</summary>
    public static string Tornado(IReadOnlyList<TornadoRow> rows)
    {
        if (rows.Count == 0) return "";
        bool both = rows.Any(r => r.Post != null);
        const int W = 960, labelW = 380;
        int rowH = both ? 44 : 32;
        int H = rows.Count * rowH + 34;
        double mid = labelW + (W - labelW) / 2.0;
        double scale = (W - labelW) / 2.0 - 50;
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {W} {H}\" width=\"100%\" role=\"img\" aria-label=\"Tornado chart of the risk ranking\">");
        sb.Append($"<line x1=\"{F(mid)}\" x2=\"{F(mid)}\" y1=\"0\" y2=\"{H - 24}\" stroke=\"var(--line)\"/>");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            double y = 5 + i * rowH;
            string lab = r.Id + " " + r.Title;
            if (lab.Length > 36) lab = lab.Substring(0, 35) + "…";
            sb.Append($"<text x=\"{labelW - 10}\" y=\"{F(y + (both ? 25 : 20))}\" text-anchor=\"end\" font-size=\"18\" style=\"fill:var(--fg)\">{E(lab)}</text>");
            void Bar(double v, double top, double h, string fill)
            {
                double w = Math.Abs(v) * scale, x = v >= 0 ? mid : mid - w;
                sb.Append($"<rect x=\"{F(x)}\" y=\"{F(top)}\" width=\"{F(Math.Max(w, 1))}\" height=\"{F(h)}\" fill=\"{fill}\"/>");
                double tx = v >= 0 ? x + w + 6 : x - 6;
                sb.Append($"<text x=\"{F(tx)}\" y=\"{F(top + h - 2)}\" text-anchor=\"{(v >= 0 ? "start" : "end")}\" font-size=\"15\">{F(v, 2)}</text>");
            }
            if (both)
            {
                Bar(r.Pre, y + 4, 16, "var(--a)");
                if (r.Post is double p) Bar(p, y + 22, 16, "var(--b)");
            }
            else Bar(r.Pre, y + 4, rowH - 9, "var(--a)");
        }
        double ly = H - 8;
        sb.Append($"<rect x=\"{labelW}\" y=\"{F(ly - 9)}\" width=\"12\" height=\"10\" fill=\"var(--a)\"/><text x=\"{labelW + 18}\" y=\"{F(ly)}\" font-size=\"15\">Pre-mitigation</text>");
        if (both)
            sb.Append($"<rect x=\"{labelW + 200}\" y=\"{F(ly - 9)}\" width=\"12\" height=\"10\" fill=\"var(--b)\"/><text x=\"{labelW + 218}\" y=\"{F(ly)}\" font-size=\"15\">Post-mitigation</text>");
        sb.Append("</svg>");
        return sb.ToString();
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

/// <summary>How the finish date histogram groups its bars.</summary>
public enum HistogramBins
{
    /// <summary>40 equal bins over the chart range .</summary>
    Auto,
    /// <summary>One bar per calendar day.</summary>
    Daily,
    /// <summary>One bar per 7-day week.</summary>
    Weekly,
}
