using System.Globalization;
using ScheduleRisk.Core.Reporting.Export;
using static ScheduleRisk.Core.Reporting.Export.XlsxStyles;

namespace ScheduleRisk.Core.Risk.Register;

/// <summary>
/// The risk register as an Excel workbook (docs/RISK_REGISTER.md, "Excel workbook"): a Guide sheet, the risks one
/// per row, their actions, and the matrix. The app writes it (a filled register, or an empty template on the
/// register's matrix) and reads it back after it has been filled in, in the app or in Excel. Like the JSON file,
/// it holds the whole register and stays on the user's device.
/// </summary>
public static class RegisterWorkbook
{
    // Written with a fixed time, so that the same register always gives the same file.
    private static readonly DateTime Stamp = new(2000, 1, 1);
    private const int SpareRows = 200, SpareCategories = 30;
    private const int RiskHeader = 2, FirstRisk = 3, ActionHeader = 1, FirstAction = 2;

    private const string Id = "ID", Title = "Title", Cause = "Cause", Event = "Event", Effect = "Effect", Category = "Category",
        Type = "Type", Status = "Status", RaisedBy = "Raised by", Raised = "Raised", Owner = "Owner",
        Probability = "Probability", Rating = "Rating",
        Response = "Response", ResponseDescription = "Response description", ResponseCost = "Response cost",
        Activities = "Model activities", Always = "Always promote", Units = "Impact units",
        HandProbability = "Probability (by hand)", Distribution = "Impact distribution", ImpactMin = "Impact min",
        ImpactMostLikely = "Impact most likely", ImpactMax = "Impact max",
        HandMitigated = "Mitigated probability (by hand)", MitigatedDistribution = "Mitigated distribution",
        MitigatedMin = "Mitigated min", MitigatedMostLikely = "Mitigated most likely", MitigatedMax = "Mitigated max";

    private const string ActionRisk = "Risk ID", ActionText = "Action", ActionOwner = "Owner", ActionDue = "Due", ActionStatusColumn = "Status";

    private static readonly (AssessmentPoint Point, string Group)[] Points =
    {
        (AssessmentPoint.Inherent, "Inherent: before any controls"),
        (AssessmentPoint.Current, "Current: with today's controls"),
        (AssessmentPoint.Target, "Target: after the response"),
    };

    private const string SettingsBlock = "Settings", BandsBlock = "Probability bands", LevelsBlock = "Severity levels",
        GridBlock = "Rating grid", AreasBlock = "Severity areas", RangesBlock = "Severity ranges",
        GuidanceBlock = "Rating guidance", CategoriesBlock = "Categories";

    private static readonly string[] Blocks = { SettingsBlock, BandsBlock, LevelsBlock, GridBlock, AreasBlock, RangesBlock, GuidanceBlock, CategoriesBlock };

    private const string NameKey = "Register name", CurrencyKey = "Currency", DelayKey = "Cost of delay per working day",
        PromoteRatingKey = "Promote at or above rating", PromoteAreaKey = "Promote on severity area", PromoteLevelKey = "Promote at or above severity";

    // ================================================================== writing

    /// <summary>The register as a workbook: its matrix, every risk and action, and room for more.</summary>
    public static byte[] Write(RiskRegister reg)
    {
        var x = new XlsxWriter { Title = reg.Name };
        var guide = x.Add("Guide");
        var risks = x.Add("Risks");
        var actions = x.Add("Actions");
        var matrix = x.Add("Matrix");
        WriteMatrix(x, matrix, reg);
        int lastRisk = WriteRisks(x, risks, reg);
        WriteActions(x, actions, reg, lastRisk);
        WriteGuide(guide, reg);
        return x.Save(Stamp);
    }

    /// <summary>An empty register on a matrix, for filling in: the same workbook with no risks.</summary>
    public static byte[] Template(MatrixSettings matrix, string name) =>
        Write(new RiskRegister { Name = name, Matrix = matrix.Clone() });

    private static string Cap<T>(T e) where T : struct, Enum => e.ToString();

    private static double Serial(DateOnly d) => d.ToDateTime(TimeOnly.MinValue).ToOADate();

    /// <summary>A row height that shows wrapped text: about one line per column width of characters.</summary>
    private static double LinesHeight(IEnumerable<(string? Text, double Width)> cells, int maxLines = 8)
    {
        int lines = 1;
        foreach (var (text, width) in cells)
        {
            if (string.IsNullOrEmpty(text)) continue;
            int perLine = Math.Max(1, (int)(width * 1.1));
            int n = text.Split('\n').Sum(l => Math.Max(1, (l.Length + perLine - 1) / perLine));
            lines = Math.Max(lines, n);
        }
        return 13.5 * Math.Min(lines, maxLines) + 3;
    }

    /// <summary>A block's title in column A, with its note beside it: rows under the title belong to the block.</summary>
    private static void BlockTitle(XlsxSheetWriter s, int row, string title, string? note = null)
    {
        s.Text(row, 1, title, XlsxStyles.Block);
        if (note != null)
        {
            s.Text(row, 2, note, XlsxStyles.Note);
            s.Merge(row, 2, row, 5);
        }
        s.Height(row, note == null ? 20 : 28);
    }

    private static void Headers(XlsxSheetWriter s, int row, params string[] headers)
    {
        for (int c = 0; c < headers.Length; c++) s.Text(row, c + 1, headers[c], Header);
    }

