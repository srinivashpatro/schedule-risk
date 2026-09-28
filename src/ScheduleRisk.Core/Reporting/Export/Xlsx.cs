using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace ScheduleRisk.Core.Reporting.Export;

// A small reader and writer for Excel workbooks (.xlsx, SpreadsheetML in an Office Open XML package), enough for
// tables of text, numbers and dates with a little formatting. Only what the risk register workbook needs.

/// <summary>One cell as read: its text (strings, booleans as TRUE/FALSE, errors as written) or its number, and
/// its formula when it has one.</summary>
internal sealed record XlsxCell(string? Text, double? Number, string? Formula)
{
    public string Value => Text ?? (Number is double n ? n.ToString("R", CultureInfo.InvariantCulture) : "");
}

internal sealed class XlsxSheet
{
    private readonly Dictionary<(int Row, int Col), XlsxCell> cells = new();

    public string Name { get; }
    public int MaxRow { get; private set; }
    public int MaxColumn { get; private set; }

    public XlsxSheet(string name) => Name = name;

    internal void Set(int row, int col, XlsxCell cell)
    {
        cells[(row, col)] = cell;
        MaxRow = Math.Max(MaxRow, row);
        MaxColumn = Math.Max(MaxColumn, col);
    }

    /// <summary>The cell at a 1-based row and column, or null when it is empty.</summary>
    public XlsxCell? Cell(int row, int col) => cells.TryGetValue((row, col), out var c) ? c : null;

    /// <summary>The cell's value as text, trimmed; null when it is empty.</summary>
    public string? Text(int row, int col)
    {
        var c = Cell(row, col);
        if (c == null) return null;
        string v = c.Value.Trim();
        return v.Length == 0 ? null : v;
    }
}

internal sealed class XlsxWorkbook
{
    private readonly List<XlsxSheet> sheets;

    public XlsxWorkbook(List<XlsxSheet> sheets) => this.sheets = sheets;

    public IReadOnlyList<string> SheetNames => sheets.Select(s => s.Name).ToList();

    /// <summary>A sheet by name, ignoring case; null when the workbook has none.</summary>
    public XlsxSheet? Sheet(string name) => sheets.FirstOrDefault(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
}

internal static class XlsxReader
{
    private const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

    /// <summary>Reads every sheet of a workbook. A file that is not an Excel workbook is refused with a FormatException.</summary>
    public static XlsxWorkbook Open(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            string workbookPath = Relations(zip, "_rels/.rels", "").FirstOrDefault(r => r.Type == OfficeDocument).Target ?? "xl/workbook.xml";
            var wbXml = Load(zip, workbookPath) ?? throw new FormatException("The file is not an Excel workbook (.xlsx): it has no workbook part.");
            string dir = workbookPath.Contains('/') ? workbookPath[..(workbookPath.LastIndexOf('/') + 1)] : "";
            var rels = Relations(zip, Relationships.PathFor(workbookPath), dir);

            var strings = new List<string>();
            string sstPath = rels.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target ?? dir + "sharedStrings.xml";
            if (Load(zip, sstPath) is XmlDocument sst)
                foreach (XmlElement si in sst.DocumentElement!.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "si"))
                    strings.Add(StringItem(si));

