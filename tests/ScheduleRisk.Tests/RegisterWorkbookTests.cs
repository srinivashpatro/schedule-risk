using System.IO.Compression;
using System.Security;
using System.Text;
using ScheduleRisk.Core.Reporting.Export;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Risk.Register;

namespace ScheduleRisk.Tests;

/// <summary>The risk register as an Excel workbook (docs/RISK_REGISTER.md, "Excel workbook"): the template the app
/// writes, and reading it back after it has been filled in, in the app or in Excel.</summary>
public class RegisterWorkbookTests
{
    private static Assessment Assess(int probability, params (string Area, int Level)[] severity)
    {
        var a = new Assessment { Probability = probability };
        foreach (var (d, l) in severity) a.Severity[d] = l;
        return a;
    }

    /// <summary>A register that uses every field, on a matrix edited away from the default.</summary>
    private static RiskRegister Full()
    {
        var reg = new RiskRegister { Name = "Q3 risk workshop" };
        var m = reg.Matrix;
        m.Probability[2].Label = "Possible";
        m.Probability[2].Guidance = "It may happen; line one\nand line two.";
        m.Ratings[0][4] = RiskRating.Red;
        m.Guidance[0].Actions.Add("Tell the sponsor");
        m.Categories.Add("Interfaces");
        m.CostOfDelayPerDay = 125000;
        m.Currency = "INR";
        m.Promote.MinRating = RiskRating.Red;
        m.Promote.MinScheduleSeverity = 2;
        var area = reg.AddDimension();
        area.Name = "Reputation";
        for (int s = 0; s < area.Bands.Count; s++) area.Bands[s].Text = $"Reputation level {s + 1}";

        var r1 = reg.Propose("Late turbine delivery", "a single qualified vendor", "the turbine arrives late", "a later start to erection",
            "Procurement", RiskKind.Threat, "A. Planner", new DateOnly(2026, 9, 1));
        r1.Status = RiskStatus.Approved;
        r1.Owner = "Procurement lead";
        r1.Inherent = Assess(4, ("schedule", 4), ("cost", 3), (area.Id, 1));
        r1.Current = Assess(3, ("schedule", 3), ("cost", 2));
        r1.Target = Assess(1, ("schedule", 2));
        r1.Response = ResponseStrategy.Mitigate;
        r1.ResponseDescription = "Expedite, with a second inspection visit;\n\"weekly\" calls.";
        r1.ResponseCost = 2500000.5;
        r1.Actions.Add(new RiskAction { Text = "Visit the works", Owner = "QA", Due = new DateOnly(2026, 10, 15), Status = ActionStatus.Open });
        r1.Actions.Add(new RiskAction { Text = "Agree the expediting plan", Owner = "PM", Due = null, Status = ActionStatus.Done });
        r1.Promotion = new Promotion
        {
            Activities = { "A1000", "A1010" }, Always = true, ImpactUnits = "percent", Probability = .35,
            Impact = new DistSpec { Distribution = "pert", Min = 5, MostLikely = 10, Max = 30 },
            MitigatedProbability = .1, MitigatedImpact = new DistSpec { Distribution = "uniform", Min = 2, MostLikely = 2, Max = 8 },
        };

        var r2 = reg.Propose("Early access to site", "", "the client hands over the site early", "", "External",
            RiskKind.Opportunity, "", new DateOnly(2026, 9, 2));
        r2.Status = RiskStatus.Approved;
        r2.Current = Assess(2, ("schedule", 1));
        r2.Response = ResponseStrategy.Exploit;
        r2.Promotion.Activities.Add("A2000");

        var r3 = reg.Propose("Rain", "the monsoon", "rain stops earthworks", "lost days", "", RiskKind.Threat, "", new DateOnly(2026, 9, 3));
        r3.Status = RiskStatus.Rejected;
        r3.Category = "Weather (not listed)";
        return reg;
    }

    [Fact]
    public void Round_trip_keeps_every_field_of_the_register_and_its_matrix()
    {
        var reg = Full();
        var back = RegisterWorkbook.Read(RegisterWorkbook.Write(reg));
        Assert.Equal(reg.ToJson(), back.ToJson());
    }

    [Fact]
    public void Round_trip_of_the_default_register_keeps_the_default_matrix()
    {
        var back = RegisterWorkbook.Read(RegisterWorkbook.Write(new RiskRegister()));
        Assert.Equal(new RiskRegister().ToJson(), back.ToJson());
        Assert.Empty(back.Validate());
    }