    private static void WriteMatrix(XlsxWriter x, XlsxSheetWriter s, RiskRegister reg)
    {
        var m = reg.Matrix;
        int levels = m.SeverityLevels.Count;
        s.TabColor = "1A1918";
        s.Widths[1] = 18; s.Widths[2] = 28;
        for (int c = 3; c <= Math.Max(5, 3 + levels); c++) s.Widths[c] = 30;
        double W(int col) => s.Widths.TryGetValue(col, out double w) ? w : 9;

        s.Text(1, 1, "Risk matrix", XlsxStyles.Title);
        s.Height(1, 24);
        s.Text(2, 1, "The matrix the risks are rated on, as in step 01 Setup. Change it here or in the app. Keep each block's title "
            + "in column A and its header row as they are; a blank row ends a block.", XlsxStyles.Note);
        s.Merge(2, 1, 2, 5);
        s.Height(2, 28);
        int r = 4;

        // Settings
        BlockTitle(s, r++, SettingsBlock);
        Headers(s, r++, "Setting", "Value", "Notes");
        void Setting(string key, string? text, double? number, int style, string note)
        {
            s.Text(r, 1, key, Key);
            if (number != null) s.Number(r, 2, number, style); else s.Text(r, 2, text, style);
            s.Text(r, 3, note, XlsxStyles.Note);
            s.Height(r, LinesHeight(new (string?, double)[] { (note, W(3)), (text, W(2)) }));
            r++;
        }
        Setting(NameKey, reg.Name, null, Body, "Shown on the reports.");
        Setting(CurrencyKey, m.Currency, null, Body, "For response costs and the cost of delay, for example INR or USD.");
        Setting(DelayKey, null, m.CostOfDelayPerDay, Money, "Value of one working day of delay to the project finish, for the cost-benefit of responses. 0 = not set.");
        int promoteRating = r;
        Setting(PromoteRatingKey, Cap(m.Promote.MinRating), null, XlsxStyles.Rating, "Risks rated at or above this go on to the model at Promote: Red, Amber or Green.");
        int promoteArea = r;
        Setting(PromoteAreaKey, m.Promote.ScheduleDimension, null, Body, "The area that measures delay (its id in Severity areas).");
        int promoteLevel = r;
        Setting(PromoteLevelKey, MatrixSettings.Roman(m.Promote.MinScheduleSeverity), null, Body, "Only risks at or above this level on that area can delay the schedule.");
        s.List(XlsxSheetWriter.Ref(promoteRating, 2), "\"Red,Amber,Green\"", "Choose Red, Amber or Green.");
        string areaIds = string.Join(",", m.Dimensions.Select(d => d.Id));
        if (areaIds.Length < 250 && !areaIds.Contains('"')) s.List(XlsxSheetWriter.Ref(promoteArea, 2), $"\"{areaIds}\"", "Choose an area id from Severity areas.");
        s.List(XlsxSheetWriter.Ref(promoteLevel, 2), "SeverityLevels", "Choose a severity level.");
        s.ColourWhenEqual(XlsxSheetWriter.Ref(promoteRating, 2), ("Red", RedDxf), ("Amber", AmberDxf), ("Green", GreenDxf));
        r++;

        // Probability bands, least likely first
        BlockTitle(s, r++, BandsBlock, "Least likely first, without overlaps. From and To are percentages.");
        Headers(s, r++, "Letter", "Label", "From", "To", "Guidance");
        int firstBand = r;
        foreach (var b in m.Probability)
        {
            s.Text(r, 1, b.Letter, Key);
            s.Text(r, 2, b.Label, Body);
            s.Number(r, 3, b.Min, Percent);
            s.Number(r, 4, b.Max, Percent);
            s.Text(r, 5, b.Guidance, Body);
            s.Height(r, LinesHeight(new (string?, double)[] { (b.Guidance, W(5)), (b.Label, W(2)) }));
            r++;
        }
        int lastBand = Math.Max(firstBand, r - 1);
        x.Names.Add(("ProbabilityLetters", s.Abs(firstBand, 1, lastBand, 1), null, false));
        s.Decimal($"C{firstBand}:D{lastBand}", 0, 1, "A probability between 0% and 100%.");
        r++;

        // Severity levels
        BlockTitle(s, r++, LevelsBlock, "Numbered I, II, III... from the least severe. The Level column is renumbered when the app writes the file.");
        Headers(s, r++, "Level", "Name");
        int firstLevel = r;
        for (int i = 0; i < levels; i++)
        {
            s.Text(r, 1, MatrixSettings.Roman(i), Key);
            s.Text(r, 2, m.SeverityLevels[i], Body);
            r++;
        }
        x.Names.Add(("SeverityLevels", s.Abs(firstLevel, 1, Math.Max(firstLevel, r - 1), 1), null, false));
        r++;

        // Rating grid: the most likely band at the top, as the heat map shows it
        BlockTitle(s, r++, GridBlock, "Rows: probability, most likely at the top. Columns: severity. Each cell is rated Red, Amber or Green.");
        s.Text(r, 1, "Probability", Header);
        s.Text(r, 2, "Label", Header);
        for (int i = 0; i < levels; i++) s.Text(r, 3 + i, $"{MatrixSettings.Roman(i)} {m.SeverityLevels[i]}", Header);
        r++;
        int firstGrid = r;
        for (int p = m.Probability.Count - 1; p >= 0; p--)
        {
            s.Text(r, 1, m.Probability[p].Letter, Key);
            s.Text(r, 2, m.Probability[p].Label, Body);
            for (int i = 0; i < levels; i++)
                s.Text(r, 3 + i, p < m.Ratings.Count && i < m.Ratings[p].Length ? Cap(m.Ratings[p][i]) : "", XlsxStyles.Rating);
            s.Height(r, 22);
            r++;
        }
        int lastGrid = Math.Max(firstGrid, r - 1), lastGridCol = 3 + Math.Max(levels, 1) - 1;
        x.Names.Add(("RiskGrid", s.Abs(firstGrid, 3, lastGrid, lastGridCol), null, false));
        x.Names.Add(("RiskGridLetters", s.Abs(firstGrid, 1, lastGrid, 1), null, false));
        string grid = $"C{firstGrid}:{XlsxSheetWriter.Col(lastGridCol)}{lastGrid}";
        s.List(grid, "\"Red,Amber,Green\"", "Rate each cell Red, Amber or Green.");
        s.ColourWhenEqual(grid, ("Red", RedDxf), ("Amber", AmberDxf), ("Green", GreenDxf));
        r++;

        // Severity areas and their bands
        BlockTitle(s, r++, AreasBlock, "An area with a Unit is measured: give its ranges under Severity ranges. Without a Unit it is described in words.");
        s.Text(r, 1, "Id", Header);
        s.Text(r, 2, "Name", Header);
        s.Text(r, 3, "Unit", Header);
        for (int i = 0; i < levels; i++) s.Text(r, 4 + i, MatrixSettings.Roman(i), Header);
        r++;
        foreach (var d in m.Dimensions)
        {
            s.Text(r, 1, d.Id, Key);
            s.Text(r, 2, d.Name, Body);
            s.Text(r, 3, d.Unit, Body);
            for (int i = 0; i < levels; i++) s.Text(r, 4 + i, i < d.Bands.Count ? d.Bands[i].Text : "", Body);
            s.Height(r, LinesHeight(d.Bands.Take(levels).Select((b, i) => ((string?)b.Text, W(4 + i))).Append((d.Unit, W(3)))));
            r++;
        }
        r++;

        // Severity ranges of the measured areas
        BlockTitle(s, r++, RangesBlock, "In the area's unit. Leave To empty on the highest level for \"and above\".");
        Headers(s, r++, "Area", "Level", "From", "To");
        int firstRange = r;
        foreach (var d in m.Dimensions.Where(d => d.Quantitative))
            for (int i = 0; i < d.Bands.Count; i++)
            {
                s.Text(r, 1, d.Id, Key);
                s.Text(r, 2, MatrixSettings.Roman(i), Body);
                s.Number(r, 3, d.Bands[i].Min, Number);
                s.Number(r, 4, d.Bands[i].Max, Number);
                r++;
            }
        if (r > firstRange) s.Decimal($"C{firstRange}:D{r - 1}", 0, null, "A number in the area's unit.");
        r++;

        // Guidance
        BlockTitle(s, r++, GuidanceBlock);
        Headers(s, r++, "Rating", "Label", "Actions (one per line)");
        int firstGuidance = r;
        foreach (var g in m.Guidance)
        {
            s.Text(r, 1, Cap(g.Rating), XlsxStyles.Rating);
            s.Text(r, 2, g.Label, Body);
            string actions = string.Join("\n", g.Actions);
            s.Text(r, 3, actions, Body);
            s.Height(r, LinesHeight(new (string?, double)[] { (actions, W(3)) }));
            r++;
        }
        if (r > firstGuidance)
            s.ColourWhenEqual($"A{firstGuidance}:A{r - 1}", ("Red", RedDxf), ("Amber", AmberDxf), ("Green", GreenDxf));
        r++;

        // Categories: the last block, with room below for more
        BlockTitle(s, r++, CategoriesBlock);
        Headers(s, r++, "Category");
        int firstCategory = r;
        foreach (var c in m.Categories) s.Text(r++, 1, c, Body);
        x.Names.Add(("Categories", s.Abs(firstCategory, 1, r - 1 + SpareCategories, 1), null, false));
        for (int i = 0; i < SpareCategories; i++) s.Style(r + i, 1, Body);
    }

