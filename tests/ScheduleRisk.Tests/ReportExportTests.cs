using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Core.Reporting.Export;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Tests;

/// <summary>One simulated report (synth_500 with its risk model, pre and post mitigation) shared by the export tests.</summary>
public sealed class ExportFixture
{
    public static readonly DateTime When = new(2026, 9, 27, 9, 30, 0);
    public ReportFonts Fonts { get; } = LoadFonts();
    public ReportContent Doc { get; }
    public ScheduleRisk.Core.Model.Schedule Schedule { get; }
    public SimulationSummary Pre { get; }
    public SimulationSummary Post { get; }
    /// <summary>The example model's risks moved to a register, with owners, a response and actions, as a full report has.</summary>
    public ScheduleRisk.Core.Risk.Register.RiskRegister Register { get; } = new();
    public Dictionary<string, ReportImage> Images { get; } = new();

    public ExportFixture()
    {
        var s = TestData.Load("synth_500.xer");
        var cpm = new CpmEngine(s);
        var res = cpm.Run();
        var m = RiskModelLoader.Load(s, TestData.PathOf("synth_500.risk.json"), cpm.CriticalToProjectFinish());
        SimulationSummary One(Scenario sc) { var mc = new MonteCarloEngine(s, m, sc); return SimulationSummary.Build(mc, mc.Run(300, 7)); }
        Schedule = s;
        Pre = One(Scenario.PreMitigation);
        Post = One(Scenario.PostMitigation);
        var doc = RiskModelDocument.FromJson(File.ReadAllText(TestData.PathOf("synth_500.risk.json")));
        ScheduleRisk.Core.Risk.Register.RegisterImporter.MoveToRegister(doc, Register, s, cpm.CriticalToProjectFinish(),
            ScheduleRisk.Core.Risk.Register.RegisterPromoter.PlannedDuration(s, res).WorkingDays);
        var r01 = Register.Risks.Single(r => r.Id == "R01");
        r01.Title = "Late vendor data, again";
        r01.Owner = "Procurement lead";
        r01.Response = ScheduleRisk.Core.Risk.Register.ResponseStrategy.Mitigate;
        r01.ResponseDescription = "Second supplier";
        r01.ResponseCost = 2_000_000;
        r01.Actions.Add(new() { Text = "Chase the second supplier", Owner = "Procurement lead", Due = new DateOnly(2026, 9, 1) });
        r01.Actions.Add(new() { Text = "Get a second quote", Owner = "Procurement lead", Due = new DateOnly(2026, 10, 15) });
        Register.Risks.Single(r => r.Id == "R04").Actions.Add(new()
            { Text = "Agree the commissioning plan", Owner = "Site manager", Due = new DateOnly(2026, 8, 1), Status = ScheduleRisk.Core.Risk.Register.ActionStatus.Done });
        var cb = CostBenefit.Run(s, m, cpm, Pre, 80, id => id == "R01" ? 2_000_000 : null, 150_000, "INR");
        Doc = ReportContent.Build(new ReportInput(s, Pre, Post,
            HealthCheck.Run(s, res), P6Verifier.Verify(s, res), "Example model", 80, When, cb, Register), Fonts);
        // stand-ins for the pictures the browser draws: 8 x 4 pixels, as PNG and as zlib-compressed RGB rows
        foreach (var c in Doc.Charts)
        {
            var rgb = Enumerable.Repeat(new byte[] { 0xEC, 0x30, 0x13 }, 32).SelectMany(x => x).ToArray();
            Images[c.Key] = new ReportImage(Png.Encode(8, 4, rgb), 8, 4, Png.Zlib(rgb));
        }
    }

    public static string FontPath(string file) =>
        Path.Combine(Path.GetDirectoryName(TestData.Dir)!, "src", "ScheduleRisk.Web", "wwwroot", file);

    public static ReportFonts LoadFonts()
    {
        var f = ReportFonts.Files.Select(n => File.ReadAllBytes(FontPath(n))).ToArray();
        return new ReportFonts(f[0], f[1], f[2]);
    }
}

