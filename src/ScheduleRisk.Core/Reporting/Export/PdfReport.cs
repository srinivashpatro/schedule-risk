using System.Globalization;
using System.Text;

namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>A chart drawn by the browser: PNG for Word and PowerPoint, and zlib-compressed RGB rows for PDF.</summary>
public sealed record ReportImage(byte[] Png, int PixelWidth, int PixelHeight, byte[]? ZlibRgb = null);

/// <summary>
/// The report as an A4 PDF in the app's look: Archivo (embedded, subset), ink on white with the red accent, tables
/// headed in small capitals over a 2px rule, the charts as pictures, and on every page the logo and name at the top and
/// the "Generated …" line and page number at the foot.
/// </summary>
public static class PdfReport
{
    public static byte[] Write(ReportContent doc, ReportFonts fonts, IReadOnlyDictionary<string, ReportImage> images) =>
        new PdfLayout(doc, fonts, images).Build();
}

internal sealed class PdfLayout
{
    private const double PageW = 595.28, PageH = 841.89, Left = 50, Top = 78, Bottom = PageH - 64;
    private const double W = PageW - 2 * Left;
    private const double Body = 8.5, Head = 6.8, PadX = 4.5, PadY = 3.8, Gap = 12, ShortTable = 230;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly ReportContent doc;
    private readonly ReportFonts faces;
    private readonly IReadOnlyDictionary<string, ReportImage> images;
    private readonly ReportPalette pal = ReportPalette.Paper;
    private readonly PdfFile pdf = new();
    private readonly Dictionary<FontWeight, PdfFont> fonts = new();
    private readonly Dictionary<string, (string Name, int Id)> xobjects = new();
    private readonly List<Page> pages = new();
    private Page page = null!;
    private double y;

    private sealed class Page
    {
        public readonly StringBuilder Ops = new();
        public readonly HashSet<string> Images = new();
    }

    public PdfLayout(ReportContent doc, ReportFonts faces, IReadOnlyDictionary<string, ReportImage> images)
    {
        this.doc = doc;
        this.faces = faces;
        this.images = images;
        int n = 1;
        foreach (var w in new[] { FontWeight.Regular, FontWeight.Bold, FontWeight.ExtraBold })
            fonts[w] = new PdfFont(pdf, faces[w], "F" + n++);
    }

    public byte[] Build()
    {
        NewPage();
        TitleBlock();
        foreach (var section in doc.Sections) SectionBlock(section);

        int pagesId = pdf.Reserve();
        var kids = new List<int>();
        for (int i = 0; i < pages.Count; i++)
        {
            page = pages[i];
            Decorate(i + 1, pages.Count);
            int content = pdf.AddStream("", Encoding.Latin1.GetBytes(page.Ops.ToString()));
            string fontRes = string.Join(' ', fonts.Values.Select(f => $"/{f.Name} {f.Id} 0 R"));
            string xres = page.Images.Count == 0 ? "" :
                " /XObject << " + string.Join(' ', page.Images.Select(k => $"/{xobjects[k].Name} {xobjects[k].Id} 0 R")) + " >>";
            kids.Add(pdf.Add($"<< /Type /Page /Parent {pagesId} 0 R /Resources << /Font << {fontRes} >>{xres} >> /Contents {content} 0 R >>"));
        }
        pdf.Set(pagesId, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} /MediaBox [0 0 {F(PageW)} {F(PageH)}] >>");
        foreach (var f in fonts.Values) f.Write(pdf);
        int catalog = pdf.Add($"<< /Type /Catalog /Pages {pagesId} 0 R /Lang (en) /ViewerPreferences << /DisplayDocTitle true >> >>");
        string made = Brand.Name + " " + Brand.Version;
        int info = pdf.Add($"<< /Title {PdfFile.TextString(doc.Title)} /Author {PdfFile.TextString(Brand.Name)} /Creator {PdfFile.TextString(made)} " +
                           $"/Producer {PdfFile.TextString(made)} /CreationDate (D:{doc.GeneratedAt.ToString("yyyyMMddHHmmss", Inv)}) >>");
        return pdf.Save(catalog, info);
    }