            var sheets = new List<XlsxSheet>();
            foreach (XmlElement s in wbXml.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "sheet"))
            {
                string id = s.GetAttribute("id", OpcPackage.RelNs);
                string? target = rels.FirstOrDefault(r => r.Id == id).Target;
                if (target == null || zip.GetEntry(target) is not ZipArchiveEntry entry) continue; // a chart sheet or a missing part
                var sheet = new XlsxSheet(s.GetAttribute("name"));
                using (var stream = entry.Open()) ReadCells(stream, sheet, strings);
                sheets.Add(sheet);
            }
            return new XlsxWorkbook(sheets);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or IOException)
        {
            throw new FormatException($"The file is not an Excel workbook (.xlsx) that can be read: {e.Message}", e);
        }
    }

    private static XmlDocument? Load(ZipArchive zip, string path)
    {
        if (zip.GetEntry(path) is not ZipArchiveEntry e) return null;
        var doc = new XmlDocument { XmlResolver = null };
        using var s = e.Open();
        using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        doc.Load(r);
        return doc;
    }

    private static List<(string Id, string Type, string Target)> Relations(ZipArchive zip, string relsPath, string baseDir)
    {
        var list = new List<(string, string, string)>();
        if (Load(zip, relsPath) is not XmlDocument doc) return list;
        foreach (XmlElement r in doc.DocumentElement!.ChildNodes.OfType<XmlElement>())
        {
            string target = r.GetAttribute("Target").Replace('\\', '/');
            target = target.StartsWith('/') ? target[1..] : Normalise(baseDir + target);
            list.Add((r.GetAttribute("Id"), r.GetAttribute("Type"), target));
        }
        return list;
    }

    private static string Normalise(string path)
    {
        var parts = new List<string>();
        foreach (var p in path.Split('/'))
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (p != "." && p.Length > 0) parts.Add(p);
        return string.Join('/', parts);
    }

    /// <summary>A shared string: its text, or its runs' text, leaving out phonetic guides.</summary>
    private static string StringItem(XmlElement si)
    {
        var sb = new StringBuilder();
        foreach (XmlElement child in si.ChildNodes.OfType<XmlElement>())
            if (child.LocalName == "t") sb.Append(child.InnerText);
            else if (child.LocalName == "r")
                foreach (XmlElement t in child.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "t")) sb.Append(t.InnerText);
        return sb.ToString();
    }

    private static void ReadCells(Stream stream, XlsxSheet sheet, List<string> strings)
    {
        using var r = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = false });
        int row = 0, col = 0;
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            if (r.LocalName == "row")
            {
                row = int.TryParse(r.GetAttribute("r"), out int rn) ? rn : row + 1;
                col = 0;
            }
            else if (r.LocalName == "c")
            {
                string? reference = r.GetAttribute("r");
                if (reference != null && Parse(reference) is (int cr, int cc)) { row = cr; col = cc; }
                else col++;
                string type = r.GetAttribute("t") ?? "n";
                string? v = null, f = null, inline = null;
                if (!r.IsEmptyElement)
                {
                    // ReadElementContentAsString leaves the reader on the next node, so it is not followed by Read.
                    int depth = r.Depth;
                    r.Read();
                    while (!r.EOF && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
                    {
                        if (r.NodeType == XmlNodeType.Element && r.LocalName == "v") v = r.ReadElementContentAsString();
                        else if (r.NodeType == XmlNodeType.Element && r.LocalName == "f") f = r.ReadElementContentAsString();
                        else if (r.NodeType == XmlNodeType.Element && r.LocalName == "is") { inline = InlineString(r); r.Read(); }
                        else r.Read();
                    }
                }
                XlsxCell? cell = type switch
                {
                    "s" => int.TryParse(v, out int i) && i >= 0 && i < strings.Count ? new XlsxCell(strings[i], null, f) : null,
                    "inlineStr" => new XlsxCell(inline ?? "", null, f),
                    "str" or "e" => new XlsxCell(v ?? "", null, f),
                    "b" => new XlsxCell(v == "1" ? "TRUE" : "FALSE", null, f),
                    _ => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? new XlsxCell(null, d, f)
                        : f != null ? new XlsxCell("", null, f) : null,
                };
                if (cell != null && row > 0 && col > 0) sheet.Set(row, col, cell);
            }
        }
    }

    private static string InlineString(XmlReader r)
    {
        if (r.IsEmptyElement) return "";
        var sb = new StringBuilder();
        int depth = r.Depth;
        bool inPhonetic = false;
        while (r.Read() && !(r.NodeType == XmlNodeType.EndElement && r.Depth == depth))
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "rPh") inPhonetic = !r.IsEmptyElement;
            else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "rPh") inPhonetic = false;
            else if (r.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace or XmlNodeType.CDATA && !inPhonetic)
                sb.Append(r.Value);
        }
        return sb.ToString();
    }

    /// <summary>"AB12" → (row 12, column 28).</summary>
    public static (int Row, int Col)? Parse(string reference)
    {
        int i = 0, col = 0;
        while (i < reference.Length && char.IsAsciiLetter(reference[i])) col = col * 26 + (char.ToUpperInvariant(reference[i++]) - 'A' + 1);
        return col > 0 && i < reference.Length && int.TryParse(reference.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out int row) && row > 0
            ? (row, col) : null;
    }
}

