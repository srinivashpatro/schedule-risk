using System.Globalization;
using System.Net;
using System.Text;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Simulation;

namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>
/// The Results charts as standalone SVG pictures for the exported reports: the app's finish date chart (as a histogram
/// and as the cumulative curve) with its legend, and the bar lists (risk ranking, criticality, drivers) drawn as the app
/// draws them. Colours are CSS variables filled from a <see cref="ReportPalette"/>, and the text is set in Archivo,
/// both added by <see cref="Finish"/> just before the browser turns the SVG into a picture.
/// </summary>
public static class ReportCharts
{
    public const int Width = 960;
    /// <summary>Bar lists are narrower, as in the app's half-width columns, so their text keeps its size on a page.</summary>
    public const int BarsWidth = 720;
    private const string Slot = "/*theme*/";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>One bar: an ID, a name, the bar's length (0 to 1) and the value printed beside it.</summary>
    public sealed record Bar(string Id, string Name, double Fraction, string Value);

    /// <summary>The finish date chart as shown in the Results, with the chosen confidence level marked.</summary>
    public static Chart Distribution(string key, string title, SimulationSummary pre, SimulationSummary? post, Schedule s,
                                     int percentile, bool histogram, ReportFonts fonts)
    {
        string svg = HtmlReport.SCurve(pre, post, s, interactive: false, percentile: percentile, histogram: histogram);
        string inner = svg[(svg.IndexOf('>') + 1)..svg.LastIndexOf("</svg>", StringComparison.Ordinal)];
        const int plot = 300;

        var items = new List<(string Key, string Text)>();
        if (!histogram)
        {
            items.Add(("a", "Pre-mitigation"));
            if (post != null) items.Add(("b", "Post-mitigation"));
        }
        items.Add(("boxes", (post != null ? "Pre-mitigation finish dates" : "Finish dates") + $", red up to P{percentile}"));
        items.Add(("dash", "Deterministic finish"));

        var legend = new StringBuilder();
        const double size = 14, gap = 24, lineH = 22;
        double x = 0, y = plot + 18;
        foreach (var (k, text) in items)
        {
            double keyW = k switch { "boxes" => 30, "dash" => 14, _ => 24 };
            double w = keyW + fonts.Regular.Width(text, size);
            if (x > 0 && x + w > Width) { x = 0; y += lineH; }
            double cy = y - 4.5;
            switch (k)
            {
                case "a":
                case "b":
                    legend.Append($"<rect x=\"{F(x)}\" y=\"{F(cy - 1)}\" width=\"16\" height=\"2\" fill=\"var(--{k})\"/>");
                    break;
                case "boxes":
                    legend.Append($"<rect x=\"{F(x)}\" y=\"{F(cy - 5)}\" width=\"10\" height=\"10\" class=\"key-in\"/>");
                    legend.Append($"<rect x=\"{F(x + 12)}\" y=\"{F(cy - 5)}\" width=\"10\" height=\"10\" class=\"key-out\"/>");
                    break;
                default:
                    legend.Append($"<line x1=\"{F(x + 5)}\" x2=\"{F(x + 5)}\" y1=\"{F(cy - 7)}\" y2=\"{F(cy + 7)}\" class=\"key-dash\"/>");
                    break;
            }
            legend.Append($"<text x=\"{F(x + keyW)}\" y=\"{F(y)}\" class=\"lg\">{E(text)}</text>");
            x += w + gap;
        }
        int height = (int)Math.Ceiling(y + 8);
        return Wrap(key, title, histogram ? "hist" : "curve", height, inner + legend,
            histogram ? "Histogram of the project finish dates" : "Cumulative probability of the project finish");
    }

