namespace ScheduleRisk.Core.Reporting.Export;

/// <summary>
/// The browser app's Modernist look for exported reports: its ink, one red accent and neutral ramp, resolved to plain
/// colours (six hex digits). The design system mixes some of them with the page ground (the rules are ink at 40%,
/// table heads ink at 60%), so each ground has its own set: white paper for PDF and Word, the app's warm grey for
/// PowerPoint slides, which are read on screen like the app.
/// </summary>
public sealed record ReportPalette(string Ground, string Divider, string Label, string Line)
{
    /// <summary>--color-text</summary>
    public string Ink => "201E1D";
    /// <summary>--color-neutral-700: secondary text, IDs, notes</summary>
    public string Muted => "605D5D";
    /// <summary>--color-accent</summary>
    public string Accent => "EC3013";
    /// <summary>--color-accent-700: the kicker and the confidence-level label</summary>
    public string AccentDeep => "AE1800";
    /// <summary>--color-accent-100: the highlighted row</summary>
    public string AccentWash => "FFF2EF";
    /// <summary>--color-neutral-200: bar tracks</summary>
    public string Track => "EAE7E7";
    /// <summary>--color-neutral-300: histogram bars</summary>
    public string Neutral300 => "D7D3D3";

    public static readonly ReportPalette Paper = new("FFFFFF", "A6A5A5", "797877", "E0E0DF");
    public static readonly ReportPalette Screen = new("F3F2F2", "9F9D9D", "747372", "D5D4D4");
}

public enum FontWeight { Regular, Bold, ExtraBold }

/// <summary>
/// The Archivo faces the exports use, as the app does: 400 for text, 600 for strong text ("Archivo" Bold in Office) and
/// 800 for headings and table heads ("Archivo ExtraBold"). The files are static instances made by tools/make_doc_fonts.py.
/// </summary>
public sealed class ReportFonts
{
    public static readonly string[] Files = { "fonts/archivo-doc-400.ttf", "fonts/archivo-doc-600.ttf", "fonts/archivo-doc-800.ttf" };

    public TrueTypeFont Regular { get; }
    public TrueTypeFont Bold { get; }
    public TrueTypeFont ExtraBold { get; }

    public ReportFonts(byte[] regular, byte[] bold, byte[] extraBold)
    {
        Regular = new TrueTypeFont(regular);
        Bold = new TrueTypeFont(bold);
        ExtraBold = new TrueTypeFont(extraBold);
    }

    public TrueTypeFont this[FontWeight w] => w switch { FontWeight.Bold => Bold, FontWeight.ExtraBold => ExtraBold, _ => Regular };

    /// <summary>The CSS weight each face stands for.</summary>
    public static int CssWeight(FontWeight w) => w switch { FontWeight.Bold => 600, FontWeight.ExtraBold => 800, _ => 400 };
}
