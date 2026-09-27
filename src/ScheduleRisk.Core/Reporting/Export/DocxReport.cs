using System.Globalization;
using System.Text;

namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>
/// The report as a Word document (A4) in the app's look: Archivo embedded (Regular, Bold and ExtraBold), headings and
/// table heads as in the app, tables that stay editable, the charts as pictures, the logo and name in the page header
/// and the "Generated …" line with page numbers in the footer.
/// </summary>
public static class DocxReport
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Ns = $"xmlns:w=\"{W}\" xmlns:r=\"{OpcPackage.RelNs}\" "
        + "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" "
        + "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" "
        + "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"";
    private const string Main = "application/vnd.openxmlformats-officedocument.wordprocessingml.";
    private const int PageW = 11906, PageH = 16838, MarginX = 1000;
    private const int TextW = PageW - 2 * MarginX;   // twips (1/20 pt)
    private const string Heavy = "Archivo ExtraBold";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static byte[] Write(ReportContent doc, ReportFonts fonts, IReadOnlyDictionary<string, ReportImage> images)
    {
        var pal = ReportPalette.Paper;
        var pkg = new OpcPackage();
        pkg.Default("png", "image/png");
        pkg.Default("odttf", "application/vnd.openxmlformats-officedocument.obfuscatedFont");

        var rels = new Relationships();
        rels.Add(OpcPackage.Rel + "styles", "styles.xml");
        rels.Add(OpcPackage.Rel + "settings", "settings.xml");
        rels.Add(OpcPackage.Rel + "fontTable", "fontTable.xml");
        string header = rels.Add(OpcPackage.Rel + "header", "header1.xml");
        string footer = rels.Add(OpcPackage.Rel + "footer", "footer1.xml");

        var body = new StringBuilder();
        int drawing = 1;
        var media = new Dictionary<string, string>();

        // title block
        body.Append(Para(Run("Risk report", Heavy, 15, pal.AccentDeep, caps: true, spacing: 15), style: "Kicker"));
        body.Append(Para(Run(doc.Title), style: "Title"));
        foreach (var line in doc.Lines) body.Append(Para(Run(line), style: "Subtitle"));

        foreach (var section in doc.Sections)
        {
            body.Append(Para(Run(section.Title), style: "Heading1"));
            if (section.Lead != null) body.Append(Para(Run(section.Lead), style: "Lead"));
            var blocks = ReportContent.Flatten(section.Blocks).ToList();
            int charts = blocks.OfType<ChartBlock>().Count();
            foreach (var b in blocks)
                switch (b)
                {
                    case TableBlock t:
                        body.Append(Table(t.Table, pal));
                        if (t.Table.Note != null) body.Append(Para(Run(t.Table.Note), style: "Note"));
                        else body.Append(Para("", style: "Gap"));
                        break;
                    case ChartBlock c:
                        if (charts > 1 || c.Chart.Title != section.Title)
                            body.Append(Para(Run(c.Chart.Title), style: "Caption"));
                        if (images.TryGetValue(c.Chart.Key, out var img))
                        {
                            if (!media.TryGetValue(c.Chart.Key, out var rid))
                            {
                                string path = $"media/{c.Chart.Key}.png";
                                pkg.Add("word/" + path, img.Png);
                                media[c.Chart.Key] = rid = rels.Add(OpcPackage.Rel + "image", path);
                            }
                            long cx = TextW * 635L, cy = cx * c.Chart.Height / c.Chart.Width;
                            body.Append(Para(Picture(rid, cx, cy, drawing++, c.Chart.Title, c.Chart.Alt), style: "Figure"));
                        }
                        else body.Append(Para(Run("Chart not available"), style: "Note"));
                        break;
                    case TextBlock t:
                        body.Append(Para(Run(t.Text, color: t.Muted ? null : pal.Ink), style: t.Muted ? "Note" : "Lead"));
                        break;
                }
        }
        body.Append($"<w:sectPr><w:headerReference w:type=\"default\" r:id=\"{header}\"/><w:footerReference w:type=\"default\" r:id=\"{footer}\"/>"
            + $"<w:pgSz w:w=\"{PageW}\" w:h=\"{PageH}\"/><w:pgMar w:top=\"1560\" w:right=\"{MarginX}\" w:bottom=\"1300\" w:left=\"{MarginX}\" "
            + "w:header=\"640\" w:footer=\"560\" w:gutter=\"0\"/><w:cols w:space=\"708\"/></w:sectPr>");

        pkg.Add("word/document.xml", $"<w:document {Ns}><w:body>{body}</w:body></w:document>", Main + "document.main+xml");
        pkg.Add("word/styles.xml", Styles(pal), Main + "styles+xml");
        pkg.Add("word/settings.xml", $"<w:settings xmlns:w=\"{W}\"><w:zoom w:percent=\"100\"/><w:embedTrueTypeFonts/><w:defaultTabStop w:val=\"720\"/>"
            + "<w:characterSpacingControl w:val=\"doNotCompress\"/><w:compat><w:compatSetting w:name=\"compatibilityMode\" "
            + "w:uri=\"http://schemas.microsoft.com/office/word\" w:val=\"15\"/></w:compat></w:settings>", Main + "settings+xml");

        // header: the red mark and the name, the project on the right, over a 2px rule
        var hrels = new Relationships();
        pkg.Add("word/media/logo.png", Png.Solid(24, 24, pal.Accent));
        string logo = hrels.Add(OpcPackage.Rel + "image", "media/logo.png");
        const long mark = 9 * 12700;
        string headXml = $"<w:hdr {Ns}>" + Para(Picture(logo, mark, mark, 1000, "Logo", Brand.Name) + Run("  ") + Run(Brand.Name, Heavy, 21, pal.Ink)
            + "<w:r><w:tab/></w:r>" + Run(doc.ProjectCode, null, 16, pal.Muted, bold: true), style: "Header") + "</w:hdr>";
        pkg.Add("word/header1.xml", headXml, Main + "header+xml");
        pkg.Add(Relationships.PathFor("word/header1.xml"), hrels.Xml());
        string footXml = $"<w:ftr {Ns}>" + Para(Run(doc.Generated) + "<w:r><w:tab/></w:r>" + Run("Page ")
            + "<w:fldSimple w:instr=\" PAGE \">" + Run("1") + "</w:fldSimple>" + Run(" of ")
            + "<w:fldSimple w:instr=\" NUMPAGES \">" + Run("1") + "</w:fldSimple>", style: "Footer") + "</w:ftr>";
        pkg.Add("word/footer1.xml", footXml, Main + "footer+xml");

        // Archivo, embedded: Regular and Bold (600) as one family, ExtraBold (800) as its own
        var frels = new Relationships();
        var fontXml = new StringBuilder($"<w:fonts xmlns:w=\"{W}\" xmlns:r=\"{OpcPackage.RelNs}\">");
        foreach (var (family, faces) in new[] { ("Archivo", new[] { ("embedRegular", fonts.Regular), ("embedBold", fonts.Bold) }), (Heavy, new[] { ("embedRegular", fonts.ExtraBold) }) })
        {
            fontXml.Append($"<w:font w:name=\"{family}\"><w:panose1 w:val=\"{Convert.ToHexString(faces[0].Item2.Panose)}\"/><w:charset w:val=\"00\"/>"
                + "<w:family w:val=\"swiss\"/><w:pitch w:val=\"variable\"/>");
            foreach (var (tag, face) in faces)
            {
                string key = FontEmbedding.Key(face.Data);
                string path = $"fonts/font{frels.Count + 1}.odttf";
                pkg.Add("word/" + path, FontEmbedding.Obfuscate(face.Data, key));
                fontXml.Append($"<w:{tag} r:id=\"{frels.Add(OpcPackage.Rel + "font", path)}\" w:fontKey=\"{key}\"/>");
            }
            fontXml.Append("</w:font>");
        }
        fontXml.Append("</w:fonts>");
        pkg.Add("word/fontTable.xml", fontXml.ToString(), Main + "fontTable+xml");
        pkg.Add(Relationships.PathFor("word/fontTable.xml"), frels.Xml());
        pkg.Add(Relationships.PathFor("word/document.xml"), rels.Xml());

        var root = new Relationships();
        root.Add(OpcPackage.Rel + "officeDocument", "word/document.xml");
        root.Add("http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        root.Add(OpcPackage.Rel + "extended-properties", "docProps/app.xml");
        pkg.Add("_rels/.rels", root.Xml());
        AddProperties(pkg, doc);
        return pkg.Save(doc.GeneratedAt);
    }

    internal static void AddProperties(OpcPackage pkg, ReportContent doc)
    {
        var utc = doc.GeneratedAt.Kind == DateTimeKind.Utc ? doc.GeneratedAt : DateTime.SpecifyKind(doc.GeneratedAt, DateTimeKind.Local).ToUniversalTime();
        string when = utc.ToString("yyyy-MM-ddTHH:mm:ss", Inv) + "Z";
        pkg.Add("docProps/core.xml", "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" "
            + "xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            + $"<dc:title>{OpcPackage.Text(doc.Title)}</dc:title><dc:creator>{OpcPackage.X(Brand.Name)}</dc:creator>"
            + $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{when}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{when}</dcterms:modified>"
            + "</cp:coreProperties>", "application/vnd.openxmlformats-package.core-properties+xml");
        pkg.Add("docProps/app.xml", "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" "
            + $"xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><Application>{OpcPackage.X(Brand.Name + " " + Brand.Version)}</Application></Properties>",
            "application/vnd.openxmlformats-officedocument.extended-properties+xml");
    }

    // ------------------------------------------------------------------ tables

    private static string Table(Table t, ReportPalette pal)
    {
        int width = (int)(TextW * t.Share);
        var cols = t.Widths.Select(v => (int)Math.Round(v / t.Widths.Sum() * width)).ToArray();
        var sb = new StringBuilder("<w:tbl><w:tblPr>");
        sb.Append($"<w:tblW w:w=\"{cols.Sum()}\" w:type=\"dxa\"/><w:tblBorders><w:top w:val=\"nil\"/><w:left w:val=\"nil\"/><w:bottom w:val=\"nil\"/>"
            + "<w:right w:val=\"nil\"/><w:insideH w:val=\"nil\"/><w:insideV w:val=\"nil\"/></w:tblBorders><w:tblLayout w:type=\"fixed\"/>"
            + "<w:tblCellMar><w:left w:w=\"90\" w:type=\"dxa\"/><w:right w:w=\"90\" w:type=\"dxa\"/></w:tblCellMar>"
            + "<w:tblLook w:val=\"0000\" w:firstRow=\"0\" w:lastRow=\"0\" w:firstColumn=\"0\" w:lastColumn=\"0\" w:noHBand=\"1\" w:noVBand=\"1\"/></w:tblPr><w:tblGrid>");
        foreach (int c in cols) sb.Append($"<w:gridCol w:w=\"{c}\"/>");
        sb.Append("</w:tblGrid>");
        // a short table is kept on one page: each row keeps with the next
        bool keep = t.Rows.Count <= 12;
        sb.Append(TableRow(t, t.Header, cols, pal, header: true, highlight: false, keepNext: true));
        for (int i = 0; i < t.Rows.Count; i++)
            sb.Append(TableRow(t, t.Rows[i].Cells, cols, pal, header: false, t.Rows[i].Highlight, keepNext: keep && i < t.Rows.Count - 1));
        return sb.Append("</w:tbl>").ToString();
    }

    private static string TableRow(Table t, IReadOnlyList<Cell> cells, int[] cols, ReportPalette pal, bool header, bool highlight, bool keepNext)
    {
        var sb = new StringBuilder("<w:tr><w:trPr><w:cantSplit/>");
        if (header) sb.Append("<w:tblHeader/>");
        sb.Append("</w:trPr>");
        int c = 0;
        foreach (var cell in cells)
        {
            int span = Math.Clamp(cell.Span, 1, cols.Length - c);
            int w = cols.Skip(c).Take(span).Sum();
            var align = cell.Align ?? t.Aligns[Math.Min(c, t.Aligns.Count - 1)];
            sb.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{w}\" w:type=\"dxa\"/>");
            if (span > 1) sb.Append($"<w:gridSpan w:val=\"{span}\"/>");
            sb.Append($"<w:tcBorders><w:bottom w:val=\"single\" w:sz=\"{(header ? 12 : 5)}\" w:space=\"0\" w:color=\"{pal.Divider}\"/></w:tcBorders>");
            if (highlight) sb.Append($"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{pal.AccentWash}\"/>");
            sb.Append("<w:vAlign w:val=\"top\"/></w:tcPr>");
            string run = header ? Run(cell.Text, Heavy, 14, pal.Label, caps: true, spacing: 11)
                : highlight ? Run(cell.Text, Heavy, 17, pal.Ink)
                : cell.Tone switch
                {
                    Tone.Muted => Run(cell.Text, null, 17, pal.Muted),
                    Tone.Strong => Run(cell.Text, null, 17, pal.Ink, bold: true),
                    Tone.Accent => Run(cell.Text, null, 17, pal.Accent, bold: true),
                    _ => Run(cell.Text, null, 17, pal.Ink),
                };
            string jc = align switch { Align.Right => "right", Align.Center => "center", _ => "left" };
            sb.Append($"<w:p><w:pPr><w:pStyle w:val=\"{(header ? "TableHead" : "TableText")}\"/>{(keepNext && !header ? "<w:keepNext/>" : "")}<w:jc w:val=\"{jc}\"/></w:pPr>{run}</w:p></w:tc>");
            c += span;
            if (c >= cols.Length) break;
        }
        return sb.Append("</w:tr>").ToString();
    }

    // ------------------------------------------------------------------ runs, paragraphs, pictures

    private static string Para(string runs, string style) =>
        $"<w:p><w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>{runs}</w:p>";

    /// <summary>A run; <paramref name="size"/> in half-points, <paramref name="spacing"/> in twentieths of a point.</summary>
    private static string Run(string text, string? font = null, int size = 0, string? color = null, bool bold = false, bool caps = false, int spacing = 0)
    {
        if (text.Length == 0) return "";
        var pr = new StringBuilder();
        if (font != null) pr.Append($"<w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:cs=\"{font}\"/>");
        if (bold) pr.Append("<w:b/><w:bCs/>");
        if (caps) pr.Append("<w:caps/>");
        if (color != null) pr.Append($"<w:color w:val=\"{color}\"/>");
        if (spacing != 0) pr.Append($"<w:spacing w:val=\"{spacing}\"/>");
        if (size > 0) pr.Append($"<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/>");
        string rpr = pr.Length > 0 ? $"<w:rPr>{pr}</w:rPr>" : "";
        return $"<w:r>{rpr}<w:t xml:space=\"preserve\">{OpcPackage.Text(text)}</w:t></w:r>";
    }

    /// <summary>An inline picture; sizes in EMU.</summary>
    private static string Picture(string rid, long cx, long cy, int id, string name, string alt) =>
        $"<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>"
        + "<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>"
        + $"<wp:docPr id=\"{id}\" name=\"{OpcPackage.Text(name)}\" descr=\"{OpcPackage.Text(alt)}\"/>"
        + "<wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>"
        + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
        + $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"{OpcPackage.Text(name)}\"/><pic:cNvPicPr/></pic:nvPicPr>"
        + $"<pic:blipFill><a:blip r:embed=\"{rid}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
        + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
        + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";

    // ------------------------------------------------------------------ styles

    private static string Styles(ReportPalette pal)
    {
        string Font(string f) => $"<w:rFonts w:ascii=\"{f}\" w:hAnsi=\"{f}\" w:cs=\"{f}\"/>";
        string P(string id, string name, string ppr, string rpr, bool custom = true, string? next = null) =>
            $"<w:style w:type=\"paragraph\"{(custom ? " w:customStyle=\"1\"" : "")} w:styleId=\"{id}\"><w:name w:val=\"{name}\"/><w:basedOn w:val=\"Normal\"/>"
            + (next != null ? $"<w:next w:val=\"{next}\"/>" : "") + "<w:qFormat/>"
            + (ppr.Length > 0 ? $"<w:pPr>{ppr}</w:pPr>" : "") + (rpr.Length > 0 ? $"<w:rPr>{rpr}</w:rPr>" : "") + "</w:style>";
        var sb = new StringBuilder($"<w:styles xmlns:w=\"{W}\">");
        sb.Append($"<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Archivo\" w:eastAsia=\"Archivo\" w:hAnsi=\"Archivo\" w:cs=\"Archivo\"/>"
            + $"<w:color w:val=\"{pal.Ink}\"/><w:sz w:val=\"18\"/><w:szCs w:val=\"18\"/><w:lang w:val=\"en-GB\" w:eastAsia=\"en-GB\" w:bidi=\"ar-SA\"/></w:rPr></w:rPrDefault>"
            + "<w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"259\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>");
        sb.Append("<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>");
        sb.Append("<w:style w:type=\"character\" w:default=\"1\" w:styleId=\"DefaultParagraphFont\"><w:name w:val=\"Default Paragraph Font\"/><w:uiPriority w:val=\"1\"/><w:semiHidden/><w:unhideWhenUsed/></w:style>");
        sb.Append("<w:style w:type=\"table\" w:default=\"1\" w:styleId=\"TableNormal\"><w:name w:val=\"Normal Table\"/><w:uiPriority w:val=\"99\"/><w:semiHidden/><w:unhideWhenUsed/>"
            + "<w:tblPr><w:tblInd w:w=\"0\" w:type=\"dxa\"/><w:tblCellMar><w:top w:w=\"0\" w:type=\"dxa\"/><w:left w:w=\"108\" w:type=\"dxa\"/>"
            + "<w:bottom w:w=\"0\" w:type=\"dxa\"/><w:right w:w=\"108\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr></w:style>");
        sb.Append(P("Kicker", "Kicker", "<w:spacing w:after=\"80\"/>", "", next: "Title"));
        sb.Append(P("Title", "Title", "<w:spacing w:after=\"120\" w:line=\"240\" w:lineRule=\"auto\"/>", Font(Heavy) + "<w:sz w:val=\"44\"/><w:szCs w:val=\"44\"/>", custom: false, next: "Subtitle"));
        sb.Append(P("Subtitle", "Subtitle", "<w:spacing w:after=\"20\"/>", $"<w:color w:val=\"{pal.Muted}\"/>", custom: false));
        sb.Append(P("Heading1", "heading 1", $"<w:keepNext/><w:keepLines/><w:pBdr><w:top w:val=\"single\" w:sz=\"12\" w:space=\"10\" w:color=\"{pal.Divider}\"/></w:pBdr>"
            + "<w:spacing w:before=\"440\" w:after=\"100\"/><w:outlineLvl w:val=\"0\"/>", Font(Heavy) + "<w:sz w:val=\"28\"/><w:szCs w:val=\"28\"/>", custom: false, next: "Lead"));
        sb.Append(P("Lead", "Lead", "<w:keepNext/><w:spacing w:after=\"160\"/>", $"<w:color w:val=\"{pal.Muted}\"/><w:sz w:val=\"17\"/><w:szCs w:val=\"17\"/>"));
        sb.Append(P("Caption", "caption", "<w:keepNext/><w:spacing w:before=\"160\" w:after=\"80\"/>",
            Font(Heavy) + $"<w:caps/><w:color w:val=\"{pal.Label}\"/><w:spacing w:val=\"11\"/><w:sz w:val=\"14\"/><w:szCs w:val=\"14\"/>", custom: false));
        sb.Append(P("Figure", "Figure", "<w:spacing w:after=\"120\"/>", ""));
        sb.Append(P("Note", "Note", "<w:spacing w:before=\"80\" w:after=\"200\"/>", $"<w:color w:val=\"{pal.Muted}\"/><w:sz w:val=\"17\"/><w:szCs w:val=\"17\"/>"));
        sb.Append(P("Gap", "Gap", "<w:spacing w:after=\"120\" w:line=\"120\" w:lineRule=\"exact\"/>", "<w:sz w:val=\"8\"/><w:szCs w:val=\"8\"/>"));
        sb.Append(P("TableHead", "Table head", "<w:keepNext/><w:spacing w:before=\"70\" w:after=\"50\"/>", ""));
        sb.Append(P("TableText", "Table text", "<w:spacing w:before=\"60\" w:after=\"60\" w:line=\"240\" w:lineRule=\"auto\"/>", ""));
        sb.Append(P("Header", "header", $"<w:pBdr><w:bottom w:val=\"single\" w:sz=\"12\" w:space=\"6\" w:color=\"{pal.Divider}\"/></w:pBdr>"
            + $"<w:tabs><w:tab w:val=\"right\" w:pos=\"{TextW}\"/></w:tabs>", "", custom: false));
        sb.Append(P("Footer", "footer", $"<w:pBdr><w:top w:val=\"single\" w:sz=\"5\" w:space=\"6\" w:color=\"{pal.Divider}\"/></w:pBdr>"
            + $"<w:tabs><w:tab w:val=\"right\" w:pos=\"{TextW}\"/></w:tabs>", $"<w:color w:val=\"{pal.Muted}\"/><w:sz w:val=\"15\"/><w:szCs w:val=\"15\"/>", custom: false));
        return sb.Append("</w:styles>").ToString();
    }
}