/// <summary>A worksheet being written: cells by 1-based row and column, with a style index each, and the sheet's
/// column widths, merged ranges, frozen panes, dropdowns and conditional colours.</summary>
internal sealed class XlsxSheetWriter
{
    private readonly SortedDictionary<int, SortedDictionary<int, string>> rows = new();
    private readonly Dictionary<int, double> heights = new();
    private readonly List<string> merges = new(), validations = new(), formats = new();

    public string Name { get; }
    public Dictionary<int, double> Widths { get; } = new();
    public int FreezeRows { get; set; }
    public int FreezeColumns { get; set; }
    public string? AutoFilter { get; set; }
    public string? TabColor { get; set; }
    public int MaxRow => rows.Count == 0 ? 0 : rows.Keys.Max();

    public XlsxSheetWriter(string name) => Name = name;

    public static string Col(int col)
    {
        string s = "";
        for (; col > 0; col = (col - 1) / 26) s = (char)('A' + (col - 1) % 26) + s;
        return s;
    }

    public static string Ref(int row, int col) => Col(col) + row;

    /// <summary>An absolute range for formulas and defined names, e.g. Matrix!$A$3:$A$7.</summary>
    public string Abs(int row1, int col1, int row2, int col2) =>
        $"{Name}!${Col(col1)}${row1}:${Col(col2)}${row2}";

    private void Put(int row, int col, string xml)
    {
        if (!rows.TryGetValue(row, out var cells)) rows[row] = cells = new SortedDictionary<int, string>();
        cells[col] = xml;
    }

    public void Text(int row, int col, string? text, int style = 0)
    {
        if (string.IsNullOrEmpty(text)) { Style(row, col, style); return; }
        Put(row, col, $"<c r=\"{Ref(row, col)}\"{S(style)} t=\"inlineStr\"><is><t xml:space=\"preserve\">{OpcPackage.Text(text)}</t></is></c>");
    }

    public void Number(int row, int col, double? value, int style = 0)
    {
        if (value is not double v || double.IsNaN(v) || double.IsInfinity(v)) { Style(row, col, style); return; }
        Put(row, col, $"<c r=\"{Ref(row, col)}\"{S(style)}><v>{v.ToString("R", CultureInfo.InvariantCulture)}</v></c>");
    }

    /// <summary>A formula giving text, with the result it gives now, so that the value shows before Excel recalculates.</summary>
    public void Formula(int row, int col, string formula, string cached, int style = 0) =>
        Put(row, col, $"<c r=\"{Ref(row, col)}\"{S(style)} t=\"str\"><f>{E(formula)}</f><v>{OpcPackage.Text(cached)}</v></c>");

    /// <summary>An empty cell with a style (borders, fills), unless the cell already has a value.</summary>
    public void Style(int row, int col, int style)
    {
        if (style == 0) return;
        if (rows.TryGetValue(row, out var cells) && cells.ContainsKey(col)) return;
        Put(row, col, $"<c r=\"{Ref(row, col)}\"{S(style)}/>");
    }

    public void Height(int row, double points) => heights[row] = points;

    public void Merge(int row1, int col1, int row2, int col2) => merges.Add($"{Ref(row1, col1)}:{Ref(row2, col2)}");

    /// <summary>A dropdown: <paramref name="source"/> is a quoted list ("\"Yes,No\"") or a defined name.</summary>
    public void List(string sqref, string source, string prompt)
    {
        validations.Add($"<dataValidation type=\"list\" allowBlank=\"1\" showInputMessage=\"1\" showErrorMessage=\"1\" errorStyle=\"warning\" "
            + $"errorTitle=\"Not in the list\" error=\"{OpcPackage.X(prompt)}\" sqref=\"{sqref}\"><formula1>{E(source)}</formula1></dataValidation>");
    }

