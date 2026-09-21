using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
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

    protected static string Pct(double x, int digits = 0) => (x * 100).ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture) + "%";
    protected static string Num(double x, int digits = 1) => x.ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture);
}
