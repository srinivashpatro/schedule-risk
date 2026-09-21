using ScheduleRisk.Core.Analysis;
using ScheduleRisk.Core.Calendars;
using ScheduleRisk.Core.Cpm;
using ScheduleRisk.Core.Model;
using ScheduleRisk.Core.Risk;
using ScheduleRisk.Core.Simulation;
using ScheduleRisk.Core.Xer;

namespace ScheduleRisk.Web.Services;

/// <summary>
/// Everything the page knows: the loaded XER, the recalculated schedule, the risk model being
/// edited and the latest results. Lives in the browser tab only; nothing is sent anywhere.
/// </summary>
public sealed class AppState
{
    public event Action? Changed;
    public void Notify() => Changed?.Invoke();

    // ---------------------------------------------------------------- schedule
    public string? FileName { get; private set; }
    public XerDocument? Doc { get; private set; }
    public List<(string Id, string ShortName)> Projects { get; private set; } = new();
    public string? ProjectId { get; private set; }
    public Schedule? Schedule { get; private set; }
    public CpmEngine? Engine { get; private set; }
    public CpmResult? Cpm { get; private set; }
    public List<ValidationCheck> Checks { get; private set; } = new();
    public VerifyReport? Verify { get; private set; }
    public List<string> Warnings { get; } = new();
    public string? Error { get; set; }
    public bool Busy { get; private set; }
    public string BusyText { get; private set; } = "";
    public TimeSpan CpmTime { get; private set; }

    public bool[] Critical => Schedule == null || Cpm == null
        ? Array.Empty<bool>()
        : Enumerable.Range(0, Schedule.Activities.Count).Select(j => Cpm.IsCritical(Schedule, j)).ToArray();

    public IEnumerable<string> CodeTypes =>
        Schedule == null ? Enumerable.Empty<string>()
        : Schedule.Activities.SelectMany(a => a.Codes.Keys).Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

    public async Task LoadXerAsync(string fileName, byte[] data)
    {
        await SetBusy($"Reading {fileName}…");
        try
        {
            ClearResults();
            Error = null;
            Warnings.Clear();
            var doc = XerDocument.FromBytes(data);
            var projects = ScheduleBuilder.ListProjects(doc);
            if (projects.Count == 0) throw new InvalidDataException("This file has no projects in it. Is it a P6 XER export?");
            FileName = fileName;
            Doc = doc;
            Projects = projects;
            Warnings.AddRange(doc.Warnings.Take(20));
            await BuildScheduleAsync(projects[0].Id);
        }
        catch (Exception e)
        {
            Doc = null;
            Schedule = null;
            Error = $"Could not read {fileName}: {e.Message}";
        }
        finally
        {
            Busy = false;
            Notify();
        }
    }

    public async Task SelectProjectAsync(string projectId)
    {
        if (Doc == null || projectId == ProjectId) return;
        await SetBusy("Recalculating…");
        try
        {
            ClearResults();
            Warnings.Clear();
            await BuildScheduleAsync(projectId);
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
            Notify();
        }
    }