    /// <summary>A bar list as in the Results: ID, name, a bar on a grey track and the value. The first bar is red unless
    /// <paramref name="ink"/> (the criticality list, drawn in ink).</summary>
    public static Chart Bars(string key, string title, IReadOnlyList<Bar> bars, bool ink, ReportFonts fonts)
    {
        const int row = 34, nameX = 60, nameW = 200, trackX = 272, valW = 52, idW = 48;
        const int trackW = BarsWidth - trackX - 12 - valW;
        var sb = new StringBuilder();
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            double top = i * row, mid = top + row / 2.0;
            string fill = ink ? "fill ink" : i == 0 ? "fill top" : "fill";
            sb.Append($"<text x=\"0\" y=\"{F(mid + 4)}\" class=\"id\">{E(Fit(b.Id, idW, fonts.Regular, 12))}</text>");
            sb.Append($"<text x=\"{nameX}\" y=\"{F(mid + 5)}\" class=\"nm\">{E(Fit(b.Name, nameW, fonts.Bold, 14))}</text>");
            sb.Append($"<rect x=\"{trackX}\" y=\"{F(mid - 7)}\" width=\"{trackW}\" height=\"14\" class=\"track\"/>");
            sb.Append($"<rect x=\"{trackX}\" y=\"{F(mid - 7)}\" width=\"{F(trackW * Math.Clamp(b.Fraction, 0, 1))}\" height=\"14\" class=\"{fill}\"/>");
            sb.Append($"<text x=\"{BarsWidth}\" y=\"{F(mid + 5)}\" text-anchor=\"end\" class=\"val\">{E(b.Value)}</text>");
            sb.Append($"<line x1=\"0\" x2=\"{BarsWidth}\" y1=\"{F(top + row - 0.5)}\" y2=\"{F(top + row - 0.5)}\" class=\"rule\"/>");
        }
        return Wrap(key, title, "bars", Math.Max(1, bars.Count) * row, sb.ToString(), title, BarsWidth);
    }

    /// <summary>
    /// The SVG ready for the browser to draw: the palette's colours, and @font-face rules carrying just the glyphs the
    /// chart uses from each Archivo face (so each picture stays small and draws the same in any browser).
    /// </summary>
    public static string Finish(Chart chart, ReportPalette palette, ReportFonts fonts, double scale = 1)
    {
        var used = new HashSet<int>();
        foreach (var r in WebUtility.HtmlDecode(chart.Svg).EnumerateRunes()) used.Add(r.Value);
        var css = new StringBuilder();
        foreach (var w in new[] { FontWeight.Regular, FontWeight.Bold, FontWeight.ExtraBold })
        {
            var font = fonts[w];
            byte[] subset = font.Subset(used.Select(font.GlyphId));
            css.Append("@font-face{font-family:Archivo;font-weight:").Append(ReportFonts.CssWeight(w))
               .Append(";src:url(data:font/ttf;base64,").Append(Convert.ToBase64String(subset)).Append(") format(\"truetype\")}");
        }
        css.Append("svg{--ground:#").Append(palette.Ground).Append(";--ink:#").Append(palette.Ink).Append(";--muted:#").Append(palette.Muted)
           .Append(";--accent:#").Append(palette.Accent).Append(";--accent-deep:#").Append(palette.AccentDeep).Append(";--track:#").Append(palette.Track)
           .Append(";--divider:#").Append(palette.Divider).Append(";--line:#").Append(palette.Line).Append(";--n300:#").Append(palette.Neutral300)
           .Append(";--a:var(--ink);--b:var(--accent);--fg:var(--ink)}");
        // Drawn at the picture's own size, so the browser sets the text at that size rather than enlarging it.
        string size = $"width=\"{chart.Width}\" height=\"{chart.Height}\"";
        return chart.Svg.Replace(Slot, css.ToString())
            .Replace(size, $"width=\"{F(Math.Round(chart.Width * scale))}\" height=\"{F(Math.Round(chart.Height * scale))}\"");
    }

    private static readonly string Style = Slot + string.Concat(
        "svg{font-family:Archivo,sans-serif;text-rendering:geometricPrecision}.ground{fill:var(--ground)}",
        "text{fill:var(--muted);font-size:13px}",
        "rect.bar{fill:var(--n300);opacity:.45}rect.bar.in{fill:var(--accent);opacity:.3}.hist rect.bar,.hist rect.bar.in{opacity:1}",
        "text.dlabel,text.plabel,text.slabel,text.mlabel{paint-order:stroke;stroke:var(--ground);stroke-width:4px;stroke-linejoin:round}",
        "text.dlabel,text.mlabel{fill:var(--ink);font-weight:600}text.plabel{fill:var(--accent-deep);font-weight:800}",
        "text.slabel{fill:var(--ink);font-weight:600;font-size:14px}",
        "text.lg{fill:var(--ink);font-size:14px}.key-in{fill:var(--accent)}.key-out{fill:var(--n300)}",
        ".key-dash{stroke:var(--ink);stroke-width:2;stroke-dasharray:4 3;opacity:.6}",
        "text.id{font-size:12px}text.nm,text.val{fill:var(--ink);font-size:14px;font-weight:600}",
        ".track{fill:var(--track)}.fill{fill:var(--muted)}.fill.top{fill:var(--accent)}.fill.ink{fill:var(--ink)}",
        ".rule{stroke:var(--divider);stroke-width:1}");

    private static Chart Wrap(string key, string title, string cls, int height, string body, string alt, int width = Width) => new()
    {
        Key = key,
        Title = title,
        Width = width,
        Height = height,
        Alt = alt,
        Svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" class=\"{cls}\">"
            + $"<style>{Style}</style><rect class=\"ground\" width=\"{width}\" height=\"{height}\"/>{body}</svg>",
    };

    /// <summary>The text, cut with an ellipsis if it is wider than the room.</summary>
    internal static string Fit(string text, double room, TrueTypeFont font, double size)
    {
        if (font.Width(text, size) <= room) return text;
        var runes = text.EnumerateRunes().ToList();
        for (int n = runes.Count - 1; n > 0; n--)
        {
            string cut = string.Concat(runes.Take(n).Select(r => r.ToString())).TrimEnd() + "…";
            if (font.Width(cut, size) <= room) return cut;
        }
        return "…";
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string F(double x) => x.ToString("0.##", Inv);
}
