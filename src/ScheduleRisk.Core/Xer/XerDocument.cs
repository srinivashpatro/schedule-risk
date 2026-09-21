using System.Text;

namespace ScheduleRisk.Core.Xer;

/// <summary>One XER table. Rows are padded to the field count; unknown columns are preserved.</summary>
public sealed class XerTable
{
    public XerTable(string name) { Name = name; }

    public string Name { get; }
    public List<string> Fields { get; } = new();
    public List<string[]> Rows { get; } = new();

    public int FieldIndex(string field) => Fields.IndexOf(field);

    public IEnumerable<XerRow> Records()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Fields.Count; i++) map.TryAdd(Fields[i], i);
        foreach (var r in Rows) yield return new XerRow(map, r);
    }
}

/// <summary>Read-only view of a row by column name. Missing columns read as "".</summary>
public readonly struct XerRow
{
    private readonly Dictionary<string, int> _map;
    private readonly string[] _values;

    public XerRow(Dictionary<string, int> map, string[] values) { _map = map; _values = values; }

    public string this[string field] => _map.TryGetValue(field, out int i) && i < _values.Length ? _values[i] : "";

    public bool Has(string field) => _map.ContainsKey(field);
}

/// <summary>
/// Tolerant reader/writer for Primavera P6 XER files (tab-delimited table dump:
/// ERMHDR, %T table, %F fields, %R rows, %E end).
/// </summary>
public sealed class XerDocument
{
    public List<string> Header { get; } = new();
    public Dictionary<string, XerTable> Tables { get; } = new(StringComparer.Ordinal);
    public List<string> TableOrder { get; } = new();
    public List<string> Warnings { get; } = new();
    public string Encoding { get; private set; } = "cp1252";

    public XerTable? Table(string name) => Tables.TryGetValue(name, out var t) ? t : null;

    public IEnumerable<XerRow> Rows(string name) => Table(name)?.Records() ?? Enumerable.Empty<XerRow>();

    static XerDocument()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static XerDocument Load(string path) => FromBytes(File.ReadAllBytes(path));

    public static XerDocument FromBytes(byte[] data)
    {
        string text;
        string enc;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            text = new UTF8Encoding(false, true).GetString(data, 3, data.Length - 3);
            enc = "utf-8-sig";
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, true).GetString(data);
                enc = "utf-8";
            }
            catch (DecoderFallbackException)
            {
                text = System.Text.Encoding.GetEncoding(1252).GetString(data);
                enc = "cp1252";
            }
        }
        var doc = Parse(text);
        doc.Encoding = enc;
        return doc;
    }

    public static XerDocument Parse(string text)
    {
        var doc = new XerDocument();
        XerTable? current = null;
        string[] lines = text.Split('\n');
        for (int ln = 0; ln < lines.Length; ln++)
        {
            string raw = lines[ln];
            if (raw.EndsWith('\r')) raw = raw.Substring(0, raw.Length - 1);
            if (raw.Length == 0) continue;
            int lineno = ln + 1;
            string[] parts = raw.Split('\t');
            string tag = parts[0];
            switch (tag)
            {
                case "ERMHDR":
                    doc.Header.Clear();
                    doc.Header.AddRange(parts.Skip(1));
                    break;
                case "%T":
                {
                    string name = parts.Length > 1 ? parts[1].Trim() : "";
                    if (doc.Tables.TryGetValue(name, out var existing))
                    {
                        doc.Warnings.Add($"line {lineno}: duplicate table {name}; rows appended");
                        current = existing;
                    }
                    else
                    {
                        current = new XerTable(name);
                        doc.Tables[name] = current;
                        doc.TableOrder.Add(name);
                    }
                    break;
                }
                case "%F":
                    if (current == null) { doc.Warnings.Add($"line {lineno}: %F before %T ignored"); break; }
                    if (current.Fields.Count == 0)
                        current.Fields.AddRange(parts.Skip(1).Select(p => p.Trim()));
                    break;
                case "%R":
                {
                    if (current == null) { doc.Warnings.Add($"line {lineno}: %R before %T ignored"); break; }
                    int n = current.Fields.Count;
                    var vals = new string[n];
                    int have = parts.Length - 1;
                    for (int i = 0; i < n; i++) vals[i] = i < have ? parts[i + 1] : "";
                    if (have > n)
                        doc.Warnings.Add($"line {lineno}: {current.Name} row has {have} values for {n} fields; extra dropped");
                    current.Rows.Add(vals);
                    break;
                }
                case "%E":
                    return doc;
                default:
                    doc.Warnings.Add($"line {lineno}: unrecognised line tag '{(tag.Length > 10 ? tag.Substring(0, 10) : tag)}'");
                    break;
            }
        }
        return doc;
    }

    public string ToXerText()
    {
        var sb = new StringBuilder();
        sb.Append("ERMHDR");
        foreach (var h in Header) sb.Append('\t').Append(h);
        sb.Append("\r\n");
        foreach (var name in TableOrder)
        {
            var t = Tables[name];
            sb.Append("%T\t").Append(t.Name).Append("\r\n");
            sb.Append("%F");
            foreach (var f in t.Fields) sb.Append('\t').Append(f);
            sb.Append("\r\n");
            foreach (var r in t.Rows)
            {
                sb.Append("%R");
                foreach (var v in r) sb.Append('\t').Append(v);
                sb.Append("\r\n");
            }
        }
        sb.Append("%E\r\n");
        return sb.ToString();
    }
}