    // ------------------------------------------------------------------ flow

    private void NewPage()
    {
        page = new Page();
        pages.Add(page);
        y = Top;
    }

    private bool Fits(double h) => y + h <= Bottom;

    /// <summary>Starts a new page unless the height fits (or the page is still empty).</summary>
    private void Ensure(double h)
    {
        if (!Fits(h) && y > Top + 0.1) NewPage();
    }

    private void TitleBlock()
    {
        Text(Left, y + 8, "RISK REPORT", FontWeight.ExtraBold, 7.5, pal.AccentDeep, 0.75);
        y += 16;
        foreach (var line in Wrap(doc.Title, faces.ExtraBold, 22, W))
        {
            Text(Left, y + 20, line, FontWeight.ExtraBold, 22, pal.Ink);
            y += 26;
        }
        y += 4;
        foreach (var l in doc.Lines)
            foreach (var line in Wrap(l, faces.Regular, 9, W))
            {
                Text(Left, y + 10, line, FontWeight.Regular, 9, pal.Muted);
                y += 13;
            }
        y += 6;
    }

    private void SectionBlock(Section s)
    {
        var lead = s.Lead == null ? new List<string>() : Wrap(s.Lead, faces.Regular, Body, W);
        double headH = 12 + 20 + lead.Count * 12 + 8;
        double first = s.Blocks.Count == 0 ? 0 : FirstHeight(s.Blocks[0]);
        y += 18;
        Ensure(headH + first);
        Line(Left, Left + W, y, 1.5, pal.Divider);
        y += 12;
        Text(Left, y + 13, s.Title, FontWeight.ExtraBold, 14, pal.Ink);
        y += 20;
        foreach (var line in lead)
        {
            Text(Left, y + 9, line, FontWeight.Regular, Body, pal.Muted);
            y += 12;
        }
        y += 8;
        int charts = ReportContent.Flatten(s.Blocks).OfType<ChartBlock>().Count();
        bool firstBlock = true;
        foreach (var b in ReportContent.Flatten(s.Blocks))
        {
            if (!firstBlock) y += Gap;
            firstBlock = false;
            switch (b)
            {
                case TableBlock t: FlowTable(t.Table); break;
                case ChartBlock c: ChartAt(c.Chart, charts > 1 || c.Chart.Title != s.Title); break;
                case TextBlock t: Paragraph(t.Text, t.Muted); break;
            }
        }
    }

    /// <summary>How much of the block must follow its heading on the same page.</summary>
    private double FirstHeight(Block b) => b switch
    {
        ChartBlock c => 16 + W * c.Chart.Height / c.Chart.Width,
        TableBlock t => TableStart(t.Table),
        ColumnsBlock c => c.Left.Count > 0 ? FirstHeight(c.Left[0]) : 40,
        _ => 30,
    };

    private void Paragraph(string text, bool muted)
    {
        foreach (var line in Wrap(text, faces.Regular, Body, W))
        {
            Ensure(12);
            Text(Left, y + 9, line, FontWeight.Regular, Body, muted ? pal.Muted : pal.Ink);
            y += 12;
        }
    }

    private void ChartAt(Chart c, bool caption)
    {
        double h = W * c.Height / c.Width, capH = caption ? 16 : 0;
        Ensure(capH + h);
        if (caption)
        {
            Text(Left, y + 8, c.Title.ToUpperInvariant(), FontWeight.ExtraBold, Head, pal.Label, 0.08 * Head);
            y += capH;
        }
        if (images.TryGetValue(c.Key, out var img) && img.ZlibRgb != null)
        {
            if (!xobjects.ContainsKey(c.Key))
            {
                int id = pdf.AddStream($"/Type /XObject /Subtype /Image /Width {img.PixelWidth} /Height {img.PixelHeight} " +
                                       "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", img.ZlibRgb, compress: false);
                xobjects[c.Key] = ("Im" + (xobjects.Count + 1), id);
            }
            page.Images.Add(c.Key);
            page.Ops.Append($"q {F(W)} 0 0 {F(h)} {F(Left)} {F(PageH - y - h)} cm /{xobjects[c.Key].Name} Do Q\n");
        }
        else
        {
            Fill(Left, y, W, h, pal.Track);
            Text(Left + 12, y + 20, "Chart not available", FontWeight.Regular, Body, pal.Muted);
        }
        y += h;
    }

