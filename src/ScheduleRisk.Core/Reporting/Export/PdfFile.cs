using System.Globalization;
using System.Text;

namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>The objects of a PDF file and its cross-reference table (PDF 1.7, no dependencies).</summary>
internal sealed class PdfFile
{
    private readonly List<byte[]> objects = new();

    /// <summary>A new object number, filled later with <see cref="Set"/> or <see cref="SetStream"/>.</summary>
    public int Reserve()
    {
        objects.Add(Array.Empty<byte>());
        return objects.Count;
    }

    public int Add(string body)
    {
        int id = Reserve();
        Set(id, body);
        return id;
    }

    public void Set(int id, string body) => objects[id - 1] = Encoding.Latin1.GetBytes(body);

    public int AddStream(string dict, byte[] data, bool compress = true)
    {
        int id = Reserve();
        SetStream(id, dict, data, compress);
        return id;
    }

    /// <summary>A stream object; <paramref name="dict"/> is the dictionary's entries without Length and Filter.</summary>
    public void SetStream(int id, string dict, byte[] data, bool compress = true)
    {
        byte[] body = compress ? Png.Zlib(data) : data;
        var ms = new MemoryStream();
        ms.Write(Encoding.Latin1.GetBytes($"<< {dict} /Length {body.Length}{(compress ? " /Filter /FlateDecode" : "")} >>\nstream\n"));
        ms.Write(body);
        ms.Write(Encoding.Latin1.GetBytes("\nendstream"));
        objects[id - 1] = ms.ToArray();
    }

    public byte[] Save(int root, int info)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");
        ms.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            W($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            W("\nendobj\n");
        }
        long xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) W(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        string id = Id();
        W($"trailer\n<< /Size {objects.Count + 1} /Root {root} 0 R /Info {info} 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>The file identifier: two FNV-1a hashes of the objects (the same content gives the same file).</summary>
    private string Id()
    {
        ulong a = 14695981039346656037UL, b = 1099511628211UL * 31;
        foreach (var o in objects)
            foreach (byte x in o)
            {
                a = (a ^ x) * 1099511628211UL;
                b = (b ^ x) * 1099511628211UL + 7;
            }
        return a.ToString("X16") + b.ToString("X16");
    }

    /// <summary>A text string for the document information: UTF-16 with a byte order mark, in hex.</summary>
    public static string TextString(string s)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (byte x in Encoding.BigEndianUnicode.GetBytes(s)) sb.Append(x.ToString("X2"));
        return sb.Append('>').ToString();
    }
}

/// <summary>
/// A TrueType face in a PDF as a Type 0 font: each character used gets its own CID (1, 2, …), mapped to its glyph by a
/// CIDToGIDMap and to Unicode by a ToUnicode CMap, so text can be copied and searched; the embedded font file is a
/// subset with only those glyphs. Characters the font lacks keep their CID and show the font's missing-glyph box.
/// </summary>
internal sealed class PdfFont
{
    private readonly Dictionary<int, int> cids = new();
    private readonly List<int> codepoints = new() { 0 };

    public TrueTypeFont Font { get; }
    public string Name { get; }
    public int Id { get; }

    public PdfFont(PdfFile pdf, TrueTypeFont font, string name)
    {
        Font = font;
        Name = name;
        Id = pdf.Reserve();
    }

    /// <summary>The text as a hex string of two-byte CIDs, for the Tj operator.</summary>
    public string Encode(string text)
    {
        var sb = new StringBuilder("<");
        foreach (var r in text.EnumerateRunes())
        {
            if (!cids.TryGetValue(r.Value, out int cid))
            {
                cid = codepoints.Count;
                codepoints.Add(r.Value);
                cids[r.Value] = cid;
            }
            sb.Append(cid.ToString("X4"));
        }
        return sb.Append('>').ToString();
    }

    public void Write(PdfFile pdf)
    {
        var inv = CultureInfo.InvariantCulture;
        int upem = Font.UnitsPerEm;
        int Scale(int v) => (int)Math.Round(v * 1000.0 / upem);
        var glyphs = codepoints.Select(Font.GlyphId).ToList();
        string baseName = Tag() + "+" + (Font.PostScriptName.Length > 0 ? Font.PostScriptName : "Font").Replace(" ", "");

        byte[] subset = Font.Subset(glyphs);
        int file = pdf.AddStream($"/Length1 {subset.Length}", subset);
        var map = new byte[2 * glyphs.Count];
        for (int i = 0; i < glyphs.Count; i++) { map[2 * i] = (byte)(glyphs[i] >> 8); map[2 * i + 1] = (byte)glyphs[i]; }
        int cidToGid = pdf.AddStream("", map);
        int toUnicode = pdf.AddStream("", Encoding.ASCII.GetBytes(ToUnicode()));

        var (x0, y0, x1, y1) = Font.BoundingBox;
        int stemV = (int)(50 + Math.Pow(Math.Max(Font.WeightClass, 100) / 65.0, 2));
        int descriptor = pdf.Add($"<< /Type /FontDescriptor /FontName /{baseName} /Flags {(Font.IsItalic ? 96 : 32)} " +
            $"/FontBBox [{Scale(x0)} {Scale(y0)} {Scale(x1)} {Scale(y1)}] /ItalicAngle {Font.ItalicAngle.ToString("0.##", inv)} " +
            $"/Ascent {Scale(Font.Ascent)} /Descent {Scale(Font.Descent)} /CapHeight {Scale(Font.CapHeight)} /XHeight {Scale(Font.XHeight)} " +
            $"/StemV {stemV} /FontFile2 {file} 0 R >>");
        var widths = string.Join(' ', glyphs.Skip(1).Select(g => Scale(Font.Advance(g)).ToString(inv)));
        int cidFont = pdf.Add($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseName} " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            $"/FontDescriptor {descriptor} 0 R /DW {Scale(Font.Advance(0))} /W [1 [{widths}]] /CIDToGIDMap {cidToGid} 0 R >>");
        pdf.Set(Id, $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseName} /Encoding /Identity-H " +
            $"/DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>");
    }

    private string ToUnicode()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        for (int start = 1; start < codepoints.Count; start += 100)
        {
            int n = Math.Min(100, codepoints.Count - start);
            sb.Append(n).Append(" beginbfchar\n");
            for (int cid = start; cid < start + n; cid++)
            {
                sb.Append('<').Append(cid.ToString("X4")).Append("> <");
                foreach (byte b in Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(codepoints[cid]))) sb.Append(b.ToString("X2"));
                sb.Append(">\n");
            }
            sb.Append("endbfchar\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }

    /// <summary>The six capital letters that mark a subset, from the characters it holds.</summary>
    private string Tag()
    {
        uint h = 2166136261;
        foreach (int c in codepoints) h = (h ^ (uint)c) * 16777619;
        foreach (char c in Name) h = (h ^ c) * 16777619;
        var t = new char[6];
        for (int i = 0; i < 6; i++) { t[i] = (char)('A' + h % 26); h /= 26; }
        return new string(t);
    }
}