    private sealed record RiskColumn(string Header, string Group, double Width, int Style);

    private static List<RiskColumn> RiskColumns(MatrixSettings m)
    {
        var cols = new List<RiskColumn>
        {
            new(Id, "Risk", 8, Key), new(Title, "Risk", 28, Body), new(Cause, "Risk", 30, Body), new(Event, "Risk", 34, Body),
            new(Effect, "Risk", 30, Body), new(Category, "Risk", 16, Body), new(Type, "Risk", 12, Body), new(Status, "Risk", 11, Body),
            new(RaisedBy, "Risk", 14, Body), new(Raised, "Risk", 12, Date), new(Owner, "Risk", 16, Body),
        };
        foreach (var (_, group) in Points)
        {
            cols.Add(new(Probability, group, 11, Body));
            foreach (var d in m.Dimensions) cols.Add(new(d.Name, group, Math.Max(9, Math.Min(16, d.Name.Length + 2)), Body));
            cols.Add(new(Rating, group, 9, Formula));
        }
        cols.AddRange(new RiskColumn[]
        {
            new(Response, "Response", 12, Body), new(ResponseDescription, "Response", 34, Body), new(ResponseCost, "Response", 14, Money),
            new(Activities, "Model link (optional): how the risk goes into the model at Promote", 18, Body),
            new(Always, "Model link (optional): how the risk goes into the model at Promote", 10, Body),
            new(Units, "Model link (optional): how the risk goes into the model at Promote", 10, Body),
            new(HandProbability, "Model link (optional): how the risk goes into the model at Promote", 12, Percent),
            new(Distribution, "Model link (optional): how the risk goes into the model at Promote", 12, Body),
            new(ImpactMin, "Model link (optional): how the risk goes into the model at Promote", 9, Number),
            new(ImpactMostLikely, "Model link (optional): how the risk goes into the model at Promote", 10, Number),
            new(ImpactMax, "Model link (optional): how the risk goes into the model at Promote", 9, Number),
            new(HandMitigated, "Model link (optional): how the risk goes into the model at Promote", 12, Percent),
            new(MitigatedDistribution, "Model link (optional): how the risk goes into the model at Promote", 12, Body),
            new(MitigatedMin, "Model link (optional): how the risk goes into the model at Promote", 9, Number),
            new(MitigatedMostLikely, "Model link (optional): how the risk goes into the model at Promote", 10, Number),
            new(MitigatedMax, "Model link (optional): how the risk goes into the model at Promote", 9, Number),
        });
        return cols;
    }

