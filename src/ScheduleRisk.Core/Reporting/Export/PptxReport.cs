using System.Globalization;
using System.Text;

namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>
/// The report as a 16:9 PowerPoint deck in the app's look: the app's warm grey ground, Archivo embedded, the red mark
/// and name on every slide, one slide per chart, tables as real (editable) tables continued over slides when long,
/// and the Summary's two columns as two slides.
/// </summary>
public static class PptxReport
{
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string Ns = $"xmlns:a=\"{A}\" xmlns:r=\"{OpcPackage.RelNs}\" xmlns:p=\"{P}\"";
    private const string Pml = "application/vnd.openxmlformats-officedocument.presentationml.";
    private const long In = 914400, Pt = 12700;
    private const long SlideW = 12192000, SlideH = 6858000, MarginX = (long)(0.6 * In);
    private const long ContentW = SlideW - 2 * MarginX, ContentTop = (long)(1.72 * In), ContentBottom = (long)(6.72 * In);
    private const string Heavy = "Archivo ExtraBold";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class Slide
    {
        public readonly StringBuilder Shapes = new();
        public readonly Relationships Rels = new();
        private int next = 2;
        public int Id() => next++;
    }

    public static byte[] Write(ReportContent doc, ReportFonts fonts, IReadOnlyDictionary<string, ReportImage> images)
    {
        var pal = ReportPalette.Screen;
        var pkg = new OpcPackage();
        pkg.Default("png", "image/png");
        pkg.Default("fntdata", "application/x-fontdata");
        var slides = new List<Slide>();
        var media = new Dictionary<string, string>();

        Slide New()
        {
            var s = new Slide();
            s.Rels.Add(OpcPackage.Rel + "slideLayout", "../slideLayouts/slideLayout1.xml");
            slides.Add(s);
            return s;
        }

        // title slide
        var first = New();
        Rect(first, MarginX, (long)(1.55 * In), (long)(0.42 * In), (long)(0.42 * In), pal.Accent);
        TextBox(first, MarginX, (long)(2.25 * In), ContentW, (long)(0.3 * In), Para("Risk report", Heavy, 1200, pal.AccentDeep, caps: true, spc: 120));
        TextBox(first, MarginX, (long)(2.6 * In), ContentW, (long)(1.5 * In), Para(doc.Title, Heavy, 4400, pal.Ink));
        TextBox(first, MarginX, (long)(4.3 * In), ContentW, (long)(1.0 * In),
            string.Concat(doc.Lines.Select(l => Para(l, "Archivo", 1400, pal.Muted))) + Para(doc.Generated, "Archivo", 1400, pal.Muted));

        // The contents slide is laid out once every section's first slide number is known.
        Slide? contentsSlide = null;
        long contentsTop = 0;
        var firstSlide = new List<(string Title, int Number)>();
        foreach (var section in doc.Sections)
        {
            if (section.Title != ReportContent.ContentsTitle) firstSlide.Add((section.Title, slides.Count + 1));
            bool leadShown = false;
            string? Lead()
            {
                if (leadShown) return null;
                leadShown = true;
                return section.Lead;
            }
            var pendingNotes = new List<string>();
            foreach (var block in section.Blocks)
                switch (block)
                {
                    case ColumnsBlock c:
                        foreach (var (title, blocks) in new[] { (c.LeftTitle, c.Left), (c.RightTitle, c.Right) })
                        {
                            var s = New();
                            long top = Header(s, section.Title + (title.Length > 0 ? ": " + title : ""), Lead(), pal);
                            var tables = blocks.OfType<TableBlock>().Select(b => b.Table).ToList();
                            // two tables side by side; three as the first on the left and the others stacked on the right
                            var groups = tables.Count <= 2 ? tables.Select(t => new List<Table> { t }).ToList()
                                : new List<List<Table>> { new() { tables[0] }, tables.Skip(1).ToList() };
                            const long gap = (long)(0.4 * In), vgap = (long)(0.28 * In);
                            long w = (ContentW - gap * (groups.Count - 1)) / Math.Max(1, groups.Count);
                            long room = ContentBottom - top - (long)(0.62 * In);
                            // the largest text that fits, preferring one where no number or date breaks over two lines
                            bool Fits(int z) => groups.All(g => g.Sum(t => TableHeight(t, t.Rows, w, fonts, z, true)) + vgap * (g.Count - 1) <= room);
                            bool Whole(int z) => groups.All(g => g.All(t => !ValuesWrap(t, w, fonts, z)));
                            var sizes = new[] { 1400, 1300, 1200, 1100 };
                            int size = sizes.FirstOrDefault(z => Fits(z) && Whole(z), sizes.FirstOrDefault(Fits, 1100));
                            for (int i = 0; i < groups.Count; i++)
                            {
                                long y = top;
                                foreach (var t in groups[i])
                                    y += TableFrame(s, t, t.Rows, MarginX + i * (w + gap), y, w, pal, fonts, size, withNote: true) + vgap;
                            }
                        }
                        break;
                    case ChartBlock cb:
                    {
                        var s = New();
                        long top = Header(s, cb.Chart.Title == section.Title ? section.Title : $"{section.Title}: {cb.Chart.Title}", Lead(), pal);
                        if (images.TryGetValue(cb.Chart.Key, out var img))
                        {
                            if (!media.ContainsKey(cb.Chart.Key))
                            {
                                media[cb.Chart.Key] = $"../media/{cb.Chart.Key}.png";
                                pkg.Add($"ppt/media/{cb.Chart.Key}.png", img.Png);
                            }
                            string rid = s.Rels.Add(OpcPackage.Rel + "image", media[cb.Chart.Key]);
                            long w = ContentW, h = w * cb.Chart.Height / cb.Chart.Width, room = ContentBottom - top;
                            if (h > room) { w = w * room / h; h = room; }
                            Picture(s, rid, MarginX, top, w, h, cb.Chart.Title, cb.Chart.Alt);
                        }
                        else TextBox(s, MarginX, top, ContentW, (long)(0.4 * In), Para("Chart not available", "Archivo", 1200, pal.Muted));
                        break;
                    }
                    case TableBlock tb:
                    {
                        var t = tb.Table;
                        long width = (long)(ContentW * Math.Max(t.Share, 0.7));
                        int at = 0, part = 0;
                        do
                        {
                            var s = New();
                            long top = Header(s, section.Title + (part++ > 0 ? " (continued)" : ""), Lead(), pal);
                            long room = ContentBottom - top;
                            long used = TableHeight(t, Array.Empty<Row>(), width, fonts, BodySize, withNote: false);
                            long note = TableHeight(t, Array.Empty<Row>(), width, fonts, BodySize, withNote: true) - used;
                            int fit = 0;
                            while (at + fit < t.Rows.Count)
                            {
                                long h = RowHeight(t, t.Rows[at + fit].Cells, width, fonts, BodySize, header: false, t.Rows[at + fit].Highlight);
                                if (used + h + (at + fit + 1 == t.Rows.Count ? note : 0) > room) break;
                                used += h;
                                fit++;
                            }
                            fit = Math.Max(1, fit);
                            int left = t.Rows.Count - at;
                            if (fit < left) fit = Math.Min(fit, (int)Math.Ceiling(left / Math.Ceiling(left / (double)fit)));
                            var rows = t.Rows.Skip(at).Take(fit).ToList();
                            at += rows.Count;
                            TableFrame(s, t, rows, MarginX, top, width, pal, fonts, BodySize, withNote: at >= t.Rows.Count);
                        } while (at < t.Rows.Count);
                        break;
                    }
                    case NotesBlock nb:
                    {
                        string title = section.Title + (nb.Title != null ? ": " + nb.Title : "");
                        var pages = NotePages(nb.Notes, fonts, out int size);
                        for (int i = 0; i < pages.Count; i++)
                        {
                            var s = New();
                            long top = Header(s, title + (i > 0 ? " (continued)" : ""), Lead(), pal);
                            for (int c = 0; c < pages[i].Count; c++)
                                TextBox(s, MarginX + c * (NoteColW + NoteGap), top, NoteColW, ContentBottom - top,
                                    string.Concat(pages[i][c].Select((n, k) => NoteParas(n, size, k == 0, pal))), autofit: true);
                        }
                        break;
                    }
                    case TextBlock tx:
                        pendingNotes.Add(tx.Text);
                        break;
                    case ContentsBlock:
                        contentsSlide = New();
                        contentsTop = Header(contentsSlide, section.Title, Lead(), pal);
                        break;
                }
            if (pendingNotes.Count > 0)
            {
                // a note goes under the section's last slide when there is one, else on a slide of its own
                bool own = section.Blocks.All(b => b is TextBlock);
                var s = own ? New() : slides[^1];
                long top = own ? Header(s, section.Title, Lead(), pal) : ContentBottom - (long)(0.5 * In);
                TextBox(s, MarginX, top, ContentW, (long)(own ? 1.5 * In : 0.55 * In),
                    string.Concat(pendingNotes.Select(n => Para(n, "Archivo", own ? 1600 : 1000, own ? pal.Ink : pal.Muted))), anchor: own ? "t" : "b");
            }
        }

        if (contentsSlide != null)
        {
            // two columns of "title ... slide" when the list is long
            int per = (int)Math.Ceiling(firstSlide.Count / (firstSlide.Count > 9 ? 2.0 : 1.0));
            long colW = firstSlide.Count > 9 ? (ContentW - (long)(0.4 * In)) / 2 : ContentW;
            for (int c = 0; c * per < firstSlide.Count; c++)
                TextBox(contentsSlide, MarginX + c * (colW + (long)(0.4 * In)), contentsTop, colW, ContentBottom - contentsTop,
                    string.Concat(firstSlide.Skip(c * per).Take(per).Select(e => Para($"{e.Title}  ·  slide {e.Number}", "Archivo", 1500, pal.Ink))), autofit: true);
        }

        // the mark and name on every slide, the project and slide number on the right
        for (int i = 0; i < slides.Count; i++)
        {
            var s = slides[i];
            long y = (long)(6.98 * In);
            Rect(s, MarginX, y + (long)(0.035 * In), (long)(0.15 * In), (long)(0.15 * In), pal.Accent);
            TextBox(s, MarginX + (long)(0.24 * In), y, (long)(4 * In), (long)(0.24 * In), Para(Brand.Name, Heavy, 1100, pal.Ink), anchor: "ctr");
            TextBox(s, SlideW - MarginX - 4 * In, y, 4 * In, (long)(0.24 * In),
                Para($"{doc.ProjectCode} · {i + 1} / {slides.Count}", "Archivo", 900, pal.Muted, algn: "r"), anchor: "ctr");
        }

        // parts
        var prels = new Relationships();
        prels.Add(OpcPackage.Rel + "slideMaster", "slideMasters/slideMaster1.xml");
        var sldIds = new StringBuilder();
        for (int i = 0; i < slides.Count; i++)
        {
            string path = $"ppt/slides/slide{i + 1}.xml";
            pkg.Add(path, $"<p:sld {Ns}><p:cSld><p:spTree>{GroupStart}{slides[i].Shapes}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>",
                Pml + "slide+xml");
            pkg.Add(Relationships.PathFor(path), slides[i].Rels.Xml());
            sldIds.Append($"<p:sldId id=\"{256 + i}\" r:id=\"{prels.Add(OpcPackage.Rel + "slide", $"slides/slide{i + 1}.xml")}\"/>");
        }
        prels.Add(OpcPackage.Rel + "presProps", "presProps.xml");
        prels.Add(OpcPackage.Rel + "viewProps", "viewProps.xml");
        prels.Add(OpcPackage.Rel + "theme", "theme/theme1.xml");
        prels.Add(OpcPackage.Rel + "tableStyles", "tableStyles.xml");

        var embedded = new StringBuilder("<p:embeddedFontLst>");
        int fontNo = 1;
        foreach (var (family, faces) in new[] { ("Archivo", new[] { ("regular", fonts.Regular), ("bold", fonts.Bold) }), (Heavy, new[] { ("regular", fonts.ExtraBold) }) })
        {
            embedded.Append($"<p:embeddedFont><p:font typeface=\"{family}\"/>");
            foreach (var (tag, face) in faces)
            {
                string path = $"fonts/font{fontNo++}.fntdata";
                pkg.Add("ppt/" + path, FontEmbedding.Eot(face));
                embedded.Append($"<p:{tag} r:id=\"{prels.Add(OpcPackage.Rel + "font", path)}\"/>");
            }
            embedded.Append("</p:embeddedFont>");
        }
        embedded.Append("</p:embeddedFontLst>");

        pkg.Add("ppt/presentation.xml", $"<p:presentation {Ns} embedTrueTypeFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>"
            + $"<p:sldIdLst>{sldIds}</p:sldIdLst><p:sldSz cx=\"{SlideW}\" cy=\"{SlideH}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>{embedded}"
            + $"<p:defaultTextStyle>{Level("defPPr", 1800, "tx1", "+mn-lt")}</p:defaultTextStyle></p:presentation>", Pml + "presentation.main+xml");
        pkg.Add(Relationships.PathFor("ppt/presentation.xml"), prels.Xml());

        pkg.Add("ppt/slideMasters/slideMaster1.xml", $"<p:sldMaster {Ns}><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"{pal.Ground}\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg>"
            + $"<p:spTree>{GroupStart}</p:spTree></p:cSld>"
            + "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" "
            + "accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>"
            + "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst>"
            + $"<p:txStyles><p:titleStyle>{Level("lvl1pPr", 3200, "tx1", "+mj-lt")}</p:titleStyle><p:bodyStyle>{Level("lvl1pPr", 1400, "tx1", "+mn-lt")}</p:bodyStyle>"
            + $"<p:otherStyle>{Level("lvl1pPr", 1400, "tx1", "+mn-lt")}</p:otherStyle></p:txStyles></p:sldMaster>", Pml + "slideMaster+xml");
        var mrels = new Relationships();
        mrels.Add(OpcPackage.Rel + "slideLayout", "../slideLayouts/slideLayout1.xml");
        mrels.Add(OpcPackage.Rel + "theme", "../theme/theme1.xml");
        pkg.Add(Relationships.PathFor("ppt/slideMasters/slideMaster1.xml"), mrels.Xml());

        pkg.Add("ppt/slideLayouts/slideLayout1.xml", $"<p:sldLayout {Ns} type=\"blank\" preserve=\"1\"><p:cSld name=\"Blank\"><p:spTree>{GroupStart}</p:spTree></p:cSld>"
            + "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>", Pml + "slideLayout+xml");
        var lrels = new Relationships();
        lrels.Add(OpcPackage.Rel + "slideMaster", "../slideMasters/slideMaster1.xml");
        pkg.Add(Relationships.PathFor("ppt/slideLayouts/slideLayout1.xml"), lrels.Xml());

        pkg.Add("ppt/theme/theme1.xml", Theme(pal), "application/vnd.openxmlformats-officedocument.theme+xml");
        pkg.Add("ppt/presProps.xml", $"<p:presentationPr {Ns}/>", Pml + "presProps+xml");
        pkg.Add("ppt/viewProps.xml", $"<p:viewPr {Ns}><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>", Pml + "viewProps+xml");
        pkg.Add("ppt/tableStyles.xml", $"<a:tblStyleLst xmlns:a=\"{A}\" def=\"{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}\"/>", Pml + "tableStyles+xml");

        var root = new Relationships();
        root.Add(OpcPackage.Rel + "officeDocument", "ppt/presentation.xml");
        root.Add("http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        root.Add(OpcPackage.Rel + "extended-properties", "docProps/app.xml");
        pkg.Add("_rels/.rels", root.Xml());
        DocxReport.AddProperties(pkg, doc);
        return pkg.Save(doc.GeneratedAt);
    }

    private const string GroupStart = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>"
        + "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";

    /// <summary>The slide's title, its lead line and the 2px rule under them; returns where the content starts.</summary>
    private static long Header(Slide s, string title, string? lead, ReportPalette pal)
    {
        TextBox(s, MarginX, (long)(0.42 * In), ContentW, (long)(0.62 * In), Para(title, Heavy, 2600, pal.Ink), anchor: "b");
        long y = (long)(1.1 * In);
        if (lead != null)
        {
            TextBox(s, MarginX, y, ContentW, (long)(0.36 * In), Para(lead, "Archivo", 1200, pal.Muted));
            y += (long)(0.4 * In);
        }
        Rect(s, MarginX, y + (long)(0.04 * In), ContentW, 19050, pal.Divider);
        return Math.Max(ContentTop, y + (long)(0.25 * In));
    }

    // ------------------------------------------------------------------ tables

    private const int BodySize = 1100;
    private const long CellX = (long)(0.07 * In), CellY = (long)(0.03 * In);

    private static int HeadSize(int body) => body * 77 / 100;

    private static long[] Columns(Table t, long width) => t.Widths.Select(v => (long)(v / t.Widths.Sum() * width)).ToArray();

    /// <summary>A row's height from its wrapped text (PowerPoint grows rows to fit; this places tables and decides where
    /// they continue).</summary>
    private static long RowHeight(Table t, IReadOnlyList<Cell> cells, long width, ReportFonts fonts, int body, bool header, bool highlight = false) =>
        (long)(CellLines(t, cells, width, fonts, body, header, highlight).Max(x => x.Lines) * (header ? HeadSize(body) : body) / 100.0 * 1.2 * Pt
               + 2 * CellY + 0.02 * In);

    /// <summary>How many lines each cell's text takes, set in the face the row uses, with a little room to spare.</summary>
    private static IEnumerable<(Align Align, int Lines)> CellLines(Table t, IReadOnlyList<Cell> cells, long width, ReportFonts fonts, int body, bool header, bool highlight)
    {
        var cols = Columns(t, width);
        double size = (header ? HeadSize(body) : body) / 100.0;
        int c = 0;
        foreach (var cell in cells)
        {
            int span = Math.Clamp(cell.Span, 1, cols.Length - c);
            double room = (cols.Skip(c).Take(span).Sum() - 2 * CellX) / (double)Pt;
            var face = header || highlight ? fonts.ExtraBold : cell.Tone is Tone.Strong or Tone.Accent ? fonts.Bold : fonts.Regular;
            int lines = PdfLayout.Wrap(header ? cell.Text.ToUpperInvariant() : cell.Text, face, size, room * 0.92, header ? size * 0.08 : 0).Count;
            yield return (cell.Align ?? t.Aligns[Math.Min(c, t.Aligns.Count - 1)], lines);
            c += span;
            if (c >= cols.Length) break;
        }
    }

    /// <summary>Whether a number or date in the table (a right-aligned or centred cell) would break over two lines.</summary>
    private static bool ValuesWrap(Table t, long width, ReportFonts fonts, int body) =>
        t.Rows.Any(r => CellLines(t, r.Cells, width, fonts, body, header: false, r.Highlight).Any(x => x.Align != Align.Left && x.Lines > 1));

    private static List<(IReadOnlyList<Cell> Cells, bool Header, bool Highlight, bool Note)> Lines(Table t, IReadOnlyList<Row> rows, bool withNote)
    {
        var all = new List<(IReadOnlyList<Cell>, bool, bool, bool)> { (t.Header, true, false, false) };
        all.AddRange(rows.Select(r => (r.Cells, false, r.Highlight, false)));
        if (withNote && t.Note != null) all.Add((new[] { new Cell(t.Note, t.Columns, Tone.Muted) }, false, false, true));
        return all;
    }

    private static long TableHeight(Table t, IReadOnlyList<Row> rows, long width, ReportFonts fonts, int body, bool withNote) =>
        Lines(t, rows, withNote).Sum(l => RowHeight(t, l.Cells, width, fonts, l.Note ? body * 85 / 100 : body, l.Header, l.Highlight));

    /// <summary>A real table; a note under it ("and 3 more") is its last row, so it moves with the table as rows grow.
    /// Returns the table's height.</summary>
    private static long TableFrame(Slide s, Table t, IReadOnlyList<Row> rows, long x, long y, long width, ReportPalette pal, ReportFonts fonts,
                                   int body, bool withNote)
    {
        var cols = Columns(t, width);
        var sb = new StringBuilder("<a:tbl><a:tblPr firstRow=\"1\"/><a:tblGrid>");
        foreach (long c in cols) sb.Append($"<a:gridCol w=\"{c}\"/>");
        sb.Append("</a:tblGrid>");
        long height = 0;
        foreach (var (cells, header, highlight, note) in Lines(t, rows, withNote))
        {
            int size = note ? body * 85 / 100 : header ? HeadSize(body) : body;
            long h = RowHeight(t, cells, width, fonts, header ? body : size, header, highlight);
            height += h;
            sb.Append($"<a:tr h=\"{h}\">");
            int c = 0;
            foreach (var cell in cells)
            {
                int span = Math.Clamp(cell.Span, 1, cols.Length - c);
                var align = cell.Align ?? t.Aligns[Math.Min(c, t.Aligns.Count - 1)];
                string algn = note ? "l" : align switch { Align.Right => "r", Align.Center => "ctr", _ => "l" };
                string para = header ? Para(cell.Text, Heavy, size, pal.Label, caps: true, spc: size * 8 / 100, algn: algn)
                    : highlight ? Para(cell.Text, Heavy, size, pal.Ink, algn: algn)
                    : cell.Tone switch
                    {
                        Tone.Muted => Para(cell.Text, "Archivo", size, pal.Muted, algn: algn),
                        Tone.Strong => Para(cell.Text, "Archivo", size, pal.Ink, bold: true, algn: algn),
                        Tone.Accent => Para(cell.Text, "Archivo", size, pal.Accent, bold: true, algn: algn),
                        _ => Para(cell.Text, "Archivo", size, pal.Ink, algn: algn),
                    };
                string border = note ? "<a:lnB w=\"0\"><a:noFill/></a:lnB>"
                    : $"<a:lnB w=\"{(header ? 19050 : 9525)}\" cap=\"flat\" cmpd=\"sng\"><a:solidFill><a:srgbClr val=\"{pal.Divider}\"/></a:solidFill></a:lnB>";
                string fill = highlight ? $"<a:solidFill><a:srgbClr val=\"{pal.AccentWash}\"/></a:solidFill>" : "<a:noFill/>";
                string pr = $"<a:tcPr marL=\"{(note ? 0 : CellX)}\" marR=\"{CellX}\" marT=\"{CellY}\" marB=\"{CellY}\" anchor=\"ctr\">"
                    + "<a:lnL w=\"0\"><a:noFill/></a:lnL><a:lnR w=\"0\"><a:noFill/></a:lnR><a:lnT w=\"0\"><a:noFill/></a:lnT>" + border + fill + "</a:tcPr>";
                sb.Append($"<a:tc{(span > 1 ? $" gridSpan=\"{span}\"" : "")}><a:txBody><a:bodyPr/><a:lstStyle/>{para}</a:txBody>{pr}</a:tc>");
                for (int k = 1; k < span; k++)
                    sb.Append($"<a:tc hMerge=\"1\"><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-GB\" sz=\"{size}\"/></a:p></a:txBody>{pr}</a:tc>");
                c += span;
                if (c >= cols.Length) break;
            }
            for (; c < cols.Length; c++)
                sb.Append($"<a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang=\"en-GB\" sz=\"{size}\"/></a:p></a:txBody><a:tcPr/></a:tc>");
            sb.Append("</a:tr>");
        }
        sb.Append("</a:tbl>");
        int id = s.Id();
        s.Shapes.Append($"<p:graphicFrame><p:nvGraphicFramePr><p:cNvPr id=\"{id}\" name=\"Table {id}\"/><p:cNvGraphicFramePr><a:graphicFrameLocks noGrp=\"1\"/></p:cNvGraphicFramePr><p:nvPr/></p:nvGraphicFramePr>"
            + $"<p:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cols.Sum()}\" cy=\"{height}\"/></p:xfrm>"
            + $"<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/table\">{sb}</a:graphicData></a:graphic></p:graphicFrame>");
        return height;
    }

