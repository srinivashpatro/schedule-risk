namespace ScheduleRisk.Core.Reporting;

public enum WorkflowStep { Setup, Identify, Assess, Promote, ScheduleCheck, Model, Results, Review }

/// <summary>One step of the risk workflow as users see it: its number (none for a bridge), title and one-line description.</summary>
public sealed record WorkflowStepInfo(WorkflowStep Step, string Number, string Title, string Description)
{
    public bool Bridge => Number.Length == 0;

    /// <summary>The small heading over a step's page, e.g. "Step 04 · Schedule check"; a bridge has only its title.</summary>
    public string Kicker => Bridge ? Title : $"Step {Number} · {Title}";
}

/// <summary>The risk workflow from register to report (design/workflow/steps.png): three qualitative steps, the Promote bridge
/// that turns scored risks into quantified ones, then the schedule, the model, the results and the reports.</summary>
public static class Workflow
{
    public static IReadOnlyList<WorkflowStepInfo> Steps { get; } = new WorkflowStepInfo[]
    {
        new(WorkflowStep.Setup, "01", "Setup", "Matrix scales, promote threshold, categories"),
        new(WorkflowStep.Identify, "02", "Identify", "Propose, approve and describe risks"),
        new(WorkflowStep.Assess, "03", "Assess", "Heat map, responses, owners and actions"),
        new(WorkflowStep.Promote, "", "Promote", "Score bands become probabilities and days"),
        new(WorkflowStep.ScheduleCheck, "04", "Schedule check", "P6 date verification and health checks"),
        new(WorkflowStep.Model, "05", "Model", "Uncertainty, risk drivers, correlation"),
        new(WorkflowStep.Results, "06", "Results", "Pre vs post mitigation, tornado, cost-benefit"),
        new(WorkflowStep.Review, "07", "Review", "PDF, Word, PowerPoint and CSV reports"),
    };

    public static WorkflowStepInfo Get(WorkflowStep step) => Steps[(int)step];
}
