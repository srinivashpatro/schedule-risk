using System.Globalization;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Core.Reporting.Export;

public enum Align { Left, Right, Center }

/// <summary>How a cell's text is set: normal ink, secondary grey (IDs, labels), strong (names), or the accent.</summary>
public enum Tone { Normal, Muted, Strong, Accent }

/// <summary>A table cell. A cell may span several columns (a value both scenarios share, a list's title).</summary>
public sealed record Cell(string Text, int Span = 1, Tone Tone = Tone.Normal, Align? Align = null);

/// <summary>A table row; a highlighted row (the chosen confidence level) is shaded and set bold, as in the app.</summary>
public sealed record Row(IReadOnlyList<Cell> Cells, bool Highlight = false);

public sealed class Table
{
    /// <summary>The head row. In the summary tables its first cell names the table, as in the app.</summary>
    public required IReadOnlyList<Cell> Header { get; init; }
    /// <summary>Relative column widths.</summary>
    public required IReadOnlyList<double> Widths { get; init; }
    public required IReadOnlyList<Align> Aligns { get; init; }
    public List<Row> Rows { get; } = new();
    /// <summary>A line under the table ("and 3 more").</summary>
    public string? Note { get; init; }
    /// <summary>Share of the page's text width the table takes (a label and one value need less than the full width).</summary>
    public double Share { get; init; } = 1;
    public int Columns => Widths.Count;
}

/// <summary>A chart as a standalone SVG (see <see cref="ReportCharts"/>), drawn by the browser into a picture.</summary>
public sealed class Chart
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Svg { get; init; }
    /// <summary>Size in CSS pixels.</summary>
    public required int Width { get; init; }
    public required int Height { get; init; }
    public string Alt { get; init; } = "";
}

public abstract record Block;
public sealed record TableBlock(Table Table) : Block;
public sealed record ChartBlock(Chart Chart) : Block;
public sealed record TextBlock(string Text, bool Muted = true) : Block;
/// <summary>The app's two Summary columns: on a portrait page one after the other (as the app stacks them on a narrow
/// screen), in slides one slide per column with its tables side by side.</summary>
public sealed record ColumnsBlock(IReadOnlyList<Block> Left, IReadOnlyList<Block> Right, string LeftTitle = "", string RightTitle = "") : Block;

/// <summary>Label and text pairs, such as "What the results mean" and its glossary: in documents a label column beside
/// the text, in slides one slide per block.</summary>
public sealed record NotesBlock(string? Title, IReadOnlyList<Note> Notes) : Block;
public sealed record Note(string Label, string Text);

public sealed class Section
{
    public required string Title { get; init; }
    /// <summary>The line under the heading, as the app's sub text.</summary>
    public string? Lead { get; init; }
    public List<Block> Blocks { get; } = new();
}

/// <summary>What was simulated and how, for <see cref="ReportContent.Build"/>.</summary>
public sealed record ReportInput(Schedule Schedule, SimulationSummary Pre, SimulationSummary? Post = null,
                                 HealthReport? Health = null, VerifyReport? Verify = null,
                                 string? ModelName = null, int Percentile = 80, DateTime? Generated = null,
                                 CostBenefitResult? CostBenefit = null);