public class ReportExportTests : IClassFixture<ExportFixture>
{
    private readonly ExportFixture fx;

    public ReportExportTests(ExportFixture fx) => this.fx = fx;

    // ------------------------------------------------------------------ fonts

    [Fact]
    public void The_document_fonts_are_archivo_in_three_weights_named_for_office()
    {
        var f = fx.Fonts;
        Assert.Equal(("Archivo", "Regular", 400, false), (f.Regular.FamilyName, f.Regular.StyleName, f.Regular.WeightClass, f.Regular.IsBold));
        Assert.Equal(("Archivo", "Bold", 600, true), (f.Bold.FamilyName, f.Bold.StyleName, f.Bold.WeightClass, f.Bold.IsBold));
        Assert.Equal(("Archivo ExtraBold", "Regular", 800, false), (f.ExtraBold.FamilyName, f.ExtraBold.StyleName, f.ExtraBold.WeightClass, f.ExtraBold.IsBold));
        foreach (var font in new[] { f.Regular, f.Bold, f.ExtraBold })
        {
            Assert.Equal(0, font.FsType);                     // installable embedding (SIL Open Font License)
            Assert.Equal(1000, font.UnitsPerEm);
            Assert.All("·−–≥≤→Δ€ŁŚčőđ".EnumerateRunes(), r => Assert.True(font.Has(r.Value), $"{font.FullName} lacks {r}"));
        }
    }

    [Fact]
    public void Widths_come_from_the_font_file()
    {
        var f = fx.Fonts.Regular;
        Assert.Equal(1, f.GlyphId('A'));
        Assert.Equal(682, f.Advance(f.GlyphId('A')));
        Assert.Equal(520, f.GlyphId('−'));
        Assert.Equal(2304, f.Width("Hello", 1000), 6);              // fontTools: 2304 units
        Assert.Equal(2304 * 0.01 + 4 * 0.5, f.Width("Hello", 10, letterSpacing: 0.5), 6);
        Assert.Equal(0, f.GlyphId(0x4E2D));                         // not in the font: .notdef
    }

    [Fact]
    public void A_subset_keeps_glyph_ids_and_widths_and_only_the_glyphs_used()
    {
        var f = fx.Fonts.Regular;
        int a = f.GlyphId('A'), composite = f.GlyphId(0x1E62), s = f.GlyphId('S'), z = f.GlyphId('z');
        var sub = new TrueTypeFont(f.Subset(new[] { a, composite }));
        Assert.Equal(f.NumGlyphs, sub.NumGlyphs);
        Assert.Equal(f.Advance(z), sub.Advance(z));
        Assert.Equal(f.GlyphLength(a), sub.GlyphLength(a) - (4 - f.GlyphLength(a) % 4) % 4);
        Assert.True(sub.GlyphLength(s) > 0);                         // a component of Ṣ comes along
        Assert.Equal(0, sub.GlyphLength(z));
        Assert.True(sub.Data.Length < f.Data.Length / 4);
        // the checksum adjustment makes the whole file sum to the magic number
        uint sum = 0;
        for (int i = 0; i < sub.Data.Length; i += 4) sum = unchecked(sum + (uint)(sub.Data[i] << 24 | sub.Data[i + 1] << 16 | sub.Data[i + 2] << 8 | sub.Data[i + 3]));
        Assert.Equal(0xB1B0AFBAu, sum);
    }

    // ------------------------------------------------------------------ content

    [Fact]
    public void The_report_follows_the_owners_order()
    {
        Assert.Equal(new[]
        {
            "Table of Contents",
            "Schedule Health Check: P6 Check Schedule", "Schedule Health Check: DCMA 14-Point Assessment", "Engine check against P6",
            "Summary & Charts", "Risk & Activity Breakdown", "Analysis of the Result", "Sensitivity & Criticality", "Finish-Driving Activities",
            "Confidence levels", "Cost-benefit of responses", "Risk register", "Risk actions", "Risk drivers", "Terms used",
            "Annexure: Milestones",
        }, fx.Doc.Sections.Select(s => s.Title));
        Assert.IsType<ContentsBlock>(Assert.Single(fx.Doc.Sections[0].Blocks));
        Assert.Equal(new[] { "histogram", "cumulative", "risks", "criticality", "heatmap-current", "heatmap-target", "drivers" }, fx.Doc.Charts.Select(c => c.Key));
        Assert.Equal("Project risk analysis: SYN500", fx.Doc.Title);
        Assert.Equal($"Generated 27-Sep-2026 09:30 by {Brand.Name} {Brand.Version}", fx.Doc.Generated);
    }