    /// <summary>Writes the Risks sheet and returns its last row with room for risks (data and spare rows).</summary>
    private static int WriteRisks(XlsxWriter x, XlsxSheetWriter s, RiskRegister reg)
    {
        var m = reg.Matrix;
        var cols = RiskColumns(m);
        int last = FirstRisk + reg.Risks.Count + SpareRows - 1;
        s.TabColor = "EC3013";
        s.FreezeRows = RiskHeader;
        s.FreezeColumns = 2;

        // Group headers over their columns, then the column headers.
        for (int c = 0; c < cols.Count;)
        {
            int end = c;
            while (end + 1 < cols.Count && cols[end + 1].Group == cols[c].Group) end++;
            s.Text(1, c + 1, cols[c].Group, Group);
            for (int k = c + 1; k <= end; k++) s.Style(1, k + 1, Group);
            if (end > c) s.Merge(1, c + 1, 1, end + 1);
            c = end + 1;
        }
        for (int c = 0; c < cols.Count; c++)
        {
            s.Text(RiskHeader, c + 1, cols[c].Header, Header);
            s.Widths[c + 1] = cols[c].Width;
        }
        s.Height(1, 20);
        s.Height(RiskHeader, 30);
        s.AutoFilter = $"A{RiskHeader}:{XlsxSheetWriter.Col(cols.Count)}{last}";
        x.Names.Add(("_xlnm._FilterDatabase", s.Abs(RiskHeader, 1, last, cols.Count), 1, true));
        x.Names.Add(("RiskIds", s.Abs(FirstRisk, 1, last, 1), null, false));

        int At(string header, string group = "") => cols.FindIndex(c => c.Header == header && (group.Length == 0 || c.Group == group)) + 1;
        string Range(int col) => $"{XlsxSheetWriter.Col(col)}{FirstRisk}:{XlsxSheetWriter.Col(col)}{last}";

        // Where each assessment's columns are, for the rating formula.
        var pointCols = Points.Select(p => (p.Point, Prob: At(Probability, p.Group),
            Areas: m.Dimensions.Select(d => At(d.Name, p.Group)).ToList(), Rating: At(Rating, p.Group))).ToList();

        for (int row = FirstRisk; row <= last; row++)
        {
            int i = row - FirstRisk;
            var r = i < reg.Risks.Count ? reg.Risks[i] : null;
            for (int c = 0; c < cols.Count; c++) s.Style(row, c + 1, cols[c].Style);
            foreach (var pc in pointCols)
            {
                string terms = string.Join(",", pc.Areas.Select(c => $"IFERROR(MATCH({XlsxSheetWriter.Col(c)}{row},SeverityLevels,0),0)"));
                string p = $"{XlsxSheetWriter.Col(pc.Prob)}{row}";
                string f = pc.Areas.Count == 0 ? "\"\""
                    : $"IFERROR(IF(OR({p}=\"\",MAX({terms})=0),\"\",INDEX(RiskGrid,MATCH({p},RiskGridLetters,0),MAX({terms}))),\"\")";
                s.Formula(row, pc.Rating, f, r != null && m.Rate(r.At(pc.Point)) is RiskRating rating ? Cap(rating) : "", Formula);
            }
            if (r == null) continue;

            s.Text(row, At(Id), r.Id, Key);
            s.Text(row, At(Title), r.Title, Body);
            s.Text(row, At(Cause), r.Cause, Body);
            s.Text(row, At(Event), r.Event, Body);
            s.Text(row, At(Effect), r.Effect, Body);
            s.Text(row, At(Category), r.Category, Body);
            s.Text(row, At(Type), Cap(r.Kind), Body);
            s.Text(row, At(Status), Cap(r.Status), Body);
            s.Text(row, At(RaisedBy), r.RaisedBy, Body);
            s.Number(row, At(Raised), r.Raised is DateOnly d ? Serial(d) : null, Date);
            s.Text(row, At(Owner), r.Owner, Body);
            foreach (var pc in pointCols)
            {
                var a = r.At(pc.Point);
                if (a.Probability is int p) s.Text(row, pc.Prob, p >= 0 && p < m.Probability.Count ? m.Probability[p].Letter : "", Body);
                for (int k = 0; k < m.Dimensions.Count; k++)
                    if (a.Severity.TryGetValue(m.Dimensions[k].Id, out int level)) s.Text(row, pc.Areas[k], MatrixSettings.Roman(level), Body);
            }
            s.Text(row, At(Response), Cap(r.Response), Body);
            s.Text(row, At(ResponseDescription), r.ResponseDescription, Body);
            s.Number(row, At(ResponseCost), r.ResponseCost, Money);
            var pr = r.Promotion;
            if (!pr.IsEmpty)
            {
                s.Text(row, At(Activities), string.Join(", ", pr.Activities), Body);
                s.Text(row, At(Always), pr.Always ? "Yes" : "", Body);
                s.Text(row, At(Units), pr.ImpactUnits, Body);
                s.Number(row, At(HandProbability), pr.Probability, Percent);
                WriteDist(s, row, pr.Impact, At(Distribution), At(ImpactMin), At(ImpactMostLikely), At(ImpactMax));
                s.Number(row, At(HandMitigated), pr.MitigatedProbability, Percent);
                WriteDist(s, row, pr.MitigatedImpact, At(MitigatedDistribution), At(MitigatedMin), At(MitigatedMostLikely), At(MitigatedMax));
            }
            double Width(string h) => cols[At(h) - 1].Width;
            s.Height(row, LinesHeight(new (string?, double)[]
            {
                (r.Title, Width(Title)), (r.Cause, Width(Cause)), (r.Event, Width(Event)), (r.Effect, Width(Effect)),
                (r.ResponseDescription, Width(ResponseDescription)),
            }, 6));
        }

        s.List(Range(At(Category)), "Categories", "Choose a category from the Matrix sheet, or add one there.");
        s.List(Range(At(Type)), "\"Threat,Opportunity\"", "Threat or Opportunity.");
        s.List(Range(At(Status)), "\"Proposed,Approved,Rejected,Closed\"", "Proposed, Approved, Rejected or Closed.");
        foreach (var pc in pointCols)
        {
            s.List(Range(pc.Prob), "ProbabilityLetters", "Choose a probability band letter from the Matrix sheet.");
            if (pc.Areas.Count > 0)
                s.List(string.Join(" ", pc.Areas.Select(Range)), "SeverityLevels", "Choose a severity level (I, II, III...), or leave it empty when the area does not apply.");
        }
        s.List(Range(At(Response)), "\"None,Avoid,Transfer,Mitigate,Accept,Exploit,Share,Enhance\"",
            "Threats: Avoid, Transfer, Mitigate or Accept. Opportunities: Exploit, Share, Enhance or Accept.");
        s.Decimal(Range(At(ResponseCost)), 0, null, "A cost of zero or more.");
        s.List(Range(At(Always)), "\"Yes,No\"", "Yes to promote the risk even below the promote rule.");
        s.List(Range(At(Units)), "\"days,percent\"", "days (working days) or percent (of each activity's remaining duration).");
        s.List($"{Range(At(Distribution))} {Range(At(MitigatedDistribution))}", "\"triangle,pert,uniform\"", "triangle, pert or uniform.");
        s.Decimal($"{Range(At(HandProbability))} {Range(At(HandMitigated))}", 0, 1, "A probability between 0% and 100%.");
        s.ColourWhenEqual(string.Join(" ", pointCols.Select(pc => Range(pc.Rating))), ("Red", RedDxf), ("Amber", AmberDxf), ("Green", GreenDxf));
        return last;
    }