    // ------------------------------------------------------------------ notes: two columns of label and text

    private const long NoteGap = (long)(0.45 * In), NoteColW = (ContentW - NoteGap) / 2;
    private static int NoteLabelSize(int size) => size * 70 / 100;
    private static int NoteSpace(int size) => size * 75 / 100;

    /// <summary>A note's height at a size: its label in small capitals, the text under it, and the space above it.</summary>
    private static long NoteHeight(Note n, ReportFonts fonts, int size, bool first)
    {
        double room = (NoteColW / (double)Pt) * 0.92, z = size / 100.0, lz = NoteLabelSize(size) / 100.0;
        int label = PdfLayout.Wrap(n.Label.ToUpperInvariant(), fonts.ExtraBold, lz, room, lz * 0.08).Count;
        int text = PdfLayout.Wrap(n.Text, fonts.Regular, z, room).Count;
        return (long)(((first ? 0 : NoteSpace(size) / 100.0) + label * lz * 1.2 + 2 + text * z * 1.2) * Pt);
    }

    /// <summary>The notes laid out as slides of two columns, in order: at the largest size from 14pt down to 11pt that
    /// needs the fewest slides, each slide's notes split between its columns as evenly as they go.</summary>
    private static List<List<List<Note>>> NotePages(IReadOnlyList<Note> notes, ReportFonts fonts, out int size)
    {
        long room = ContentBottom - ContentTop - (long)(0.4 * In);   // a lead line above, as on the section's first slide
        var sizes = new[] { 1400, 1300, 1200, 1100 };
        List<List<Note>> Columns(int z)
        {
            var cols = new List<List<Note>> { new() };
            long used = 0;
            foreach (var n in notes)
            {
                long h = NoteHeight(n, fonts, z, cols[^1].Count == 0);
                if (cols[^1].Count > 0 && used + h > room) { cols.Add(new()); used = 0; h = NoteHeight(n, fonts, z, true); }
                cols[^1].Add(n);
                used += h;
            }
            return cols;
        }
        int fewest = sizes.Min(z => (Columns(z).Count + 1) / 2);
        size = sizes.First(z => (Columns(z).Count + 1) / 2 == fewest);
        int zs = size;
        var cols = Columns(size);
        var pages = new List<List<List<Note>>>();
        for (int i = 0; i < cols.Count; i += 2)
        {
            var all = cols.Skip(i).Take(2).SelectMany(c => c).ToList();
            var heights = all.Select((n, k) => NoteHeight(n, fonts, zs, false)).ToList();
            // the split that keeps the taller column shortest, within the room
            int best = all.Count;
            long bestH = long.MaxValue;
            for (int k = 1; k <= all.Count; k++)
            {
                long left = heights.Take(k).Sum(), right = heights.Skip(k).Sum();
                if (left > room + NoteSpace(zs) * Pt / 100 || right > room + NoteSpace(zs) * Pt / 100) continue;
                if (Math.Max(left, right) < bestH) { bestH = Math.Max(left, right); best = k; }
            }
            pages.Add(new List<List<Note>> { all.Take(best).ToList(), all.Skip(best).ToList() }.Where(c => c.Count > 0).ToList());
        }
        return pages;
    }