    private Section Titled(string title) => fx.Doc.Sections.Single(s => s.Title == title);

    [Fact]
    public void The_summary_holds_the_finish_and_spread_tables_with_the_bell_curve_and_s_curve()
    {
        var blocks = Titled("Summary & Charts").Blocks;
        var finish = Assert.IsType<TableBlock>(blocks[0]).Table;
        Assert.Equal(new[] { "FINISH", "PRE-MITIGATION", "POST-MITIGATION" }, finish.Header.Select(h => h.Text.ToUpperInvariant()));
        Assert.Equal(new[] { "Deterministic", "Chance of deterministic", "P50", "P50 − deterministic", "P80", "P80 − deterministic", "Mean", "Median" },
                     finish.Rows.Select(r => r.Cells[0].Text));
        Assert.Equal(new[] { "P80", "P80 − deterministic" }, finish.Rows.Where(r => r.Highlight).Select(r => r.Cells[0].Text));
        Assert.Equal(2, finish.Rows[0].Cells[1].Span);                // the deterministic finish is shared by both columns
        Assert.Equal("Spread of the duration", Assert.IsType<TableBlock>(blocks[1]).Table.Header[0].Text);
        Assert.Equal("histogram", Assert.IsType<ChartBlock>(blocks[2]).Chart.Key);
        Assert.Equal("cumulative", Assert.IsType<ChartBlock>(blocks[3]).Chart.Key);
    }

    [Fact]
    public void The_breakdown_lists_the_top_drivers_and_every_critical_and_near_critical_activity()
    {
        var tables = Titled("Risk & Activity Breakdown").Blocks.Cast<TableBlock>().Select(b => b.Table).ToList();
        Assert.Equal("Top risk drivers", tables[0].Header[0].Text);
        Assert.StartsWith("Critical activities (", tables[1].Header[0].Text);
        Assert.StartsWith("Near-critical activities (", tables[2].Header[0].Text);
        var sum = ResultsSummary.Build(fx.Pre, fx.Post, 80);
        Assert.Equal(Math.Max(1, sum.Critical.Count), tables[1].Rows.Count);          // all of them, not the top five
        Assert.Equal(Math.Max(1, sum.NearCritical.Count), tables[2].Rows.Count);
        Assert.Null(tables[1].Note);
    }

