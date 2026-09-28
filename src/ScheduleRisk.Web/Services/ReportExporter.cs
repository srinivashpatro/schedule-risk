using Microsoft.JSInterop;
using ScheduleRisk.Core.Reporting.Export;

namespace ScheduleRisk.Web.Services;

public enum ReportFormat { Pdf, Word, PowerPoint, Html, Csv }

/// <summary>
/// Writes the PDF, Word and PowerPoint reports in the browser. The Archivo files for documents come from this site's own
/// fonts folder the first time a report is made; the browser draws each chart into a picture (sraRaster in js/app.js);
/// the file is written here and handed to the browser as a download. Nothing is sent anywhere.
/// </summary>
public sealed class ReportExporter
{
    private const double Scale = 2;   // chart pictures at twice their size: about 280 dpi across an A4 page

    private readonly HttpClient http;
    private readonly IJSRuntime js;
    private readonly AppState state;
    private ReportFonts? fonts;

    public ReportExporter(HttpClient http, IJSRuntime js, AppState state)
    {
        this.http = http;
        this.js = js;
        this.state = state;
    }

    public static string Name(ReportFormat f) => f switch
    {
        ReportFormat.Pdf => "PDF",
        ReportFormat.Word => "Word",
        ReportFormat.PowerPoint => "PowerPoint",
        ReportFormat.Html => "HTML report",
        _ => "CSV tables",
    };

    public async Task ExportAsync(ReportFormat format)
    {
        if (state.Schedule is not { } s || state.Pre is not { } pre) return;
        string what = Name(format);
        try
        {
            await state.ShowBusy($"Preparing the {what} report…");
            if (fonts == null)
            {
                var files = new List<byte[]>();
                foreach (var f in ReportFonts.Files) files.Add(await http.GetByteArrayAsync(f));
                fonts = new ReportFonts(files[0], files[1], files[2]);
            }
            var doc = ReportContent.Build(new ReportInput(s, pre, state.Post, state.Health,
                state.Verify is { Compared: > 0 } v ? v : null, state.Model.Name, state.Percentile), fonts);

            var palette = format == ReportFormat.PowerPoint ? ReportPalette.Screen : ReportPalette.Paper;
            var images = new Dictionary<string, ReportImage>();
            var charts = doc.Charts.ToList();
            for (int i = 0; i < charts.Count; i++)
            {
                await state.ShowBusy($"Drawing the charts ({i + 1} of {charts.Count})…");
                var c = charts[i];
                int w = (int)Math.Round(c.Width * Scale), h = (int)Math.Round(c.Height * Scale);
                byte[] png = await js.InvokeAsync<byte[]>("sraRaster", ReportCharts.Finish(c, palette, fonts, Scale), w, h);
                byte[]? rgb = format == ReportFormat.Pdf ? await js.InvokeAsync<byte[]>("sraRasterRgb") : null;
                images[c.Key] = new ReportImage(png, w, h, rgb);
            }

            await state.ShowBusy($"Writing the {what} report…");
            var (bytes, ext, type) = format switch
            {
                ReportFormat.Pdf => (PdfReport.Write(doc, fonts, images), "pdf", "application/pdf"),
                ReportFormat.Word => (DocxReport.Write(doc, fonts, images), "docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
                _ => (PptxReport.Write(doc, fonts, images), "pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
            };
            await js.InvokeVoidAsync("sraDownload", $"{s.ProjectCode}-risk-report.{ext}", type, bytes);
        }
        catch (HttpRequestException e)   // only the fonts are fetched
        {
            state.Error = $"Could not make the {what} report. {SiteFiles.LoadFailed("the report fonts", e)}";
        }
        catch (Exception e)
        {
            state.Error = $"Could not make the {what} report: {e.Message}";
        }
        finally
        {
            state.EndBusy();
        }
    }
}
