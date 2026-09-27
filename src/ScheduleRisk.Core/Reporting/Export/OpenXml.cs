using System.Buffers.Binary;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>
/// An Office Open XML package (the zip behind .docx and .pptx): its parts, their content types and relationships.
/// The namespace addresses in the parts only name XML vocabularies; the documents link to nothing outside themselves.
/// </summary>
internal sealed class OpcPackage
{
    public const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string Rel = RelNs + "/";

    private readonly List<(string Path, byte[] Data)> parts = new();
    private readonly SortedDictionary<string, string> defaults = new(StringComparer.Ordinal)
    {
        ["rels"] = "application/vnd.openxmlformats-package.relationships+xml",
        ["xml"] = "application/xml",
    };
    private readonly SortedDictionary<string, string> overrides = new(StringComparer.Ordinal);

    public void Default(string extension, string contentType) => defaults[extension] = contentType;

    public void Add(string path, string xml, string? contentType = null) =>
        Add(path, Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" + xml), contentType);

    public void Add(string path, byte[] data, string? contentType = null)
    {
        parts.Add((path, data));
        if (contentType != null) overrides["/" + path] = contentType;
    }

    public byte[] Save(DateTime stamp)
    {
        var types = new StringBuilder("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        foreach (var (ext, ct) in defaults) types.Append($"<Default Extension=\"{ext}\" ContentType=\"{ct}\"/>");
        foreach (var (name, ct) in overrides) types.Append($"<Override PartName=\"{name}\" ContentType=\"{ct}\"/>");
        types.Append("</Types>");
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string path, byte[] data)
            {
                bool packed = path.EndsWith(".png", StringComparison.Ordinal);
                var e = zip.CreateEntry(path, packed ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(Math.Max(stamp.Year, 1980), stamp.Month, stamp.Day, stamp.Hour, stamp.Minute, stamp.Second, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(data);
            }
            Entry("[Content_Types].xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" + types));
            foreach (var (path, data) in parts) Entry(path, data);
        }
        return ms.ToArray();
    }

    public static string X(string s) => SecurityElement.Escape(s) ?? "";

    /// <summary>Text for XML, without characters XML 1.0 cannot hold.</summary>
    public static string Text(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF) sb.Append(c);
        return X(sb.ToString());
    }
}

/// <summary>The relationships of one part (its _rels file).</summary>
internal sealed class Relationships
{
    private readonly List<(string Id, string Type, string Target)> list = new();

    public string Add(string type, string target)
    {
        string id = "rId" + (list.Count + 1);
        list.Add((id, type, target));
        return id;
    }

    public int Count => list.Count;

    public string Xml()
    {
        var sb = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        foreach (var (id, type, target) in list)
            sb.Append($"<Relationship Id=\"{id}\" Type=\"{type}\" Target=\"{OpcPackage.X(target)}\"/>");
        return sb.Append("</Relationships>").ToString();
    }

    /// <summary>The _rels path for a part: word/document.xml → word/_rels/document.xml.rels.</summary>
    public static string PathFor(string part)
    {
        int slash = part.LastIndexOf('/');
        return slash < 0 ? "_rels/" + part + ".rels" : part[..slash] + "/_rels/" + part[(slash + 1)..] + ".rels";
    }
}

/// <summary>How Word and PowerPoint carry an embedded font.</summary>
internal static class FontEmbedding
{
    /// <summary>A stable key for a font file, as a GUID string in braces.</summary>
    public static string Key(byte[] font)
    {
        ulong a = 14695981039346656037UL, b = 7809847782465536322UL;
        foreach (byte x in font) { a = (a ^ x) * 1099511628211UL; b = (b ^ x) * 1099511628211UL + 1; }
        string h = a.ToString("X16") + b.ToString("X16");
        return "{" + h[..8] + "-" + h[8..12] + "-" + h[12..16] + "-" + h[16..20] + "-" + h[20..32] + "}";
    }

    /// <summary>
    /// Word's obfuscated font (ECMA-376 Part 1, 17.8.1): the first 32 bytes of the font XORed with the key's 16 bytes
    /// taken in reverse order of the GUID string.
    /// </summary>
    public static byte[] Obfuscate(byte[] font, string key)
    {
        string hex = key.Replace("{", "").Replace("}", "").Replace("-", "");
        var k = new byte[16];
        for (int i = 0; i < 16; i++) k[i] = Convert.ToByte(hex.Substring(30 - 2 * i, 2), 16);
        var data = (byte[])font.Clone();
        for (int i = 0; i < 32 && i < data.Length; i++) data[i] ^= k[i % 16];
        return data;
    }

    /// <summary>
    /// PowerPoint's embedded font (.fntdata): an Embedded OpenType file (version 0x00020002) holding the TrueType font
    /// uncompressed and not encrypted, written field for field as LibreOffice's EOTConverter writes it for PPTX export.
    /// </summary>
    public static byte[] Eot(TrueTypeFont font)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0u);                                  // EOTSize, set below
        w.Write((uint)font.Data.Length);
        w.Write(0x00020002u);
        w.Write(0u);                                  // flags: no subsetting, compression or XOR
        w.Write(font.Panose);
        w.Write((byte)0);                             // charset
        w.Write((byte)(font.IsItalic ? 1 : 0));
        w.Write((uint)font.WeightClass);
        w.Write(font.FsType);
        w.Write((ushort)0x504C);                      // magic number
        foreach (uint r in font.UnicodeRange) w.Write(r);
        foreach (uint r in font.CodePageRange) w.Write(r);
        w.Write(font.CheckSumAdjustment);
        for (int i = 0; i < 4; i++) w.Write(0u);      // reserved
        foreach (var name in new[] { font.FamilyName, font.StyleName, font.VersionName, font.FullName })
        {
            w.Write((ushort)0);                       // padding 1 to 4
            byte[] utf16 = Encoding.Unicode.GetBytes(name + "\0");
            w.Write((ushort)utf16.Length);            // size, with the terminating null
            w.Write(utf16);
        }
        w.Write((ushort)0);                           // padding 5
        w.Write((ushort)0);                           // root string size
        w.Write(new byte[] { 0x42, 0x53, 0x47, 0x50 }); // root string checksum for an empty root string
        w.Write(1252u);                               // EUDC code page
        w.Write((ushort)0);                           // padding 6
        w.Write((ushort)0);                           // signature size
        w.Write(0u);                                  // EUDC flags
        w.Write(0u);                                  // EUDC font size
        w.Write(font.Data);
        w.Flush();
        var eot = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(eot, (uint)eot.Length);
        return eot;
    }
}