    [Fact]
    public void Analysis_of_the_result_is_a_parameter_value_and_statement_table()
    {
        var sec = Titled("Analysis of the Result");
        Assert.Equal(ResultsNarrative.Lead, sec.Lead);
        var t = Assert.IsType<TableBlock>(Assert.Single(sec.Blocks)).Table;
        Assert.Equal(new[] { "Parameter", "Value", "Analysis Statement" }, t.Header.Select(h => h.Text));
        Assert.Equal(new[] { "Current finish", "P50", "P80", "Mean and median", "Skewness", "Kurtosis", "Spread", "Mitigation",
                             "What drives it", "Critical activities", "About these results" }, t.Rows.Select(r => r.Cells[0].Text));
        Assert.All(t.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.Cells[1].Text)));
        var p80 = t.Rows.Single(r => r.Cells[0].Text == "P80");
        Assert.Matches(@"^\d\d-[A-Z][a-z]{2}-\d{4} \(\+?-?[\d,]+ working days\)$", p80.Cells[1].Text);
        Assert.StartsWith("There is an 80% chance of finishing by ", p80.Cells[2].Text);
        var terms = Assert.IsType<NotesBlock>(Assert.Single(Titled("Terms used").Blocks));
        Assert.Equal(ResultsNarrative.Terms.Select(x => (x.Name, x.Meaning)), terms.Notes.Select(x => (x.Label, x.Text)));
    }

    [Fact]
    public void Sensitivity_and_criticality_follow_the_analysis_and_milestones_are_the_annexure()
    {
        var sc = Titled("Sensitivity & Criticality");
        var charts = sc.Blocks.OfType<ChartBlock>().Select(c => c.Chart.Key).ToList();
        Assert.Equal(new[] { "risks", "criticality" }, charts);
        Assert.Equal("Finish-Driving Activities", fx.Doc.Sections[fx.Doc.Sections.IndexOf(sc) + 1].Title);
        Assert.Equal("Annexure: Milestones", fx.Doc.Sections[^1].Title);
        Assert.DoesNotContain(fx.Doc.Sections, s => s.Title.Contains("Annexure") && s != fx.Doc.Sections[^1]);
    }

    [Fact]
    public void The_pdf_contents_page_gives_each_sections_page()
    {
        var r = new MiniPdf(PdfReport.Write(fx.Doc, fx.Fonts, fx.Images));
        var lines = r.Text().Split('\n');
        int pages = int.Parse(Regex.Match(r.All, @"/Type /Pages /Kids \[[^\]]*\] /Count (\d+)").Groups[1].Value);
        var titles = fx.Doc.Sections.Skip(1).Select(s => s.Title).ToList();
        var numbers = new List<int>();
        foreach (var title in titles)
        {
            // each entry: the title, its dotted leader, then the page number
            int k = Array.FindIndex(lines, x => x == title);
            while (k >= 0 && !(k + 2 < lines.Length && lines[k + 1].StartsWith("....") && int.TryParse(lines[k + 2], out _)))
                k = Array.FindIndex(lines, k + 1, x => x == title);
            Assert.True(k >= 0, $"no contents entry for {title}");
            numbers.Add(int.Parse(lines[k + 2]));
        }
        Assert.Equal(2, numbers[0]);                                    // the health check starts after the contents page
        Assert.Equal(numbers.OrderBy(n => n), numbers);
        Assert.InRange(numbers[^1], 2, pages);
    }

    [Fact]
    public void Every_format_has_the_contents_and_the_analysis_table()
    {
        var t = Assert.IsType<TableBlock>(Titled("Analysis of the Result").Blocks[0]).Table;
        string p80 = t.Rows.Single(r => r.Cells[0].Text == "P80").Cells[2].Text;

        string pdf = new MiniPdf(PdfReport.Write(fx.Doc, fx.Fonts, fx.Images)).Text();
        foreach (var expected in new[] { "Table of Contents", "Analysis of the Result", "ANALYSIS STATEMENT", "Terms used", "Mean and median", "There is an 80% chance" })
            Assert.Contains(expected, pdf);
        Assert.True(pdf.LastIndexOf("Schedule Health Check: P6 Check Schedule") < pdf.LastIndexOf("Summary & Charts"));
        Assert.True(pdf.LastIndexOf("Analysis of the Result") < pdf.LastIndexOf("Sensitivity & Criticality"));

        using (var zip = new ZipArchive(new MemoryStream(DocxReport.Write(fx.Doc, fx.Fonts, fx.Images))))
        {
            string body = Part(zip, "word/document.xml");
            Assert.Contains(" TOC \\o \"1-1\" \\h \\z \\u ", body);
            Assert.Contains("<w:updateFields w:val=\"true\"/>", Part(zip, "word/settings.xml"));
            Assert.Contains(p80, Regex.Replace(body.Replace("<w:noBreakHyphen/>", "-"), "<[^>]+>", ""));
            Assert.Contains("<w:noBreakHyphen/>", body);
            Assert.True(body.LastIndexOf(">Schedule Health Check: P6 Check Schedule<") < body.LastIndexOf(">Summary &amp; Charts<"));
            Assert.True(body.LastIndexOf(">Analysis of the Result<") < body.LastIndexOf(">Sensitivity &amp; Criticality<"));
        }

        using (var zip = new ZipArchive(new MemoryStream(PptxReport.Write(fx.Doc, fx.Fonts, fx.Images))))
        {
            var slides = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                .OrderBy(e => int.Parse(Regex.Match(e.FullName, @"\d+").Value)).Select(e => Part(zip, e.FullName)).ToList();
            Assert.Contains(">Table of Contents<", slides[1]);                         // straight after the title slide
            // every contents entry names the slide its section starts on
            foreach (Match m in Regex.Matches(slides[1], @">([^<]+)  ·  slide (\d+)<"))
            {
                string title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                Assert.Contains(">" + System.Security.SecurityElement.Escape(title), slides[int.Parse(m.Groups[2].Value) - 1]);
            }
            Assert.Equal(fx.Doc.Sections.Count - 1, Regex.Matches(slides[1], @"  ·  slide \d+<").Count);
        }
    }

    [Fact]
    public void Without_risks_duration_sensitivity_takes_the_place_of_the_risk_tornado()
    {
        var s = TestData.Load("parallel_2.xer");
        var m = RiskModelLoader.LoadJson(s, """{"uncertainty":[{"filter":{"all":true},"distribution":"uniform","min":50,"mostLikely":100,"max":150}]}""");
        var mc = new MonteCarloEngine(s, m);
        var doc = ReportContent.Build(new ReportInput(s, SimulationSummary.Build(mc, mc.Run(200, 3)), Generated: ExportFixture.When), fx.Fonts);
        var titles = doc.Sections.Select(x => x.Title).ToList();
        var sc = doc.Sections.Single(x => x.Title == "Sensitivity & Criticality");
        Assert.Equal(new[] { "sensitivity", "criticality" }, sc.Blocks.OfType<ChartBlock>().Select(c => c.Chart.Key));
        Assert.DoesNotContain("Risk drivers", titles);
        Assert.DoesNotContain(titles, t => t.StartsWith("Schedule Health Check", StringComparison.Ordinal));
        var finish = ((TableBlock)doc.Sections.Single(x => x.Title == "Summary & Charts").Blocks[0]).Table;
        Assert.Equal(2, finish.Columns);                               // no post-mitigation column
    }

    [Fact]
    public void Charts_are_standalone_svg_with_the_theme_and_archivo_inside()
    {
        foreach (var c in fx.Doc.Charts)
        {
            string svg = ReportCharts.Finish(c, ReportPalette.Paper, fx.Fonts, scale: 2);
            var root = XDocument.Parse(svg).Root!;
            Assert.Equal("svg", root.Name.LocalName);
            Assert.Equal((c.Width * 2).ToString(), root.Attribute("width")!.Value);
            Assert.Equal($"0 0 {c.Width} {c.Height}", root.Attribute("viewBox")!.Value);
            Assert.Equal(3, Regex.Matches(svg, "@font-face").Count);
            Assert.Contains("--ground:#FFFFFF", svg);
            Assert.DoesNotContain("/*theme*/", svg);
        }
        Assert.Contains("--ground:#F3F2F2", ReportCharts.Finish(fx.Doc.Charts.First(), ReportPalette.Screen, fx.Fonts));
    }

    [Fact]
    public void Long_names_in_bar_charts_are_cut_to_fit_with_an_ellipsis()
    {
        string cut = ReportCharts.Fit("Late vendor data for long-lead equipment and more", 200, fx.Fonts.Bold, 14);
        Assert.EndsWith("…", cut);
        Assert.True(fx.Fonts.Bold.Width(cut, 14) <= 200);
        Assert.Equal("Short", ReportCharts.Fit("Short", 200, fx.Fonts.Bold, 14));
    }

    // ------------------------------------------------------------------ PDF

    [Fact]
    public void The_pdf_is_an_a4_document_with_archivo_embedded_and_readable_text()
    {
        byte[] pdf = PdfReport.Write(fx.Doc, fx.Fonts, fx.Images);
        var r = new MiniPdf(pdf);
        Assert.StartsWith("%PDF-1.7", Encoding.Latin1.GetString(pdf, 0, 8));
        Assert.EndsWith("%%EOF\n", Encoding.Latin1.GetString(pdf, pdf.Length - 6, 6));
        Assert.Contains("/MediaBox [0 0 595.28 841.89]", r.All);
        Assert.Equal(3, r.Objects.Count(o => o.Value.Contains("/Subtype /CIDFontType2")));
        Assert.Equal(3, r.Objects.Count(o => o.Value.Contains("/FontFile2")));
        Assert.Contains("+Archivo-ExtraBold", r.All);
        int pages = int.Parse(Regex.Match(r.All, @"/Type /Pages /Kids \[[^\]]*\] /Count (\d+)").Groups[1].Value);
        Assert.InRange(pages, 4, 12);

        string text = r.Text();
        foreach (var expected in new[] { "Project risk analysis: SYN500", "Summary & Charts", "P80 − deterministic", "BELL CURVE: FINISH DATE DISTRIBUTION",
                                         "Confidence levels", "FINISH", Brand.Name, $"Page 1 of {pages}", "Engine check against P6" })
            Assert.Contains(expected, text);
    }

    [Fact]
    public void Pdf_charts_are_images_and_a_missing_chart_is_marked()
    {
        byte[] pdf = PdfReport.Write(fx.Doc, fx.Fonts, fx.Images);
        var r = new MiniPdf(pdf);
        Assert.Equal(fx.Doc.Charts.Count(), r.Objects.Count(o => o.Value.Contains("/Subtype /Image /Width 8 /Height 4")));
        Assert.Contains("/Im1 Do", r.Contents());

        var without = new MiniPdf(PdfReport.Write(fx.Doc, fx.Fonts, new Dictionary<string, ReportImage>()));
        Assert.DoesNotContain("/Subtype /Image", without.All);
        Assert.Contains("Chart not available", without.Text());
    }

    [Fact]
    public void The_same_report_gives_the_same_pdf()
    {
        Assert.Equal(PdfReport.Write(fx.Doc, fx.Fonts, fx.Images), PdfReport.Write(fx.Doc, fx.Fonts, fx.Images));
    }

    // ------------------------------------------------------------------ Word and PowerPoint

    [Fact]
    public void The_word_document_is_valid_open_xml_with_archivo_embedded()
    {
        byte[] docx = DocxReport.Write(fx.Doc, fx.Fonts, fx.Images);
        Assert.Empty(Validate(docx, word: true));
        using var zip = new ZipArchive(new MemoryStream(docx));
        string body = Part(zip, "word/document.xml");
        foreach (var expected in new[] { "Project risk analysis: SYN500", "P80 − deterministic", "Bell curve: finish date distribution", "Schedule Health Check: P6 Check Schedule", "DCMA 14-Point Assessment" })
            Assert.Contains(expected, body);
        Assert.Equal(fx.Doc.Charts.Count(), zip.Entries.Count(e => e.FullName.StartsWith("word/media/") && e.Name != "logo.png"));
        Assert.Contains("<w:tblHeader/>", body);                     // table heads repeat on each page

        // the embedded fonts: obfuscated with their key, and the same fonts once the key is applied again
        var fonts = XDocument.Parse(Part(zip, "word/fontTable.xml"));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main", r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var embeds = fonts.Descendants().Where(e => e.Name.LocalName.StartsWith("embed")).ToList();
        Assert.Equal(3, embeds.Count);
        var rels = XDocument.Parse(Part(zip, "word/_rels/fontTable.xml.rels")).Root!.Elements().ToDictionary(e => e.Attribute("Id")!.Value, e => e.Attribute("Target")!.Value);
        var originals = new[] { fx.Fonts.Regular.Data, fx.Fonts.Bold.Data, fx.Fonts.ExtraBold.Data };
        for (int i = 0; i < 3; i++)
        {
            byte[] stored = Bytes(zip, "word/" + rels[embeds[i].Attribute(r + "id")!.Value]);
            Assert.NotEqual(originals[i][..32], stored[..32]);
            Assert.Equal(originals[i], FontEmbedding.Obfuscate(stored, embeds[i].Attribute(w + "fontKey")!.Value));
        }
        Assert.Contains("<w:embedTrueTypeFonts/>", Part(zip, "word/settings.xml"));
    }

    [Fact]
    public void The_deck_is_valid_open_xml_with_real_tables_and_archivo_embedded()
    {
        byte[] pptx = PptxReport.Write(fx.Doc, fx.Fonts, fx.Images);
        Assert.Empty(Validate(pptx, word: false));
        using var zip = new ZipArchive(new MemoryStream(pptx));
        var slides = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$")).ToList();
        Assert.InRange(slides.Count, 12, 30);
        string all = string.Concat(slides.Select(s => Part(zip, s.FullName)));
        Assert.Contains("<a:tbl>", all);
        Assert.Contains("P80 − deterministic", all);
        Assert.Equal(fx.Doc.Charts.Count(), all.Split("<p:pic>").Length - 1);
        Assert.Contains(Brand.Name, Part(zip, "ppt/slides/slide1.xml"));

        string pres = Part(zip, "ppt/presentation.xml");
        Assert.Contains("embedTrueTypeFonts=\"1\"", pres);
        Assert.Contains("<p:font typeface=\"Archivo\"/><p:regular", pres);
        Assert.Contains("<p:font typeface=\"Archivo ExtraBold\"/>", pres);
        var eots = zip.Entries.Where(e => e.FullName.EndsWith(".fntdata")).Select(e => Bytes(zip, e.FullName)).ToList();
        Assert.Equal(3, eots.Count);
        var originals = new[] { fx.Fonts.Regular, fx.Fonts.Bold, fx.Fonts.ExtraBold };
        for (int i = 0; i < 3; i++)
        {
            byte[] eot = eots[i];
            Assert.Equal((uint)eot.Length, BitConverter.ToUInt32(eot, 0));
            Assert.Equal((uint)originals[i].Data.Length, BitConverter.ToUInt32(eot, 4));
            Assert.Equal(0x00020002u, BitConverter.ToUInt32(eot, 8));
            Assert.Equal((ushort)0x504C, BitConverter.ToUInt16(eot, 34));
            Assert.Equal((uint)originals[i].WeightClass, BitConverter.ToUInt32(eot, 28));
            Assert.Equal(originals[i].Data, eot[^originals[i].Data.Length..]);
            Assert.Contains(originals[i].FamilyName, Encoding.Unicode.GetString(eot, 80, 200));
        }
    }

    [Fact]
    public void Exported_files_link_to_nothing_outside_themselves()
    {
        foreach (var file in new[] { DocxReport.Write(fx.Doc, fx.Fonts, fx.Images), PptxReport.Write(fx.Doc, fx.Fonts, fx.Images) })
        {
            using var zip = new ZipArchive(new MemoryStream(file));
            foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".rels")))
                Assert.DoesNotContain("TargetMode=\"External\"", Part(zip, e.FullName));
        }
        string pdf = new MiniPdf(PdfReport.Write(fx.Doc, fx.Fonts, fx.Images)).All;
        foreach (var action in new[] { "/URI", "/Launch", "/JavaScript", "/SubmitForm", "/GoToR" })
            Assert.DoesNotContain(action, pdf);
    }

    // ------------------------------------------------------------------ helpers

    private static List<string> Validate(byte[] file, bool word)
    {
        using var ms = new MemoryStream(file);
        using OpenXmlPackage pkg = word ? WordprocessingDocument.Open(ms, false) : PresentationDocument.Open(ms, false);
        return new OpenXmlValidator(FileFormatVersions.Office2019).Validate(pkg).Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
    }

    private static string Part(ZipArchive zip, string name) => Encoding.UTF8.GetString(Bytes(zip, name));

    private static byte[] Bytes(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Just enough of a PDF reader to check the exports: objects found through the cross-reference table, streams
/// inflated, and page text decoded through each font's ToUnicode map.</summary>
internal sealed class MiniPdf
{
    public Dictionary<int, string> Objects { get; } = new();
    private readonly Dictionary<int, byte[]> streams = new();
    public string All { get; }

    public MiniPdf(byte[] pdf)
    {
        All = Encoding.Latin1.GetString(pdf);
        int xref = int.Parse(Regex.Match(All, @"startxref\n(\d+)\n%%EOF").Groups[1].Value);
        Assert.StartsWith("xref\n", All[xref..]);
        var head = Regex.Match(All[xref..], @"^xref\n0 (\d+)\n");
        int count = int.Parse(head.Groups[1].Value);
        for (int i = 1; i < count; i++)
        {
            int offset = int.Parse(All.Substring(xref + head.Length + 20 * i, 10));
            string start = $"{i} 0 obj\n";
            Assert.Equal(start, All.Substring(offset, start.Length));      // the table points at each object
            int body = offset + start.Length;
            int end = All.IndexOf("\nendobj", body, StringComparison.Ordinal);
            int st = All.IndexOf(">>\nstream\n", body, StringComparison.Ordinal);
            if (st >= 0 && st < end)
            {
                string dict = All[body..(st + 2)];
                int len = int.Parse(Regex.Match(dict, @"/Length (\d+)").Groups[1].Value);
                var data = pdf.AsSpan(st + 10, len).ToArray();
                if (dict.Contains("/FlateDecode") && !dict.Contains("/Subtype /Image"))
                {
                    using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                    var ms = new MemoryStream();
                    z.CopyTo(ms);
                    data = ms.ToArray();
                }
                streams[i] = data;
                Objects[i] = dict;
                end = All.IndexOf("\nendobj", st + 10 + len, StringComparison.Ordinal);
            }
            else Objects[i] = All[body..end];
        }
    }

    /// <summary>The pages' content streams, inflated, in page order.</summary>
    public string Contents() =>
        string.Concat(Regex.Matches(Regex.Match(All, @"/Type /Pages /Kids \[([^\]]*)\]").Groups[1].Value, @"(\d+) 0 R")
            .Select(k => Encoding.Latin1.GetString(streams[int.Parse(Regex.Match(Objects[int.Parse(k.Groups[1].Value)], @"/Contents (\d+) 0 R").Groups[1].Value)])));

    /// <summary>The text shown on each page, in page order.</summary>
    public string Text()
    {
        var maps = new Dictionary<string, Dictionary<string, string>>();   // font object "n 0 R" → CID hex → text
        foreach (var (id, dict) in Objects.Where(o => o.Value.Contains("/Subtype /Type0")))
        {
            int tu = int.Parse(Regex.Match(dict, @"/ToUnicode (\d+) 0 R").Groups[1].Value);
            var map = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(Encoding.ASCII.GetString(streams[tu]), @"<([0-9A-F]{4})> <([0-9A-F]+)>"))
                map[m.Groups[1].Value] = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(m.Groups[2].Value));
            maps[$"{id} 0 R"] = map;
        }
        var sb = new StringBuilder();
        string kids = Regex.Match(All, @"/Type /Pages /Kids \[([^\]]*)\]").Groups[1].Value;
        foreach (Match k in Regex.Matches(kids, @"(\d+) 0 R"))
        {
            string page = Objects[int.Parse(k.Groups[1].Value)];
            var fonts = Regex.Matches(page, @"/(F\d+) (\d+ 0 R)").ToDictionary(m => m.Groups[1].Value, m => maps[m.Groups[2].Value]);
            string content = Encoding.Latin1.GetString(streams[int.Parse(Regex.Match(page, @"/Contents (\d+) 0 R").Groups[1].Value)]);
            Dictionary<string, string>? font = null;
            foreach (Match m in Regex.Matches(content, @"/(F\d+) [\d.]+ Tf|<([0-9A-F]*)> Tj"))
            {
                if (m.Groups[1].Success) { font = fonts[m.Groups[1].Value]; continue; }
                string hex = m.Groups[2].Value;
                for (int i = 0; i + 4 <= hex.Length; i += 4) sb.Append(font![hex.Substring(i, 4)]);
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}