    private static void WriteDist(XlsxSheetWriter s, int row, DistSpec? d, int kind, int min, int ml, int max)
    {
        if (d == null) return;
        bool uniform = string.Equals(d.Distribution, "uniform", StringComparison.OrdinalIgnoreCase);
        s.Text(row, kind, d.Distribution, Body);
        s.Number(row, min, d.Min, Number);
        s.Number(row, ml, uniform ? d.Min : d.MostLikely, Number);
        s.Number(row, max, d.Max, Number);
    }

    private static void WriteActions(XlsxWriter x, XlsxSheetWriter s, RiskRegister reg, int lastRisk)
    {
        s.TabColor = "6B6660";
        s.FreezeRows = ActionHeader;
        Headers(s, ActionHeader, ActionRisk, ActionText, ActionOwner, ActionDue, ActionStatusColumn);
        s.Height(ActionHeader, 22);
        double[] widths = { 10, 60, 20, 13, 12 };
        for (int c = 0; c < widths.Length; c++) s.Widths[c + 1] = widths[c];
        int row = FirstAction;
        foreach (var r in reg.Risks)
            foreach (var a in r.Actions)
            {
                s.Text(row, 1, r.Id, Key);
                s.Text(row, 2, a.Text, Body);
                s.Text(row, 3, a.Owner, Body);
                s.Number(row, 4, a.Due is DateOnly d ? Serial(d) : null, Date);
                s.Text(row, 5, Cap(a.Status), Body);
                s.Height(row, LinesHeight(new (string?, double)[] { (a.Text, widths[1]) }, 4));
                row++;
            }
        int last = row + SpareRows - 1;
        int[] styles = { Key, Body, Body, Date, Body };
        for (int rr = FirstAction; rr <= last; rr++)
            for (int c = 0; c < styles.Length; c++) s.Style(rr, c + 1, styles[c]);
        s.AutoFilter = $"A{ActionHeader}:E{last}";
        x.Names.Add(("_xlnm._FilterDatabase", s.Abs(ActionHeader, 1, last, 5), 2, true));
        s.List($"A{FirstAction}:A{last}", "RiskIds", "Choose the ID of a risk on the Risks sheet.");
        s.List($"E{FirstAction}:E{last}", "\"Open,Done,Cancelled\"", "Open, Done or Cancelled.");
    }

    private static void WriteGuide(XlsxSheetWriter s, RiskRegister reg)
    {
        s.TabColor = "EC3013";
        s.Widths[1] = 30;
        s.Widths[2] = 100;
        int r = 1;
        s.Text(r, 1, "Risk register", XlsxStyles.Title);
        s.Height(r++, 26);
        s.Text(r, 1, $"{reg.Name}: the qualitative risk register of Project Risk Analysis as a workbook. Fill it in here or in the app, "
            + "then open it in the app with Open... in step 01 Setup or 02 Identify.", Subtitle);
        s.Merge(r, 1, r, 2);
        s.Height(r++, 32);
        r++;

        void Para(string key, string text)
        {
            s.Text(r, 1, key, Key);
            s.Text(r, 2, text, Body);
            s.Height(r, LinesHeight(new (string?, double)[] { (text, 100.0), (key, 30.0) }));
            r++;
        }

        BlockTitle(s, r++, "How to use it");
        Para("1. Matrix", "Check the matrix first: the probability bands, the severity levels and areas, and the Red, Amber and Green rating grid. "
            + "The dropdowns on the Risks sheet come from it.");
        Para("2. Risks", "One row per risk. Write it as cause, event and effect. Leave the ID empty and the app gives the risk the next free ID (R01, R02...).");
        Para("3. Assessments", "For each of Inherent, Current and Target, choose the probability letter and a severity level on each area that applies; "
            + "leave an area empty when it does not. The Rating column works out Red, Amber or Green from the Matrix sheet.");
        Para("4. Actions", "One row per action on the Actions sheet, with the ID of its risk.");
        Para("5. Open in the app", "Save as .xlsx, then Open... in step 01 Setup or 02 Identify. Opening replaces the register in the app. "
            + "The app reads the file in your browser: nothing is uploaded.");
        r++;

        BlockTitle(s, r++, "What the app checks");
        Para("Values", "Type, Status, Response and the Actions' Status must be one of the dropdown's values (any capitals). Probability letters must be "
            + "bands of the matrix and severities its levels. Dates are date cells, or text as yyyy-mm-dd. A value it cannot read stops the file "
            + "opening, with the sheet, row and column named.");
        Para("Approval", "An approved risk needs a title and an event. Approved risks are closed, not deleted, so the register keeps its history.");
        Para("Promote", "Approved risks at or above the promote rule on the Matrix sheet go on to the model at Promote, with the Model link columns "
            + "(activities, and any probability or impact set by hand).");
        Para("Ratings", "The Rating columns are worked out by Excel for your convenience; the app works every rating out again from the matrix.");
        r++;

        BlockTitle(s, r++, "Risks sheet");
        Para("ID", "The risk's ID, unique in the register. Empty: the next free ID.");
        Para("Title, Cause, Event, Effect", "\"Because of (cause), (event), which would lead to (effect).\" Title and Event are needed to approve the risk.");
        Para("Category", "One of the categories on the Matrix sheet.");
        Para("Type", "Threat or Opportunity.");
        Para("Status", "Proposed, Approved, Rejected or Closed. Empty: Proposed.");
        Para("Raised by, Raised, Owner", "Who raised the risk and when, and who owns it.");
        Para("Probability", "Under Inherent, Current and Target: the probability band's letter.");
        Para("Severity areas", "One column per area of the matrix: the severity level (I, II...) on that area, or empty. The risk's severity is its worst area.");
        Para("Response", "Threats: Avoid, Transfer, Mitigate or Accept. Opportunities: Exploit, Share, Enhance or Accept. With its description and cost.");
        Para("Model activities", "The P6 activity IDs the risk would delay, separated by commas.");
        Para("Always promote", "Yes to promote the risk to the model even below the promote rule (it must still be approved).");
        Para("Impact units", "days (working days) or percent (of each activity's remaining duration), for impacts set by hand.");
        Para("By hand", "Probability, impact distribution (triangle, pert or uniform) and min, most likely and max, before and after mitigation, "
            + "to replace what Promote works out from the matrix. Leave them empty to use the matrix.");
        r++;

        BlockTitle(s, r++, "Actions sheet");
        Para("Risk ID, Action", "The risk the action belongs to, and what is to be done.");
        Para("Owner, Due, Status", "Who does it, by when (a date), and Open, Done or Cancelled. An open action past its due date is overdue.");
    }