    public void Decimal(string sqref, double min, double? max, string message)
    {
        string op = max == null ? "greaterThanOrEqual" : "between";
        string f = $"<formula1>{min.ToString(CultureInfo.InvariantCulture)}</formula1>" + (max is double mx ? $"<formula2>{mx.ToString(CultureInfo.InvariantCulture)}</formula2>" : "");
        validations.Add($"<dataValidation type=\"decimal\" operator=\"{op}\" allowBlank=\"1\" showErrorMessage=\"1\" errorStyle=\"warning\" "
            + $"errorTitle=\"Check the value\" error=\"{OpcPackage.X(message)}\" sqref=\"{sqref}\">{f}</dataValidation>");
    }

    /// <summary>Colours cells whose text equals one of the values, with the differential style given for each.</summary>
    public void ColourWhenEqual(string sqref, params (string Value, int Dxf)[] rules)
    {
        var sb = new StringBuilder($"<conditionalFormatting sqref=\"{sqref}\">");
        foreach (var (value, dxf) in rules)
            sb.Append($"<cfRule type=\"cellIs\" dxfId=\"{dxf}\" priority=\"{{P}}\" operator=\"equal\"><formula>\"{E(value)}\"</formula></cfRule>");
        formats.Add(sb.Append("</conditionalFormatting>").ToString());
    }

    private static string S(int style) => style == 0 ? "" : $" s=\"{style}\"";

    /// <summary>Formula text in an element: quotes stay as they are, so the XML reads like the formula.</summary>
    private static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public string Xml(bool selected, ref int priority)
    {
        var sb = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
            + $"xmlns:r=\"{OpcPackage.RelNs}\">");
        if (TabColor != null) sb.Append($"<sheetPr><tabColor rgb=\"FF{TabColor}\"/></sheetPr>");
        sb.Append($"<sheetViews><sheetView workbookViewId=\"0\"{(selected ? " tabSelected=\"1\"" : "")}>");
        if (FreezeRows > 0 || FreezeColumns > 0)
        {
            string pane = FreezeRows > 0 && FreezeColumns > 0 ? "bottomRight" : FreezeRows > 0 ? "bottomLeft" : "topRight";
            string topLeft = Ref(FreezeRows + 1, FreezeColumns + 1);
            sb.Append("<pane");
            if (FreezeColumns > 0) sb.Append($" xSplit=\"{FreezeColumns}\"");
            if (FreezeRows > 0) sb.Append($" ySplit=\"{FreezeRows}\"");
            sb.Append($" topLeftCell=\"{topLeft}\" activePane=\"{pane}\" state=\"frozen\"/><selection pane=\"{pane}\" activeCell=\"{topLeft}\" sqref=\"{topLeft}\"/>");
        }
        sb.Append("</sheetView></sheetViews><sheetFormatPr defaultRowHeight=\"15\"/>");
        if (Widths.Count > 0)
        {
            sb.Append("<cols>");
            foreach (var (col, w) in Widths.OrderBy(x => x.Key))
                sb.Append($"<col min=\"{col}\" max=\"{col}\" width=\"{w.ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            sb.Append("</cols>");
        }
        sb.Append("<sheetData>");
        foreach (var (row, cells) in rows)
        {
            sb.Append($"<row r=\"{row}\"");
            if (heights.TryGetValue(row, out double h)) sb.Append($" ht=\"{h.ToString(CultureInfo.InvariantCulture)}\" customHeight=\"1\"");
            sb.Append('>');
            foreach (var c in cells.Values) sb.Append(c);
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        if (AutoFilter != null) sb.Append($"<autoFilter ref=\"{AutoFilter}\"/>");
        if (merges.Count > 0) sb.Append($"<mergeCells count=\"{merges.Count}\">").Append(string.Concat(merges.Select(m => $"<mergeCell ref=\"{m}\"/>"))).Append("</mergeCells>");
        foreach (var f in formats)
        {
            var text = new StringBuilder(f);
            int at;
            while ((at = text.ToString().IndexOf("{P}", StringComparison.Ordinal)) >= 0) text.Remove(at, 3).Insert(at, priority++);
            sb.Append(text);
        }
        if (validations.Count > 0) sb.Append($"<dataValidations count=\"{validations.Count}\">").Append(string.Concat(validations)).Append("</dataValidations>");
        sb.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
        sb.Append("<pageSetup orientation=\"landscape\" paperSize=\"9\" fitToHeight=\"0\"/>");
        return sb.Append("</worksheet>").ToString();
    }
}