    private static string NoteParas(Note n, int size, bool first, ReportPalette pal)
    {
        int lz = NoteLabelSize(size);
        string Run(string text, string font, int z, string color, bool caps) =>
            $"<a:r><a:rPr lang=\"en-GB\" sz=\"{z}\" b=\"0\"{(caps ? " cap=\"all\"" : "")}{(caps ? $" spc=\"{lz * 8 / 100}\"" : "")} dirty=\"0\">"
            + $"<a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"{font}\"/><a:cs typeface=\"{font}\"/></a:rPr><a:t>{OpcPackage.Text(text)}</a:t></a:r>";
        return $"<a:p><a:pPr algn=\"l\"><a:spcBef><a:spcPts val=\"{(first ? 0 : NoteSpace(size))}\"/></a:spcBef><a:spcAft><a:spcPts val=\"200\"/></a:spcAft></a:pPr>"
             + Run(n.Label, Heavy, lz, pal.Label, caps: true) + "</a:p>"
             + $"<a:p><a:pPr algn=\"l\"/>{Run(ResultsNarrative.NoBreakHyphens(n.Text), "Archivo", size, pal.Ink, caps: false)}</a:p>";
    }

    // ------------------------------------------------------------------ shapes

    private static void TextBox(Slide s, long x, long y, long w, long h, string paragraphs, string anchor = "t", bool autofit = false)
    {
        int id = s.Id();
        s.Shapes.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Text {id}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr>"
            + $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr>"
            + $"<p:txBody><a:bodyPr wrap=\"square\" lIns=\"0\" tIns=\"0\" rIns=\"0\" bIns=\"0\" anchor=\"{anchor}\">{(autofit ? "<a:normAutofit/>" : "<a:noAutofit/>")}</a:bodyPr><a:lstStyle/>{paragraphs}</p:txBody></p:sp>");
    }

    private static void Rect(Slide s, long x, long y, long w, long h, string color)
    {
        int id = s.Id();
        s.Shapes.Append($"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"Shape {id}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>"
            + $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>"
            + $"<a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:ln><a:noFill/></a:ln></p:spPr></p:sp>");
    }

    private static void Picture(Slide s, string rid, long x, long y, long w, long h, string name, string alt)
    {
        int id = s.Id();
        s.Shapes.Append($"<p:pic><p:nvPicPr><p:cNvPr id=\"{id}\" name=\"{OpcPackage.Text(name)}\" descr=\"{OpcPackage.Text(alt)}\"/>"
            + "<p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>"
            + $"<p:blipFill><a:blip r:embed=\"{rid}\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>"
            + $"<p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{w}\" cy=\"{h}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>");
    }

    /// <summary>A paragraph of one run; <paramref name="size"/> in hundredths of a point, <paramref name="spc"/> letter spacing likewise.</summary>
    private static string Para(string text, string font, int size, string color, bool bold = false, bool caps = false, int spc = 0, string algn = "l")
    {
        string attrs = $"lang=\"en-GB\" sz=\"{size}\" b=\"{(bold ? 1 : 0)}\"" + (caps ? " cap=\"all\"" : "") + (spc != 0 ? $" spc=\"{spc}\"" : "") + " dirty=\"0\"";
        string rpr = $"<a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"{font}\"/><a:cs typeface=\"{font}\"/>";
        return text.Length == 0
            ? $"<a:p><a:pPr algn=\"{algn}\"/><a:endParaRPr {attrs}>{rpr}</a:endParaRPr></a:p>"
            : $"<a:p><a:pPr algn=\"{algn}\"/><a:r><a:rPr {attrs}>{rpr}</a:rPr><a:t>{OpcPackage.Text(text)}</a:t></a:r></a:p>";
    }

    private static string Level(string tag, int size, string color, string font) =>
        $"<a:{tag} marL=\"0\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:buNone/>"
        + $"<a:defRPr sz=\"{size}\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"{font}\"/></a:defRPr></a:{tag}>";

    private static string Theme(ReportPalette pal)
    {
        string C(string name, string hex) => $"<a:{name}><a:srgbClr val=\"{hex}\"/></a:{name}>";
        string three(string one) => one + one + one;
        return $"<a:theme xmlns:a=\"{A}\" name=\"Modernist\"><a:themeElements><a:clrScheme name=\"Modernist\">"
            + C("dk1", pal.Ink) + C("lt1", "FFFFFF") + C("dk2", "444141") + C("lt2", pal.Ground)
            + C("accent1", pal.Accent) + C("accent2", pal.Muted) + C("accent3", pal.AccentDeep) + C("accent4", "9B9797")
            + C("accent5", "E15B47") + C("accent6", pal.Neutral300) + C("hlink", pal.AccentDeep) + C("folHlink", "7C1405")
            + "</a:clrScheme><a:fontScheme name=\"Archivo\">"
            + $"<a:majorFont><a:latin typeface=\"{Heavy}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>"
            + "<a:minorFont><a:latin typeface=\"Archivo\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
            + "<a:fmtScheme name=\"Flat\">"
            + "<a:fillStyleLst>" + three("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>") + "</a:fillStyleLst>"
            + "<a:lnStyleLst>" + string.Concat(new[] { 6350, 12700, 19050 }.Select(w => $"<a:ln w=\"{w}\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>")) + "</a:lnStyleLst>"
            + "<a:effectStyleLst>" + three("<a:effectStyle><a:effectLst/></a:effectStyle>") + "</a:effectStyleLst>"
            + "<a:bgFillStyleLst>" + three("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>") + "</a:bgFillStyleLst>"
            + "</a:fmtScheme></a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }
}