    // ================================================================== reading

    /// <summary>
    /// Reads a register workbook: the Risks sheet (required), and the Actions and Matrix sheets when present. Without a
    /// Matrix sheet, or a block of it, the default matrix is used for that part. Columns are found by their header, so
    /// they can be moved and others added; empty rows are skipped. A value that cannot be read is refused with a
    /// FormatException naming its sheet, row and column.
    /// </summary>
    public static RiskRegister Read(byte[] data)
    {
        var wb = XlsxReader.Open(data);
        var reg = new RiskRegister();
        if (wb.Sheet("Matrix") is XlsxSheet matrix) ReadMatrix(matrix, reg);
        var risks = wb.Sheet("Risks") ?? throw new FormatException(
            $"The workbook has no Risks sheet (it has {string.Join(", ", wb.SheetNames)}). Start from the template the app writes.");
        ReadRisks(risks, reg);
        if (wb.Sheet("Actions") is XlsxSheet actions) ReadActions(actions, reg);
        return reg;
    }

    private static string Norm(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static FormatException Bad(XlsxSheet s, int row, string column, string value, string problem) =>
        new($"{s.Name}, row {row}, column {column}: \"{value}\" {problem}");

    private static T ParseEnum<T>(XlsxSheet s, int row, string column, string? text, T dflt) where T : struct, Enum
    {
        if (text == null) return dflt;
        return System.Enum.TryParse<T>(text.Trim(), ignoreCase: true, out var v) && System.Enum.IsDefined(v) && !int.TryParse(text, out _) ? v
            : throw Bad(s, row, column, text, $"is not one of: {string.Join(", ", System.Enum.GetNames<T>())}.");
    }

    private static double? ParseNumber(XlsxSheet s, int row, int col, string column)
    {
        var c = s.Cell(row, col);
        if (c?.Number is double n) return n;
        string? t = s.Text(row, col);
        if (t == null) return null;
        return double.TryParse(t, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double d) ? d
            : throw Bad(s, row, column, t, "is not a number.");
    }

    /// <summary>A probability as a fraction: 0.3, 30% and 30 all read as 0.3.</summary>
    private static double? ParseFraction(XlsxSheet s, int row, int col, string column)
    {
        var c = s.Cell(row, col);
        string? t = s.Text(row, col);
        double v;
        if (c?.Number is double n) v = n;
        else if (t == null) return null;
        else if (t.EndsWith('%') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)) return pct / 100;
        else if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) throw Bad(s, row, column, t, "is not a probability (use 30% or 0.3).");
        return v > 1 ? v / 100 : v;
    }

    private static readonly string[] DateFormats = { "yyyy-MM-dd", "yyyy/MM/dd", "d-MMM-yyyy", "dd-MMM-yyyy", "d MMM yyyy", "dd MMM yyyy", "d-MMM-yy", "dd-MMM-yy" };

    private static DateOnly? ParseDate(XlsxSheet s, int row, int col, string column)
    {
        var c = s.Cell(row, col);
        if (c?.Number is double n)
            return n is >= 1 and < 2958466 ? DateOnly.FromDateTime(DateTime.FromOADate(Math.Floor(n)))
                : throw Bad(s, row, column, c.Value, "is not a date.");
        string? t = s.Text(row, col);
        if (t == null) return null;
        return DateOnly.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d
            : throw Bad(s, row, column, t, "is not a date (use a date cell, or text as yyyy-mm-dd).");
    }

    // ------------------------------------------------------------------ the Matrix sheet

    /// <summary>The rows of a block: from below its header row to the first empty row or the next block's title.
    /// Null when the sheet has no such block.</summary>
    private static List<int>? BlockRows(XlsxSheet s, string title)
    {
        int at = Enumerable.Range(1, s.MaxRow).FirstOrDefault(r => s.Text(r, 1) is string t && Norm(t) == Norm(title));
        if (at == 0) return null;
        var rows = new List<int>();
        for (int r = at + 2; r <= s.MaxRow; r++)
        {
            if (Enumerable.Range(1, s.MaxColumn).All(c => s.Text(r, c) == null)) break;
            if (s.Text(r, 1) is string t && Blocks.Any(b => Norm(b) == Norm(t))) break;
            rows.Add(r);
        }
        return rows;
    }

    private static RiskRating ParseRating(XlsxSheet s, int row, int col, string column)
    {
        string? t = s.Text(row, col);
        return Norm(t ?? "") switch
        {
            "red" or "r" => RiskRating.Red,
            "amber" or "a" => RiskRating.Amber,
            "green" or "g" => RiskRating.Green,
            _ => throw Bad(s, row, column, t ?? "", "is not a rating: use Red, Amber or Green."),
        };
    }

    private static int ParseLevel(XlsxSheet s, int row, int col, string column)
    {
        string t = s.Text(row, col) ?? "";
        int level = MatrixSettings.ParseRoman(t.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
        return level >= 0 ? level : throw Bad(s, row, column, t, "is not a severity level (use I, II, III...).");
    }

    private static void ReadMatrix(XlsxSheet s, RiskRegister reg)
    {
        var dflt = MatrixSettings.Default();
        var m = new MatrixSettings();

        if (BlockRows(s, BandsBlock) is { } bands)
            foreach (int r in bands)
                m.Probability.Add(new ProbabilityBand
                {
                    Letter = s.Text(r, 1) ?? "", Label = s.Text(r, 2) ?? "",
                    Min = ParseFraction(s, r, 3, "From") ?? 0, Max = ParseFraction(s, r, 4, "To") ?? 0,
                    Guidance = s.Text(r, 5) ?? "",
                });
        else m.Probability = dflt.Probability;

        m.SeverityLevels = BlockRows(s, LevelsBlock) is { } levels ? levels.Select(r => s.Text(r, 2) ?? "").ToList() : dflt.SeverityLevels;
        int ns = m.SeverityLevels.Count;

        if (BlockRows(s, AreasBlock) is { } areas)
            foreach (int r in areas)
            {
                string id = s.Text(r, 1) ?? throw Bad(s, r, "Id", "", "is empty: every severity area needs an id.");
                var d = new SeverityDimension { Id = id, Name = s.Text(r, 2) ?? id, Unit = s.Text(r, 3) };
                for (int i = 0; i < ns; i++) d.Bands.Add(new SeverityBand { Text = s.Text(r, 4 + i) ?? "" });
                m.Dimensions.Add(d);
            }
        else m.Dimensions = dflt.Dimensions;

        SeverityDimension? Area(string text) =>
            m.Dimensions.FirstOrDefault(d => string.Equals(d.Id, text, StringComparison.OrdinalIgnoreCase))
            ?? m.Dimensions.FirstOrDefault(d => string.Equals(d.Name, text, StringComparison.OrdinalIgnoreCase));

        if (BlockRows(s, RangesBlock) is { } ranges)
            foreach (int r in ranges)
            {
                string text = s.Text(r, 1) ?? "";
                var d = Area(text) ?? throw Bad(s, r, "Area", text, "is not an area under Severity areas.");
                int level = ParseLevel(s, r, 2, "Level");
                if (level >= d.Bands.Count) throw Bad(s, r, "Level", s.Text(r, 2) ?? "", "is not a level of the matrix.");
                d.Bands[level].Min = ParseNumber(s, r, 3, "From");
                d.Bands[level].Max = ParseNumber(s, r, 4, "To");
            }

        if (BlockRows(s, GridBlock) is { } grid)
            foreach (var band in m.Probability)
            {
                int r = grid.FirstOrDefault(r => string.Equals(s.Text(r, 1), band.Letter, StringComparison.OrdinalIgnoreCase));
                if (r == 0) throw new FormatException($"{s.Name}: the {GridBlock} has no row for probability {band.Letter}.");
                m.Ratings.Add(Enumerable.Range(0, ns).Select(i => ParseRating(s, r, 3 + i, MatrixSettings.Roman(i))).ToArray());
            }
        else m.Ratings = dflt.Ratings;

        if (BlockRows(s, GuidanceBlock) is { } guidance)
            foreach (int r in guidance)
                m.Guidance.Add(new RatingGuidance
                {
                    Rating = ParseRating(s, r, 1, "Rating"), Label = s.Text(r, 2) ?? "",
                    Actions = (s.Text(r, 3) ?? "").Split('\n').Select(a => a.Trim()).Where(a => a.Length > 0).ToList(),
                });
        else m.Guidance = dflt.Guidance;

        m.Categories = BlockRows(s, CategoriesBlock) is { } cats ? cats.Select(r => s.Text(r, 1)).OfType<string>().ToList() : dflt.Categories;

        if (BlockRows(s, SettingsBlock) is { } settings)
            foreach (int r in settings)
            {
                string key = Norm(s.Text(r, 1) ?? "");
                string? value = s.Text(r, 2);
                string column = s.Text(r, 1) ?? "";
                if (key == Norm(NameKey)) reg.Name = value ?? reg.Name;
                else if (key == Norm(CurrencyKey)) m.Currency = value ?? "";
                else if (key.StartsWith("cost of delay", StringComparison.Ordinal)) m.CostOfDelayPerDay = ParseNumber(s, r, 2, column) ?? 0;
                else if (key == Norm(PromoteRatingKey)) m.Promote.MinRating = value == null ? RiskRating.Amber : ParseRating(s, r, 2, column);
                else if (key == Norm(PromoteAreaKey)) m.Promote.ScheduleDimension = value == null ? "schedule" : Area(value)?.Id ?? value;
                else if (key == Norm(PromoteLevelKey)) m.Promote.MinScheduleSeverity = value == null ? 1 : ParseLevel(s, r, 2, column);
            }
        reg.Matrix = m;
    }

    // ------------------------------------------------------------------ the Risks sheet

    private static int HeaderRow(XlsxSheet s, string first) =>
        Enumerable.Range(1, Math.Min(10, s.MaxRow)).FirstOrDefault(r => Enumerable.Range(1, s.MaxColumn).Any(c => s.Text(r, c) is string t && Norm(t) == Norm(first)));

    private static void ReadRisks(XlsxSheet s, RiskRegister reg)
    {
        var m = reg.Matrix;
        int header = HeaderRow(s, Id);
        if (header == 0) throw new FormatException($"{s.Name}: no header row with an {Id} column in the first 10 rows. Start from the template the app writes.");

        // Plain columns by header; assessment columns by their group (row above) or a prefix such as "Current schedule".
        var plain = new Dictionary<string, (int Col, string Header)>();
        var prob = new Dictionary<AssessmentPoint, (int Col, string Header)>();
        var sev = Points.ToDictionary(p => p.Point, _ => new List<(SeverityDimension Area, int Col, string Header)>());
        string group = "";
        for (int c = 1; c <= s.MaxColumn; c++)
        {
            if (header > 1 && s.Text(header - 1, c) is string g) group = g;
            if (s.Text(header, c) is not string h) continue;
            string n = Norm(h);
            AssessmentPoint? point = null;
            foreach (var p in Points)
            {
                string word = p.Point.ToString().ToLowerInvariant();
                if (n.StartsWith(word + " ", StringComparison.Ordinal)) { point = p.Point; n = n[(word.Length + 1)..]; break; }
                if (Norm(group).StartsWith(word, StringComparison.Ordinal)) point = p.Point;
            }
            if (point is AssessmentPoint pt)
            {
                if (n == Norm(Probability)) prob.TryAdd(pt, (c, h));
                else if (m.Dimensions.FirstOrDefault(d => Norm(d.Name) == n || Norm(d.Id) == n) is { } area && sev[pt].All(x => x.Area != area))
                    sev[pt].Add((area, c, h));
                continue;                                       // Rating and anything else under an assessment is ignored
            }
            plain.TryAdd(n, (c, h));
        }
        var used = plain.Values.Select(v => v.Col).Concat(prob.Values.Select(v => v.Col)).Concat(sev.Values.SelectMany(l => l.Select(x => x.Col))).ToList();

        for (int r = header + 1; r <= s.MaxRow; r++)
        {
            if (used.All(c => s.Text(r, c) == null)) continue;
            string? T(string h) => plain.TryGetValue(Norm(h), out var col) ? s.Text(r, col.Col) : null;
            string Head(string h) => plain.TryGetValue(Norm(h), out var col) ? col.Header : h;
            int Col(string h) => plain.TryGetValue(Norm(h), out var col) ? col.Col : 0;
            double? Num(string h) => Col(h) > 0 ? ParseNumber(s, r, Col(h), Head(h)) : null;
            double? Frac(string h) => Col(h) > 0 ? ParseFraction(s, r, Col(h), Head(h)) : null;

            var risk = new RegisterRisk
            {
                Id = T(Id) ?? "", Title = T(Title) ?? "", Cause = T(Cause) ?? "", Event = T(Event) ?? "", Effect = T(Effect) ?? "",
                Category = T(Category) ?? "", Kind = ParseEnum(s, r, Head(Type), T(Type), RiskKind.Threat),
                Status = ParseEnum(s, r, Head(Status), T(Status), RiskStatus.Proposed),
                RaisedBy = T(RaisedBy) ?? "", Raised = Col(Raised) > 0 ? ParseDate(s, r, Col(Raised), Head(Raised)) : null, Owner = T(Owner) ?? "",
                Response = ParseEnum(s, r, Head(Response), T(Response), ResponseStrategy.None),
                ResponseDescription = T(ResponseDescription) ?? "", ResponseCost = Num(ResponseCost),
            };
            foreach (var (point, _) in Points)
            {
                var a = new Assessment();
                if (prob.TryGetValue(point, out var pc) && s.Text(r, pc.Col) is string letter)
                {
                    int i = m.ProbabilityIndex(letter);
                    a.Probability = i >= 0 ? i : throw Bad(s, r, $"{pc.Header} ({point})", letter, "is not a probability band of the matrix.");
                }
                foreach (var (area, col, h) in sev[point])
                    if (s.Text(r, col) != null) a.Severity[area.Id] = ParseLevel(s, r, col, $"{h} ({point})");
                switch (point)
                {
                    case AssessmentPoint.Inherent: risk.Inherent = a; break;
                    case AssessmentPoint.Current: risk.Current = a; break;
                    default: risk.Target = a; break;
                }
            }

            string? always = T(Always);
            string? units = T(Units);
            risk.Promotion = new Promotion
            {
                Activities = (T(Activities) ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                Always = Norm(always ?? "") switch
                {
                    "" or "no" or "n" or "false" or "0" => false,
                    "yes" or "y" or "true" or "1" or "x" => true,
                    _ => throw Bad(s, r, Head(Always), always!, "is not Yes or No."),
                },
                ImpactUnits = Norm(units ?? "") switch
                {
                    "" or "days" or "day" or "working days" => "days",
                    "percent" or "%" or "per cent" => "percent",
                    _ => throw Bad(s, r, Head(Units), units!, "is not days or percent."),
                },
                Probability = Frac(HandProbability),
                Impact = Dist(Distribution, ImpactMin, ImpactMostLikely, ImpactMax),
                MitigatedProbability = Frac(HandMitigated),
                MitigatedImpact = Dist(MitigatedDistribution, MitigatedMin, MitigatedMostLikely, MitigatedMax),
            };
            reg.Risks.Add(risk);

            DistSpec? Dist(string kind, string min, string ml, string max)
            {
                double? lo = Num(min), mode = Num(ml), hi = Num(max);
                string? k = T(kind);
                if (lo == null && mode == null && hi == null) return null;
                string canonical = DistSpec.Canonical(k ?? "triangle");
                if (canonical is not ("triangle" or "pert" or "uniform")) throw Bad(s, r, Head(kind), k!, "is not triangle, pert or uniform.");
                double l = lo ?? 0;
                return new DistSpec { Distribution = canonical, Min = l, MostLikely = mode ?? l, Max = hi ?? l };
            }
        }
        foreach (var risk in reg.Risks.Where(x => x.Id.Length == 0)) risk.Id = reg.NextId();
    }

    // ------------------------------------------------------------------ the Actions sheet

    private static void ReadActions(XlsxSheet s, RiskRegister reg)
    {
        int header = HeaderRow(s, ActionRisk);
        if (header == 0) return;
        var cols = new Dictionary<string, (int Col, string Header)>();
        for (int c = 1; c <= s.MaxColumn; c++)
            if (s.Text(header, c) is string h) cols.TryAdd(Norm(h), (c, h));
        int Col(string h) => cols.TryGetValue(Norm(h), out var x) ? x.Col : 0;
        string Head(string h) => cols.TryGetValue(Norm(h), out var x) ? x.Header : h;
        var used = cols.Values.Select(v => v.Col).ToList();

        for (int r = header + 1; r <= s.MaxRow; r++)
        {
            if (used.All(c => s.Text(r, c) == null)) continue;
            string? T(string h) => Col(h) > 0 ? s.Text(r, Col(h)) : null;
            string id = T(ActionRisk) ?? "";
            var risk = reg.Risks.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw Bad(s, r, Head(ActionRisk), id, "is not the ID of a risk on the Risks sheet.");
            risk.Actions.Add(new RiskAction
            {
                Text = T(ActionText) ?? "", Owner = T(ActionOwner) ?? "",
                Due = Col(ActionDue) > 0 ? ParseDate(s, r, Col(ActionDue), Head(ActionDue)) : null,
                Status = ParseEnum(s, r, Head(ActionStatusColumn), T(ActionStatusColumn), ActionStatus.Open),
            });
        }
    }
}