/// <summary>A workbook being written: its sheets, defined names and a fixed style sheet (<see cref="XlsxStyles"/>).</summary>
internal sealed class XlsxWriter
{
    private const string Main = "application/vnd.openxmlformats-officedocument.spreadsheetml";

    public List<XlsxSheetWriter> Sheets { get; } = new();
    /// <summary>Defined names: (name, range, sheet index for a name local to one sheet, hidden).</summary>
    public List<(string Name, string Range, int? LocalSheet, bool Hidden)> Names { get; } = new();
    public string Title { get; set; } = "";

    public XlsxSheetWriter Add(string name)
    {
        var s = new XlsxSheetWriter(name);
        Sheets.Add(s);
        return s;
    }

    public byte[] Save(DateTime stamp)
    {
        var pkg = new OpcPackage();
        var root = new Relationships();
        root.Add(OpcPackage.Rel + "officeDocument", "xl/workbook.xml");
        root.Add("http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml");
        pkg.Add("_rels/.rels", root.Xml());
        string when = stamp.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        pkg.Add("docProps/core.xml",
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
            + "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            + $"<dc:title>{OpcPackage.Text(Title)}</dc:title><dc:creator>Project Risk Analysis</dc:creator>"
            + $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{when}</dcterms:created><dcterms:modified xsi:type=\"dcterms:W3CDTF\">{when}</dcterms:modified>"
            + "</cp:coreProperties>", "application/vnd.openxmlformats-package.core-properties+xml");

        var rels = new Relationships();
        var wb = new StringBuilder($"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{OpcPackage.RelNs}\">"
            + "<bookViews><workbookView activeTab=\"0\"/></bookViews><sheets>");
        int priority = 1;
        for (int i = 0; i < Sheets.Count; i++)
        {
            string path = $"worksheets/sheet{i + 1}.xml";
            string id = rels.Add(OpcPackage.Rel + "worksheet", path);
            wb.Append($"<sheet name=\"{OpcPackage.X(Sheets[i].Name)}\" sheetId=\"{i + 1}\" r:id=\"{id}\"/>");
            pkg.Add("xl/" + path, Sheets[i].Xml(i == 0, ref priority), Main + ".worksheet+xml");
        }
        wb.Append("</sheets>");
        if (Names.Count > 0)
        {
            wb.Append("<definedNames>");
            foreach (var (name, range, local, hidden) in Names.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
                wb.Append($"<definedName name=\"{name}\"{(local is int l ? $" localSheetId=\"{l}\"" : "")}{(hidden ? " hidden=\"1\"" : "")}>{OpcPackage.X(range)}</definedName>");
            wb.Append("</definedNames>");
        }
        wb.Append("<calcPr calcId=\"191029\" fullCalcOnLoad=\"1\"/></workbook>");
        rels.Add(OpcPackage.Rel + "styles", "styles.xml");
        pkg.Add("xl/workbook.xml", wb.ToString(), Main + ".sheet.main+xml");
        pkg.Add("xl/_rels/workbook.xml.rels", rels.Xml());
        pkg.Add("xl/styles.xml", XlsxStyles.Xml, Main + ".styles+xml");
        return pkg.Save(stamp);
    }
}

/// <summary>
/// The workbook's styles, in the app's look: ink headers, the red accent, thin rules, and Red, Amber and Green
/// fills for ratings. The constants are cell style indexes (cellXfs) and differential styles (dxfs).
/// </summary>
internal static class XlsxStyles
{
    public const int Body = 1, Header = 2, Group = 3, Date = 4, Money = 5, Percent = 6, Title = 7, Block = 8,
        Note = 9, Rating = 10, Key = 11, Number = 12, Formula = 13, Subtitle = 14;
    public const int RedDxf = 0, AmberDxf = 1, GreenDxf = 2;

    private const string Ink = "1A1918", Accent = "EC3013", Rule = "BFBAB4", Muted = "6B6660", Shade = "F3F1EE";

