using System.Text;

namespace ScheduleRisk.Core.Calendars;

/// <summary>
/// Parser for P6's CALENDAR.clndr_data nested format:
/// (0||CalendarData()( (0||DaysOfWeek()( (0||2()( (0||0(s|08:00|f|12:00)()) ... )) ... ))
///   (0||Exceptions()( (0||0(d|46023)()) ... )) ))
/// Days of week: 1 = Sunday ... 7 = Saturday. Exception dates are Excel serials.
/// </summary>
public static class CalendarDataParser
{
    private sealed class Node
    {
        public string Key = "";
        public string Attrs = "";
        public List<Node> Children = new();
    }

    public static Dictionary<int, List<WorkShift>> DefaultWeek()
    {
        var w = new Dictionary<int, List<WorkShift>>();
        for (int d = 0; d < 7; d++)
            w[d] = d < 5 ? new List<WorkShift> { new WorkShift(480, 720), new WorkShift(780, 1020) } : new List<WorkShift>();
        return w;
    }

    public static (Dictionary<int, List<WorkShift>> Week, Dictionary<DateOnly, List<WorkShift>> Exceptions, List<string> Warnings)
        Parse(string? text)
    {
        var warnings = new List<string>();
        var sb = new StringBuilder((text ?? "").Length);
        foreach (char ch in text ?? "") sb.Append(ch < ' ' || ch == '\x7f' ? ' ' : ch);
        string s = sb.ToString();
        if (s.Trim().Length == 0)
        {
            warnings.Add("empty calendar data; Mon-Fri 08:00-17:00 assumed");
            return (DefaultWeek(), new Dictionary<DateOnly, List<WorkShift>>(), warnings);
        }
        int pos = 0;
        var nodes = ParseNodes(s, ref pos);
        var root = Find(nodes, "CalendarData");
        var top = root != null ? root.Children : nodes;
        var week = new Dictionary<int, List<WorkShift>>();
        for (int d = 0; d < 7; d++) week[d] = new List<WorkShift>();
        var dow = Find(top, "DaysOfWeek");
        if (dow == null)
        {
            warnings.Add("no DaysOfWeek in calendar data; Mon-Fri 08:00-17:00 assumed");
            week = DefaultWeek();
        }
        else
        {
            foreach (var d in dow.Children)
            {
                if (!int.TryParse(d.Key, out int p6day)) continue;
                int wd = ((p6day + 5) % 7 + 7) % 7; // Sunday(1)->6, Monday(2)->0
                week[wd] = Shifts(d.Children);
            }
        }
        var exceptions = new Dictionary<DateOnly, List<WorkShift>>();
        var exc = Find(top, "Exceptions");
        if (exc != null)
        {
            foreach (var e in exc.Children)
            {
                var a = AttrMap(e.Attrs);
                if (!a.TryGetValue("d", out var ds)) continue;
                if (!int.TryParse(ds, out int serial))
                {
                    warnings.Add($"bad exception date '{ds}'");
                    continue;
                }
                exceptions[Time.ExcelEpoch.AddDays(serial)] = Shifts(e.Children);
            }
        }
        return (week, exceptions, warnings);
    }

    private static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';

    private static List<Node> ParseNodes(string s, ref int i)
    {
        var nodes = new List<Node>();
        int n = s.Length;
        while (i < n)
        {
            while (i < n && IsWs(s[i])) i++;
            if (i >= n || s[i] != '(') break;
            i++;
            int bar = s.IndexOf("||", i, StringComparison.Ordinal);
            if (bar < 0) throw new FormatException("calendar data: missing '||'");
            i = bar + 2;
            int p = s.IndexOf('(', i);
            if (p < 0) throw new FormatException("calendar data: missing '('");
            string key = s.Substring(i, p - i).Trim();
            int q = s.IndexOf(')', p);
            if (q < 0) throw new FormatException("calendar data: missing ')'");
            string attrs = s.Substring(p + 1, q - p - 1);
            i = q + 1;
            while (i < n && IsWs(s[i])) i++;
            List<Node> children;
            if (i < n && s[i] == '(')
            {
                i++;
                children = ParseNodes(s, ref i);
                while (i < n && IsWs(s[i])) i++;
                if (i < n && s[i] == ')') i++;
            }
            else
            {
                children = new List<Node>();
            }
            while (i < n && IsWs(s[i])) i++;
            if (i < n && s[i] == ')') i++;
            nodes.Add(new Node { Key = key, Attrs = attrs, Children = children });
        }
        return nodes;
    }

    private static Dictionary<string, string> AttrMap(string attrs)
    {
        var parts = attrs.Split('|');
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int k = 0; k + 1 < parts.Length; k += 2) map[parts[k].Trim()] = parts[k + 1].Trim();
        return map;
    }

    private static int HhMm(string s)
    {
        var p = s.Split(':');
        return int.Parse(p[0]) * 60 + int.Parse(p[1]);
    }

    private static List<WorkShift> Shifts(List<Node> children)
    {
        var list = new List<WorkShift>();
        foreach (var ch in children)
        {
            var a = AttrMap(ch.Attrs);
            if (a.TryGetValue("s", out var st) && a.TryGetValue("f", out var fi))
            {
                int s0 = HhMm(st), f0 = HhMm(fi);
                if (f0 <= s0) f0 += Time.MinutesPerDay;
                list.Add(new WorkShift(s0, f0));
            }
        }
        list.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : x.End.CompareTo(y.End));
        return list;
    }

    private static Node? Find(List<Node> nodes, string key)
    {
        foreach (var nd in nodes) if (nd.Key == key) return nd;
        return null;
    }
}
