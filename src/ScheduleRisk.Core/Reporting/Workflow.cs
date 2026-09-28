namespace ScheduleRisk.Core.Reporting;

public enum WorkflowStep { Setup, Identify, Assess, Promote, ScheduleCheck, Model, Results, Review }

/// <summary>One step of the risk workflow as users see it: its number (none for a bridge), title and one-line description.
/// <see cref="Planned"/> lists what a step not built yet will hold.</summary>
public sealed record WorkflowStepInfo(WorkflowStep Step, string Number, string Title, string Description, IReadOnlyList<string> Planned)
{
    public bool Bridge => Number.Length == 0;

    /// <summary>The small heading over a step's page, e.g. "Step 04 · Schedule check"; a bridge has only its title.</summary>
    public string Kicker => Bridge ? Title : $"Step {Number} · {Title}";
}

/// <summary>The risk workflow from register to report (design/workflow/steps.png): three qualitative steps, the Promote bridge
/// that turns scored risks into quantified ones, then the schedule, the model, the results and the reports.</summary>
public static class Workflow
{
    private static readonly string[] None = Array.Empty<string>();

    public static IReadOnlyList<WorkflowStepInfo> Steps { get; } = new WorkflowStepInfo[]
    {
        new(WorkflowStep.Setup, "01", "Setup", "Matrix scales, promote threshold, categories", None),
        new(WorkflowStep.Identify, "02", "Identify", "Propose, approve and describe risks", None),
        new(WorkflowStep.Assess, "03", "Assess", "Heat map, responses, owners and actions", new[]
        {
            "Probability and severity of each approved risk: inherent, current and target",
            "A heat map with each risk's movement across the matrix",
            "Response, owner, cost and actions for each risk",
        }),
        new(WorkflowStep.Promote, "", "Promote", "Score bands become probabilities and days", new[]
        {
            "Red and Amber risks become risks in the model",
            "A probability from the probability band, a range of days from the schedule severity and the planned duration",
            "Each promoted risk mapped to the activities it affects",
        }),
        new(WorkflowStep.ScheduleCheck, "04", "Schedule check", "P6 date verification and health checks", None),
        new(WorkflowStep.Model, "05", "Model", "Uncertainty, risk drivers, correlation", None),
        new(WorkflowStep.Results, "06", "Results", "Pre vs post mitigation, tornado, cost-benefit", None),
        new(WorkflowStep.Review, "07", "Review", "PDF, Word, PowerPoint and CSV reports", None),
    };

    public static WorkflowStepInfo Get(WorkflowStep step) => Steps[(int)step];
}