    private async Task BuildScheduleAsync(string projectId)
    {
        var s = ScheduleBuilder.Build(Doc!, projectId);
        Warnings.AddRange(s.Warnings);
        await SetBusy($"Scheduling {s.Activities.Count:N0} activities…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CpmEngine engine;
        try
        {
            engine = new CpmEngine(s);
        }
        catch (ScheduleLoopException e)
        {
            Schedule = s;
            Engine = null;
            Cpm = null;
            ProjectId = projectId;
            Error = "The schedule has a logic loop, so it cannot be calculated. " + e.Message;
            return;
        }
        var r = engine.Run();
        CpmTime = sw.Elapsed;
        Schedule = s;
        Engine = engine;
        Cpm = r;
        ProjectId = projectId;
        Checks = ScheduleValidator.Validate(s, r);
        Verify = P6Verifier.Verify(s, r);
        Error = null;
    }

    private async Task SetBusy(string text)
    {
        Busy = true;
        BusyText = text;
        Notify();
        await Task.Delay(20); // let the browser paint before heavy work
    }

    // ---------------------------------------------------------------- risk model
    public RiskModelDocument Model { get; set; } = RiskModelDocument.Default();

    /// <summary>Compile the editable model against the schedule. Throws with a readable message.</summary>
    public RiskModel CompileModel()
    {
        if (Schedule == null) throw new InvalidOperationException("Load a schedule first.");
        return RiskModelLoader.LoadJson(Schedule, Model.ToJson(), Critical);
    }

    // ---------------------------------------------------------------- simulation
    public SimulationSummary? Pre { get; private set; }
    public SimulationSummary? Post { get; private set; }
    public SimulationResult? PreResult { get; private set; }
    public List<string> ModelWarnings { get; } = new();
    public bool Running { get; private set; }
    public string RunLabel { get; private set; } = "";
    public int Done { get; private set; }
    public int Total { get; private set; }
    private CancellationTokenSource? _cts;

    public void ClearResults()
    {
        Pre = Post = null;
        PreResult = null;
        ModelWarnings.Clear();
    }

    public void Cancel() => _cts?.Cancel();

    public async Task RunAsync(bool withMitigation)
    {
        if (Schedule == null || Engine == null || Running) return;
        Error = null;
        ClearResults();
        RiskModel model;
        try
        {
            model = CompileModel();
        }
        catch (Exception e)
        {
            Error = "Risk model problem: " + e.Message;
            Notify();
            return;
        }
        ModelWarnings.AddRange(model.Warnings);
        Running = true;
        _cts = new CancellationTokenSource();
        try
        {
            var scenarios = withMitigation ? new[] { Scenario.PreMitigation, Scenario.PostMitigation } : new[] { Scenario.PreMitigation };
            foreach (var sc in scenarios)
            {
                RunLabel = sc == Scenario.PreMitigation ? "Pre-mitigation" : "Post-mitigation";
                var mc = new MonteCarloEngine(Schedule, model, sc, Engine) { ChunkSize = ChunkFor(Schedule.Activities.Count) };
                var progress = new InlineProgress(p => { Done = p.Done; Total = p.Total; });
                var res = await mc.RunAsync(progress: progress, yieldBetweenChunks: YieldToBrowser, cancel: _cts.Token);
                var sum = SimulationSummary.Build(mc, res);
                ModelWarnings.AddRange(res.Warnings);
                if (sc == Scenario.PreMitigation) { Pre = sum; PreResult = res; }
                else Post = sum;
                Notify();
            }
        }
        catch (OperationCanceledException)
        {
            Error = "Simulation cancelled.";
            ClearResults();
        }
        catch (Exception e)
        {
            Error = "Simulation failed: " + e.Message;
            ClearResults();
        }
        finally
        {
            Running = false;
            _cts.Dispose();
            _cts = null;
            Notify();
        }
    }

    /// <summary>Keep each chunk to roughly 50 ms of work so the page stays responsive.</summary>
    private static int ChunkFor(int activities) => Math.Clamp(250_000 / Math.Max(1, activities), 1, 200);

    private async Task YieldToBrowser()
    {
        Notify();
        await Task.Delay(1);
    }

    /// <summary>IProgress that runs synchronously (Progress&lt;T&gt; would post to the sync context).</summary>
    private sealed class InlineProgress : IProgress<(int Done, int Total)>
    {
        private readonly Action<(int Done, int Total)> _a;
        public InlineProgress(Action<(int Done, int Total)> a) => _a = a;
        public void Report((int Done, int Total) value) => _a(value);
    }

    public static string Day(long m) => m == Time.None ? "" : Time.FromMinutes(m).ToString("dd-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture);
    public static string DayTime(long m) => m == Time.None ? "" : Time.FromMinutes(m).ToString("dd-MMM-yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