/// <summary>
/// The report the PDF, Word and PowerPoint exports share, in the order of the app's Results: the Summary and what the
/// results mean, the finish date distribution (histogram and cumulative curve), the confidence levels, the risk
/// ranking (or duration sensitivity), the criticality index, the risk drivers, the activities that drive the finish
/// and the milestones; then, as in the HTML report, the schedule health checks (P6 Check Schedule and DCMA 14-Point) and the engine check against P6.
/// </summary>
public sealed class ReportContent
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public required string ProjectCode { get; init; }
    public required string Title { get; init; }
    /// <summary>The model and run lines under the title.</summary>
    public required IReadOnlyList<string> Lines { get; init; }
    /// <summary>"Generated … by …", for the footers.</summary>
    public required string Generated { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public List<Section> Sections { get; } = new();

    public IEnumerable<Chart> Charts => Sections.SelectMany(s => Flatten(s.Blocks)).OfType<ChartBlock>().Select(c => c.Chart);

    internal static IEnumerable<Block> Flatten(IEnumerable<Block> blocks) =>
        blocks.SelectMany(b => b is ColumnsBlock c ? Flatten(c.Left).Concat(Flatten(c.Right)) : new[] { b });

    public static ReportContent Build(ReportInput input, ReportFonts fonts)
    {
        var (s, pre, post) = (input.Schedule, input.Pre, input.Post);
        int pct = Math.Clamp(input.Percentile, 5, 95);
        bool both = post != null;
        var sum = ResultsSummary.Build(pre, post, pct);
        var pcal = s.Settings.ProjectCalendar;
        double WorkDays(long a, long b) => pcal.WorkBetween(a, b) / pcal.MinutesPerDay;
        DateTime when = input.Generated ?? DateTime.Now;

        var doc = new ReportContent
        {
            ProjectCode = s.ProjectCode,
            Title = "Project risk analysis: " + s.ProjectCode,
            Lines = new[] { sum.Model + (string.IsNullOrEmpty(input.ModelName) ? "" : " · risk model: " + input.ModelName), sum.Run },
            Generated = $"Generated {when.ToString("dd-MMM-yyyy HH:mm", Inv)} by {Brand.Name} {Brand.Version}",
            GeneratedAt = when,
        };

        // Summary: finish and spread on the left, what drives them on the right, as in the app.
        var summary = new Section { Title = "Summary" };
        summary.Blocks.Add(new ColumnsBlock(
            new Block[] { new TableBlock(Stats("Finish", sum.Finish, both, pct)), new TableBlock(Stats("Spread of the duration", sum.Spread, both, pct)) },
            new Block[]
            {
                new TableBlock(Named("Top risk drivers", "Sensitivity", sum.Drivers.Select(d => (d.Id, d.Title, N(d.Sensitivity, 2))).ToList(), -1,
                    "Nothing in the model varies the finish.")),
                new TableBlock(Named("Critical activities", "Criticality", sum.Critical.Select(a => (a.Code, a.Name, ResultsSummary.Pct(a.Criticality))).ToList(),
                    sum.Critical.Count, "None.")),
                new TableBlock(Named("Near-critical activities", "Criticality", sum.NearCritical.Select(a => (a.Code, a.Name, ResultsSummary.Pct(a.Criticality))).ToList(),
                    sum.NearCritical.Count, "None.")),
            },
            "Finish dates and their spread", "What drives the finish"));
        doc.Sections.Add(summary);

        // What the results mean: the Summary's figures in plain words, then the terms they use.
        var notes = new Section { Title = "What the results mean", Lead = ResultsNarrative.Lead };
        notes.Blocks.Add(new NotesBlock(null, ResultsNarrative.Build(pre, post, pct, sum).Findings.Select(f => new Note(f.Label, f.Text)).ToList()));
        notes.Blocks.Add(new NotesBlock("Terms used", ResultsNarrative.Terms.Select(t => new Note(t.Name, t.Meaning)).ToList()));
        doc.Sections.Add(notes);

        long cut = Statistics.PercentileSorted(pre.SortedFinish, pct);
        var dist = new Section
        {
            Title = "Finish date distribution",
            Lead = $"Project finish dates over {pre.Iterations.ToString("N0", Inv)} iterations; P{pct} is {Day(cut)}. Dashed line: the deterministic finish.",
        };
        dist.Blocks.Add(new ChartBlock(ReportCharts.Distribution("histogram", "Finish date distribution", pre, post, s, pct, histogram: true, fonts)));
        dist.Blocks.Add(new ChartBlock(ReportCharts.Distribution("cumulative", "Cumulative probability of finish", pre, post, s, pct, histogram: false, fonts)));
        doc.Sections.Add(dist);

        var conf = new Section
        {
            Title = "Confidence levels",
            Lead = both ? "Finish date at each confidence level, before and after mitigation, and the working days mitigation gains."
                        : "Finish date at each confidence level and its distance from the deterministic finish in working days.",
        };
        var levels = new Table
        {
            Header = both ? Heads("Level", "Pre-mitigation", "Post-mitigation", "Gain (working days)") : Heads("Level", "Finish", "Change (working days)"),
            Widths = both ? new[] { 0.7, 1.2, 1.2, 1.2 } : new[] { 0.7, 1.2, 1.4 },
            Aligns = both ? new[] { Align.Left, Align.Right, Align.Right, Align.Right } : new[] { Align.Left, Align.Right, Align.Right },
        };
        foreach (var (level, finish) in pre.FinishPercentiles)
        {
            var cells = new List<Cell> { new($"P{level}"), new(Day(finish)) };
            if (post != null)
            {
                long pv = post.FinishPercentiles[level];
                cells.Add(new(Day(pv)));
                cells.Add(new(N(WorkDays(pv, finish), 1)));
            }
            else
            {
                double d = WorkDays(pre.DeterministicFinish, finish);
                cells.Add(new((d >= 0 ? "+" : "") + N(d, 0)));
            }
            levels.Rows.Add(new Row(cells, level == pct));
        }
        conf.Blocks.Add(new TableBlock(levels));
        doc.Sections.Add(conf);

        if (pre.Risks.Count > 0)
        {
            var ranking = new Section { Title = "Risk ranking", Lead = "Rank correlation of each risk's impact with the project finish"
                + (post != null ? ", before and after mitigation." : ".") + " Bars to the left shorten the finish." };
            ranking.Blocks.Add(new ChartBlock(ReportCharts.Tornado("risks", "Risk tornado", RiskTornado.Build(pre, post, 12), fonts)));
            var detail = new Table
            {
                Header = Heads("ID", "Risk", "Occurred", "Sensitivity", "Finish delta (working days)"),
                Widths = new[] { 0.6, 2.6, 0.8, 0.8, 1.2 },
                Aligns = new[] { Align.Left, Align.Left, Align.Right, Align.Right, Align.Right },
                Note = "Finish delta: the mean finish when the risk occurs minus the mean finish when it does not.",
            };
            foreach (var r in pre.Risks)
                detail.Rows.Add(new Row(new Cell[]
                {
                    new(r.Id, Tone: Tone.Muted), new(r.Title, Tone: Tone.Strong), new(ResultsSummary.Pct(r.Occurrence)), new(N(r.Sensitivity, 2)),
                    new(r.MeanFinishDeltaDays is double dd ? N(dd, 1) : ""),
                }));
            ranking.Blocks.Add(new TableBlock(detail));
            doc.Sections.Add(ranking);
        }
        else
        {
            var bySens = pre.Activities.OrderByDescending(a => Math.Abs(a.Sensitivity)).Take(12).ToList();
            double top = Math.Max(0.01, bySens.Select(a => Math.Abs(a.Sensitivity)).DefaultIfEmpty(0).Max());
            var sens = new Section { Title = "Duration sensitivity", Lead = "Correlation of activity duration with the project finish." };
            sens.Blocks.Add(new ChartBlock(ReportCharts.Bars("sensitivity", "Duration sensitivity",
                bySens.Select(a => new ReportCharts.Bar(a.Code, a.Name, Math.Abs(a.Sensitivity) / top, N(a.Sensitivity, 2))).ToList(), false, fonts)));
            doc.Sections.Add(sens);
        }
        if (input.CostBenefit is { Rows.Count: > 0 } cb)
        {
            string cur = cb.Currency.Length > 0 ? cb.Currency + " " : "";
            string M(double? v) => v is double x ? cur + x.ToString("N0", Inv) : "–";
            var sec = new Section
            {
                Title = "Cost-benefit of responses",
                Lead = "For each risk with a response, a run with only that risk mitigated on the same seed and iterations: the days it saves and their value"
                       + (cb.CostOfDelayPerDay > 0 ? $" at {cur}{cb.CostOfDelayPerDay.ToString("N0", Inv)} per working day of delay." : " (no cost of delay set)."),
            };
            var t = new Table
            {
                Header = Heads("Risk", "Response cost", "Days saved at P80", $"at P{cb.Level}", "on average", "Value", "Net benefit", "Benefit / cost"),
                Widths = new[] { 2.2, 1, 0.8, 0.7, 0.7, 1, 1, 0.7 },
                Aligns = new[] { Align.Left, Align.Right, Align.Right, Align.Right, Align.Right, Align.Right, Align.Right, Align.Right },
                Note = "Working days of the project calendar. Value: days saved at P80 times the cost of delay; net benefit: value minus response cost.",
            };
            foreach (var r in cb.Rows)
                t.Rows.Add(new Row(new Cell[]
                {
                    new(r.Id + " " + r.Title, Tone: Tone.Strong), new(M(r.ResponseCost)), new(N(r.SavedP80, 1), Tone: Tone.Strong),
                    new(N(r.SavedAtLevel, 1)), new(N(r.SavedMean, 1)), new(M(r.Value)),
                    new(M(r.Net), Tone: r.Net < 0 ? Tone.Accent : Tone.Normal), new(r.Ratio is double x ? N(x, 1) + "×" : "–"),
                }));
            sec.Blocks.Add(new TableBlock(t));
            doc.Sections.Add(sec);
        }

        var crit = new Section { Title = "Criticality index", Lead = "Share of iterations in which the activity sits on the critical path." };
        crit.Blocks.Add(new ChartBlock(ReportCharts.Bars("criticality", "Criticality index",
            pre.Activities.OrderByDescending(a => a.Criticality).ThenByDescending(a => a.Cruciality).Take(12)
                .Select(a => new ReportCharts.Bar(a.Code, a.Name, a.Criticality, ResultsSummary.Pct(a.Criticality))).ToList(), true, fonts)));
        doc.Sections.Add(crit);

        if (pre.Drivers.Count > 0)
        {
            double top = Math.Max(0.01, pre.Drivers.Max(d => Math.Abs(d.Sensitivity)));
            var drivers = new Section { Title = "Risk drivers", Lead = "Rank correlation of each driver's sampled factor with the project finish." };
            drivers.Blocks.Add(new ChartBlock(ReportCharts.Bars("drivers", "Risk drivers",
                pre.Drivers.Select(d => new ReportCharts.Bar(d.Id, d.Title, Math.Abs(d.Sensitivity) / top, N(d.Sensitivity, 2))).ToList(), false, fonts)));
            doc.Sections.Add(drivers);
        }

        var acts = new Section
        {
            Title = "Activities that drive the finish",
            Lead = "Criticality: share of iterations in which the activity was critical. Sensitivity: how closely its duration tracks "
                 + "the finish date. Cruciality: both together; start here.",
        };
        var actTable = new Table
        {
            Header = Heads("ID", "Activity", "Criticality", "Sensitivity", "Cruciality"),
            Widths = new[] { 0.8, 3.0, 0.9, 0.9, 0.9 },
            Aligns = new[] { Align.Left, Align.Left, Align.Right, Align.Right, Align.Right },
            Note = pre.Activities.Count > 25 ? $"The top 25 of {pre.Activities.Count.ToString("N0", Inv)} activities, by cruciality." : null,
        };
        foreach (var a in pre.Activities.Take(25))
            actTable.Rows.Add(new Row(new Cell[]
            {
                new(a.Code, Tone: Tone.Muted), new(a.Name, Tone: Tone.Strong), new(ResultsSummary.Pct(a.Criticality)), new(N(a.Sensitivity, 2)), new(N(a.Cruciality, 3)),
            }));
        acts.Blocks.Add(new TableBlock(actTable));
        doc.Sections.Add(acts);

        if (pre.Milestones.Count > 0)
        {
            var ms = new Section { Title = "Milestones" };
            var t = new Table
            {
                Header = Heads("ID", "Milestone", "Deterministic", "P10", "P50", "P80", "P90"),
                Widths = new[] { 0.8, 2.0, 1.4, 1, 1, 1, 1 },
                Aligns = new[] { Align.Left, Align.Left, Align.Right, Align.Right, Align.Right, Align.Right, Align.Right },
            };
            foreach (var m in pre.Milestones)
                t.Rows.Add(new Row(new Cell[]
                {
                    new(m.Code, Tone: Tone.Muted), new(m.Name, Tone: Tone.Strong), new(Day(m.Deterministic)), new(Day(m.P10)), new(Day(m.P50)), new(Day(m.P80)), new(Day(m.P90)),
                }));
            ms.Blocks.Add(new TableBlock(t));
            doc.Sections.Add(ms);
        }

        if (input.Health is { } health)
        {
            void HealthSection(string title, string lead, IEnumerable<HealthItem> items)
            {
                var list = items.ToList();
                var (p, f, na) = health.Score(list);
                var sec = new Section { Title = title, Lead = $"{lead} {p} passed, {f} failed, {na} not applicable." };
                var t = new Table
                {
                    Header = Heads("Check", "Status", "Actual", "Target", "Count", "Notes"),
                    Widths = new[] { 1.9, 0.8, 0.7, 0.7, 0.9, 2.2 },
                    Aligns = new[] { Align.Left, Align.Left, Align.Right, Align.Right, Align.Right, Align.Left },
                };
                foreach (var i in list)
                {
                    string note = i.Status is HealthStatus.Fail or HealthStatus.FailInformational && i.Flagged.Count > 0 ? i.Examples(5) : i.Note ?? "";
                    if (i.StatusConventional is HealthStatus sc) note = $"Conventional reading: {HealthCheck.StatusText(sc)}. " + note;
                    t.Rows.Add(new Row(new Cell[]
                    {
                        new((i.Number != null ? i.Number + ". " : "") + i.Label, Tone: Tone.Strong),
                        new(i.StatusText, Tone: i.Status == HealthStatus.Pass ? Tone.Normal : i.Status == HealthStatus.NotApplicable ? Tone.Muted : Tone.Accent),
                        new(i.ActualText), new(i.TargetText), new(i.CountText), new(note, Tone: Tone.Muted),
                    }));
                }
                sec.Blocks.Add(new TableBlock(t));
                doc.Sections.Add(sec);
            }
            HealthSection("Schedule health: P6 Check Schedule", "The parameters of P6's Check Schedule dialog.", health.P6);
            HealthSection("Schedule health: DCMA 14-Point Assessment", "The DCMA 14-Point schedule assessment.", health.Dcma);
        }
        if (input.Verify is { Compared: > 0 } v)
        {
            var ec = new Section { Title = "Engine check against P6" };
            ec.Blocks.Add(new TextBlock($"{v.FieldsMatched.ToString("N0", Inv)} of {v.FieldsCompared.ToString("N0", Inv)} date and float fields match the "
                + $"values P6 stored in the file ({v.ActivitiesMatched.ToString("N0", Inv)} of {v.Compared.ToString("N0", Inv)} activities fully match).", Muted: false));
            doc.Sections.Add(ec);
        }
        return doc;
    }

    /// <summary>A summary table: label, then the pre-mitigation value (and post-mitigation, when there is one).</summary>
    private static Table Stats(string title, List<StatRow> rows, bool both, int pct)
    {
        var t = new Table
        {
            Header = both ? Heads(title, "Pre-mitigation", "Post-mitigation") : new[] { new Cell(title), new Cell("") },
            Widths = both ? new[] { 1.05, 1, 1 } : new[] { 1.2, 1 },
            Aligns = both ? new[] { Align.Left, Align.Right, Align.Right } : new[] { Align.Left, Align.Right },
            Share = both ? 1 : 0.64,
        };
        foreach (var r in rows)
        {
            bool chosen = r.Label == $"P{pct}" || r.Label == $"P{pct} − deterministic";
            var cells = new List<Cell> { new(r.Label, Tone: Tone.Muted) };
            if (both && r.Shared) cells.Add(new Cell(r.Pre, 2, Align: Align.Center));
            else
            {
                cells.Add(new Cell(r.Pre));
                if (both) cells.Add(new Cell(r.Post ?? ""));
            }
            t.Rows.Add(new Row(cells, chosen));
        }
        return t;
    }

    /// <summary>A short list (top drivers, critical activities): ID, name and value, the top five, and how many more.</summary>
    private static Table Named(string title, string valueHead, List<(string Id, string Name, string Value)> items, int count, string none)
    {
        const int top = 5;
        var t = new Table
        {
            Header = new[] { new Cell(count >= 0 ? $"{title} ({count.ToString("N0", Inv)})" : title, 2), new Cell(valueHead) },
            Widths = new[] { 0.62, 1.9, 0.72 },
            Aligns = new[] { Align.Left, Align.Left, Align.Right },
            Note = items.Count > top ? $"and {(items.Count - top).ToString("N0", Inv)} more" : null,
        };
        foreach (var (id, name, value) in items.Take(top))
            t.Rows.Add(new Row(new Cell[] { new(id, Tone: Tone.Muted), new(name, Tone: Tone.Strong), new(value) }));
        if (items.Count == 0) t.Rows.Add(new Row(new Cell[] { new(none, 3, Tone.Muted) }));
        return t;
    }

    private static Cell[] Heads(params string[] texts) => texts.Select(x => new Cell(x)).ToArray();

    private static string Day(long m) => m == Time.None ? "" : Time.FromMinutes(m).ToString("dd-MMM-yyyy", Inv);
    private static string N(double x, int digits) => x.ToString("F" + digits, Inv);
}