    // ------------------------------------------------------------------ tables

    private sealed record Laid(List<string> Lines, FontWeight Weight, string Color, Align Align, double X, double Width, double Spacing);

    /// <summary>The height a table needs on the page to start there: all of a short table (kept on one page), else its
    /// head and first row (with the note, when that row is the last).</summary>
    private double TableStart(Table t)
    {
        var (head, rows, noteH) = Measure(t);
        double headH = RowHeight(head, Head);
        double total = headH + rows.Sum(r => RowHeight(r.Cells, Body)) + noteH;
        return total <= ShortTable ? total : headH + (rows.Count > 0 ? RowHeight(rows[0].Cells, Body) : 0) + (rows.Count == 1 ? noteH : 0);
    }

    private (List<Laid> Head, List<(Row Row, List<Laid> Cells)> Rows, double NoteH) Measure(Table t)
    {
        double width = W * t.Share;
        var cols = t.Widths.Select(v => v / t.Widths.Sum() * width).ToArray();
        var head = LayRow(t, t.Header, cols, header: true, highlight: false);
        var rows = t.Rows.Select(r => (Row: r, Cells: LayRow(t, r.Cells, cols, header: false, r.Highlight))).ToList();
        double noteH = t.Note == null ? 0 : 4 + 12 * Wrap(t.Note, faces.Regular, Body, W).Count;
        return (head, rows, noteH);
    }

    private void FlowTable(Table t)
    {
        var (head, rows, noteH) = Measure(t);
        double headH = RowHeight(head, Head);
        Ensure(TableStart(t));
        DrawRow(head, Head, headH, header: true, highlight: false);
        for (int i = 0; i < rows.Count; i++)
        {
            var (row, cells) = rows[i];
            double h = RowHeight(cells, Body);
            if (!Fits(h + (i == rows.Count - 1 ? noteH : 0)))
            {
                NewPage();
                DrawRow(head, Head, headH, header: true, highlight: false);
            }
            DrawRow(cells, Body, h, header: false, row.Highlight);
        }
        if (t.Note != null)
        {
            y += 4;
            Paragraph(t.Note, muted: true);
        }
    }

    private List<Laid> LayRow(Table t, IReadOnlyList<Cell> cells, double[] cols, bool header, bool highlight)
    {
        var laid = new List<Laid>();
        int c = 0;
        double x = Left;
        foreach (var cell in cells)
        {
            int span = Math.Clamp(cell.Span, 1, cols.Length - c);
            double w = cols.Skip(c).Take(span).Sum();
            var (weight, color) = header ? (FontWeight.ExtraBold, pal.Label)
                : highlight ? (FontWeight.ExtraBold, pal.Ink)
                : cell.Tone switch
                {
                    Tone.Muted => (FontWeight.Regular, pal.Muted),
                    Tone.Strong => (FontWeight.Bold, pal.Ink),
                    Tone.Accent => (FontWeight.Bold, pal.Accent),
                    _ => (FontWeight.Regular, pal.Ink),
                };
            double size = header ? Head : Body, spacing = header ? 0.08 * Head : 0;
            string text = header ? cell.Text.ToUpperInvariant() : cell.Text;
            var lines = text.Length == 0 ? new List<string> { "" } : Wrap(text, faces[weight], size, w - 2 * PadX, spacing);
            laid.Add(new Laid(lines, weight, color, cell.Align ?? t.Aligns[Math.Min(c, t.Aligns.Count - 1)], x, w, spacing));
            x += w;
            c += span;
            if (c >= cols.Length) break;
        }
        return laid;
    }

    private static double LineH(double size) => size * 1.32;

    private static double RowHeight(List<Laid> cells, double size) =>
        cells.Max(c => c.Lines.Count) * LineH(size) + 2 * PadY;