    [Fact]
    public void Template_carries_the_matrix_and_no_risks()
    {
        var m = MatrixSettings.Default();
        m.Categories.Add("Interfaces");
        var back = RegisterWorkbook.Read(RegisterWorkbook.Template(m, "New register"));
        Assert.Empty(back.Risks);
        Assert.Equal("New register", back.Name);
        Assert.Contains("Interfaces", back.Matrix.Categories);
        Assert.Equal(new RiskRegister { Name = "New register", Matrix = m }.ToJson(), back.ToJson());
    }

    [Fact]
    public void Workbook_has_the_guide_risks_actions_and_matrix_sheets_with_dropdowns_and_rating_formulas()
    {
        var bytes = RegisterWorkbook.Write(Full());
        var wb = XlsxReader.Open(bytes);
        Assert.Equal(new[] { "Guide", "Risks", "Actions", "Matrix" }, wb.SheetNames);

        string Part(string name)
        {
            using var zip = new ZipArchive(new MemoryStream(bytes));
            using var r = new StreamReader(zip.GetEntry(name)!.Open());
            return r.ReadToEnd();
        }
        string workbook = Part("xl/workbook.xml"), risks = Part("xl/worksheets/sheet2.xml");
        foreach (var name in new[] { "RiskGrid", "RiskGridLetters", "ProbabilityLetters", "SeverityLevels", "Categories" })
            Assert.Contains($"name=\"{name}\"", workbook);
        Assert.Contains("<dataValidations", risks);
        Assert.Contains("\"Threat,Opportunity\"", risks);
        Assert.Contains("<formula1>ProbabilityLetters</formula1>", risks);
        Assert.Contains("<conditionalFormatting", risks);
        Assert.Contains("<pane", risks);                                // header rows and the ID column stay in view
        Assert.Contains("INDEX(RiskGrid,MATCH(", risks);
        Assert.DoesNotContain("http://", Part("xl/worksheets/sheet1.xml").Replace("http://schemas.openxmlformats.org", ""));
    }

    [Fact]
    public void Rating_columns_hold_the_apps_rating_as_the_formulas_cached_value()
    {
        var reg = Full();
        var sheet = XlsxReader.Open(RegisterWorkbook.Write(reg)).Sheet("Risks")!;
        int header = Enumerable.Range(1, 5).First(r => sheet.Text(r, 1) == "ID");
        var ratingCols = Enumerable.Range(1, sheet.MaxColumn).Where(c => sheet.Text(header, c) == "Rating").ToList();
        Assert.Equal(3, ratingCols.Count);
        for (int i = 0; i < reg.Risks.Count; i++)
        {
            var r = reg.Risks[i];
            var expected = new[] { r.Inherent, r.Current, r.Target }.Select(a => reg.Matrix.Rate(a)?.ToString() ?? "").ToArray();
            Assert.Equal(expected, ratingCols.Select(c => sheet.Text(header + 1 + i, c) ?? "").ToArray());
            Assert.All(ratingCols, c => Assert.StartsWith("IFERROR(", sheet.Cell(header + 1 + i, c)!.Formula));
        }
    }

    [Fact]
    public void Template_in_the_docs_is_the_one_the_app_writes()
    {
        // docs/risk-register-template.xlsx, for downloading from the repository; regenerate it with
        // "sra register template --out docs/risk-register-template.xlsx" when the workbook changes.
        string path = Path.Combine(TestData.Dir, "..", "docs", "risk-register-template.xlsx");
        Assert.Equal(RegisterWorkbook.Template(MatrixSettings.Default(), "Risk register"), File.ReadAllBytes(path));
    }

    [Fact]
    public void Same_register_gives_the_same_bytes()
    {
        Assert.Equal(RegisterWorkbook.Write(Full()), RegisterWorkbook.Write(Full()));
    }

    // ------------------------------------------------------------------ workbooks saved by Excel

