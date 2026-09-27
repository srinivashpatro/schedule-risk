using System.Buffers.Binary;
using System.Text;

namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>
/// What the exported reports need from a TrueType font: metrics and glyph widths to lay out text, the character map,
/// the names and OS/2 fields that PDF, Word and PowerPoint embedding ask for, and subsets that keep only the glyphs a
/// document uses (glyph ids unchanged, other outlines emptied, layout tables dropped).
/// </summary>
public sealed class TrueTypeFont
{
    private readonly Dictionary<string, (int Offset, int Length)> tables = new(StringComparer.Ordinal);
    private readonly Dictionary<int, int> cmap = new();
    private readonly ushort[] advances;
    private readonly int[] loca;

    public byte[] Data { get; }
    public int UnitsPerEm { get; }
    /// <summary>Ascender and descender (negative) from hhea, in font units.</summary>
    public int Ascent { get; }
    public int Descent { get; }
    public int LineGap { get; }
    public int CapHeight { get; }
    public int XHeight { get; }
    public int NumGlyphs { get; }
    public (int XMin, int YMin, int XMax, int YMax) BoundingBox { get; }
    public int WeightClass { get; }
    public ushort FsType { get; }
    public ushort FsSelection { get; }
    public byte[] Panose { get; } = new byte[10];
    public uint[] UnicodeRange { get; } = new uint[4];
    public uint[] CodePageRange { get; } = new uint[2];
    public uint CheckSumAdjustment { get; }
    public double ItalicAngle { get; }
    public string FamilyName { get; } = "";
    public string StyleName { get; } = "";
    public string FullName { get; } = "";
    public string VersionName { get; } = "";
    public string PostScriptName { get; } = "";
    public bool IsBold => (FsSelection & 0x20) != 0;
    public bool IsItalic => (FsSelection & 0x01) != 0;

    public TrueTypeFont(byte[] data)
    {
        Data = data;
        uint version = U32(0);
        if (version != 0x00010000 && version != 0x74727565) throw new InvalidDataException("Not a TrueType font.");
        int n = U16(4);
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + 16 * i;
            tables[Encoding.ASCII.GetString(data, rec, 4)] = ((int)U32(rec + 8), (int)U32(rec + 12));
        }
        foreach (var tag in new[] { "head", "hhea", "maxp", "hmtx", "cmap", "loca", "glyf" })
            if (!tables.ContainsKey(tag)) throw new InvalidDataException($"The font has no {tag} table.");

        int head = tables["head"].Offset;
        CheckSumAdjustment = U32(head + 8);
        UnitsPerEm = U16(head + 18);
        BoundingBox = (I16(head + 36), I16(head + 38), I16(head + 40), I16(head + 42));
        bool longLoca = I16(head + 50) == 1;

        int hhea = tables["hhea"].Offset;
        Ascent = I16(hhea + 4);
        Descent = I16(hhea + 6);
        LineGap = I16(hhea + 8);
        int hMetrics = U16(hhea + 34);

        NumGlyphs = U16(tables["maxp"].Offset + 4);
        advances = new ushort[NumGlyphs];
        int hmtx = tables["hmtx"].Offset;
        for (int g = 0; g < NumGlyphs; g++)
            advances[g] = (ushort)U16(hmtx + 4 * Math.Min(g, hMetrics - 1));

        loca = new int[NumGlyphs + 1];
        int lo = tables["loca"].Offset;
        for (int g = 0; g <= NumGlyphs; g++)
            loca[g] = longLoca ? (int)U32(lo + 4 * g) : 2 * U16(lo + 2 * g);

        ReadCmap(tables["cmap"].Offset);