    private void DrawRow(List<Laid> cells, double size, double h, bool header, bool highlight)
    {
        double rowW = cells.Sum(c => c.Width);
        if (highlight) Fill(Left, y, rowW, h, pal.AccentWash);
        var face = faces.Regular;
        double box = (face.Ascent - face.Descent) * size / face.UnitsPerEm;
        double firstBase = PadY + (LineH(size) - box) / 2 + face.Ascent * size / face.UnitsPerEm;
        foreach (var c in cells)
            for (int i = 0; i < c.Lines.Count; i++)
                TextIn(c.X + PadX, c.Width - 2 * PadX, y + firstBase + i * LineH(size), c.Lines[i], c.Weight, size, c.Color, c.Align, c.Spacing);
        y += h;
        Line(Left, Left + rowW, y - (header ? 0.75 : 0.3), header ? 1.5 : 0.6, pal.Divider);
    }

    // ------------------------------------------------------------------ page furniture

    private void Decorate(int number, int count)
    {
        Fill(Left, 33, 9, 9, pal.Accent);
        Text(Left + 14, 41.4, Brand.Name, FontWeight.ExtraBold, 10.5, pal.Ink);
        TextIn(Left, W, 41, doc.ProjectCode, FontWeight.Bold, 8, pal.Muted, Align.Right, 0);
        Line(Left, Left + W, 53, 1.5, pal.Divider);
        Line(Left, Left + W, PageH - 47, 0.6, pal.Divider);
        Text(Left, PageH - 33, doc.Generated, FontWeight.Regular, 7.5, pal.Muted);
        TextIn(Left, W, PageH - 33, $"Page {number} of {count}", FontWeight.Regular, 7.5, pal.Muted, Align.Right, 0);
    }

    // ------------------------------------------------------------------ drawing (y measured down from the top)

    private void Text(double x, double baseline, string text, FontWeight w, double size, string color, double spacing = 0)
    {
        if (text.Length == 0) return;
        page.Ops.Append($"BT /{fonts[w].Name} {F(size)} Tf {F(spacing)} Tc {Rgb(color)} rg 1 0 0 1 {F(x)} {F(PageH - baseline)} Tm {fonts[w].Encode(text)} Tj ET\n");
    }

    private void TextIn(double x, double width, double baseline, string text, FontWeight w, double size, string color, Align align, double spacing)
    {
        double tw = faces[w].Width(text, size, spacing);
        double at = align switch { Align.Right => x + width - tw, Align.Center => x + (width - tw) / 2, _ => x };
        Text(at, baseline, text, w, size, color, spacing);
    }

    private void Fill(double x, double top, double width, double height, string color) =>
        page.Ops.Append($"{Rgb(color)} rg {F(x)} {F(PageH - top - height)} {F(width)} {F(height)} re f\n");

    private void Line(double x1, double x2, double at, double thickness, string color) =>
        page.Ops.Append($"{Rgb(color)} RG {F(thickness)} w {F(x1)} {F(PageH - at)} m {F(x2)} {F(PageH - at)} l S\n");

    /// <summary>Breaks text into lines that fit the width, at spaces, or inside a word too long for a line.</summary>
    internal static List<string> Wrap(string text, TrueTypeFont font, double size, double width, double spacing = 0)
    {
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            string line = "";
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (font.Width(candidate, size, spacing) <= width) { line = candidate; continue; }
                if (line.Length > 0) lines.Add(line);
                string rest = word;
                while (rest.Length > 1 && font.Width(rest, size, spacing) > width)
                {
                    int n = rest.Length - 1;
                    while (n > 1 && font.Width(rest[..n], size, spacing) > width) n--;
                    if (char.IsHighSurrogate(rest[n - 1]) && n > 1) n--;
                    lines.Add(rest[..n]);
                    rest = rest[n..];
                }
                line = rest;
            }
            lines.Add(line);
        }
        return lines;
    }

    private static string Rgb(string hex) =>
        string.Join(' ', new[] { 0, 2, 4 }.Select(i => (Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0).ToString("0.###", Inv)));

    private static string F(double v) => v.ToString("0.###", Inv);
}
