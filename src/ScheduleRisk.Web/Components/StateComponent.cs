using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using ScheduleRisk.Core.Reporting;
using ScheduleRisk.Web.Services;

namespace ScheduleRisk.Web.Components;

/// <summary>Base for panels: re-renders whenever <see cref="AppState"/> changes.</summary>
public abstract class StateComponent : ComponentBase, IDisposable
{
    [Inject] protected AppState State { get; set; } = null!;
    [Inject] protected IJSRuntime JS { get; set; } = null!;

    protected override void OnInitialized() => State.Changed += OnStateChanged;

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => State.Changed -= OnStateChanged;

    protected ValueTask Download(string fileName, string contentType, string text) =>
        JS.InvokeVoidAsync("sraDownload", fileName, contentType, text);

    protected async Task DownloadReport()
    {
        if (State.Pre == null || State.Schedule == null || State.Cpm == null) return;
        string html = HtmlReport.Build(State.Schedule, State.Pre, State.Post, State.Checks,
            State.Verify is { Compared: > 0 } ? State.Verify : null, State.Model.Name);
        await Download($"{State.Schedule.ProjectCode}-risk-report.html", "text/html", html);
    }

    protected async Task DownloadTables()
    {
        if (State.Pre == null || State.Schedule == null) return;
        string code = State.Schedule.ProjectCode;
        await Download($"{code}-activities.csv", "text/csv", CsvExport.Activities(State.Pre));
        await Download($"{code}-risks.csv", "text/csv", CsvExport.Risks(State.Pre));
        if (State.PreResult != null) await Download($"{code}-iterations.csv", "text/csv", CsvExport.Iterations(State.PreResult));
    }

    protected static string Pct(double x, int digits = 0) => (x * 100).ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture) + "%";
    protected static string Num(double x, int digits = 1) => x.ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture);
}