    public static readonly string Xml =
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
        + "<numFmts count=\"3\"><numFmt numFmtId=\"164\" formatCode=\"dd\\-mmm\\-yyyy\"/><numFmt numFmtId=\"165\" formatCode=\"#,##0.00\"/>"
        + "<numFmt numFmtId=\"166\" formatCode=\"0.##%\"/></numFmts>"
        + "<fonts count=\"7\">"
        + "<font><sz val=\"10\"/><name val=\"Arial\"/><family val=\"2\"/></font>"                                          // 0 body
        + $"<font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Arial\"/><family val=\"2\"/></font>"             // 1 header
        + $"<font><b/><sz val=\"16\"/><color rgb=\"FF{Ink}\"/><name val=\"Arial\"/><family val=\"2\"/></font>"               // 2 title
        + $"<font><b/><sz val=\"11\"/><color rgb=\"FF{Accent}\"/><name val=\"Arial\"/><family val=\"2\"/></font>"            // 3 block
        + $"<font><i/><sz val=\"9\"/><color rgb=\"FF{Muted}\"/><name val=\"Arial\"/><family val=\"2\"/></font>"              // 4 note
        + $"<font><b/><sz val=\"10\"/><color rgb=\"FF{Ink}\"/><name val=\"Arial\"/><family val=\"2\"/></font>"               // 5 key
        + $"<font><sz val=\"11\"/><color rgb=\"FF{Muted}\"/><name val=\"Arial\"/><family val=\"2\"/></font>"                 // 6 subtitle
        + "</fonts>"
        + "<fills count=\"5\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
        + $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{Ink}\"/><bgColor indexed=\"64\"/></patternFill></fill>"      // 2 ink
        + $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{Accent}\"/><bgColor indexed=\"64\"/></patternFill></fill>"   // 3 accent
        + $"<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF{Shade}\"/><bgColor indexed=\"64\"/></patternFill></fill>"    // 4 shade
        + "</fills>"
        + "<borders count=\"2\"><border><left/><right/><top/><bottom/><diagonal/></border>"
        + $"<border><left style=\"thin\"><color rgb=\"FF{Rule}\"/></left><right style=\"thin\"><color rgb=\"FF{Rule}\"/></right>"
        + $"<top style=\"thin\"><color rgb=\"FF{Rule}\"/></top><bottom style=\"thin\"><color rgb=\"FF{Rule}\"/></bottom><diagonal/></border></borders>"
        + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
        + "<cellXfs count=\"15\">"
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"                                                                                  // 0
        + "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" // 1 body (text)
        + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"center\" wrapText=\"1\"/></xf>" // 2 header
        + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"3\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\"/></xf>" // 3 group
        + "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"top\"/></xf>" // 4 date
        + "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" // 5 money
        + "<xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>" // 6 percent
        + "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"                                                                   // 7 title
        + "<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"                                                                   // 8 block
        + "<xf numFmtId=\"0\" fontId=\"4\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" // 9 note
        + "<xf numFmtId=\"49\" fontId=\"5\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"top\"/></xf>" // 10 rating
        + "<xf numFmtId=\"49\" fontId=\"5\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" // 11 key
        + "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyBorder=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\"/></xf>"             // 12 number
        + "<xf numFmtId=\"0\" fontId=\"5\" fillId=\"4\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"top\"/></xf>" // 13 formula
        + "<xf numFmtId=\"0\" fontId=\"6\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf>" // 14 subtitle
        + "</cellXfs>"
        + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>"
        + "<dxfs count=\"3\">"
        + "<dxf><font><b/><color rgb=\"FFFFFFFF\"/></font><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFC62828\"/><bgColor rgb=\"FFC62828\"/></patternFill></fill></dxf>"
        + "<dxf><font><b/><color rgb=\"FF1A1918\"/></font><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2A900\"/><bgColor rgb=\"FFF2A900\"/></patternFill></fill></dxf>"
        + "<dxf><font><b/><color rgb=\"FFFFFFFF\"/></font><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF3F8F47\"/><bgColor rgb=\"FF3F8F47\"/></patternFill></fill></dxf>"
        + "</dxfs></styleSheet>";
}