    /// <summary>A minimal workbook as Excel saves one: text in the shared-string table, numbers (dates too) as values,
    /// and only the sheets given. Each row is a list of cells; a double is written as a number, a string as text.</summary>
    private static byte[] ExcelStyle(params (string Name, object?[][] Rows)[] sheets)
    {
        var strings = new List<string>();
        int Str(string s) { int i = strings.IndexOf(s); if (i < 0) { strings.Add(s); i = strings.Count - 1; } return i; }
        static string Col(int c) { string s = ""; for (c++; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s; return s; }
        var sheetXml = sheets.Select(sh =>
        {
            var sb = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            for (int r = 0; r < sh.Rows.Length; r++)
            {
                sb.Append($"<row r=\"{r + 1}\">");
                for (int c = 0; c < sh.Rows[r].Length; c++)
                    switch (sh.Rows[r][c])
                    {
                        case null: break;
                        case double d: sb.Append($"<c r=\"{Col(c)}{r + 1}\" s=\"1\"><v>{d.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>"); break;
                        case string s: sb.Append($"<c r=\"{Col(c)}{r + 1}\" t=\"s\"><v>{Str(s)}</v></c>"); break;
                    }
                sb.Append("</row>");
            }
            return sb.Append("</sheetData></worksheet>").ToString();
        }).ToList();

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(xml);
            }
            Add("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            Add("xl/workbook.xml", "<x:workbook xmlns:x=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><x:sheets>"
                + string.Concat(sheets.Select((s, i) => $"<x:sheet name=\"{SecurityElement.Escape(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</x:sheets></x:workbook>");
            Add("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"/xl/worksheets/s{i + 1}.xml\"/>"))
                + "</Relationships>");
            for (int i = 0; i < sheets.Length; i++) Add($"xl/worksheets/s{i + 1}.xml", sheetXml[i]);
            Add("xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + string.Concat(strings.Select(s => $"<si><r><t xml:space=\"preserve\">{SecurityElement.Escape(s[..(s.Length / 2)])}</t></r><r><t xml:space=\"preserve\">{SecurityElement.Escape(s[(s.Length / 2)..])}</t></r></si>"))
                + "</sst>");
        }
        return ms.ToArray();
    }

    private static readonly object?[] RiskHeader = { "id", "TITLE", "Cause", "Event", "Effect", "Category", "Type", "Status", "Raised by", "Raised", "Owner", "Notes of our own" };

    [Fact]
    public void Reads_a_workbook_saved_by_Excel_with_shared_strings_and_dates_as_numbers()
    {
        var bytes = ExcelStyle(
            ("Risks", new[]
            {
                RiskHeader,
                new object?[] { "R07", "Crane failure", "old crane", "the crane breaks down", "lifts stop", "Construction", "threat", "APPROVED", "Site", 46296.0, "Site manager", "ignored" },
            }),
            ("Actions", new[]
            {
                new object?[] { "Risk ID", "Action", "Owner", "Due", "Status" },
                new object?[] { "R07", "Hire a standby crane", "Site", "2026-10-01", "open" },
            }));
        var reg = RegisterWorkbook.Read(bytes);
        var r = Assert.Single(reg.Risks);
        Assert.Equal(("R07", "Crane failure", "the crane breaks down", RiskStatus.Approved), (r.Id, r.Title, r.Event, r.Status));
        Assert.Equal(new DateOnly(2026, 10, 1), r.Raised);                 // Excel serial 46296
        Assert.Equal(new DateOnly(2026, 10, 1), Assert.Single(r.Actions).Due);
        Assert.Equal(MatrixSettings.Default().Categories, reg.Matrix.Categories); // no Matrix sheet: the default matrix
    }

    [Fact]
    public void Blank_ids_get_the_next_free_id_and_blank_rows_are_skipped()
    {
        var reg = RegisterWorkbook.Read(ExcelStyle(("Risks", new[]
        {
            RiskHeader,
            new object?[] { "R01", "First" , "", "one" },
            new object?[] { },
            new object?[] { null, "Second", "", "two" },
            new object?[] { "", "Third", "", "three" },
        })));
        Assert.Equal(new[] { "R01", "R02", "R03" }, reg.Risks.Select(r => r.Id));
        Assert.All(reg.Risks, r => Assert.Equal(RiskStatus.Proposed, r.Status));
    }

    [Fact]
    public void Probabilities_set_by_hand_read_as_fractions_or_percent()
    {
        var header = new object?[] { "ID", "Title", "Event", "Probability (by hand)", "Mitigated probability (by hand)" };
        var reg = RegisterWorkbook.Read(ExcelStyle(("Risks", new[]
        {
            header,
            new object?[] { "R01", "a", "e", 0.3, 20.0 },
            new object?[] { "R02", "b", "e", "30%", null },
        })));
        Assert.Equal(.3, reg.Risks[0].Promotion.Probability!.Value, 9);
        Assert.Equal(.2, reg.Risks[0].Promotion.MitigatedProbability!.Value, 9);
        Assert.Equal(.3, reg.Risks[1].Promotion.Probability!.Value, 9);
    }

    [Theory]
    [InlineData("Status", "Maybe", "Risks, row 2, column Status")]
    [InlineData("Type", "Risk", "Risks, row 2, column Type")]
    [InlineData("Raised", "next week", "Risks, row 2, column Raised")]
    public void A_value_that_cannot_be_read_names_its_sheet_row_and_column(string column, string value, string where)
    {
        var bytes = ExcelStyle(("Risks", new[] { new object?[] { "ID", "Title", column }, new object?[] { "R01", "x", value } }));
        var ex = Assert.Throws<FormatException>(() => RegisterWorkbook.Read(bytes));
        Assert.Contains(where, ex.Message);
        Assert.Contains(value, ex.Message);
    }

    [Fact]
    public void An_action_for_a_risk_not_in_the_register_is_refused()
    {
        var bytes = ExcelStyle(
            ("Risks", new[] { RiskHeader, new object?[] { "R01", "x", "", "e" } }),
            ("Actions", new[] { new object?[] { "Risk ID", "Action" }, new object?[] { "R09", "Do it" } }));
        var ex = Assert.Throws<FormatException>(() => RegisterWorkbook.Read(bytes));
        Assert.Contains("Actions, row 2", ex.Message);
        Assert.Contains("R09", ex.Message);
    }

    [Fact]
    public void A_workbook_without_a_risks_sheet_is_refused()
    {
        var ex = Assert.Throws<FormatException>(() => RegisterWorkbook.Read(ExcelStyle(("Sheet1", new[] { new object?[] { "x" } }))));
        Assert.Contains("Risks", ex.Message);
    }

    [Fact]
    public void Severity_columns_are_matched_by_area_name_or_id_under_their_assessment()
    {
        var bytes = ExcelStyle(("Risks", new[]
        {
            new object?[] { null, null, null, "Inherent", null, null, "Current", null, null },
            new object?[] { "ID", "Title", "Event", "Probability", "Schedule", "safety", "Probability", "SCHEDULE", "Rating" },
            new object?[] { "R01", "x", "e", "e", "V", "iii", "D", "IV", "whatever Excel says" },
        }));
        var r = Assert.Single(RegisterWorkbook.Read(bytes).Risks);
        Assert.Equal(4, r.Inherent.Probability);
        Assert.Equal(new Dictionary<string, int> { ["schedule"] = 4, ["safety"] = 2 }, r.Inherent.Severity);
        Assert.Equal(3, r.Current.Probability);
        Assert.Equal(new Dictionary<string, int> { ["schedule"] = 3 }, r.Current.Severity);
        Assert.False(r.Target.Complete);
    }

    [Fact]
    public void Matrix_sheet_edits_are_read_back()
    {
        var reg = new RiskRegister();
        reg.Matrix.Probability[4].Label = "Almost certain";
        reg.Matrix.Ratings[4][0] = RiskRating.Red;
        reg.Matrix.SeverityLevels[0] = "Negligible";
        reg.Matrix.Dimension("schedule")!.Bands[4].Max = 25;
        var back = RegisterWorkbook.Read(RegisterWorkbook.Write(reg)).Matrix;
        Assert.Equal("Almost certain", back.Probability[4].Label);
        Assert.Equal(RiskRating.Red, back.Rate(4, 0));
        Assert.Equal("Negligible", back.SeverityLevels[0]);
        Assert.Equal(25, back.Dimension("schedule")!.Bands[4].Max);
    }

    [Fact]
    public void Open_reads_a_workbook_or_a_json_file()
    {
        var reg = Full();
        Assert.Equal(reg.ToJson(), RiskRegister.Open(RegisterWorkbook.Write(reg)).ToJson());
        Assert.Equal(reg.ToJson(), RiskRegister.Open(Encoding.UTF8.GetBytes(reg.ToJson())).ToJson());
        Assert.Equal(reg.ToJson(), RiskRegister.Open(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(reg.ToJson())).ToArray()).ToJson());
    }

    [Fact]
    public void A_file_that_is_neither_is_refused_with_a_format_error()
    {
        Assert.Throws<FormatException>(() => RiskRegister.Open(new byte[] { 0x50, 0x4B, 3, 4, 1, 2, 3 }));
    }
}