        CapHeight = (int)(0.7 * UnitsPerEm);
        XHeight = (int)(0.5 * UnitsPerEm);
        if (tables.TryGetValue("OS/2", out var os2))
        {
            int o = os2.Offset, ver = U16(o);
            WeightClass = U16(o + 4);
            FsType = (ushort)U16(o + 8);
            Array.Copy(data, o + 32, Panose, 0, 10);
            for (int k = 0; k < 4; k++) UnicodeRange[k] = U32(o + 42 + 4 * k);
            FsSelection = (ushort)U16(o + 62);
            if (ver >= 1 && os2.Length >= 86) { CodePageRange[0] = U32(o + 78); CodePageRange[1] = U32(o + 82); }
            if (ver >= 2 && os2.Length >= 90) { XHeight = I16(o + 86); CapHeight = I16(o + 88); }
        }
        if (tables.TryGetValue("post", out var post)) ItalicAngle = I32(post.Offset + 4) / 65536.0;
        if (tables.TryGetValue("name", out var name))
        {
            FamilyName = Name(name.Offset, 1);
            StyleName = Name(name.Offset, 2);
            FullName = Name(name.Offset, 4);
            VersionName = Name(name.Offset, 5);
            PostScriptName = Name(name.Offset, 6);
        }
    }

    public bool Has(int codepoint) => cmap.ContainsKey(codepoint);

    /// <summary>The glyph for a character, or 0 (.notdef) when the font does not have it.</summary>
    public int GlyphId(int codepoint) => cmap.TryGetValue(codepoint, out int g) ? g : 0;

    /// <summary>Bytes of a glyph's outline in the glyf table (0 for an empty glyph).</summary>
    internal int GlyphLength(int glyph) => loca[glyph + 1] - loca[glyph];

    /// <summary>Advance width of a glyph, in font units.</summary>
    public int Advance(int glyph) => advances[Math.Clamp(glyph, 0, advances.Length - 1)];

    /// <summary>Width of a text in points at a size in points: the sum of the advance widths (no kerning), plus the
    /// letter spacing after every character but the last.</summary>
    public double Width(string text, double size, double letterSpacing = 0)
    {
        long units = 0;
        int count = 0;
        foreach (var r in text.EnumerateRunes()) { units += Advance(GlyphId(r.Value)); count++; }
        return units * size / UnitsPerEm + (count > 1 ? (count - 1) * letterSpacing : 0);
    }

    /// <summary>
    /// A copy of the font with only these glyphs' outlines (and the components of composite glyphs, and .notdef).
    /// Glyph ids and widths stay as they are, so text set with the full font draws the same with the subset.
    /// </summary>
    public byte[] Subset(IEnumerable<int> glyphs)
    {
        var keep = new SortedSet<int>();
        var todo = new Stack<int>(glyphs.Append(0));
        while (todo.Count > 0)
        {
            int g = todo.Pop();
            if (g < 0 || g >= NumGlyphs || !keep.Add(g)) continue;
            foreach (int c in Components(g)) todo.Push(c);
        }

        int glyfAt = tables["glyf"].Offset;
        var glyf = new MemoryStream();
        var newLoca = new byte[4 * (NumGlyphs + 1)];
        for (int g = 0; g < NumGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * g), (uint)glyf.Length);
            int len = loca[g + 1] - loca[g];
            if (len <= 0 || !keep.Contains(g)) continue;
            glyf.Write(Data, glyfAt + loca[g], len);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(4 * NumGlyphs), (uint)glyf.Length);

        var parts = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (tag, (offset, length)) in tables)
        {
            if (tag is "GSUB" or "GPOS" or "GDEF" or "kern" or "DSIG" or "glyf" or "loca" or "hdmx" or "LTSH" or "VDMX") continue;
            parts[tag] = Data.AsSpan(offset, length).ToArray();
        }
        parts["glyf"] = glyf.ToArray();
        parts["loca"] = newLoca;
        var headTable = parts["head"];
        BinaryPrimitives.WriteInt16BigEndian(headTable.AsSpan(50), 1);       // long loca offsets
        BinaryPrimitives.WriteUInt32BigEndian(headTable.AsSpan(8), 0);       // checkSumAdjustment, set below
        return Assemble(parts);
    }

    /// <summary>Writes an sfnt from its tables: the directory in tag order, each table 4-byte aligned with its checksum,
    /// and head's checkSumAdjustment for the whole file.</summary>
    internal static byte[] Assemble(SortedDictionary<string, byte[]> parts)
    {
        int n = parts.Count, pow = 1, log = 0;
        while (pow * 2 <= n) { pow *= 2; log++; }
        int dirLen = 12 + 16 * n;
        int total = dirLen + parts.Values.Sum(p => (p.Length + 3) & ~3);
        var font = new byte[total];
        var span = font.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)(pow * 16));
        BinaryPrimitives.WriteUInt16BigEndian(span[8..], (ushort)log);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], (ushort)(n * 16 - pow * 16));
        int at = dirLen, rec = 12, headAt = -1;
        foreach (var (tag, bytes) in parts)
        {
            bytes.CopyTo(font, at);
            Encoding.ASCII.GetBytes(tag).CopyTo(font, rec);
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 4)..], Checksum(font, at, (bytes.Length + 3) & ~3));
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 8)..], (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(span[(rec + 12)..], (uint)bytes.Length);
            if (tag == "head") headAt = at;
            at += (bytes.Length + 3) & ~3;
            rec += 16;
        }
        if (headAt >= 0)
            BinaryPrimitives.WriteUInt32BigEndian(span[(headAt + 8)..], unchecked(0xB1B0AFBA - Checksum(font, 0, font.Length)));
        return font;
    }

    private static uint Checksum(byte[] b, int offset, int length)
    {
        uint sum = 0;
        for (int i = 0; i < length; i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++) word = (word << 8) | (offset + i + k < b.Length && i + k < length ? b[offset + i + k] : 0u);
            sum = unchecked(sum + word);
        }
        return sum;
    }

    /// <summary>The glyphs a composite glyph is built from (none for a simple or empty glyph).</summary>
    private IEnumerable<int> Components(int g)
    {
        if (loca[g + 1] - loca[g] < 10) yield break;
        int p = tables["glyf"].Offset + loca[g];
        if (I16(p) >= 0) yield break;
        p += 10;
        int flags;
        do
        {
            flags = U16(p);
            yield return U16(p + 2);
            p += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            if ((flags & 0x0008) != 0) p += 2;
            else if ((flags & 0x0040) != 0) p += 4;
            else if ((flags & 0x0080) != 0) p += 8;
        } while ((flags & 0x0020) != 0);
    }

    private void ReadCmap(int at)
    {
        int n = U16(at + 2), best = -1, bestRank = int.MaxValue;
        for (int i = 0; i < n; i++)
        {
            int pid = U16(at + 4 + 8 * i), eid = U16(at + 6 + 8 * i), sub = at + (int)U32(at + 8 + 8 * i), format = U16(sub);
            int rank = (pid, eid, format) switch
            {
                (3, 10, 12) => 0,
                (0, _, 12) => 1,
                (3, 1, 4) => 2,
                (0, _, 4) => 3,
                (_, _, 4 or 12) => 4,
                _ => int.MaxValue,
            };
            if (rank < bestRank) { bestRank = rank; best = sub; }
        }
        if (best < 0) throw new InvalidDataException("The font has no Unicode character map.");
        if (U16(best) == 12)
        {
            long groups = U32(best + 12);
            for (long k = 0; k < groups; k++)
            {
                int gAt = best + 16 + (int)(12 * k);
                uint start = U32(gAt), end = U32(gAt + 4), glyph = U32(gAt + 8);
                for (uint c = start; c <= end && c <= 0x10FFFF; c++) cmap[(int)c] = (int)(glyph + (c - start));
            }
            return;
        }
        int segs = U16(best + 6) / 2;
        int ends = best + 14, starts = ends + 2 * segs + 2, deltas = starts + 2 * segs, ranges = deltas + 2 * segs;
        for (int s = 0; s < segs; s++)
        {
            int end = U16(ends + 2 * s), start = U16(starts + 2 * s), delta = I16(deltas + 2 * s), ro = U16(ranges + 2 * s);
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (ro == 0) glyph = (c + delta) & 0xFFFF;
                else
                {
                    glyph = U16(ranges + 2 * s + ro + 2 * (c - start));
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                if (glyph != 0) cmap[c] = glyph;
            }
        }
    }

    /// <summary>A name from the name table: Windows English first, then any Windows language, then Macintosh Roman.</summary>
    private string Name(int at, int id)
    {
        int count = U16(at + 2), strings = at + U16(at + 4);
        string? windows = null, other = null, mac = null;
        for (int i = 0; i < count; i++)
        {
            int r = at + 6 + 12 * i;
            int pid = U16(r), lang = U16(r + 4), nid = U16(r + 6), len = U16(r + 8), off = U16(r + 10);
            if (nid != id) continue;
            if (pid == 3)
            {
                string s = Encoding.BigEndianUnicode.GetString(Data, strings + off, len);
                if (lang == 0x409) windows ??= s; else other ??= s;
            }
            else if (pid == 1) mac ??= Encoding.Latin1.GetString(Data, strings + off, len);
        }
        return windows ?? other ?? mac ?? "";
    }

    private int U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(at));
    private int I16(int at) => BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(at));
    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(at));
    private int I32(int at) => BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(at));
}
